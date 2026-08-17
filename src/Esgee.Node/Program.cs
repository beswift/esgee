using System.Runtime.InteropServices;
using Esgee.Peers;
using Esgee.Store;

namespace Esgee.Node;

/// <summary>
/// esgee-node: the peer and share APIs with no desktop attached. --serve is
/// the same PeerServer the WPF app runs; --serve-share is the team share node
/// (docs/SHARES.md) the WPF app deliberately never hosts. Either way the
/// archive served is its own — never commingled with a personal one. Serve
/// modes hold the process open until SIGTERM/SIGINT so a systemd unit stops
/// them cleanly; the query verbs (--recent, --search, --doctor) pass straight
/// through to Core's Cli.
/// </summary>
internal static class Program
{
    /// <summary>Shares get their own default port so a box can host a peer
    /// archive and a share side by side (docs/SHARES.md uses 43118 throughout).</summary>
    private const int ShareDefaultPort = 43118;

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

        // "serve-share" before "serve": the flags are distinct strings, but the
        // longer one must never be reachable only when the shorter is absent.
        if (HasFlag(list, "serve-share"))
            return ServeShare(list, settings, explicitRoot: rootOverride is not null);
        if (HasFlag(list, "share-invite"))
            return ShareInvite(list, settings, explicitRoot: rootOverride is not null);
        if (HasFlag(list, "serve"))
            return Serve(list, settings, explicitRoot: rootOverride is not null);

