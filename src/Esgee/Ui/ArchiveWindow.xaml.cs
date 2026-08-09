using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Esgee.Interop;
using Esgee.Peers;
using Esgee.Store;

namespace Esgee.Ui;

/// <summary>
/// The payoff of the OCR index: type words that were on screen weeks ago, get
/// the screenshot back, drag it straight out as a file.
///
/// With peers configured, the machine switcher browses another machine's
/// archive over the tailnet through the same grid/search/preview UX. Remote
/// files are materialized into a local cache before anything OS-facing (drag,
/// clipboard, video playback) touches them — CF_HDROP must name a real file.
/// </summary>
public partial class ArchiveWindow : Window
{
    private const int PageSize = 200;

    private readonly ShotStore _store;
    private readonly Settings _settings;
    private readonly Action _beforeClipboardWrite;
    private readonly DispatcherTimer _debounce;
    private readonly DispatcherTimer _livePoll;
    private string _lastToken = "";
    private bool _dragging;

    // Non-null while browsing a peer instead of the local store.
    private PeerClient? _remote;

    public ArchiveWindow(ShotStore store, Action beforeClipboardWrite, Settings settings)
    {
        InitializeComponent();

        _store = store;
        _settings = settings;
        _beforeClipboardWrite = beforeClipboardWrite;

        // Search-as-you-type, but not query-per-keystroke.
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _debounce.Tick += (_, _) => { _debounce.Stop(); Refresh(); };

        // Live refresh. This window is often a SEPARATE process from the
        // resident app doing the capturing (taskbar pin launches --archive), so
        // no in-memory event can reach it — instead poll the index for a cheap
        // change token (WAL read, sub-ms) and refresh when it moves. Also picks
        // up OCR completions, so an open search gains matches as text lands.
        // Local only: a remote view refreshes on demand, not by polling a peer.
        _livePoll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        _livePoll.Tick += (_, _) =>
        {
            if (!IsVisible || _dragging || _debounce.IsEnabled || _remote is not null) return;

            // Never rebuild while the left button is down: a refresh replaces
            // every tile, and a tile destroyed between mouse-down and mouse-up
            // silently eats the click (or a nascent drag). This was the "newest
            // capture won't open" bug — OCR completing on a fresh shot fired a
            // refresh right as the user clicked it.
            if (Mouse.LeftButton == MouseButtonState.Pressed) return;

            try
            {
                var token = _store.ChangeToken();
                if (token == _lastToken) return;

                // Tiles don't render OCR state, so if only ocr_done moved and no
                // search is active there is nothing visual to update — skip the
                // rebuild (and its 200 background decodes) entirely.
                if (SearchBox.Text.Trim().Length == 0 && SameRows(token, _lastToken))
                {
                    _lastToken = token;
                    return;
                }

                _lastToken = token;
                Log.Info("archive: index changed, auto-refreshing");
                Refresh();
            }
            catch (Exception ex)
            {
                Log.Warn($"archive live poll failed: {ex.Message}");
            }
        };
        _livePoll.Start();

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                // Esc peels one layer at a time: preview first, then the window.
                if (PreviewLayer.Visibility == Visibility.Visible) ClosePreview();
                else Close();
                e.Handled = true;
                return;
            }
            if (PreviewLayer.Visibility == Visibility.Visible &&
                e.Key is Key.Left or Key.Right)
            {
                StepPreview(e.Key == Key.Left ? -1 : +1);
                e.Handled = true;
                return;
            }
            if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
            {
                SearchBox.Focus();
                SearchBox.SelectAll();
            }
        };

        Loaded += (_, _) => { Refresh(); SearchBox.Focus(); InitMachineSwitcher(); };
        Closed += (_, _) => { _livePoll.Stop(); _debounce.Stop(); _remote?.Dispose(); };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Win32.UseDarkTitleBar(new WindowInteropHelper(this).Handle);
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        Placeholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _debounce.Stop();
        _debounce.Start();
    }

    // ---- machine switcher ---------------------------------------------------

    /// <summary>Populates the switcher: This PC plus every peer that answers
    /// /ping with our token. Hidden entirely until a PeerToken exists, so the
    /// default configuration renders the exact pre-peers window.</summary>
    private void InitMachineSwitcher()
    {
        if (string.IsNullOrEmpty(_settings.PeerToken)) return;

        MachineBox.Visibility = Visibility.Visible;
        MachineBox.Items.Clear();
        MachineBox.Items.Add("This PC");
        MachineBox.SelectedIndex = 0;

        _ = Task.Run(async () =>
        {
            try
            {
                var found = await PeerClient.DiscoverAsync(_settings);
                await Dispatcher.BeginInvoke(() =>
                {
                    foreach (var (info, ping) in found)
                        MachineBox.Items.Add(new PeerChoice(info,
                            $"{info.Name}  ({ping.Captures})"));
                });
            }
            catch (Exception ex)
            {
                Log.Warn($"peers: discovery failed: {ex.Message}");
            }
        });
    }

    private sealed record PeerChoice(PeerInfo Info, string Label)
    {
        public override string ToString() => Label;
    }

    private void OnMachineChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;

        var old = _remote;
        _remote = MachineBox.SelectedItem is PeerChoice choice
            ? new PeerClient(choice.Info, _settings.PeerToken)
            : null;
        old?.Dispose();

        if (_remote is not null)
            Log.Info($"archive: switched to peer {_remote.Peer.Name} ({_remote.Peer.BaseUrl})");
        else
            Log.Info("archive: switched to local store");

        ClosePreview();
        Refresh();
    }

    // Bumped on every refresh so in-flight thumbnail decodes from a superseded
    // search can't paint into the new result set.
    private int _generation;

    // Entries behind the current tiles. Refresh() assigns a NEW list each time,
    // so a preview that grabbed the old reference keeps a stable snapshot to
    // navigate even if a live-poll refresh replaces the grid underneath it.
    private List<Entry> _currentShots = [];
    private List<Entry> _previewShots = [];
    private int _previewIndex = -1;

    /// <summary>
    /// One grid tile / preview subject, local or remote. The wrapped Shot is
    /// the single shape everything downstream consumes: for a local capture it
    /// IS the store row; for a remote one its Path points at the peer-cache
    /// location and MaterializeAsync() makes that path real (idempotent, off
    /// the UI thread) before drag/copy/preview needs it.
    /// </summary>
    private sealed class Entry
    {
        public required Shot Shot { get; init; }
        public ShotDto? Dto { get; init; }
        public PeerClient? Remote { get; init; }
        private Task<Shot>? _fetch;

        public bool IsRemote => Remote is not null;

        public Task<Shot> MaterializeAsync()
            => Remote is null
                ? Task.FromResult(Shot)
                // Task.Run so awaits inside never capture the dispatcher
                // context — drag-out blocks on this task from the UI thread.
                : _fetch ??= Task.Run(() => Remote.EnsureLocalAsync(Dto!));
    }

    private void Refresh()
    {
        var query = SearchBox.Text.Trim();
        var gen = ++_generation;

        if (_remote is { } remote)
        {
            var pageEmpty = Results.Items.Count == 0;
            Empty.Text = $"Loading from {remote.Peer.Name}…";
            Empty.Visibility = pageEmpty ? Visibility.Visible : Visibility.Collapsed;

            _ = Task.Run(async () =>
            {
                List<ShotDto> dtos;
                try
                {
                    dtos = query.Length == 0
                        ? await remote.RecentAsync(PageSize)
                        : await remote.SearchAsync(query);
                }
                catch (Exception ex)
                {
                    Log.Warn($"peer {remote.Peer.Name}: query failed: {ex.Message}");
                    dtos = [];
                }

                await Dispatcher.BeginInvoke(() =>
                {
                    if (gen != _generation || !ReferenceEquals(_remote, remote)) return;

                    var entries = dtos.Select(d => new Entry
                    {
                        Shot = remote.ToLocalShot(d, remote.CachePathFor(d)),
                        Dto = d,
                        Remote = remote,
                    }).ToList();

                    Populate(entries, gen,
                        query.Length == 0
                            ? $"No captures on {remote.Peer.Name} (or it didn't answer)."
                            : $"Nothing matching \"{query}\" on {remote.Peer.Name}.");
                });
            });
            return;
        }

        // Any refresh observes the current index state; keep the poll's token
        // in step so it doesn't immediately re-refresh over us.
        try { _lastToken = _store.ChangeToken(); } catch { }

        List<Shot> shots;
        try
        {
            shots = query.Length == 0
                ? _store.Recent(PageSize)
                : _store.Search(ShotStore.FtsQuery(query), PageSize);
        }
        catch (Exception ex)
        {
            // An unbalanced quote in an FTS query throws; treat as no results
            // rather than a crash while the user is mid-keystroke.
            Log.Warn($"archive query failed: {ex.Message}");
            shots = [];
        }

        Populate(shots.Select(s => new Entry { Shot = s }).ToList(), gen,
            query.Length == 0
                ? "No captures yet — take one with the hotkey."
                : $"Nothing matching \"{query}\".");
    }

    private void Populate(List<Entry> entries, int gen, string emptyText)
    {
        if (gen != _generation) return;

        _currentShots = entries;
        Results.Items.Clear();
        foreach (var entry in entries)
            Results.Items.Add(BuildTile(entry, gen));

        Empty.Text = emptyText;
        Empty.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>True when two "maxid:count:ocrdone" tokens differ only in the
    /// ocr_done component — i.e. no rows were added or removed.</summary>
    private static bool SameRows(string a, string b)
    {
        var pa = a.Split(':');
        var pb = b.Split(':');
        return pa.Length == 3 && pb.Length == 3 && pa[0] == pb[0] && pa[1] == pb[1];
    }

    private UIElement BuildTile(Entry entry, int gen)
    {
        var shot = entry.Shot;

        var thumb = new Image
        {
            Width = 224,
            MinHeight = 60, // holds layout while the decode is in flight
            MaxHeight = 150,
            Stretch = System.Windows.Media.Stretch.Uniform,
        };
        System.Windows.Media.RenderOptions.SetBitmapScalingMode(
            thumb, System.Windows.Media.BitmapScalingMode.HighQuality);

        // Decode OFF the UI thread. With ~200 tiles of ultrawide PNGs, doing
        // this synchronously froze the window (and, opened from the tray, the
        // whole resident app: tray menu, hotkeys, shelf) for ~10s per refresh.
        // A frozen Freezable is legal to build on a worker and hand across.
        // Remote tiles fetch the peer's pre-scaled JPEG instead of a local file.
        _ = Task.Run(async () =>
        {
            try
            {
                var bmp = entry is { IsRemote: true, Remote: { } remote, Dto: { } dto }
                    ? DecodeFrozen(await remote.ThumbAsync(dto.Id), decodeWidth: 0)
                    : DecodeFile(shot.ThumbPath, decodeWidth: 448);

                await Dispatcher.BeginInvoke(() =>
                {
                    if (gen == _generation) thumb.Source = bmp;
                });
            }
            catch (Exception ex)
            {
                Log.Warn($"archive thumb failed for {shot.ThumbPath}: {ex.Message}");
            }
        });

        var when = $"{shot.TakenAt:MMM d, HH:mm}";
        var dims = $"{shot.Width}×{shot.Height}";
        var origin = shot.Origin.Length > 0 && !entry.IsRemote ? $"   ⇄ {shot.Origin}" : "";
        var caption = new TextBlock
        {
            Text = shot.IsVideo
                ? $"{when}   ▶ {shot.DurationText}   {dims}{origin}"
                : $"{when}   {dims}{origin}",
            Foreground = (System.Windows.Media.Brush)FindResource("InkMuted"),
            FontSize = 11,
            Margin = new Thickness(2, 5, 2, 1),
        };

        var panel = new StackPanel { Children = { thumb, caption } };

        panel.ToolTip = entry.IsRemote
            ? $"on {entry.Remote!.Peer.Name} — click to preview · drag out · right-click to pull"
            : "click to preview · drag out · right-click for more";

        Point pressAt = default;
        var pressed = false;

        panel.PreviewMouseLeftButtonDown += (_, e) =>
        {
            pressed = true;
            pressAt = e.GetPosition(panel);
            // Remote: start the download NOW, so by the time a drag crosses the
            // threshold (or a preview opens) the file is usually already local.
            if (entry.IsRemote) _ = entry.MaterializeAsync();
        };
        panel.PreviewMouseMove += (_, e) =>
        {
            if (!pressed || e.LeftButton != MouseButtonState.Pressed) return;
            var p = e.GetPosition(panel);
            if (Math.Abs(p.X - pressAt.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(p.Y - pressAt.Y) < SystemParameters.MinimumVerticalDragDistance)
                return;

            pressed = false;
            // DoDragDrop pumps messages, so the live-poll timer CAN fire inside
            // it — a mid-drag refresh would tear the tile out from under the
            // drag. The flag makes the poll sit out until the drop completes.
            _dragging = true;
            try
            {
                // Local: completes synchronously. Remote: usually already done
                // (mouse-down prefetch); a cold drag blocks here while the file
                // downloads — slower, but the drop still lands a real file.
                var local = entry.MaterializeAsync().GetAwaiter().GetResult();
                if (entry.IsRemote)
                    Log.Info($"archive: dragging remote shot {local.Id} via cache {local.Path}");
                DragDrop.DoDragDrop(panel, DragSource.BuildDataObject(local), DragDropEffects.Copy);
            }
            catch (Exception ex)
            {
                Log.Error($"archive drag failed: {ex.Message}");
            }
            finally
            {
                _dragging = false;
            }
        };
        panel.PreviewMouseLeftButtonUp += (_, _) =>
        {
            // A press that never crossed the drag threshold is a click: preview.
            if (pressed) OpenPreview(entry);
            pressed = false;
        };

        var menu = new ContextMenu();
        var copy = new MenuItem { Header = "Copy to clipboard" };
        copy.Click += (_, _) => Copy(entry);
        menu.Items.Add(copy);
        if (entry.IsRemote)
        {
            var pull = new MenuItem { Header = "Pull to this PC" };
            pull.Click += (_, _) => Pull(entry);
            menu.Items.Add(pull);
        }
        else
        {
            var reveal = new MenuItem { Header = "Show in folder" };
            reveal.Click += (_, _) => Reveal(shot);
            menu.Items.Add(reveal);
        }
        panel.ContextMenu = menu;

        return panel;
    }

    private static BitmapImage DecodeFile(string path, int decodeWidth)
    {
        using var fs = System.IO.File.OpenRead(path);
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.StreamSource = fs;
        if (decodeWidth > 0) bmp.DecodePixelWidth = decodeWidth;
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    private static BitmapImage DecodeFrozen(byte[] bytes, int decodeWidth)
    {
        using var ms = new System.IO.MemoryStream(bytes);
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.StreamSource = ms;
        if (decodeWidth > 0) bmp.DecodePixelWidth = decodeWidth;
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    // ---- pull to this PC ----------------------------------------------------

    /// <summary>Makes a remote capture a first-class LOCAL one: download (via
    /// the cache), copy into the archive tree, insert a row that imports the
    /// peer's OCR text + engine version from /meta, marked with its origin
    /// machine. Content-hash dedupe makes a double-pull a no-op.</summary>
    private async void Pull(Entry entry)
    {
        if (entry is not { IsRemote: true, Remote: { } remote, Dto: { } dto }) return;
        try
        {
            var (ingested, duplicate) = await Task.Run(async () =>
            {
                var meta = await remote.MetaAsync(dto.Id);
                var cached = await remote.EnsureLocalAsync(dto);

                var ext = System.IO.Path.GetExtension(cached.Path);
                var dest = _store.PlanIngestPath(cached.TakenAt, ext);
                System.IO.File.Copy(cached.Path, dest);
                if (cached.IsVideo)
                {
                    var gif = System.IO.Path.ChangeExtension(cached.Path, ".gif");
                    if (System.IO.File.Exists(gif))
                        System.IO.File.Copy(gif, System.IO.Path.ChangeExtension(dest, ".gif"), true);
                    if (System.IO.File.Exists(cached.Path + ".png"))
                        System.IO.File.Copy(cached.Path + ".png", dest + ".png", true);
                }

                var (shot, dup) = _store.Ingest(dest, cached.Sha256, cached.TakenAt,
                    cached.Width, cached.Height, cached.Kind, cached.DurationMs,
                    meta?.OcrText, meta?.OcrEngineVersion ?? "", cached.Origin);
                if (dup)
                {
                    try { System.IO.File.Delete(dest); } catch { }
                    try { System.IO.File.Delete(System.IO.Path.ChangeExtension(dest, ".gif")); } catch { }
                    try { System.IO.File.Delete(dest + ".png"); } catch { }
                }
                return (shot, dup);
            });

            Log.Info(duplicate
                ? $"archive: pull of remote shot {dto.Id} deduplicated (already local as {ingested.Id})"
                : $"archive: pulled remote shot {dto.Id} from {remote.Peer.Name} -> {ingested.Path} (local id {ingested.Id})");
            FlashTitle(duplicate
                ? $"already on this PC ({System.IO.Path.GetFileName(ingested.Path)})"
                : $"pulled to this PC ({System.IO.Path.GetFileName(ingested.Path)})");
        }
        catch (Exception ex)
        {
            Log.Error($"archive: pull failed: {ex.Message}");
            FlashTitle("pull failed — see log");
        }
    }

    private DispatcherTimer? _titleReset;

    private void FlashTitle(string message)
    {
        Title = $"esgee archive — {message}";
        _titleReset?.Stop();
        _titleReset = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _titleReset.Tick += (_, _) => { _titleReset!.Stop(); Title = "esgee archive"; };
        _titleReset.Start();
    }

    // ---- lightbox preview ---------------------------------------------------

    private Entry? PreviewEntry =>
        _previewIndex >= 0 && _previewIndex < _previewShots.Count
            ? _previewShots[_previewIndex] : null;

    private void OpenPreview(Entry entry)
    {
        _previewShots = _currentShots;
        _previewIndex = _previewShots.FindIndex(s => s.Shot.Id == entry.Shot.Id);
        if (_previewIndex < 0) { _previewShots = [entry]; _previewIndex = 0; }

        PreviewLayer.Visibility = Visibility.Visible;
        ShowPreviewContent(entry);
        Focus(); // arrow keys must land on the window, not the search box
    }

    private void StepPreview(int delta)
    {
        if (_previewShots.Count == 0) return;
        var next = Math.Clamp(_previewIndex + delta, 0, _previewShots.Count - 1);
        if (next == _previewIndex) return;
        _previewIndex = next;
        ShowPreviewContent(_previewShots[next]);
    }

    private void ShowPreviewContent(Entry entry)
    {
        var shot = entry.Shot;
        var from = entry.IsRemote ? $"   on {entry.Remote!.Peer.Name}" : "";
        PreviewCaption.Text = shot.IsVideo
            ? $"{shot.TakenAt:MMM d, yyyy  HH:mm}   ▶ {shot.DurationText}   {shot.Width}×{shot.Height}{from}"
            : $"{shot.TakenAt:MMM d, yyyy  HH:mm}   {shot.Width}×{shot.Height}{from}";
        PreviewPullBtn.Visibility = entry.IsRemote ? Visibility.Visible : Visibility.Collapsed;
        PreviewFolderBtn.Visibility = entry.IsRemote ? Visibility.Collapsed : Visibility.Visible;

        var expected = shot.Id;

        if (shot.IsVideo)
        {
            // Play the actual clip — muted loop; a frozen thumbnail would be a
            // letdown for the one media type whose point is motion. A remote
            // clip downloads to the cache first (MediaElement needs a file).
            PreviewImage.Visibility = Visibility.Collapsed;
            PreviewImage.Source = null;
            PreviewVideo.Visibility = Visibility.Visible;
            _ = Task.Run(async () =>
            {
                try
                {
                    var local = await entry.MaterializeAsync();
                    await Dispatcher.BeginInvoke(() =>
                    {
                        if (PreviewEntry?.Shot.Id != expected) return;
                        PreviewVideo.Source = new Uri(local.Path);
                        PreviewVideo.Position = TimeSpan.Zero;
                        PreviewVideo.Play();
                    });
                }
                catch (Exception ex)
                {
                    Log.Warn($"preview video failed for {shot.Path}: {ex.Message}");
                }
            });
            return;
        }

        PreviewVideo.Stop();
        PreviewVideo.Source = null; // release the file handle
        PreviewVideo.Visibility = Visibility.Collapsed;
        PreviewImage.Visibility = Visibility.Visible;

        // Full-quality decode, off the UI thread; guard against the user having
        // stepped on before a slow decode (or a remote download) lands. This
        // also doubles as the remote prefetch: previewing a tile warms the
        // cache, so a drag right after is instant.
        _ = Task.Run(async () =>
        {
            try
            {
                var local = await entry.MaterializeAsync();
                var bmp = DecodeFile(local.Path, decodeWidth: 0);
                await Dispatcher.BeginInvoke(() =>
                {
                    if (PreviewEntry?.Shot.Id == expected) PreviewImage.Source = bmp;
                });
            }
            catch (Exception ex)
            {
                Log.Warn($"preview decode failed for {shot.Path}: {ex.Message}");
            }
        });
    }

    private void ClosePreview()
    {
        PreviewVideo.Stop();
        PreviewVideo.Source = null;
        PreviewImage.Source = null;
        PreviewLayer.Visibility = Visibility.Collapsed;
        _previewIndex = -1;
        SearchBox.Focus();
    }

    private void OnScrimClick(object sender, MouseButtonEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, PreviewScrim)) ClosePreview();
    }

    private void OnPreviewClose(object sender, RoutedEventArgs e) => ClosePreview();

    private void OnPreviewCopy(object sender, RoutedEventArgs e)
    {
        if (PreviewEntry is { } entry) Copy(entry);
    }

    private void OnPreviewPull(object sender, RoutedEventArgs e)
    {
        if (PreviewEntry is { } entry) Pull(entry);
    }

    private void OnPreviewReveal(object sender, RoutedEventArgs e)
    {
        if (PreviewEntry is { IsRemote: false } entry) Reveal(entry.Shot);
    }

    private void OnPreviewVideoEnded(object sender, RoutedEventArgs e)
    {
        PreviewVideo.Position = TimeSpan.Zero;
        PreviewVideo.Play();
    }

    private async void Copy(Entry entry)
    {
        try
        {
            // Remote: download first (off the UI thread); the clipboard write
            // itself must happen back here on the STA.
            var local = await entry.MaterializeAsync();
            _beforeClipboardWrite();
            Clipboard.SetDataObject(DragSource.BuildDataObject(local), copy: true);
        }
        catch (Exception ex)
        {
            Log.Error($"archive copy failed: {ex.Message}");
        }
    }

    private static void Reveal(Shot shot)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{shot.Path}\"")
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Log.Error($"archive reveal failed: {ex.Message}");
        }
    }
}
