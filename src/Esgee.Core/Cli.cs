using System.IO;
using System.Runtime.InteropServices;
using Esgee.Store;

namespace Esgee;

/// <summary>
/// Headless queries against the archive. Beyond being the easiest way to verify
/// the OCR index, it means an agent can find a past screenshot by its contents:
///   esgee --search "connection refused"
/// The WPF-only diagnostic verbs (--check-drag, --check-peer) live in the app's
/// CliChecks — everything here runs identically in the app and the node.
/// </summary>
public static partial class Cli
{
    private const int AttachParentProcess = -1;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachConsole(int processId);

    /// <summary>Returns true if the process handled a command and should exit.</summary>
    public static bool TryRun(string[] args, Settings settings)
    {
        if (args.Length == 0) return false;

        var verb = args[0].TrimStart('-').ToLowerInvariant();
        if (verb is not ("search" or "recent" or "doctor")) return false;

        // A WinExe host has no console of its own; borrow the calling shell's.
        // (Harmless no-op for the node — a real console app already has one.)
        if (OperatingSystem.IsWindows()) AttachConsole(AttachParentProcess);

        try
        {
            using var store = new ShotStore(settings.ArchiveRoot);

            if (verb == "doctor")
            {
                Doctor(store, settings);
                return true;
            }

            // FtsQuery, not raw text: PROTOCOL.md promises the same quoting
            // rules everywhere, and raw FTS5 turns "text:" into a syntax error.
            var shots = verb == "search"
                ? store.Search(ShotStore.FtsQuery(string.Join(' ', args.Skip(1))))
                : store.Recent(Math.Max(1, args.Length > 1 && int.TryParse(args[1], out var n) ? n : 20));

            if (shots.Count == 0)
            {
                Console.WriteLine("no matches");
                return true;
            }

            foreach (var shot in shots)
                Console.WriteLine($"{shot.TakenAt:yyyy-MM-dd HH:mm:ss}  {shot.Width}x{shot.Height}  {shot.Path}");

            Console.WriteLine($"\n{shots.Count} result(s)");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"esgee: {ex.Message}");
        }

        return true;
    }

    /// <summary>Only the desktop recorder ever invokes ffmpeg, but doctor output
    /// is a machine report people paste into bug reports — so check where the
    /// Windows app installs it AND the PATH, instead of probing a Windows-only
    /// location that reads "missing" on every Linux node regardless of
    /// /usr/bin/ffmpeg.</summary>
    private static bool FfmpegPresent()
    {
        if (OperatingSystem.IsWindows() && File.Exists(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "esgee", "bin", "ffmpeg.exe")))
            return true;

        var exe = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        return (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(dir => File.Exists(Path.Combine(dir.Trim(), exe)));
    }

    /// <summary>
    /// Local health report — the "telemetry" that never phones home. Everything
    /// here comes from the machine it runs on: archive stats, duplicate-content
    /// groups (the double-shot signature), and a digest of the local log. Users
    /// can paste the output into a bug report; nothing is ever transmitted.
    /// </summary>
    private static void Doctor(ShotStore store, Settings settings)
    {
        Console.WriteLine($"esgee v{AppVersion.Current}");
        Console.WriteLine($"archive : {store.Root}");
        Console.WriteLine($"settings: {Settings.Path}");
        Console.WriteLine();

        var (total, videos, pending, dups) = store.Doctor();
        Console.WriteLine($"captures     : {total} ({videos} recordings)");
        Console.WriteLine($"ocr backlog  : {pending}");
        Console.WriteLine($"ffmpeg       : {(FfmpegPresent() ? "present" : "missing")}");
        Console.WriteLine();

        Console.WriteLine(dups.Count == 0
            ? "duplicate-content groups: none"
            : $"duplicate-content groups ({dups.Count} shown, newest first):");
        foreach (var d in dups) Console.WriteLine($"  {d}");
        Console.WriteLine();

        var logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "esgee", "esgee.log");
        if (!File.Exists(logPath))
        {
            Console.WriteLine("log: none");
            return;
        }

        var lines = File.ReadAllLines(logPath);
        var errors = lines.Where(l => l.Contains(" ERR ")).ToList();
        var warns = lines.Where(l => l.Contains(" WARN ")).ToList();
        Console.WriteLine($"log: {lines.Length} lines, {errors.Count} errors, {warns.Count} warnings");
        foreach (var e in errors.TakeLast(5)) Console.WriteLine($"  {e}");
        foreach (var w in warns.TakeLast(5)) Console.WriteLine($"  {w}");
    }
}

