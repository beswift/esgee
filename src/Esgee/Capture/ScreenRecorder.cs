using System.Diagnostics;
using System.Globalization;
using System.IO;
using DrawingRect = System.Drawing.Rectangle;

namespace Esgee.Capture;

/// <summary>
/// One ffmpeg screen-capture process, start to finalized MP4. esgee bundles a
/// static ffmpeg at %LOCALAPPDATA%\esgee\bin\ rather than hand-rolling Media
/// Foundation — gdigrab + libx264 is boring and battle-tested, and the same
/// binary does the thumbnail and GIF post-steps.
/// </summary>
public sealed class ScreenRecorder
{
    internal static readonly string BinDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "esgee", "bin");

    private Process? _proc;
    private readonly List<string> _stderrTail = [];
    private readonly Stopwatch _clock = new();

    public string OutputPath { get; }
    public DrawingRect Region { get; }
    public DateTimeOffset StartedAt { get; private set; }
    public TimeSpan Elapsed => _clock.Elapsed;

    public ScreenRecorder(string outputPath, DrawingRect region)
        => (OutputPath, Region) = (outputPath, region);

    /// <summary>Bundled copy first, PATH as a fallback. Null means recording is
    /// unavailable — callers must handle that, not crash.</summary>
    public static string? FindFfmpeg() => FindTool("ffmpeg.exe");
    public static string? FindFfprobe() => FindTool("ffprobe.exe");

    private static string? FindTool(string exe)
    {
        var bundled = Path.Combine(BinDir, exe);
        if (File.Exists(bundled)) return bundled;

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            try
            {
                var candidate = Path.Combine(dir.Trim(), exe);
                if (File.Exists(candidate)) return candidate;
            }
            catch { /* malformed PATH entry */ }
        }
        return null;
    }

    /// <summary>Spawns ffmpeg capturing <see cref="Region"/> (virtual-screen
    /// coordinates, dimensions already even-aligned by the caller).</summary>
    public void Start(string ffmpeg, int fps)
    {
        StartedAt = DateTimeOffset.Now;

        var psi = new ProcessStartInfo
        {
            FileName = ffmpeg,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,   // stop = 'q' on stdin
            RedirectStandardOutput = true,
            RedirectStandardError = true,   // must be drained or ffmpeg blocks on a full pipe
        };
        foreach (var arg in new[]
        {
            "-y",
            "-f", "gdigrab",
            "-framerate", fps.ToString(CultureInfo.InvariantCulture),
            "-offset_x", Region.X.ToString(CultureInfo.InvariantCulture),
            "-offset_y", Region.Y.ToString(CultureInfo.InvariantCulture),
            "-video_size", $"{Region.Width}x{Region.Height}",
            "-i", "desktop",
            "-c:v", "libx264",
            "-preset", "veryfast",
            "-pix_fmt", "yuv420p",          // the compatible-everywhere profile
            "-movflags", "+faststart",
            OutputPath,
        }) psi.ArgumentList.Add(arg);

        _proc = new Process { StartInfo = psi };
        _proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (_stderrTail)
            {
                _stderrTail.Add(e.Data);
                if (_stderrTail.Count > 40) _stderrTail.RemoveAt(0);
            }
        };
        _proc.Start();
        _proc.BeginErrorReadLine();
        _proc.BeginOutputReadLine();
        _clock.Start();
        Log.Info($"recording started: {Region.Width}x{Region.Height}+{Region.X}+{Region.Y} @ {fps}fps -> {OutputPath} (ffmpeg pid {_proc.Id})");
    }

    /// <summary>
    /// Graceful stop: 'q' on stdin so ffmpeg finalizes the MP4 (killing the
    /// process corrupts it — no moov atom). Returns true when ffmpeg exited
    /// cleanly and the file exists.
    /// </summary>
    public async Task<bool> StopAsync()
    {
        var proc = _proc;
        if (proc is null) return false;
        _clock.Stop();

        try
        {
            proc.StandardInput.Write('q');
            proc.StandardInput.Flush();
        }
        catch (Exception ex)
        {
            Log.Warn($"recording stop: stdin write failed ({ex.Message})");
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            await proc.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            Log.Error("recording stop: ffmpeg ignored 'q' for 15s; killing (file will be corrupt)");
            try { proc.Kill(); } catch { }
            return false;
        }

        var ok = proc.ExitCode == 0 && File.Exists(OutputPath) && new FileInfo(OutputPath).Length > 0;
        if (ok)
        {
            Log.Info($"recording finalized: {Elapsed.TotalSeconds:0.0}s, {new FileInfo(OutputPath).Length / 1024} KB, ffmpeg exit 0");
        }
        else
        {
            string tail;
            lock (_stderrTail) tail = string.Join(" | ", _stderrTail.TakeLast(6));
            Log.Error($"recording failed: ffmpeg exit {proc.ExitCode}; stderr tail: {tail}");
        }
        proc.Dispose();
        _proc = null;
        return ok;
    }

    /// <summary>Last-resort teardown for app exit mid-recording.</summary>
    public void Abort()
    {
        var proc = _proc;
        if (proc is null) return;
        try
        {
            proc.StandardInput.Write('q');
            proc.StandardInput.Flush();
            if (!proc.WaitForExit(3000)) proc.Kill();
        }
        catch { try { proc.Kill(); } catch { } }
        _proc = null;
    }

    // ---- post-steps (blocking; call from a background thread) ---------------

    /// <summary>Exact duration in ms via ffprobe, falling back to the wall clock.</summary>
    public static long ProbeDurationMs(string mp4, TimeSpan fallback)
    {
        var ffprobe = FindFfprobe();
        if (ffprobe is not null)
        {
            var (exit, stdout, _) = Run(ffprobe,
                ["-v", "error", "-show_entries", "format=duration", "-of", "default=nw=1:nk=1", mp4],
                10_000);
            if (exit == 0 && double.TryParse(stdout.Trim(), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var seconds))
                return (long)(seconds * 1000);
        }
        return (long)fallback.TotalMilliseconds;
    }

    /// <summary>Extracts a mid-clip frame as the card/archive thumbnail.</summary>
    public static bool ExtractThumbnail(string ffmpeg, string mp4, string pngOut, long durationMs)
    {
        var seek = Math.Max(0, durationMs / 2) / 1000.0;
        var (exit, _, err) = Run(ffmpeg,
            ["-y", "-ss", seek.ToString("0.###", CultureInfo.InvariantCulture),
             "-i", mp4, "-frames:v", "1", pngOut],
            30_000);
        if (exit != 0 || !File.Exists(pngOut))
        {
            Log.Warn($"thumbnail extraction failed (exit {exit}): {Tail(err)}");
            return false;
        }
        return true;
    }

    /// <summary>MP4 → GIF with palettegen/paletteuse for non-dithered-mush color.
    /// Scale is computed here (not an ffmpeg expression) to dodge filtergraph
    /// quoting entirely.</summary>
    public static bool MakeGif(string ffmpeg, string mp4, string gifOut,
        int sourceWidth, int gifFps, int gifMaxWidth)
    {
        var scale = sourceWidth > gifMaxWidth ? $"scale={gifMaxWidth}:-2:flags=lanczos," : "";
        var filter = $"fps={gifFps},{scale}split[a][b];[a]palettegen=stats_mode=diff[p];[b][p]paletteuse=dither=bayer:bayer_scale=4";

        var (exit, _, err) = Run(ffmpeg, ["-y", "-i", mp4, "-vf", filter, gifOut], 120_000);
        if (exit != 0 || !File.Exists(gifOut))
        {
            Log.Warn($"gif encode failed (exit {exit}): {Tail(err)}");
            return false;
        }
        return true;
    }

    private static (int Exit, string StdOut, string StdErr) Run(
        string exe, string[] args, int timeoutMs)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeoutMs))
        {
            try { p.Kill(); } catch { }
            return (-1, "", "timed out");
        }
        return (p.ExitCode, stdout.Result, stderr.Result);
    }

    private static string Tail(string s)
    {
        var lines = s.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join(" | ", lines.TakeLast(3));
    }
}
