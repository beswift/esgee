using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Esgee.Peers;

namespace Esgee;

/// <summary>One joined team share (docs/SHARES.md). A different noun from a
/// peer: per-member token, inbound only, explicit per-capture pushes. Entries
/// are written by the tray's "Join a team share…" flow, not by hand.</summary>
public sealed class ShareEntry
{
    /// <summary>The share's own name (from GET /share) — what the switcher,
    /// the card menu, and DefaultShare call it. Unique across entries.</summary>
    public string Name { get; set; } = "";

    /// <summary>Opaque base URL, routes appended (docs/PROTOCOL.md
    /// "Addressing") — e.g. http://100.64.0.9:43118.</summary>
    public string BaseUrl { get; set; } = "";

    /// <summary>THIS member's token, minted at join. Never the mesh PeerToken,
    /// never shared between members.</summary>
    public string MemberToken { get; set; } = "";

    /// <summary>The member id the share knows this person by (mem_…).</summary>
    public string MemberId { get; set; } = "";
}

public sealed class Settings
{
    /// <summary>Where PNGs and the index live.</summary>
    public string ArchiveRoot { get; set; } = DefaultArchiveRoot();

    /// <summary>Seconds a card sits on the shelf before it fades.</summary>
    public int LingerSeconds { get; set; } = 8;

    /// <summary>Cards visible at once before the oldest is pushed off.</summary>
    public int MaxCards { get; set; } = 6;

    /// <summary>Run OCR over each capture so the archive is full-text searchable.</summary>
    public bool OcrEnabled { get; set; } = true;

    /// <summary>Opens the region-select overlay. Ctrl+Shift+S and PrintScreen
    /// are also always bound to this. Win+Shift+C rather than the obvious
    /// Win+Shift+S: that chord belongs to the shell's own snip hotkey and
    /// usually can't be registered (those captures still reach the shelf via
    /// the clipboard watcher), and PrintScreen only comes alive after the next
    /// sign-out (see README).</summary>
    public string RegionHotkey { get; set; } = "Win+Shift+C";

    /// <summary>Captures the entire screen immediately — no overlay.
    /// (Win+Shift+P is often owned by vendor display/projector tools.)</summary>
    public string FullscreenHotkey { get; set; } = "Win+Shift+F";

    /// <summary>Re-captures the last selected rectangle immediately. Falls back
    /// to the region overlay if no region has been selected yet.</summary>
    public string LastRegionHotkey { get; set; } = "Win+Shift+L";

    /// <summary>Timed capture: countdown pill, then the region overlay opens on
    /// a freshly frozen frame — for hover states and menus you have to arm.
    /// (Win+Shift+T is the shell's text-extractor on newer builds; D is free.)</summary>
    public string TimerHotkey { get; set; } = "Win+Shift+D";

    /// <summary>Seconds on the timed-capture fuse.</summary>
    public int TimerSeconds { get; set; } = 3;

    /// <summary>Screen-coordinate rect [x,y,w,h] of the last committed
    /// selection, persisted so "repeat last region" survives restarts.</summary>
    public int[]? LastRegion { get; set; }

    /// <summary>Toggles screen recording: press to start, press again to stop.
    /// Records the last selected region if one exists, else the full screen.
    /// Win+Shift+G tends to be free of shell/vendor hotkey collisions.</summary>
    public string RecordHotkey { get; set; } = "Win+Shift+G";

    /// <summary>Capture framerate for MP4 recordings (clamped 5–60).</summary>
    public int RecordFps { get; set; } = 30;

    /// <summary>Recordings at or under this many seconds also get a GIF —
    /// the paste-anywhere artifact. Longer ones stay MP4-only. 0 disables GIFs.</summary>
    public int GifMaxSeconds { get; set; } = 15;

    /// <summary>GIF sampling framerate. 12 reads smoothly without ballooning size.</summary>
    public int GifFps { get; set; } = 12;

    /// <summary>GIFs wider than this are scaled down (aspect preserved).</summary>
    public int GifMaxWidth { get; set; } = 960;

    // ---- Peers (machine-to-machine over Tailscale) --------------------------
    // All OFF by default. With PeersEnabled=false esgee opens zero sockets and
    // behaves exactly as before these settings existed.

    /// <summary>Serve this machine's archive to other machines on the tailnet.
    /// The server binds ONLY to this machine's Tailscale IP — never a public or
    /// LAN interface — and every request must carry PeerToken.</summary>
    public bool PeersEnabled { get; set; } = false;

    /// <summary>TCP port for the peer API (on the Tailscale IP only).</summary>
    public int PeerPort { get; set; } = 43117;

