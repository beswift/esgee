using System.IO;
using System.Threading.Channels;
using Esgee.Store;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace Esgee.Ocr;

/// <summary>
/// Reads the text out of every capture and files it into FTS. This is what turns
/// a folder of thousands of near-identical PNGs into something you can find in —
/// "that screenshot with the 401 in it" instead of scrubbing thumbnails.
///
/// Uses the OCR engine already built into Windows: no model download, no network,
/// nothing leaves the machine.
/// </summary>
public sealed class OcrIndexer : IAsyncDisposable
{
    private readonly ShotStore _store;
    private readonly OcrEngine? _engine;
    private readonly Channel<Shot> _queue = Channel.CreateUnbounded<Shot>(
        new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _pump;

    public bool Available => _engine is not null;

    /// <summary>Which engine produced a given ocr_text — recorded per shot and
    /// carried in sync sidecars, so a future better engine can re-OCR only the
    /// rows an older engine produced. Windows.Media.Ocr has no version of its
    /// own; the OS build is the honest proxy.</summary>
    public static string EngineVersion { get; } = $"winocr/{Environment.OSVersion.Version}";

    public OcrIndexer(ShotStore store)
    {
        _store = store;

        _engine = OcrEngine.TryCreateFromUserProfileLanguages();
        if (_engine is null)
            Log.Warn("no OCR language pack available; archive search will be filename/date only");

        _pump = Task.Run(PumpAsync);
    }

    public void Enqueue(Shot shot)
    {
        if (_engine is null) return;
        _queue.Writer.TryWrite(shot);
    }

    /// <summary>Picks up anything that was captured while OCR was off or the app
    /// was closed, so the index self-heals instead of developing permanent holes.</summary>
    public void EnqueueBacklog()
    {
        if (_engine is null) return;
        foreach (var shot in _store.PendingOcr(limit: 500))
            _queue.Writer.TryWrite(shot);
    }

    private async Task PumpAsync()
    {
        try
        {
            await foreach (var shot in _queue.Reader.ReadAllAsync(_cts.Token))
            {
                try
                {
                    var text = await RecognizeAsync(shot.Path);
                    _store.SetOcr(shot.Id, text, EngineVersion);
                }
                catch (Exception ex)
                {
                    // Mark it done anyway — a file that can't be read won't start
                    // working on the next pass, and retrying forever would wedge
                    // the queue behind it.
                    Log.Warn($"ocr failed for {shot.Path}: {ex.Message}");
                    try { _store.SetOcr(shot.Id, string.Empty); } catch { }
                }
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
    }

    private async Task<string> RecognizeAsync(string path)
    {
        var bytes = await File.ReadAllBytesAsync(path, _cts.Token);

        // Feed WinRT through an in-memory stream rather than StorageFile: no
        // broker round-trip, and it sidesteps the .NET stream-interop extensions.
        using var ras = new InMemoryRandomAccessStream();
        var writer = new DataWriter(ras);
        writer.WriteBytes(bytes);
        await writer.StoreAsync();
        await writer.FlushAsync();
        writer.DetachStream();
        ras.Seek(0);

        var decoder = await BitmapDecoder.CreateAsync(ras);
        using var bitmap = await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);

        var result = await _engine!.RecognizeAsync(bitmap);
        return result.Text ?? string.Empty;
    }

    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        _cts.Cancel();
        try { await _pump; } catch { }
        _cts.Dispose();
    }
}
