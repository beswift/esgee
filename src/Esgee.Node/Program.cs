using System.Runtime.InteropServices;
using Esgee.Peers;
using Esgee.Store;

namespace Esgee.Node;

/// <summary>
/// esgee-node: the peer API with no desktop attached — the same PeerServer the
/// WPF app runs, on an archive that is its own (never commingled with a
/// personal one; see docs/SHARES.md). Serve mode holds the process open until
/// SIGTERM/SIGINT so a systemd unit stops it cleanly; the query verbs
/// (--recent, --search, --doctor) pass straight through to Core's Cli.
/// </summary>
internal static class Program
{
    public static int Main(string[] args)
    {
        var list = args.ToList();
        var settings = Settings.Load();

        // Same hidden override the app honors: point ANY verb at a different
        // archive than settings.json names.
        var rootOverride = TakeOption(list, "archive-root");
        if (rootOverride is not null)
            settings.ArchiveRoot = rootOverride;

        // Query verbs before the serve check — the same order App.xaml.cs uses,
        // so `--search vite serve` stays a search instead of silently becoming
        // a server that blocks forever on a search-shaped command line.
        if (Cli.TryRun([.. list], settings)) return 0;

        if (list.Any(a => a.TrimStart('-').Equals("serve", StringComparison.OrdinalIgnoreCase)))
            return Serve(list, settings, explicitRoot: rootOverride is not null);

        Console.Error.WriteLine("""
            esgee-node — headless esgee peer (docs/PROTOCOL.md)

            usage:
              esgee-node --serve --archive-root <dir> --port <p> --token <t> [--bind <ip>]
              esgee-node --recent [n]        [--archive-root <dir>]
              esgee-node --search <words...> [--archive-root <dir>]
              esgee-node --doctor            [--archive-root <dir>]

            --serve binds the machine's Tailscale IPv4 unless --bind names a
            specific interface address (127.0.0.1 is fine for local testing;
            0.0.0.0 is refused). A token is required — the server will not
            start without one.
            """);
        return 2;
    }

    private static int Serve(List<string> args, Settings settings, bool explicitRoot)
    {
        var port = int.TryParse(TakeOption(args, "port"), out var p) ? p : settings.PeerPort;
        var token = TakeOption(args, "token") ?? settings.PeerToken;
        var bind = TakeOption(args, "bind");

        // Every recognized option has been consumed; anything left besides the
        // serve flag itself is a typo (--archive_root, --prot) that would
        // otherwise silently change which archive or port this node serves.
        var leftovers = args.Where(a =>
            !a.TrimStart('-').Equals("serve", StringComparison.OrdinalIgnoreCase)).ToList();
        if (leftovers.Count > 0)
        {
            Console.Error.WriteLine(
                $"esgee-node: unrecognized serve argument(s): {string.Join(' ', leftovers)}");
            return 2;
        }

        // A node archive is its own, never a personal one — so serving the
        // settings/default root by omission is exactly the commingling this
        // binary exists to prevent. The operator must name the directory.
        if (!explicitRoot)
        {
            Console.Error.WriteLine(
                "esgee-node: --serve requires --archive-root <dir> (the node archive is never a personal one)");
            return 2;
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            Console.Error.WriteLine(
                "esgee-node: refusing to serve without a token (--token <t>, or PeerToken in settings.json)");
            return 1;
        }

        using var store = new ShotStore(settings.ArchiveRoot);
        using var server = PeerServer.TryStart(store, token, port, new ImageSharpThumbEncoder(), bind);
        if (server is null)
        {
            Console.Error.WriteLine("esgee-node: server failed to start — esgee.log has the reason");
            return 1;
        }

        Console.WriteLine($"esgee-node v{Esgee.AppVersion.Current}: " +
                          $"serving {store.Root} on http://{server.BoundAddress}");

        // systemd stops units with SIGTERM; a terminal sends SIGINT. Both land
        // on the same graceful path: dispose the listener (via using), exit 0.
        using var done = new ManualResetEventSlim();
        using var onTerm = PosixSignalRegistration.Create(PosixSignal.SIGTERM,
            ctx => { ctx.Cancel = true; done.Set(); });
        using var onInt = PosixSignalRegistration.Create(PosixSignal.SIGINT,
            ctx => { ctx.Cancel = true; done.Set(); });
        done.Wait();

        Console.WriteLine("esgee-node: stopping");
        return 0;
    }

    /// <summary>Removes "--name value" from the list and returns the value —
    /// same shape App.xaml.cs uses, so flags mean the same thing everywhere.</summary>
    private static string? TakeOption(List<string> args, string name)
    {
        var idx = args.FindIndex(a => a.TrimStart('-').Equals(name, StringComparison.OrdinalIgnoreCase));
        if (idx < 0 || idx + 1 >= args.Count) return null;
        var value = args[idx + 1];
        args.RemoveRange(idx, 2);
        return value;
    }
}
