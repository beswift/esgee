using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Esgee;

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
    /// Empty = no push sync.</summary>
    public string SyncTargetPeer { get; set; } = "";

    [JsonIgnore]
    public static string Path { get; } = System.IO.Path.Combine(
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
        try
        {
            if (File.Exists(Path))
            {
                var loaded = JsonSerializer.Deserialize<Settings>(File.ReadAllText(Path));
                if (loaded is not null) return loaded;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"settings unreadable, using defaults: {ex.Message}");
        }

        var fresh = new Settings();
        fresh.Save(); // so there's something to edit on first run
        return fresh;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllText(Path, JsonSerializer.Serialize(this,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Log.Warn($"settings save failed: {ex.Message}");
        }
    }
}
