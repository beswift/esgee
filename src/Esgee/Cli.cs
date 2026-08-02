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
        if (verb is not ("search" or "recent" or "check-drag")) return false;

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
