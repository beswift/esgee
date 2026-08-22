using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using Esgee.Store;

namespace Esgee.Interop;

/// <summary>
/// Orders every clipboard intent and keeps file I/O / bitmap decode off the WPF
/// dispatcher. The final OLE handoff intentionally remains on the application's
/// existing STA: this preserves clipboard ownership and persistence semantics
/// while timing any remaining flush stall.
/// </summary>
public sealed class ClipboardService
{
    public readonly record struct Intent(
        long Sequence, uint ClipboardSequence, CancellationToken Cancellation);

    private readonly Dispatcher _dispatcher;
    private readonly Action _beforeWrite;
    private readonly Action<string> _noteKnownContent;
    private readonly SemaphoreSlim _prepareGate = new(initialCount: 1);
    private readonly object _intentGate = new();
    private long _latestRequest;
    private CancellationTokenSource? _latestCancellation;

    public ClipboardService(Dispatcher dispatcher, Action? beforeWrite = null,
        Action<string>? noteKnownContent = null)
    {
        _dispatcher = dispatcher;
        _beforeWrite = beforeWrite ?? (() => { });
        _noteKnownContent = noteKnownContent ?? (_ => { });
    }

    /// <summary>Reserve at the user/capture intent boundary, before any encode,
    /// materialization, OCR fetch, or other await that could reorder completion.</summary>
    public Intent ReserveIntent()
    {
        lock (_intentGate)
        {
            _latestCancellation?.Cancel();
            _latestCancellation = new CancellationTokenSource();
            return new Intent(++_latestRequest, Win32.GetClipboardSequenceNumber(),
                _latestCancellation.Token);
        }
    }

    public Task<bool> CopyShotAsync(Shot shot, string source)
        => CopyShotAsync(shot, source, ReserveIntent());

    /// <summary>Returns false when a newer clipboard intent superseded this one.
    /// Preparation is single-file so rapid requests cannot fan out WIC decodes.</summary>
    public async Task<bool> CopyShotAsync(Shot shot, string source, Intent intent)
    {
        var prepare = Stopwatch.StartNew();
        DragSource.PreparedTransfer transfer;
        try
        {
            await _prepareGate.WaitAsync(intent.Cancellation).ConfigureAwait(false);
            try
            {
                if (!IsCurrent(intent)) return false;
                transfer = await DragSource.PrepareAsync(shot, intent.Cancellation)
                    .ConfigureAwait(false);
            }
            finally
            {
                _prepareGate.Release();
            }
        }
        catch (OperationCanceledException) when (intent.Cancellation.IsCancellationRequested)
        {
            return false;
        }
        prepare.Stop();

        return await _dispatcher.InvokeAsync(() =>
        {
            // Construct on the STA, but before the final sequence check, so
            // almost no work separates that check from the OLE write.
            var data = DragSource.BuildDataObject(transfer);
            if (!IsCurrent(intent))
            {
                Log.Info($"clipboard: skipped superseded/external-change {source} " +
                         $"request for shot {shot.Id} " +
                         $"after {prepare.ElapsedMilliseconds} ms preparation");
                return false;
            }

            _beforeWrite(); // adjacent to the real write; the guard cannot expire during prep
            // The shot's stored SHA is the hash of the exact bytes BuildDataObject
            // put in the "PNG" format, so a later save/restore of this clipboard
            // content by another app is recognizable by content, not just timing.
            _noteKnownContent(shot.Sha256);
            var commit = Stopwatch.StartNew();
            Clipboard.SetDataObject(data, copy: true);
            commit.Stop();
            Log.Info($"clipboard: copied shot {shot.Id} from {source} " +
                     $"(prepare {prepare.ElapsedMilliseconds} ms, commit {commit.ElapsedMilliseconds} ms)");
            return true;
        }, DispatcherPriority.Normal);
    }

    public Task<bool> CopyTextAsync(string text, string source)
        => CopyTextAsync(text, source, ReserveIntent());

    public async Task<bool> CopyTextAsync(string text, string source, Intent intent)
    {
        return await _dispatcher.InvokeAsync(() =>
        {
            if (!IsCurrent(intent)) return false;
            _beforeWrite();
            var commit = Stopwatch.StartNew();
            Clipboard.SetText(text);
            commit.Stop();
            Log.Info($"clipboard: copied text from {source} (commit {commit.ElapsedMilliseconds} ms)");
            return true;
        }, DispatcherPriority.Normal);
    }

    private bool IsCurrent(Intent intent)
        => !intent.Cancellation.IsCancellationRequested &&
           intent.Sequence == Volatile.Read(ref _latestRequest) &&
           intent.ClipboardSequence == Win32.GetClipboardSequenceNumber();
}
