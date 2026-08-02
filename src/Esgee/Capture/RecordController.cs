using System.IO;
using System.Windows;
using DrawingRect = System.Drawing.Rectangle;

namespace Esgee.Capture;

/// <summary>Everything a finished recording produced. The GIF is null when the
/// clip ran past the GIF cutoff (or the encode failed — MP4 still stands).</summary>
public sealed record RecordingResult(
    string Mp4Path,
    string? GifPath,
    string ThumbPath,
    int Width,
    int Height,
    DateTimeOffset StartedAt,
    long DurationMs);

/// <summary>
/// The record start/stop state machine: one hotkey toggles, the pill's click
/// stops, and only one recording can exist at a time. Scope rule: the last
/// selected region when there is one, else the full virtual screen — matching
/// what Win+Shift+L would re-shoot.
/// </summary>
public sealed class RecordController : IDisposable
{
    private readonly Settings _settings;
    private readonly string _archiveRoot;

    private ScreenRecorder? _recorder;
    private RecordingIndicatorWindow? _pill;
    private bool _stopping; // finalize in flight; ignore toggles until done

    public bool IsRecording => _recorder is not null;

    /// <summary>Raised on the UI thread once the MP4 (and GIF/thumb) are on disk.</summary>
    public event Action<RecordingResult>? Completed;

    /// <summary>Recording state for the tray item's label.</summary>
    public event Action<bool>? StateChanged;

    public RecordController(Settings settings, string archiveRoot)
        => (_settings, _archiveRoot) = (settings, archiveRoot);

    public void Toggle()
    {
        if (_stopping) { Log.Info("record toggle ignored; finalize in progress"); return; }
        if (IsRecording) _ = StopAsync();
        else Start();
    }

    private void Start()
    {
        Log.Info("recording requested");

        var ffmpeg = ScreenRecorder.FindFfmpeg();
        if (ffmpeg is null)
        {
            OfferFfmpeg();
            return;
        }

        var region = ResolveRegion();
        if (region.Width < 32 || region.Height < 32)
        {
            Log.Error($"recording region degenerate ({region}); refusing to start");
            return;
        }

        var takenAt = DateTimeOffset.Now;
        var dir = Path.Combine(_archiveRoot, takenAt.ToString("yyyy"), takenAt.ToString("MM"));
        Directory.CreateDirectory(dir);
        var mp4 = Unique(Path.Combine(dir, $"{takenAt:yyyy-MM-dd_HH-mm-ss}.mp4"));

        try
        {
            var recorder = new ScreenRecorder(mp4, region);
            recorder.Start(ffmpeg, Math.Clamp(_settings.RecordFps, 5, 60));
            _recorder = recorder;

            _pill = new RecordingIndicatorWindow(region, () => recorder.Elapsed);
            _pill.StopRequested += Toggle;
            _pill.Show();

            StateChanged?.Invoke(true);
        }
        catch (Exception ex)
        {
            Log.Error($"recording start failed: {ex}");
            _recorder = null;
            _pill?.Close();
            _pill = null;
        }
    }