    /// <summary>Shared secret for the peer API. Generated automatically the
    /// first time PeersEnabled is turned on; copy the SAME value into
    /// settings.json on every machine that should talk to this one.</summary>
    public string PeerToken { get; set; } = "";

    /// <summary>Manual peer list, as "name=host:port", "host:port", or a full
    /// URL ("name=http://…" / "https://…") — a fallback for when
    /// tailscale-status discovery can't see a machine.</summary>
    public string[] Peers { get; set; } = [];

    /// <summary>When set (a tailnet machine name, "host:port", or a full
    /// http(s) URL), every new capture/recording is pushed to that peer in
    /// the background. Requires PeerToken to match on both ends.
    /// Empty = no push sync. May name a PEER only — a value that resolves to
    /// a Shares entry is rejected at load (docs/SHARES.md "The invariant").</summary>
    public string SyncTargetPeer { get; set; } = "";

    // ---- Team shares (docs/SHARES.md) ---------------------------------------
    // A share is a different noun from a peer: it holds only what someone
    // deliberately pushed, and nothing here can make pushes automatic.

    /// <summary>Shares this person has joined. Managed from the tray
    /// ("Join a team share…" / remove); each entry carries its own member
    /// token.</summary>
    public ShareEntry[] Shares { get; set; } = [];

    /// <summary>Name of the last-used share: the one a bare click on the card's
    /// share icon targets, and the first entry in every share menu.</summary>
    public string DefaultShare { get; set; } = "";

