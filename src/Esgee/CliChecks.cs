using System.IO;
using System.Runtime.InteropServices;
using Esgee.Store;

namespace Esgee;

/// <summary>
/// The Windows-only diagnostic verbs: --check-drag and --check-peer both prove
/// their point by round-tripping a payload through the OS clipboard, which
/// needs WPF — so they live with the app while the portable verbs (search,
/// recent, doctor) live in Esgee.Core's Cli.
/// </summary>
internal static partial class CliChecks
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
        if (verb is not ("check-drag" or "check-peer")) return false;

        // WinExe has no console of its own; borrow the calling shell's.
        AttachConsole(AttachParentProcess);

        if (verb == "check-peer")
        {
            CheckPeer(settings, args.Length > 1 ? args[1] : null);
            return true;
        }

        try
        {
            using var store = new ShotStore(settings.ArchiveRoot);
            CheckDrag(store, args.Length > 1 ? args[1] : null);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"esgee: {ex.Message}");
        }

        return true;
    }

    /// <summary>
    /// Diagnostic for the peer layer:
    /// `esgee --check-peer [host[:port] | name | url]`.
    /// Exercises the EXACT components a remote archive-tile drag uses — /ping,
    /// /recent, EnsureLocalAsync's cache download, DragSource.BuildDataObject —
    /// then round-trips the payload through the OS clipboard like --check-drag.
    /// If CF_HDROP comes back naming a file that exists in the peer cache, a
    /// drag of that remote tile hands a drop target a real local file.
    /// </summary>
    private static void CheckPeer(Settings settings, string? target)
    {
        try
        {
            if (string.IsNullOrEmpty(settings.PeerToken))
            {
                Console.WriteLine("no PeerToken in settings — enable peers first");
                return;
            }

            var addr = target;
            if (addr is null)
            {
                addr = Task.Run(() => Peers.Tailscale.SelfIPv4()).GetAwaiter().GetResult();
                if (addr is null) { Console.WriteLine("no tailscale IP found"); return; }
            }

            // Accepts everything Settings can name: a tailnet machine name,
            // host[:port], or a full URL (docs/PROTOCOL.md "Addressing").
            var baseUrl = Task.Run(() => Peers.PeerClient.ResolveTargetUrl(addr, settings.PeerPort))
                .GetAwaiter().GetResult();
            if (baseUrl is null)
            {
                Console.WriteLine($"'{addr}' is not on the tailnet (or is a malformed address)");
                return;
            }

            using var client = new Peers.PeerClient(
                new Peers.PeerInfo(target ?? "self", baseUrl), settings.PeerToken);

            var ping = Task.Run(() => client.PingAsync(TimeSpan.FromSeconds(5)))
                .GetAwaiter().GetResult();
            Console.WriteLine($"peer   : {ping!.Machine} v{ping.Version} " +
                              $"(proto {ping.Proto}, caps [{string.Join(' ', ping.EffectiveCapabilities)}], " +
                              $"{ping.Captures} captures) at {baseUrl}");

            var recent = Task.Run(() => client.RecentAsync(1)).GetAwaiter().GetResult();
            if (recent.Count == 0) { Console.WriteLine("peer archive is empty"); return; }

            var dto = recent[0];
            var local = Task.Run(() => client.EnsureLocalAsync(dto)).GetAwaiter().GetResult();
            Console.WriteLine($"cached : {local.Path} " +
                              $"({new System.IO.FileInfo(local.Path).Length / 1024} KB)");

            var data = Interop.DragSource.BuildDataObject(local);
            System.Windows.Clipboard.SetDataObject(data, copy: true);
            var back = System.Windows.Clipboard.GetDataObject();
            var files = back?.GetData(System.Windows.DataFormats.FileDrop) as string[];
            var dropped = files is { Length: > 0 } ? files[0] : null;
            Console.WriteLine($"CF_HDROP : {dropped ?? "MISSING"}");
            Console.WriteLine(dropped is not null && File.Exists(dropped)
                ? "OK: drop target would receive a real local file"
                : "FAIL: CF_HDROP missing or file does not exist");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"check-peer failed: {ex.Message}");
        }
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
