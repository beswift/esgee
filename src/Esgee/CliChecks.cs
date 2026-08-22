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
        if (verb is not ("check-drag" or "check-peer" or "check-shelf")) return false;

        // WinExe has no console of its own; borrow the calling shell's.
        AttachConsole(AttachParentProcess);

        if (verb == "check-peer")
        {
            CheckPeer(settings, args.Length > 1 ? args[1] : null);
            return true;
        }

        if (verb == "check-shelf")
        {
            using var store = new ShotStore(settings.ArchiveRoot);
            CheckShelf(store);
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
            Environment.ExitCode = 1;
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
                Environment.ExitCode = 1;
                return;
            }

            var addr = target;
            if (addr is null)
            {
                addr = Task.Run(() => Peers.Tailscale.SelfIPv4()).GetAwaiter().GetResult();
                if (addr is null)
                {
                    Console.WriteLine("no tailscale IP found");
                    Environment.ExitCode = 1;
                    return;
                }
            }

            // Accepts everything Settings can name: a tailnet machine name,
            // host[:port], or a full URL (docs/PROTOCOL.md "Addressing").
            var baseUrl = Task.Run(() => Peers.PeerClient.ResolveTargetUrl(addr, settings.PeerPort))
                .GetAwaiter().GetResult();
            if (baseUrl is null)
            {
                Console.WriteLine($"'{addr}' is not on the tailnet (or is a malformed address)");
                Environment.ExitCode = 1;
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
            if (recent.Count == 0)
            {
                Console.WriteLine("peer archive is empty");
                Environment.ExitCode = 1;
                return;
            }

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
            var ok = dropped is not null && File.Exists(dropped);
            Console.WriteLine(ok
                ? "OK: drop target would receive a real local file"
                : "FAIL: CF_HDROP missing or file does not exist");
            if (!ok) Environment.ExitCode = 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"check-peer failed: {ex.Message}");
            Environment.ExitCode = 1;
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
            if (recent.Count == 0)
            {
                Console.WriteLine("FAIL: archive is empty");
                Environment.ExitCode = 1;
                return;
            }
            path = recent[0].Path;
        }

        var shot = store.Recent(500).FirstOrDefault(s =>
            string.Equals(s.Path, path, StringComparison.OrdinalIgnoreCase));
        if (shot is null)
        {
            Console.WriteLine($"FAIL: not in archive: {path}");
            Environment.ExitCode = 1;
            return;
        }

        var prepare = System.Diagnostics.Stopwatch.StartNew();
        var transfer = Interop.DragSource.Prepare(shot);
        prepare.Stop();
        var data = Interop.DragSource.BuildDataObject(transfer);
        Console.WriteLine($"source: {shot.Path}");
        Console.WriteLine($"formats offered: {string.Join(", ", data.GetFormats())}");

        var commit = System.Diagnostics.Stopwatch.StartNew();
        System.Windows.Clipboard.SetDataObject(data, copy: true);
        commit.Stop();
        Console.WriteLine($"timing   : prepare {prepare.ElapsedMilliseconds} ms, " +
                          $"commit {commit.ElapsedMilliseconds} ms");

        // Read back through the OS rather than the in-process object.
        var back = System.Windows.Clipboard.GetDataObject();
        if (back is null)
        {
            Console.WriteLine("FAIL: clipboard returned nothing");
            Environment.ExitCode = 1;
            return;
        }

        var formats = back.GetFormats(autoConvert: false);
        var files = back.GetData(System.Windows.DataFormats.FileDrop, autoConvert: false) as string[];
        Console.WriteLine($"CF_HDROP : {(files is { Length: > 0 } ? files[0] : "MISSING")}");

        var marker = back.GetDataPresent(Interop.DragSource.ClipboardMarker, autoConvert: false);
        Console.WriteLine($"Marker   : {(marker ? "present" : "MISSING")}");

        var png = formats.Contains("PNG", StringComparer.Ordinal)
            ? back.GetData("PNG", autoConvert: false) as System.IO.Stream
            : null;
        Console.WriteLine($"PNG      : {(png is not null ? $"{png.Length} bytes" : "MISSING")}");

        var pngExact = shot.IsVideo;
        if (png is not null && transfer.Png is not null)
        {
            if (png.CanSeek) png.Position = 0;
            var expected = System.Security.Cryptography.SHA256.HashData(transfer.Png);
            var actual = System.Security.Cryptography.SHA256.HashData(png);
            pngExact = expected.AsSpan().SequenceEqual(actual);
            Console.WriteLine($"PNG exact: {(pngExact ? "yes" : "NO")}");
        }

        var bitmap = back.GetDataPresent(System.Windows.DataFormats.Bitmap, autoConvert: false);
        Console.WriteLine($"Bitmap   : {(bitmap ? "present" : "MISSING")}");

        var dropOk = files is { Length: 1 } && File.Exists(files[0]) &&
                     string.Equals(Path.GetFullPath(files[0]), Path.GetFullPath(transfer.DropPath),
                         StringComparison.OrdinalIgnoreCase);
        var formatsOk = shot.IsVideo
            ? png is null && !bitmap
            : png is not null && pngExact && bitmap;
        if (!marker || !dropOk || !formatsOk)
        {
            Console.WriteLine("FAIL: clipboard format contract did not round-trip");
            Environment.ExitCode = 1;
        }
        else
        {
            Console.WriteLine("OK: clipboard format contract round-tripped");
        }
    }

    /// <summary>Regression check for the old capacity spin: push several cards
    /// synchronously, before WPF can complete any leave animation.</summary>
    private static void CheckShelf(ShotStore store)
    {
        var shot = store.Recent(1).FirstOrDefault();
        if (shot is null)
        {
            Console.WriteLine("FAIL: archive is empty");
            Environment.ExitCode = 1;
            return;
        }

        var clipboard = new Interop.ClipboardService(
            System.Windows.Application.Current.Dispatcher);
        var shelf = new Ui.ShelfWindow(clipboard)
        {
            MaxCards = 3,
            Linger = TimeSpan.FromHours(1),
        };

        try
        {
            for (var i = 0; i < 7; i++) shelf.Push(shot);
            var burstActive = shelf.ActiveCardCount;
            var burstTotal = shelf.CardCount;
            var burstOk = burstActive == 3 && burstTotal == 7;

            PumpDispatcher(TimeSpan.FromMilliseconds(300));
            var settledOk = shelf.ActiveCardCount == 3 && shelf.CardCount == 3;

            shelf.ClearAll();
            shelf.Push(shot);
            var clearRaceActive = shelf.ActiveCardCount;
            PumpDispatcher(TimeSpan.FromMilliseconds(300));
            var clearRaceOk = clearRaceActive == 1 &&
                              shelf.ActiveCardCount == 1 && shelf.CardCount == 1;

            // A hand-edited invalid setting must still behave as capacity one.
            shelf.MaxCards = 0;
            shelf.Push(shot);
            var zeroCapacityActive = shelf.ActiveCardCount;
            PumpDispatcher(TimeSpan.FromMilliseconds(300));
            var zeroCapacityOk = zeroCapacityActive == 1 &&
                                 shelf.ActiveCardCount == 1 && shelf.CardCount == 1;

            var ok = burstOk && settledOk && clearRaceOk && zeroCapacityOk;
            var result = $"check-shelf: {(ok ? "OK" : "FAIL")} " +
                         $"burst-active={burstActive} burst-total={burstTotal} " +
                         $"settled={settledOk} clear-race={clearRaceOk} " +
                         $"zero-capacity={zeroCapacityOk}";
            Console.WriteLine(result);
            Log.Info(result);
            if (!ok) Environment.ExitCode = 1;
        }
        finally
        {
            shelf.AllowClose = true;
            shelf.Close();
        }
    }

    private static void PumpDispatcher(TimeSpan duration)
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = duration,
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }
}
