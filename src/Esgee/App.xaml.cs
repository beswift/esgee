using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows;
using Esgee.Capture;
using Esgee.Ocr;
using Esgee.Peers;
using Esgee.Store;
using Esgee.Ui;
using Forms = System.Windows.Forms;

namespace Esgee;

public partial class App : Application
{
    private static Mutex? _single;

    private Settings _settings = null!;
    private ShotStore _store = null!;
    private ShelfWindow _shelf = null!;
    private ClipboardWatcher _watcher = null!;
    private OcrIndexer? _ocr;
    private HotkeyManager? _hotkey;
    private CaptureController? _capture;
    private RecordController? _record;
    private ArchiveWindow? _archive;
    private Forms.NotifyIcon _tray = null!;
    private PeerServer? _peerServer;
    private Task<PeerServer?>? _serverTask;
    private SyncQueue? _sync;
    private PairingWindow? _pairingWindow;
    private PairingEnterWindow? _pairingEnter;
    private int _lastPeerCount = -1;
    private DateTime _lastPeerCountAt = DateTime.MinValue;
    private readonly UpdateService _update = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _settings = Settings.Load();

        // Hidden global override for test harnesses and side-by-side archives:
        // `--archive-root <path>` points ANY mode (search, serve, resident) at a
        // different archive than settings.json names.
        var args = e.Args.ToList();
        if (TakeOption(args, "archive-root") is { } rootOverride)
        {
            _settings.ArchiveRoot = rootOverride;
            Log.Info($"archive root overridden by CLI: {rootOverride}");
        }

        // Query mode runs against the same archive and exits without a tray icon,
        // so it must be handled before the singleton check. Portable verbs live
        // in Core's Cli; the clipboard-bound check verbs in CliChecks.
        if (Cli.TryRun([.. args], _settings) || CliChecks.TryRun([.. args], _settings))
        {
            Shutdown();
            return;
        }