        Console.Error.WriteLine("""
            esgee-node — headless esgee peer & share node (docs/PROTOCOL.md, docs/SHARES.md)

            usage:
              esgee-node --serve --archive-root <dir> --port <p> --token <t> [--bind <ip>]
                     (--token-file <path> reads the token from a file instead,
                      keeping it out of process listings and unit files)
              esgee-node --serve-share --archive-root <dir> --share-name <name>
                     (--token <t> | --token-file <path>)
                     [--port 43118] [--bind <ip>] [--retention <days>]
              esgee-node --share-invite --archive-root <dir> [--hint <name>] [--port 43118]
              esgee-node --recent [n]        [--archive-root <dir>]
              esgee-node --search <words...> [--archive-root <dir>]
              esgee-node --doctor            [--archive-root <dir>]

            --serve binds the machine's Tailscale IPv4 unless --bind names a
            specific interface address (127.0.0.1 is fine for local testing;
            0.0.0.0 is refused). A token is required — the server will not
            start without one.

            --serve-share serves ONE team share from its own archive root. The
            token is the OPERATOR's bootstrap credential (rotating the file
            rotates it at next start); members never see it — they join with
            single-use invites and each get their own token. --retention <days>
            tombstones and deletes items older than that (default: unlimited).

            --share-invite mints a single-use invite (expires 24h) and prints
            the code plus its esgee-share://<host>:<port>#<code> URL. The host
            is this node's tailnet IP at mint time — rewrite it if members
            reach the node at a different address.
            """);
        return 2;
    }

    private static int Serve(List<string> args, Settings settings, bool explicitRoot)
    {
        var port = int.TryParse(TakeOption(args, "port"), out var p) ? p : settings.PeerPort;
        var token = TakeOption(args, "token") ?? settings.PeerToken;
        var bind = TakeOption(args, "bind");

        if (ReadTokenFile(args, ref token) is { } tokenFileError)
        {
            Console.Error.WriteLine(tokenFileError);
            return 1;
        }

        if (RejectLeftovers(args, "serve") is { } leftoverError)
        {
            Console.Error.WriteLine(leftoverError);
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
        return WaitForShutdown();
    }

    private static int ServeShare(List<string> args, Settings settings, bool explicitRoot)
    {
        var port = int.TryParse(TakeOption(args, "port"), out var p) ? p : ShareDefaultPort;
        var name = TakeOption(args, "share-name");
        var bind = TakeOption(args, "bind");
        var retentionRaw = TakeOption(args, "retention");
        var token = TakeOption(args, "token");

        if (ReadTokenFile(args, ref token) is { } tokenFileError)
        {
            Console.Error.WriteLine(tokenFileError);
            return 1;
        }

        if (RejectLeftovers(args, "serve-share") is { } leftoverError)
        {
            Console.Error.WriteLine(leftoverError);
            return 2;
        }

        if (!explicitRoot)
        {
            Console.Error.WriteLine(
                "esgee-node: --serve-share requires --archive-root <dir> (a share archive is never a personal one)");
            return 2;
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            Console.Error.WriteLine("esgee-node: --serve-share requires --share-name <name>");
            return 2;
        }

        // No settings fallback here on purpose: PeerToken is the mesh secret,
        // and reusing it as the operator credential is exactly the collapse
        // of peer and share the design forbids.
        if (string.IsNullOrWhiteSpace(token))
        {
            Console.Error.WriteLine(
                "esgee-node: --serve-share requires the operator token (--token <t> or --token-file <path>)");
            return 1;
        }

        // "90" or "90d"; 0/absent = unlimited.
        var retentionDays = 0;
        if (retentionRaw is not null &&
            (!int.TryParse(retentionRaw.TrimEnd('d', 'D'), out retentionDays) || retentionDays < 0))
        {
            Console.Error.WriteLine($"esgee-node: bad --retention '{retentionRaw}' (want days, e.g. 90)");
            return 2;
        }

        using var store = new ShareStore(settings.ArchiveRoot);
        var operatorId = store.EnsureOperator(token.Trim());
        using var server = ShareServer.TryStart(store, name, retentionDays, port,
            new ImageSharpThumbEncoder(), bind);
        if (server is null)
        {
            Console.Error.WriteLine("esgee-node: share server failed to start — esgee.log has the reason");
            return 1;
        }

        Console.WriteLine($"esgee-node v{Esgee.AppVersion.Current}: " +
                          $"serving share \"{name}\" ({store.ShareId}) from {store.Shots.Root} " +
                          $"on http://{server.BoundAddress}, operator {operatorId}, " +
                          (retentionDays > 0 ? $"retention {retentionDays}d" : "retention unlimited"));
        return WaitForShutdown();
    }

    private static int ShareInvite(List<string> args, Settings settings, bool explicitRoot)
    {
        var hint = TakeOption(args, "hint");
        var port = int.TryParse(TakeOption(args, "port"), out var p) ? p : ShareDefaultPort;

        if (RejectLeftovers(args, "share-invite") is { } leftoverError)
        {
            Console.Error.WriteLine(leftoverError);
            return 2;
        }

        if (!explicitRoot)
        {
            Console.Error.WriteLine("esgee-node: --share-invite requires --archive-root <dir> (the share's root)");
            return 2;
        }

        using var store = new ShareStore(settings.ArchiveRoot);
        var code = store.MintInvite(hint);

        // The URL's host is a best guess at mint time. stdout stays two clean
        // lines (code, then URL) so operators can pipe either one.
        var host = Tailscale.SelfIPv4() ?? "<tailnet-ip>";
        Console.WriteLine(code);
        Console.WriteLine($"esgee-share://{host}:{port}#{code}");
        Console.Error.WriteLine(
            $"single-use, expires in {(int)ShareStore.InviteLifetime.TotalHours}h" +
            (hint is null ? "" : $", hint '{hint.Trim()}'") +
            "; rewrite the host if members reach this node at a different address");
        return 0;
    }

    /// <summary>--token-file wins over --token when both are given — the
    /// systemd-friendly way to keep the secret out of `ps` and unit files.
    /// Returns an error message, or null on success.</summary>
    private static string? ReadTokenFile(List<string> args, ref string? token)
    {
        if (TakeOption(args, "token-file") is not { } tokenFile) return null;
        try
        {
            token = File.ReadAllText(tokenFile).Trim();
            return null;
        }
        catch (Exception ex)
        {
            return $"esgee-node: cannot read --token-file: {ex.Message}";
        }
    }

    /// <summary>Every recognized option has been consumed; anything left
    /// besides the verb flag itself is a typo (--archive_root, --prot) that
    /// would otherwise silently change which archive or port a node serves.</summary>
    private static string? RejectLeftovers(List<string> args, string verb)
    {
        var leftovers = args.Where(a =>
            !a.TrimStart('-').Equals(verb, StringComparison.OrdinalIgnoreCase)).ToList();
        return leftovers.Count == 0 ? null
            : $"esgee-node: unrecognized {verb} argument(s): {string.Join(' ', leftovers)}";
    }

    /// <summary>systemd stops units with SIGTERM; a terminal sends SIGINT. Both
    /// land on the same graceful path: dispose the servers (via using), exit 0.</summary>
    private static int WaitForShutdown()
    {
        using var done = new ManualResetEventSlim();
        using var onTerm = PosixSignalRegistration.Create(PosixSignal.SIGTERM,
            ctx => { ctx.Cancel = true; done.Set(); });
        using var onInt = PosixSignalRegistration.Create(PosixSignal.SIGINT,
            ctx => { ctx.Cancel = true; done.Set(); });
        done.Wait();

        Console.WriteLine("esgee-node: stopping");
        return 0;
    }

    private static bool HasFlag(List<string> args, string flag)
        => args.Any(a => a.TrimStart('-').Equals(flag, StringComparison.OrdinalIgnoreCase));

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