    /// <summary>settings.json's location. ESGEE_SETTINGS overrides it — the
    /// same harness escape `--archive-root` gives the archive, so load/save
    /// behavior is testable against a scratch file instead of the real one.</summary>
    [JsonIgnore]
    public static string Path { get; } =
        Environment.GetEnvironmentVariable("ESGEE_SETTINGS") is { Length: > 0 } overridden
            ? overridden
            : System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "esgee", "settings.json");

    /// <summary>
    /// Deliberately NOT Pictures. Pictures is frequently OneDrive-redirected,
    /// and pushing thousands of PNGs a day through sync is its own outage. Sits at
    /// the top of the user profile so it's still trivial to find and search.
    /// </summary>
    private static string DefaultArchiveRoot()
        => System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "esgee");

    public static Settings Load()
    {
        if (!File.Exists(Path))
        {
            var fresh = new Settings();
            fresh.Save(); // so there's something to edit on first run
            return fresh;
        }

        try
        {
            var loaded = JsonSerializer.Deserialize<Settings>(File.ReadAllText(Path));
            if (loaded is not null)
            {
                loaded.EnforceInvariants();
                return loaded;
            }
            Log.Warn("settings unreadable (JSON null), running on defaults; file left for repair");
        }
        catch (Exception ex)
        {
            // Unreadable is NOT replaceable: the file may hold a PeerToken and
            // share MemberTokens recoverable only by re-pairing / a fresh
            // invite, and hand-editing it is a documented workflow. Run on
            // defaults but leave the file exactly as the user left it.
            Log.Warn($"settings unreadable, running on defaults; file left for repair: {ex.Message}");
        }
        return new Settings();
    }

    /// <summary>Structural rules the rest of the app may assume after Load —
    /// run on every load, and again by anything that edits Shares in-process.
    /// Public so a harness can drive the rules without touching the real
    /// settings file.</summary>
    public void EnforceInvariants()
    {
        // Hand-editing settings.json is a documented workflow, and JSON null
        // is a valid value for every one of these fields — each must degrade
        // to its default, never throw. A throw here lands in Load()'s catch,
        // which treats the file as unreadable.
        ArchiveRoot = string.IsNullOrWhiteSpace(ArchiveRoot) ? DefaultArchiveRoot() : ArchiveRoot;
        PeerToken ??= "";
        SyncTargetPeer ??= "";
        DefaultShare ??= "";
        Peers = Peers is null ? [] : Array.FindAll(Peers, p => p is not null);
        Shares = Shares is null ? [] : Array.FindAll(Shares, s => s is not null);

        // A share entry missing any credential piece can't do anything but
        // fail downstream affordances one by one; drop it at the edge.
        var usable = Array.FindAll(Shares, s =>
            !string.IsNullOrWhiteSpace(s.Name) &&
            !string.IsNullOrWhiteSpace(s.BaseUrl) &&
            !string.IsNullOrWhiteSpace(s.MemberToken));
        if (usable.Length != Shares.Length)
        {
            Log.Warn($"settings: dropped {Shares.Length - usable.Length} share " +
                     "entry(ies) missing Name, BaseUrl, or MemberToken");
            Shares = usable;
        }

        // Names key DefaultShare, last-used menu ordering, the switcher
        // label, and the share cache directory (share_<Name>) — "Unique
        // across entries" is load-bearing, not cosmetic. The tray join path
        // suffixes collisions as it saves; entries arriving any other way
        // (hand-edit, a settings file copied from another machine) get the
        // same suffix here so the first entry keeps the name.
        for (var i = 0; i < Shares.Length; i++)
        {
            var name = Shares[i].Name;
            for (var n = 2; Shares.Take(i).Any(s =>
                     s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)); n++)
                name = $"{Shares[i].Name} ({n})";
            if (!string.Equals(name, Shares[i].Name, StringComparison.Ordinal))
            {
                Log.Warn($"settings: share name '{Shares[i].Name}' duplicates an " +
                         $"earlier entry; renamed to '{name}'");
                Shares[i].Name = name;
            }
        }

        // THE invariant (docs/SHARES.md): a share receives a capture only
        // through an explicit per-capture act. SyncTargetPeer is the automatic
        // channel, so it must never resolve to a share — not by name, not by
        // endpoint. Hard rule, not a guideline: reject and treat as unset.
        if (SyncTargetPeer.Length > 0 &&
            Shares.FirstOrDefault(SyncTargetNamesShare) is { } collided)
        {
            Log.Warn($"settings: SyncTargetPeer '{SyncTargetPeer}' resolves to the share " +
                     $"'{collided.Name}' — captures never flow to a team automatically " +
                     "(docs/SHARES.md); treating SyncTargetPeer as unset");
            SyncTargetPeer = "";
        }

        // A DefaultShare naming a share that no longer exists just makes the
        // "last-used first" ordering lie.
        if (DefaultShare.Length > 0 &&
            !Shares.Any(s => s.Name.Equals(DefaultShare, StringComparison.OrdinalIgnoreCase)))
            DefaultShare = "";
    }

    /// <summary>True when SyncTargetPeer names this share, by name or by
    /// endpoint. Endpoint comparison expands both sides the same way manual
    /// Peers entries expand (no network, no tailscale lookup — a bare machine
    /// name can only ever be a peer).</summary>
    private bool SyncTargetNamesShare(ShareEntry share)
    {
        if (SyncTargetPeer.Trim().Equals(share.Name, StringComparison.OrdinalIgnoreCase))
            return true;

        var target = PeerClient.ToBaseUrl(SyncTargetPeer, PeerPort);
        var shareUrl = PeerClient.ToBaseUrl(share.BaseUrl, PeerPort);
        return target is not null && shareUrl is not null &&
               string.Equals(target, shareUrl, StringComparison.OrdinalIgnoreCase);
    }

    public void Save()
    {
        try
        {
            WriteLocked(() => WriteFile(this));
        }
        catch (Exception ex)
        {
            Log.Warn($"settings save failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Cross-process read-modify-write, for processes whose Settings snapshot
    /// may be stale — the standalone `esgee --archive` window above all. A
    /// whole-object Save() from such a process rolls the file back to its
    /// launch snapshot, erasing everything the resident app persisted since
    /// (a joined share's MemberToken exists nowhere else — the node keeps
    /// only hashes). This re-reads the CURRENT file under the write gate,
    /// applies one change, and writes that. An unreadable file skips the
    /// write: losing a cosmetic update beats destroying credentials.
    /// </summary>
    public static void TryUpdate(Action<Settings> change)
    {
        try
        {
            WriteLocked(() =>
            {
                if (!File.Exists(Path)) return;
                if (JsonSerializer.Deserialize<Settings>(File.ReadAllText(Path))
                    is not { } current) return;
                current.EnforceInvariants();
                change(current);
                WriteFile(current);
            });
        }
        catch (Exception ex)
        {
            Log.Warn($"settings update skipped: {ex.Message}");
        }
    }

    /// <summary>Serializes settings writers across processes (resident app,
    /// archive windows). A missed acquire still writes — a wedged gate must
    /// not make settings unsavable — the gate exists so two writers can't
    /// tear the file or interleave with TryUpdate's read.</summary>
    private static void WriteLocked(Action write)
    {
        using var gate = new Mutex(initiallyOwned: false, @"Local\esgee.settings");
        var owned = false;
        try { owned = gate.WaitOne(TimeSpan.FromSeconds(5)); }
        catch (AbandonedMutexException) { owned = true; } // prior holder died mid-write; proceed
        try { write(); }
        finally { if (owned) gate.ReleaseMutex(); }
    }

    /// <summary>Whole-file-or-nothing: write beside, then move over. A torn
    /// settings.json reads as "unreadable" and benches every token in it.</summary>
    private static void WriteFile(Settings settings)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var tmp = Path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings,
            new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, Path, overwrite: true);
    }
}
