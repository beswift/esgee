using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows;
using Esgee.Capture;
using Esgee.Ocr;
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
    private readonly UpdateService _update = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _settings = Settings.Load();

        // Query mode runs against the same archive and exits without a tray icon,
        // so it must be handled before the singleton check.
        if (Cli.TryRun(e.Args, _settings))
        {
            Shutdown();
            return;
        }

        // `esgee --archive`: just the browser window, no tray/watcher/hotkeys.
        // Deliberately exempt from the singleton — WAL mode lets it read the
        // index alongside the resident instance.
        if (e.Args.Any(a => a.TrimStart('-').Equals("archive", StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode = ShutdownMode.OnLastWindowClose;
            _store = new ShotStore(_settings.ArchiveRoot);
            new ArchiveWindow(_store, () => { }).Show();
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

        BuildTray();
        _update.StartBackgroundChecks();
        Log.Info($"esgee v{UpdateService.CurrentVersion} up; watching clipboard, archiving to {_store.Root}");
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

        _archive = new ArchiveWindow(_store, () => _watcher.IgnoreNextChange());
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