        // `esgee --serve`: headless peer server only — no tray, watcher, or
        // hotkeys, and exempt from the singleton. Exists for testing the peer
        // layer (serve a second archive root on another port) but is a real
        // server: same code path the resident app runs.
        if (args.Any(a => a.TrimStart('-').Equals("serve", StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var port = int.TryParse(TakeOption(args, "port"), out var p) ? p : _settings.PeerPort;
            var token = TakeOption(args, "token") ?? _settings.PeerToken;
            _store = new ShotStore(_settings.ArchiveRoot);
            _peerServer = PeerServer.TryStart(_store, token, port, new Interop.WpfThumbEncoder());
            if (_peerServer is null)
            {
                Log.Error("serve mode: server failed to start; exiting");
                Shutdown();
            }
            else
            {
                Log.Info($"serve mode: archive {_store.Root} on {_peerServer.BoundAddress}; " +
                         "kill the process to stop");
            }
            return;
        }

        // `esgee --archive`: just the browser window, no tray/watcher/hotkeys.
        // Deliberately exempt from the singleton — WAL mode lets it read the
        // index alongside the resident instance.
        if (args.Any(a => a.TrimStart('-').Equals("archive", StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode = ShutdownMode.OnLastWindowClose;
            _store = new ShotStore(_settings.ArchiveRoot);
            new ArchiveWindow(_store, () => { }, _settings).Show();
            return;
        }

        // One resident instance, or two watchers would each save every capture.
        _single = new Mutex(initiallyOwned: true, "esgee.singleton", out var isFirst);
        if (!isFirst)
        {
            Log.Info("another instance is already running; exiting");
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error($"unhandled (dispatcher): {args.Exception}");
            args.Handled = true; // a tray app should survive a bad capture
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Error($"unhandled (appdomain, terminating={args.IsTerminating}): {args.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error($"unhandled (task): {args.Exception}");
            args.SetObserved();
        };
        Dispatcher.ShutdownStarted += (_, _) => Log.Warn("dispatcher shutdown STARTED — who asked?");
        Exit += (_, _) => Log.Info("app Exit event");

        _store = new ShotStore(_settings.ArchiveRoot);
        _watcher = new ClipboardWatcher();
        _shelf = new ShelfWindow(() => _watcher.IgnoreNextChange())
        {
            Linger = TimeSpan.FromSeconds(_settings.LingerSeconds),
            MaxCards = _settings.MaxCards
        };
        _watcher.Captured += img => OnCaptured(img, toClipboard: false);

        // Our own capture paths: hotkey → same pipeline. Unlike watcher captures
        // (already on the clipboard by definition), these also need to be PUT
        // there, so Ctrl+V muscle memory keeps working.
        _capture = new CaptureController(_shelf, _settings);
        _capture.Captured += img => OnCaptured(img, toClipboard: true);

        // Screen recording: same toggle chord to start and stop, recordings land
        // in the same pipeline as screenshots (minus OCR).
        _record = new RecordController(_settings, _store.Root);
        _record.Completed += OnRecorded;

        // Extra region chords ride along: Win+Shift+S usually loses to the
        // shell's own snip hotkey (harmless — those captures arrive via the
        // clipboard watcher), and PrintScreen only fires after the next
        // sign-out. Ctrl+Shift+S is the always-alive one.
        _hotkey = new HotkeyManager(
        [
            (_settings.RegionHotkey, "region"),
            ("Ctrl+Shift+S", "region"),
            ("PrintScreen", "region"),
            (_settings.FullscreenHotkey, "screen"),
            (_settings.LastRegionHotkey, "last"),
            (_settings.TimerHotkey, "timer"),
            (_settings.RecordHotkey, "record"),
        ]);
        _hotkey.Pressed += action =>
        {
            switch (action)
            {
                case "region": _capture.Begin(); break;
                case "screen": _capture.BeginFullscreen(); break;
                case "last": _capture.BeginLastRegion(); break;
                case "timer": _capture.BeginTimed(); break;
                case "record": _record?.Toggle(); break;
            }
        };

        if (_settings.OcrEnabled)
        {
            _ocr = new OcrIndexer(_store);
            _ocr.EnqueueBacklog();
        }

        StartPeers();

        BuildTray();
        _update.StartBackgroundChecks();
        Log.Info($"esgee v{UpdateService.CurrentVersion} up; watching clipboard, archiving to {_store.Root}");
    }

    /// <summary>Peer layer, entirely opt-in. With PeersEnabled=false and no
    /// SyncTargetPeer this opens zero sockets and starts zero threads — the
    /// default configuration behaves exactly like pre-peers builds.</summary>
    private void StartPeers()
    {
        if (_settings.PeersEnabled && string.IsNullOrEmpty(_settings.PeerToken))
        {
            // First enable: mint the shared secret the user copies to the other
            // machines' settings.json.
            _settings.PeerToken = Convert.ToHexString(
                System.Security.Cryptography.RandomNumberGenerator.GetBytes(24));
            _settings.Save();
            Log.Info("peers: generated PeerToken (copy it into settings.json on your other machines)");
        }

        if (_settings.PeersEnabled)
        {
            // Off the startup path: resolving the tailscale IP shells out.
            _ = EnsureServerAsync();
        }

        if (_settings.SyncTargetPeer.Length > 0)
        {
            if (string.IsNullOrEmpty(_settings.PeerToken))
            {
                Log.Warn("sync: SyncTargetPeer set but no PeerToken; sync disabled");
                return;
            }
            _sync = new SyncQueue(_store, _settings);
            _ = Task.Run(_sync.EnqueueBacklog);
            Log.Info($"sync: pushing new captures to {_settings.SyncTargetPeer}");
        }
    }

    /// <summary>Starts the peer server exactly once, off the UI thread (TryStart
    /// shells out for the tailscale IP). UI-thread callers only, so the
    /// task/field handoff is race-free. A failed start (tailscale down, port
    /// taken) clears the task so a later call — e.g. the user clicking "Pair a
    /// new machine…" after starting Tailscale — retries cleanly.</summary>
    private async Task<PeerServer?> EnsureServerAsync()
    {
        if (_peerServer is not null) return _peerServer;
        if (_serverTask is null)
        {
            var token = _settings.PeerToken;
            var port = _settings.PeerPort;
            _serverTask = Task.Run(() =>
                PeerServer.TryStart(_store, token, port, new Interop.WpfThumbEncoder()));
        }

        var server = await _serverTask;
        if (server is null) _serverTask = null; // allow retry
        else _peerServer ??= server;
        return _peerServer;
    }

    /// <summary>Tears the peer server down (pairing window included). The token
    /// always survives a disable so re-enabling doesn't force a re-pair.</summary>
    private void StopServer()
    {
        _pairingWindow?.Close();
        _peerServer?.Dispose();
        _peerServer = null;
        _serverTask = null;
    }

    /// <summary>Removes "--name value" from the list and returns the value.</summary>
    private static string? TakeOption(List<string> args, string name)
    {
        var idx = args.FindIndex(a => a.TrimStart('-').Equals(name, StringComparison.OrdinalIgnoreCase));
        if (idx < 0 || idx + 1 >= args.Count) return null;
        var value = args[idx + 1];
        args.RemoveRange(idx, 2);
        return value;
    }

    private async void OnCaptured(CapturedImage capture, bool toClipboard)
    {
        try
        {
            // Encoding and hashing an ultrawide grab is tens of milliseconds;
            // off the UI thread so the shelf animation never stutters.
            var shot = await Task.Run(() =>
            {
                var png = capture.ToPng();
                return _store.Add(png, capture.Width, capture.Height, capture.TakenAt);
            });

            _shelf.Push(shot);
            _ocr?.Enqueue(shot);
            _sync?.Enqueue(shot.Id); // non-blocking channel write — never delays capture

            if (toClipboard)
            {
                // The full multi-format object, so a paste target gets its pick
                // of file / PNG / bitmap — same payload as a drag.
                _watcher.IgnoreNextChange();
                Clipboard.SetDataObject(Interop.DragSource.BuildDataObject(shot), copy: true);
            }

            Log.Info($"captured {shot.Width}x{shot.Height} -> {shot.Path}");
        }
        catch (Exception ex)
        {
            Log.Error($"capture pipeline failed: {ex}");
        }
        finally
        {
            capture.Dispose();
        }
    }

    /// <summary>Recording twin of OnCaptured: the files are already on disk, so
    /// this just records, shelves, and puts CF_HDROP on the clipboard. No OCR —
    /// there's no still text to read.</summary>
    private async void OnRecorded(RecordingResult rec)
    {
        try
        {
            var shot = await Task.Run(() => _store.AddFile(
                rec.Mp4Path, rec.Width, rec.Height, rec.StartedAt, "video", rec.DurationMs));

            _shelf.Push(shot);
            _sync?.Enqueue(shot.Id);

            // CF_HDROP with the GIF when there is one (the paste-anywhere pick),
            // else the MP4 — same choice DragSource makes for drag-out.
            _watcher.IgnoreNextChange();
            Clipboard.SetDataObject(Interop.DragSource.BuildDataObject(shot), copy: true);

            Log.Info($"recorded {shot.Width}x{shot.Height} {shot.DurationText} -> {shot.Path} (id {shot.Id})");
        }
        catch (Exception ex)
        {
            Log.Error($"recording pipeline failed: {ex}");
        }
    }

    private void OpenArchiveWindow()
    {
        if (_archive is { IsLoaded: true })
        {
            _archive.Activate();
            return;
        }

        _archive = new ArchiveWindow(_store, () => _watcher.IgnoreNextChange(), _settings);
        _archive.Show();
    }

    private void BuildTray()
    {
        var menu = new Forms.ContextMenuStrip();

        string? ChordFor(string action) =>
            _hotkey?.Bound.FirstOrDefault(b => b.Action == action).Chord;
        string Item(string label, string action) =>
            ChordFor(action) is { } c ? $"{label}  ({c})" : label;

        menu.Items.Add(Item("Capture region", "region"), null, (_, _) => _capture?.Begin());
        menu.Items.Add(Item("Capture screen", "screen"), null, (_, _) => _capture?.BeginFullscreen());
        menu.Items.Add(Item("Repeat last region", "last"), null, (_, _) => _capture?.BeginLastRegion());
        menu.Items.Add(Item($"Timed capture ({Math.Clamp(_settings.TimerSeconds, 1, 60)}s)", "timer"),
            null, (_, _) => _capture?.BeginTimed());

        var record = new Forms.ToolStripMenuItem(Item("Record region / screen", "record"));
        record.Click += (_, _) => _record?.Toggle();
        if (_record is not null)
            _record.StateChanged += recording =>
                record.Text = recording ? Item("Stop recording", "record")
                                        : Item("Record region / screen", "record");
        menu.Items.Add(record);

        menu.Items.Add("Search archive…", null, (_, _) => OpenArchiveWindow());
        menu.Items.Add("Open archive folder", null, (_, _) => OpenArchive());
        menu.Items.Add(BuildPeersMenu(menu));
        menu.Items.Add("Clear shelf", null, (_, _) => _shelf.ClearAll());
        menu.Items.Add(new Forms.ToolStripSeparator());

        var linger = new Forms.ToolStripMenuItem("Cards linger for");
        foreach (var seconds in new[] { 4, 8, 15, 30 })
        {
            var choice = seconds;
            var item = new Forms.ToolStripMenuItem($"{choice}s")
            {
                Checked = choice == _settings.LingerSeconds,
                CheckOnClick = true
            };
            item.Click += (_, _) =>
            {
                _shelf.Linger = TimeSpan.FromSeconds(choice);
                _settings.LingerSeconds = choice;
                _settings.Save();
                foreach (Forms.ToolStripMenuItem sibling in linger.DropDownItems)
                    sibling.Checked = ReferenceEquals(sibling, item);
            };
            linger.DropDownItems.Add(item);
        }
        menu.Items.Add(linger);

        var startup = new Forms.ToolStripMenuItem("Start with Windows")
        {
            Checked = Autostart.IsEnabled,
            CheckOnClick = true
        };
        startup.Click += (_, _) => Autostart.Set(startup.Checked);
        menu.Items.Add(startup);

        menu.Items.Add("Edit settings", null, (_, _) => OpenSettings());
        menu.Items.Add($"Check for updates  (v{UpdateService.CurrentVersion})",
            null, async (_, _) => await CheckForUpdatesInteractive());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Quit());

        _tray = new Forms.NotifyIcon
        {
            Icon = LoadTrayIcon(),
            Text = _hotkey?.Bound.FirstOrDefault(b => b.Action == "region").Chord is { } main
                ? Truncate($"esgee — {main} to capture", 63)
                : "esgee — screenshots land here",
            Visible = true,
            ContextMenuStrip = menu
        };
        _tray.DoubleClick += (_, _) => OpenArchiveWindow();
    }

    /// <summary>The Peers submenu: live on/off state, the two halves of PIN
    /// pairing, and the off switch. State text refreshes each time the tray
    /// menu opens; the machine count comes from a throttled background
    /// discovery so opening the menu never blocks on the network.</summary>
    private Forms.ToolStripMenuItem BuildPeersMenu(Forms.ContextMenuStrip menu)
    {
        var root = new Forms.ToolStripMenuItem("Peers");
        var state = new Forms.ToolStripMenuItem("Peers: off") { Enabled = false };
        var detail = new Forms.ToolStripMenuItem { Enabled = false, Visible = false };
        var pairHost = new Forms.ToolStripMenuItem("Pair a new machine…");
        var pairJoin = new Forms.ToolStripMenuItem("Pair with another machine…");
        var disable = new Forms.ToolStripMenuItem("Disable peers");

        pairHost.Click += async (_, _) => await OpenPairNewMachineAsync();
        pairJoin.Click += (_, _) => OpenPairWithMachine();
        disable.Click += (_, _) => DisablePeers();

        root.DropDownItems.Add(state);
        root.DropDownItems.Add(detail);
        root.DropDownItems.Add(new Forms.ToolStripSeparator());
        root.DropDownItems.Add(pairHost);
        root.DropDownItems.Add(pairJoin);
        root.DropDownItems.Add(new Forms.ToolStripSeparator());
        root.DropDownItems.Add(disable);

        void SetStateText()
        {
            state.Text = !_settings.PeersEnabled ? "Peers: off"
                : _lastPeerCount switch
                {
                    < 0 => "Peers: on",
                    1 => "Peers: on (1 machine)",
                    var n => $"Peers: on ({n} machines)",
                };
        }

        void RefreshStatus()
        {
            SetStateText();
            disable.Enabled = _settings.PeersEnabled;

            var serving = _peerServer is not null ? $"serving on {_peerServer.BoundAddress}" : null;
            var syncing = _sync is not null
                ? $"sync to {_sync.Target}: " + (_sync.Offline
                    ? $"offline, {_sync.Pending} queued"
                    : _sync.Pending > 0 ? $"{_sync.Pending} pending" : "up to date")
                : null;
            var text = string.Join("  ·  ", new[] { serving, syncing }.Where(s => s is not null));
            detail.Text = Truncate(text, 100);
            detail.Visible = text.Length > 0;

            // Count reachable archives in the background (throttled — discovery
            // probes the tailnet). The label updates in place when it lands.
            if (_settings.PeersEnabled && _settings.PeerToken.Length > 0 &&
                DateTime.UtcNow - _lastPeerCountAt > TimeSpan.FromSeconds(20))
            {
                _lastPeerCountAt = DateTime.UtcNow;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var found = await PeerClient.DiscoverAsync(_settings);
                        _lastPeerCount = found.Count;
                        await Dispatcher.BeginInvoke(SetStateText);
                    }
                    catch { /* count stays stale — harmless */ }
                });
            }
        }

        RefreshStatus();
        menu.Opening += (_, _) => RefreshStatus();
        return root;
    }