    /// <summary>Fresh machine, no ffmpeg: recording must not silently fail, so
    /// the hotkey's first press explains and offers the one-time download.</summary>
    private void OfferFfmpeg()
    {
        if (FfmpegSetup.InProgress)
        {
            Log.Info("recording unavailable: ffmpeg download still in progress");
            return;
        }

        Log.Warn(@"recording unavailable: ffmpeg.exe not found (expected in %LOCALAPPDATA%\esgee\bin or PATH); offering download");
        var pick = MessageBox.Show(
            "Recording needs FFmpeg, which isn't on this machine yet.\n\n" +
            $"Download a one-time copy ({FfmpegSetup.SizeHint}) from gyan.dev into esgee's bin folder?",
            "esgee — set up recording", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (pick != MessageBoxResult.Yes)
        {
            Log.Info("ffmpeg download declined");
            return;
        }

        _ = InstallFfmpegAsync();
    }

    private async Task InstallFfmpegAsync()
    {
        var ok = await FfmpegSetup.TryInstallAsync();
        if (ok)
            MessageBox.Show($"FFmpeg is ready — press {_settings.RecordHotkey} to record.",
                "esgee", MessageBoxButton.OK, MessageBoxImage.Information);
        else
            MessageBox.Show("FFmpeg download failed — see esgee.log for details.\nRecording stays unavailable.",
                "esgee", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private async Task StopAsync()
    {
        var recorder = _recorder;
        if (recorder is null) return;

        Log.Info("recording stop requested");
        _stopping = true;
        _recorder = null;
        _pill?.Close();
        _pill = null;
        StateChanged?.Invoke(false);

        try
        {
            var ok = await recorder.StopAsync();
            if (!ok)
            {
                Log.Error("recording did not finalize; nothing archived");
                return;
            }

            // ffprobe + thumbnail + GIF are seconds of subprocess work on a long
            // clip — never on the UI thread. Awaits hop us back to it after.
            var result = await Task.Run(() => PostProcess(recorder));
            if (result is not null) Completed?.Invoke(result);
        }
        catch (Exception ex)
        {
            Log.Error($"recording finalize failed: {ex}");
        }
        finally
        {
            _stopping = false;
        }
    }

    private RecordingResult? PostProcess(ScreenRecorder recorder)
    {
        var ffmpeg = ScreenRecorder.FindFfmpeg()!;
        var mp4 = recorder.OutputPath;
        var durationMs = ScreenRecorder.ProbeDurationMs(mp4, recorder.Elapsed);

        var thumb = mp4 + ".png";
        if (!ScreenRecorder.ExtractThumbnail(ffmpeg, mp4, thumb, durationMs))
            thumb = mp4; // card shows a blank; archive row still lands

        string? gif = null;
        if (_settings.GifMaxSeconds > 0 && durationMs <= _settings.GifMaxSeconds * 1000L)
        {
            var gifPath = Path.ChangeExtension(mp4, ".gif");
            if (ScreenRecorder.MakeGif(ffmpeg, mp4, gifPath,
                    recorder.Region.Width,
                    Math.Clamp(_settings.GifFps, 4, 30),
                    Math.Max(120, _settings.GifMaxWidth)))
            {
                gif = gifPath;
                Log.Info($"gif ready: {gifPath} ({new FileInfo(gifPath).Length / 1024} KB)");
            }
        }

        Log.Info($"recording ready: {mp4} ({durationMs / 1000.0:0.0}s, gif={(gif is null ? "no" : "yes")})");
        return new RecordingResult(mp4, gif, thumb,
            recorder.Region.Width, recorder.Region.Height, recorder.StartedAt, durationMs);
    }

    /// <summary>LastRegion clamped to the current virtual screen, else the whole
    /// virtual screen. Width/height rounded down to even — libx264 with yuv420p
    /// rejects odd dimensions.</summary>
    private DrawingRect ResolveRegion()
    {
        var v = System.Windows.Forms.SystemInformation.VirtualScreen;
        var vb = new DrawingRect(v.X, v.Y, v.Width, v.Height);

        var rect = vb;
        if (_settings.LastRegion is { Length: 4 } last)
        {
            var candidate = new DrawingRect(last[0], last[1], last[2], last[3]);
            candidate.Intersect(vb);
            if (candidate.Width >= 32 && candidate.Height >= 32) rect = candidate;
            else Log.Warn("stored last region unusable for recording; using full screen");
        }

        rect.Width -= rect.Width % 2;
        rect.Height -= rect.Height % 2;
        return rect;
    }

    private static string Unique(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 2; ; i++)
        {
            var candidate = Path.Combine(dir, $"{stem}_{i}{ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    /// <summary>App exit while recording: finalize rather than orphan a corrupt
    /// MP4. Blocks a few seconds at most.</summary>
    public void Dispose()
    {
        _pill?.Close();
        _recorder?.Abort();
        _recorder = null;
    }
}
