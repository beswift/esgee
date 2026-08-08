using System.IO;
using System.Runtime.InteropServices;
using Esgee.Store;

namespace Esgee;

/// <summary>
/// Headless queries against the archive. Beyond being the easiest way to verify
/// the OCR index, it means an agent can find a past screenshot by its contents:
///   esgee --search "connection refused"
/// </summary>
internal static partial class Cli
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
        if (verb is not ("search" or "recent" or "check-drag" or "doctor")) return false;

        // WinExe has no console of its own; borrow the calling shell's.
        AttachConsole(AttachParentProcess);

        try
        {
            using var store = new ShotStore(settings.ArchiveRoot);

            if (verb == "check-drag")
            {
                CheckDrag(store, args.Length > 1 ? args[1] : null);
                return true;
            }

            if (verb == "doctor")
            {
                Doctor(store, settings);
                return true;
            }

            var shots = verb == "search"
                ? store.Search(string.Join(' ', args.Skip(1)))
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

    /// <summary>
    /// Local health report — the "telemetry" that never phones home. Everything
    /// here comes from the machine it runs on: archive stats, duplicate-content
    /// groups (the double-shot signature), and a digest of the local log. Users
    /// can paste the output into a bug report; nothing is ever transmitted.
    /// </summary>
    private static void Doctor(ShotStore store, Settings settings)
    {
        Console.WriteLine($"esgee v{UpdateService.CurrentVersion}");
        Console.WriteLine($"archive : {store.Root}");
        Console.WriteLine($"settings: {Settings.Path}");
        Console.WriteLine();

        var (total, videos, pending, dups) = store.Doctor();
        Console.WriteLine($"captures     : {total} ({videos} recordings)");
        Console.WriteLine($"ocr backlog  : {pending}");
        Console.WriteLine($"ffmpeg       : {(File.Exists(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "esgee", "bin", "ffmpeg.exe")) ? "present" : "missing")}");
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

    /// <summary>
    /// Diagnostic for the drag payload. Drag-and-drop and the clipboard share the
    /// same IDataObject marshalling, so pushing the object the shelf would hand to
    /// DoDragDrop onto the clipboard and reading it back exercises the real path
    /// without needing a human to hold the mouse button down.
    /// </summary>
    private static void CheckDrag(ShotStore store, string? path)
    {
        if (path is null)
        {
            var recent = store.Recent(1);
            if (recent.Count == 0) { Console.WriteLine("archive is empty"); return; }
            path = recent[0].Path;
        }

        var shot = store.Recent(500).FirstOrDefault(s =>
            string.Equals(s.Path, path, StringComparison.OrdinalIgnoreCase));
        if (shot is null) { Console.WriteLine($"not in archive: {path}"); return; }

        var data = Interop.DragSource.BuildDataObject(shot);
        Console.WriteLine($"source: {shot.Path}");
        Console.WriteLine($"formats offered: {string.Join(", ", data.GetFormats())}");

        System.Windows.Clipboard.SetDataObject(data, copy: true);

        // Read back through the OS rather than the in-process object.
        var back = System.Windows.Clipboard.GetDataObject();
        if (back is null) { Console.WriteLine("FAIL: clipboard returned nothing"); return; }

        var files = back.GetData(System.Windows.DataFormats.FileDrop) as string[];
        Console.WriteLine($"CF_HDROP : {(files is { Length: > 0 } ? files[0] : "MISSING")}");

        var png = back.GetData("PNG") as System.IO.Stream;
        Console.WriteLine($"PNG      : {(png is not null ? $"{png.Length} bytes" : "MISSING")}");

        Console.WriteLine($"Bitmap   : {(back.GetDataPresent(System.Windows.DataFormats.Bitmap) ? "present" : "MISSING")}");
    }
}
