using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;

namespace Esgee.Capture;

/// <summary>
/// First-run acquisition of ffmpeg/ffprobe. Recording needs a real encoder and
/// bundling ~100 MB of ffmpeg into every esgee update would be absurd, so on a
/// machine without it we download one pinned, hash-verified static build from
/// gyan.dev (the same builds winget's Gyan.FFmpeg ships) into
/// %LOCALAPPDATA%\esgee\bin — outside the app install dir, so it survives
/// updates and is fetched exactly once per machine.
/// </summary>
internal static class FfmpegSetup
{
    // Pinned release, not "latest": the URL is immutable and the hash below was
    // verified against gyan.dev's published .sha256 at pin time. Bump both
    // together, deliberately.
    private const string Url =
        "https://www.gyan.dev/ffmpeg/builds/packages/ffmpeg-8.1.2-essentials_build.zip";
    private const string Sha256 =
        "db580001caa24ac104c8cb856cd113a87b0a443f7bdf47d8c12b1d740584a2ec";
    public const string SizeHint = "about 105 MB";

    private static int _inFlight;

    public static bool InProgress => Volatile.Read(ref _inFlight) == 1;

    /// <summary>Download, verify, and install ffmpeg.exe + ffprobe.exe. Safe to
    /// call from any thread; returns false on any failure (offline, hash
    /// mismatch, disk) — recording simply stays unavailable and the log says why.</summary>
    public static async Task<bool> TryInstallAsync()
    {
        if (Interlocked.Exchange(ref _inFlight, 1) == 1)
        {
            Log.Info("ffmpeg download already in progress");
            return false;
        }

        var zip = Path.Combine(Path.GetTempPath(), "esgee-ffmpeg-download.zip");
        try
        {
            Log.Info($"ffmpeg download starting: {Url}");
            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) })
            await using (var response = await http.GetStreamAsync(Url))
            await using (var file = File.Create(zip))
                await response.CopyToAsync(file);

            string actual;
            await using (var stream = File.OpenRead(zip))
                actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream));
            if (actual != Sha256)
            {
                Log.Error($"ffmpeg download REJECTED: sha256 {actual} != pinned {Sha256}");
                return false;
            }

            Directory.CreateDirectory(ScreenRecorder.BinDir);
            using var archive = ZipFile.OpenRead(zip);
            foreach (var name in new[] { "ffmpeg.exe", "ffprobe.exe" })
            {
                var entry = archive.Entries.FirstOrDefault(e =>
                        e.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidDataException($"{name} not found in archive");
                entry.ExtractToFile(Path.Combine(ScreenRecorder.BinDir, name), overwrite: true);
            }

            Log.Info($"ffmpeg installed to {ScreenRecorder.BinDir} (pinned 8.1.2 essentials, sha256 ok)");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"ffmpeg download failed (offline?): {ex.Message}");
            return false;
        }
        finally
        {
            try { File.Delete(zip); } catch { /* temp file; best effort */ }
            Volatile.Write(ref _inFlight, 0);
        }
    }
}