    /// <summary>"Pair a new machine…": this machine shows the PIN. First use is
    /// the enable switch — it mints the token, flips PeersEnabled, and brings
    /// the server up, all without touching settings.json by hand.</summary>
    private async Task OpenPairNewMachineAsync()
    {
        try
        {
            if (_pairingWindow is { IsLoaded: true })
            {
                _pairingWindow.Activate();
                return;
            }

            if (string.IsNullOrEmpty(_settings.PeerToken))
            {
                _settings.PeerToken = Convert.ToHexString(
                    System.Security.Cryptography.RandomNumberGenerator.GetBytes(24));
                Log.Info("peers: generated PeerToken (first pairing)");
            }
            if (!_settings.PeersEnabled)
            {
                _settings.PeersEnabled = true;
                Log.Info("peers: enabled (first pairing)");
            }
            _settings.Save();

            var server = await EnsureServerAsync();
            if (server is null)
            {
                MessageBox.Show(
                    "Pairing needs Tailscale running on this machine — the peer API " +
                    "binds only to the Tailscale address.\n\nStart Tailscale and try " +
                    "again; esgee.log has details.",
                    "esgee — pairing", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _pairingWindow = new PairingWindow(new PairingSession(), server);
            _pairingWindow.Show();
            _pairingWindow.Activate();
        }
        catch (Exception ex)
        {
            Log.Error($"peers: pairing window failed: {ex}");
        }
    }

    /// <summary>"Pair with another machine…": this machine types the PIN.</summary>
    private void OpenPairWithMachine()
    {
        if (_pairingEnter is { IsLoaded: true })
        {
            _pairingEnter.Activate();
            return;
        }
        _pairingEnter = new PairingEnterWindow(_settings, ApplyPairedToken);
        _pairingEnter.Show();
        _pairingEnter.Activate();
    }

    /// <summary>A pairing succeeded: persist the received token, flip peers on,
    /// and bring the server up (or bounce it onto the new token) in-process —
    /// the running app is fully paired with no restart.</summary>
    private void ApplyPairedToken(PairResult pair)
    {
        var tokenChanged = !string.Equals(_settings.PeerToken, pair.Token, StringComparison.Ordinal);
        _settings.PeerToken = pair.Token;
        _settings.PeersEnabled = true;
        _settings.Save();
        Log.Info($"peers: paired with '{pair.Machine}' — peers enabled, token " +
                 (tokenChanged ? "adopted" : "unchanged") + ", settings saved");

        if (tokenChanged && (_peerServer is not null || _serverTask is not null))
            StopServer(); // old token is dead; restart on the adopted one
        _ = EnsureServerAsync();
        _lastPeerCountAt = DateTime.MinValue; // next menu open recounts
    }

    /// <summary>Peers off: server (and any open pairing window) down, zero
    /// sockets again. The token is kept so pairing again is instant.</summary>
    private void DisablePeers()
    {
        _settings.PeersEnabled = false;
        _settings.Save();
        StopServer();
        _lastPeerCount = -1;
        Log.Info("peers: disabled from tray (token kept; pair again any time)");
    }

    /// <summary>Tray-menu update check: reports "up to date", or offers to
    /// restart into the new version now (declining still applies it on the next
    /// launch — the background staging already happened).</summary>
    private async Task CheckForUpdatesInteractive()
    {
        try
        {
            if (!_update.IsInstalled)
            {
                MessageBox.Show(
                    $"This copy (v{UpdateService.CurrentVersion}) was built from source, so it doesn't self-update.\n" +
                    $"Installs from {UpdateService.RepoUrl}/releases do.",
                    "esgee", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var target = await _update.CheckAndStageAsync();
            if (target is null)
            {
                MessageBox.Show($"esgee v{UpdateService.CurrentVersion} is up to date.",
                    "esgee", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var pick = MessageBox.Show(
                $"esgee v{target} is downloaded (you're running v{UpdateService.CurrentVersion}).\n\n" +
                "Restart now to finish updating? Choosing No applies it the next time esgee starts.",
                "esgee — update ready", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (pick == MessageBoxResult.Yes) await _update.UpdateNowAsync();
        }
        catch (Exception ex)
        {
            Log.Error($"interactive update check failed: {ex.Message}");
            MessageBox.Show("Update check failed — see esgee.log for details.",
                "esgee", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OpenSettings()
    {
        try
        {
            // Notepad, deliberately: the shell association hands .json to the
            // user's IDE, whose cold start is slow enough that the click reads
            // as "nothing happened" — and then an IDE window ambushes them ten
            // seconds later during something unrelated.
            Process.Start("notepad.exe", $"\"{Settings.Path}\"");
        }
        catch (Exception ex)
        {
            Log.Error($"open settings failed: {ex.Message}");
        }
    }

    private void OpenArchive()
    {
        try
        {
            Process.Start(new ProcessStartInfo(_store.Root) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error($"open archive failed: {ex.Message}");
        }
    }

    /// <summary>The shipped glass .ico (same art as the exe/shortcuts), with the
    /// drawn glyph as fallback so a missing file never blanks the tray.</summary>
    private static Icon LoadTrayIcon()
    {
        try
        {
            var ico = System.IO.Path.Combine(AppContext.BaseDirectory, "esgee.ico");
            if (System.IO.File.Exists(ico)) return new Icon(ico, 32, 32);
        }
        catch (Exception ex)
        {
            Log.Warn($"tray icon load failed, using drawn fallback: {ex.Message}");
        }
        return MakeIcon();
    }

    /// <summary>Drawn fallback for when the .ico beside the exe is missing.</summary>
    private static Icon MakeIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;

            using var body = new SolidBrush(Color.FromArgb(0x5B, 0x8C, 0xFF));
            using var path = new GraphicsPath();
            const int r = 8;
            path.AddArc(2, 2, r, r, 180, 90);
            path.AddArc(30 - r, 2, r, r, 270, 90);
            path.AddArc(30 - r, 30 - r, r, r, 0, 90);
            path.AddArc(2, 30 - r, r, r, 90, 90);
            path.CloseFigure();
            g.FillPath(body, path);

            // A crop-mark aperture: two corner brackets.
            using var pen = new Pen(Color.White, 2.4f);
            g.DrawLines(pen, [new PointF(10, 13), new PointF(10, 10), new PointF(13, 10)]);
            g.DrawLines(pen, [new PointF(19, 22), new PointF(22, 22), new PointF(22, 19)]);
        }

        var hicon = bmp.GetHicon();
        using var tmp = Icon.FromHandle(hicon);
        return (Icon)tmp.Clone(); // clone so the HICON can be released with the bitmap
    }

    /// <summary>NotifyIcon.Text throws over 63 chars.</summary>
    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..(max - 1)] + "…";

    private void Quit()
    {
        _tray.Visible = false;
        _shelf.AllowClose = true;
        _shelf.Close();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _record?.Dispose(); // finalizes any in-flight ffmpeg so the MP4 survives
        _peerServer?.Dispose();
        _sync?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
        _ocr?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
        _hotkey?.Dispose();
        _watcher?.Dispose();
        _store?.Dispose();
        _tray?.Dispose();
        _single?.Dispose();
        Log.Info("esgee down");
        base.OnExit(e);
    }
}
