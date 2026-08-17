using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Esgee.Interop;
using Esgee.Peers;
using Esgee.Shares;
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

    // Non-null while browsing a team share — shares ride the same switcher
    // (their one shared namespace with peers, docs/SHARES.md) but are a
    // different noun: per-member token, read-only here beyond pushes.
    private ShareClient? _share;
    private Dictionary<string, string>? _shareMembers; // member_id -> display name
    private readonly SharePusher _sharePush;

    public ArchiveWindow(ShotStore store, Action beforeClipboardWrite, Settings settings)
    {
        InitializeComponent();

        _store = store;
        _settings = settings;
        _beforeClipboardWrite = beforeClipboardWrite;
        _sharePush = new SharePusher(store, settings);

        // Every window announces its provenance. When a stale window from an
        // old process is mistaken for the current build ("the switcher is
        // gone?"), this line names the binary that actually drew it.
        Log.Info($"archive window: v{UpdateService.CurrentVersion} " +
                 $"from {AppContext.BaseDirectory}, " +
                 $"peer token {(string.IsNullOrEmpty(settings.PeerToken) ? "absent" : "present")}");

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
            if (!IsVisible || _dragging || _debounce.IsEnabled ||
                _remote is not null || _share is not null) return;

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
                // Arrows navigate the preview — unless the caret is in the
                // screen-text panel, where they belong to text selection.
                if (OcrTextBox.IsKeyboardFocusWithin) return;
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

        Loaded += (_, _) => { Refresh(); SearchBox.Focus(); RefreshMachineSwitcher(); };
        Closed += (_, _) => { _livePoll.Stop(); _debounce.Stop(); _remote?.Dispose(); _share?.Dispose(); };
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

    // Bumped on every switcher rebuild so an in-flight discovery from a
    // superseded build can't insert peers into a list whose landmarks moved.
    private int _switcherGen;

    /// <summary>Populates the switcher: This PC, every peer that answers /ping
    /// with our token, and — visually separated, never mixed in
    /// (docs/SHARES.md) — the joined team shares. Hidden entirely until a
    /// PeerToken or a share exists, so the default configuration renders the
    /// exact pre-peers window. Public and re-runnable: the resident app calls
    /// it again when a share is joined or removed (or a pairing lands) while
    /// this window is open — the join dialog promises "it's in the archive's
    /// machine list", so the switcher must track settings, not snapshot them
    /// at Loaded.</summary>
    public void RefreshMachineSwitcher()
    {
        if (!IsLoaded) return; // Loaded runs the first build

        var gen = ++_switcherGen;

        // A rebuild keeps already-discovered peers (a share join is no reason
        // to re-probe the tailnet) and the current selection when its entry
        // survived the change; a removed share's selection falls back to This
        // PC rather than keeping a forgotten membership browsable.
        var knownPeers = MachineBox.Items.OfType<PeerChoice>().ToList();
        var selected = MachineBox.SelectedItem;

        MachineBox.Items.Clear();

        if (string.IsNullOrEmpty(_settings.PeerToken) && _settings.Shares.Length == 0)
        {
            MachineBox.Visibility = Visibility.Collapsed;
            return;
        }

        MachineBox.Visibility = Visibility.Visible;
        MachineBox.Items.Add("This PC");
        foreach (var peer in knownPeers) MachineBox.Items.Add(peer);

        // Shares come from settings, not discovery — list them immediately.
        object? sharesStart = null;
        if (_settings.Shares.Length > 0)
        {
            sharesStart = SwitcherDivider();
            MachineBox.Items.Add(sharesStart);
            MachineBox.Items.Add(SwitcherHeader("Team shares"));
            foreach (var share in _settings.Shares)
                MachineBox.Items.Add(new ShareChoice(share, share.Name));
        }

        MachineBox.SelectedItem =
            (selected switch
            {
                ShareChoice was => MachineBox.Items.OfType<ShareChoice>()
                    .FirstOrDefault(c => string.Equals(c.Share.BaseUrl, was.Share.BaseUrl,
                        StringComparison.OrdinalIgnoreCase)),
                PeerChoice peer when knownPeers.Contains(peer) => (object)peer,
                _ => null,
            }) ?? MachineBox.Items[0];

        // Discovery once per set of known peers: the first build probes, a
        // settings-change rebuild reuses what the probe found.
        if (string.IsNullOrEmpty(_settings.PeerToken) || knownPeers.Count > 0) return;

        _ = Task.Run(async () =>
        {
            try
            {
                var found = await PeerClient.DiscoverAsync(_settings);
                await Dispatcher.BeginInvoke(() =>
                {
                    if (gen != _switcherGen) return; // a newer rebuild owns the list

                    // Peers land above the shares section, wherever it sits.
                    var at = sharesStart is null
                        ? MachineBox.Items.Count
                        : MachineBox.Items.IndexOf(sharesStart);
                    foreach (var (info, ping) in found)
                        MachineBox.Items.Insert(at++, new PeerChoice(info,
                            $"{info.Name}  ({ping.Captures})"));
                });
            }
            catch (Exception ex)
            {
                Log.Warn($"peers: discovery failed: {ex.Message}");
            }
        });
    }

    /// <summary>The switcher's section break — a hairline the pointer can't
    /// land on. An explicit ComboBoxItem so IsEnabled=false keeps keyboard
    /// navigation stepping over it.</summary>
    private static ComboBoxItem SwitcherDivider() => new()
    {
        IsEnabled = false,
        Padding = new Thickness(4, 0, 4, 0),
        Content = new Border
        {
            Height = 1,
            Margin = new Thickness(0, 4, 0, 4),
            Background = (System.Windows.Media.Brush)Application.Current.Resources["Hairline"],
        },
    };

    private static ComboBoxItem SwitcherHeader(string text) => new()
    {
        IsEnabled = false,
        Padding = new Thickness(12, 2, 12, 2),
        Content = new TextBlock
        {
            Text = text.ToUpperInvariant(),
            FontSize = 10,
            Foreground = (System.Windows.Media.Brush)Application.Current.Resources["InkMuted"],
        },
    };

    private sealed record PeerChoice(PeerInfo Info, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record ShareChoice(ShareEntry Share, string Label)
    {
        public override string ToString() => Label;
    }

    private void OnMachineChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;

        var oldRemote = _remote;
        var oldShare = _share;
        _remote = MachineBox.SelectedItem is PeerChoice choice
            ? new PeerClient(choice.Info, _settings.PeerToken)
            : null;
        _share = MachineBox.SelectedItem is ShareChoice picked
            ? new ShareClient(picked.Share.BaseUrl, picked.Share.MemberToken, picked.Share.Name)
            : null;
        _shareMembers = null;
        oldRemote?.Dispose();
        oldShare?.Dispose();

        if (_remote is not null)
            Log.Info($"archive: switched to peer {_remote.Peer.Name} ({_remote.Peer.BaseUrl})");
        else if (_share is not null)
            Log.Info($"archive: switched to share {_share.Name} ({_share.BaseUrl})");
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
    /// One grid tile / preview subject — local, remote peer, or share item.
    /// The wrapped Shot is the single shape everything downstream consumes:
    /// for a local capture it IS the store row; for a remote or share one its
    /// Path points at the cache location and MaterializeAsync() makes that
    /// path real (idempotent, off the UI thread) before drag/copy/preview
    /// needs it.
    /// </summary>
    private sealed class Entry
    {
        public required Shot Shot { get; init; }
        public ShotDto? Dto { get; init; }
        public PeerClient? Remote { get; init; }
        public ShareItemDto? ShareItem { get; init; }
        public ShareClient? Share { get; init; }
        private readonly object _fetchGate = new();
        private Task<Shot>? _fetch;

        public bool IsRemote => Remote is not null;
        public bool IsShare => Share is not null;

        public Task<Shot> MaterializeAsync()
        {
            if (Share is null && Remote is null) return Task.FromResult(Shot);

            // Task.Run so awaits inside never capture the dispatcher
            // context — drag-out blocks on this task from the UI thread.
            // A gate, not ??=: callers arrive from both the UI thread and
            // preview workers.
            lock (_fetchGate)
            {
                // A finished FAILED fetch must not stick: shares have no live
                // poll, so a cached fault (node briefly unreachable during the
                // mouse-down prefetch) would brick this tile's drag / preview /
                // copy until the user forces a refresh. Drop it and retry.
                if (_fetch is { IsCompleted: true, IsCompletedSuccessfully: false })
                    _fetch = null;

                return _fetch ??= Share is not null
                    ? Task.Run(() => Share.EnsureLocalAsync(ShareItem!))
                    : Task.Run(() => Remote!.EnsureLocalAsync(Dto!));
            }
        }
    }

    private void Refresh()
    {
        var query = SearchBox.Text.Trim();
        var gen = ++_generation;

        if (_share is { } share)
        {
            Empty.Text = $"Loading from {share.Name}…";
            Empty.Visibility = Results.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            _ = Task.Run(async () =>
            {
                List<ShareItemDto> items;
                var members = _shareMembers;
                try
                {
                    // Roster once per share session: tiles say who shared,
                    // and the wire carries member ids, not display names.
                    members ??= (await share.MembersAsync()).ToDictionary(
                        m => m.MemberId, m => m.DisplayName, StringComparer.Ordinal);

                    items = query.Length == 0
                        ? (await share.ItemsAsync(n: PageSize)).Items
                        : await share.SearchAsync(query);
                }
                catch (Exception ex)
                {
                    Log.Warn($"share {share.Name}: query failed: {ex.Message}");
                    items = [];
                }

                await Dispatcher.BeginInvoke(() =>
                {
                    if (gen != _generation || !ReferenceEquals(_share, share)) return;

                    // Keep null on a failed roster fetch so the next refresh
                    // retries instead of showing member ids for the session.
                    if (members is not null) _shareMembers = members;
                    var entries = items.Select(d => new Entry
                    {
                        Shot = share.ToLocalShot(d, share.CachePathFor(d)),
                        ShareItem = d,
                        Share = share,
                    }).ToList();

                    Populate(entries, gen,
                        query.Length == 0
                            ? $"Nothing shared to {share.Name} yet (or it didn't answer)."
                            : $"Nothing matching \"{query}\" in {share.Name}.");
                });
            });
            return;
        }

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
                var bmp = entry switch
                {
                    { IsShare: true, Share: { } sc, ShareItem: { } si }
                        => DecodeFrozen(await sc.ThumbAsync(si.Item), decodeWidth: 0),
                    { IsRemote: true, Remote: { } remote, Dto: { } dto }
                        => DecodeFrozen(await remote.ThumbAsync(dto.Id), decodeWidth: 0),
                    _ => DecodeFile(shot.ThumbPath, decodeWidth: 448),
                };

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
        var origin = shot.Origin.Length > 0 && !entry.IsRemote && !entry.IsShare
            ? $"   ⇄ {shot.Origin}" : "";

        // Share tiles carry the traceability bits instead of origin: who
        // shared it, and how much conversation hangs off it.
        var shareTrail = "";
        if (entry.ShareItem is { } shared)
        {
            var by = SharedByName(shared);
            var comments = shared.CommentCount > 0 ? $"   💬 {shared.CommentCount}" : "";
            shareTrail = $"   {by}{comments}";
        }

        var caption = new TextBlock
        {
            Text = shot.IsVideo
                ? $"{when}   ▶ {shot.DurationText}   {dims}{origin}{shareTrail}"
                : $"{when}   {dims}{origin}{shareTrail}",
            Foreground = (System.Windows.Media.Brush)FindResource("InkMuted"),
            FontSize = 11,
            Margin = new Thickness(2, 5, 2, 1),
        };

        var panel = new StackPanel { Children = { thumb, caption } };

        panel.ToolTip = entry switch
        {
            { IsShare: true, ShareItem: { } item } =>
                $"in {entry.Share!.Name}, shared by {SharedByName(item)} — " +
                "click to preview · drag out · right-click to pull",
            { IsRemote: true } =>
                $"on {entry.Remote!.Peer.Name} — click to preview · drag out · right-click to pull",
            _ => "click to preview · drag out · right-click for more",
        };

        Point pressAt = default;
        var pressed = false;

        panel.PreviewMouseLeftButtonDown += (_, e) =>
        {
            pressed = true;
            pressAt = e.GetPosition(panel);
            // Browsed (peer or share): start the download NOW, so by the time
            // a drag crosses the threshold (or a preview opens) the file is
            // usually already local.
            if (entry.IsRemote || entry.IsShare) _ = entry.MaterializeAsync();
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
                else if (entry is { IsShare: true, ShareItem: { } dragged })
                    Log.Info($"archive: dragging share item {dragged.Item} via cache {local.Path}");
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
        if (!shot.IsVideo)
        {
            var copyText = new MenuItem { Header = "Copy text" };
            copyText.Click += (_, _) => CopyOcrText(entry);
            menu.Items.Add(copyText);
        }
        if (entry.IsRemote || entry.IsShare)
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

            // Mirrors the shelf card's share icon: local captures only —
            // pushing something you're merely browsing is a pull-then-push.
            if (_sharePush.Any)
            {
                var pushMenu = new MenuItem { Header = "Push to share" };
                foreach (var share in _sharePush.Ordered())
                {
                    var pick = share;
                    var target = new MenuItem { Header = pick.Name };
                    target.Click += (_, _) => PushToShare(shot, pick);
                    pushMenu.Items.Add(target);
                }
                menu.Items.Add(pushMenu);
            }
        }
        panel.ContextMenu = menu;

        return panel;
    }

    /// <summary>Display name for a share item's author — the roster resolves
    /// member ids; a member gone from the roster keeps their id.</summary>
    private string SharedByName(ShareItemDto item)
        => _shareMembers is { } members &&
           members.TryGetValue(item.SharedBy, out var name) && !string.IsNullOrEmpty(name)
            ? name : item.SharedBy;

    /// <summary>Tile context menu push — same background rule as the card:
    /// fire, get out of the way, report through the title flash + log.</summary>
    private void PushToShare(Shot shot, ShareEntry share)
    {
        FlashTitle($"pushing to {share.Name}…");
        _ = Task.Run(async () =>
        {
            try
            {
                var item = await _sharePush.PushAsync(shot, share);
                await Dispatcher.BeginInvoke(() => FlashTitle(item.Duplicate == true
                    ? $"already in {share.Name}"
                    : $"pushed to {share.Name}"));
            }
            catch (Exception ex)
            {
                Log.Error($"share {share.Name}: push of shot {shot.Id} failed: {ex.Message}");
                await Dispatcher.BeginInvoke(() =>
                    FlashTitle($"push to {share.Name} failed — see log"));
            }
        });
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

    /// <summary>Makes a browsed capture a first-class LOCAL one, whichever
    /// kind of endpoint it lives on. Content-hash dedupe makes a double-pull
    /// a no-op either way.</summary>
    private void Pull(Entry entry)
    {
        if (entry.IsShare) PullShare(entry);
        else if (entry.IsRemote) PullPeer(entry);
    }

    /// <summary>Peer pull: download (via the cache), copy into the archive
    /// tree, insert a row that imports the peer's OCR text + engine version
    /// from /meta, marked with its origin machine.</summary>
    private async void PullPeer(Entry entry)
    {
        if (entry is not { IsRemote: true, Remote: { } remote, Dto: { } dto }) return;
        try
        {
            var (ingested, duplicate) = await Task.Run(async () =>
            {
                var meta = await remote.MetaAsync(dto.Id);
                var cached = await remote.EnsureLocalAsync(dto);
                return IngestCached(cached, meta?.OcrText, meta?.OcrEngineVersion ?? "",
                    cached.Origin);
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

    /// <summary>Share pull: same shape, but the OCR sidecar rides the item
    /// detail fetch and the local row's origin is the SHARE's name — an item
    /// deliberately never says which of the sharer's machines it came from
    /// (docs/SHARES.md). The share keeps its copy; a pull is a copy, never a
    /// move.</summary>
    private async void PullShare(Entry entry)
    {
        if (entry is not { IsShare: true, Share: { } share, ShareItem: { } item }) return;
        try
        {
            var (ingested, duplicate) = await Task.Run(async () =>
            {
                var full = await share.ItemAsync(item.Item);
                var cached = await share.EnsureLocalAsync(item);
                return IngestCached(cached, full?.OcrText, full?.OcrEngineVersion ?? "",
                    share.Name);
            });

            Log.Info(duplicate
                ? $"archive: pull of share item {item.Item} deduplicated (already local as {ingested.Id})"
                : $"archive: pulled item {item.Item} from share {share.Name} -> {ingested.Path} (local id {ingested.Id})");
            FlashTitle(duplicate
                ? $"already on this PC ({System.IO.Path.GetFileName(ingested.Path)})"
                : $"pulled to this PC ({System.IO.Path.GetFileName(ingested.Path)})");
        }
        catch (Exception ex)
        {
            Log.Error($"archive: share pull failed: {ex.Message}");
            FlashTitle("pull failed — see log");
        }
    }

    /// <summary>The shared back half of both pulls: copy the cached file (and
    /// a recording's siblings) into the archive tree and insert the row.
    /// Worker-thread only.</summary>
    private (Shot Shot, bool Duplicate) IngestCached(
        Shot cached, string? ocrText, string ocrEngineVersion, string origin)
    {
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
            ocrText, ocrEngineVersion, origin);
        if (dup)
        {
            try { System.IO.File.Delete(dest); } catch { }
            try { System.IO.File.Delete(System.IO.Path.ChangeExtension(dest, ".gif")); } catch { }
            try { System.IO.File.Delete(dest + ".png"); } catch { }
        }
        return (shot, dup);
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
        var from = entry switch
        {
            { IsShare: true, ShareItem: { } item } =>
                $"   in {entry.Share!.Name} — {SharedByName(item)}" +
                (item.CommentCount > 0 ? $"   💬 {item.CommentCount}" : ""),
            { IsRemote: true } => $"   on {entry.Remote!.Peer.Name}",
            _ => "",
        };
        PreviewCaption.Text = shot.IsVideo
            ? $"{shot.TakenAt:MMM d, yyyy  HH:mm}   ▶ {shot.DurationText}   {shot.Width}×{shot.Height}{from}"
            : $"{shot.TakenAt:MMM d, yyyy  HH:mm}   {shot.Width}×{shot.Height}{from}";
        var browsed = entry.IsRemote || entry.IsShare;
        PreviewPullBtn.Visibility = browsed ? Visibility.Visible : Visibility.Collapsed;
        PreviewFolderBtn.Visibility = browsed ? Visibility.Collapsed : Visibility.Visible;

        // Recordings carry no OCR text; the panel only makes sense for stills.
        PreviewTextBtn.Visibility = shot.IsVideo ? Visibility.Collapsed : Visibility.Visible;
        if (shot.IsVideo) SetOcrPanelOpen(false);
        else if (_ocrPanelOpen) LoadOcrPanel(entry);

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
        SetOcrPanelOpen(false);
        _previewIndex = -1;
        SearchBox.Focus();
    }

    // ---- screen text (OCR) --------------------------------------------------

    private bool _ocrPanelOpen;
    private int _ocrLoadSeq;      // stale-load guard, same idea as _generation
    private string? _ocrRealText; // non-null only when the panel shows actual text

    private void OnPreviewText(object sender, RoutedEventArgs e)
    {
        SetOcrPanelOpen(!_ocrPanelOpen);
        if (_ocrPanelOpen) LoadOcrPanel(PreviewEntry);
    }

    private void SetOcrPanelOpen(bool open)
    {
        _ocrPanelOpen = open;
        OcrPanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        if (open) PreviewTextBtn.Foreground = (System.Windows.Media.Brush)FindResource("Accent");
        else PreviewTextBtn.ClearValue(ForegroundProperty);
    }

    private void LoadOcrPanel(Entry? entry)
    {
        if (entry is null) return;
        var seq = ++_ocrLoadSeq;
        _ocrRealText = null;
        OcrTextBox.Text = "…";

        _ = Task.Run(async () =>
        {
            var (done, text, problem) = await FetchOcrAsync(entry);
            await Dispatcher.BeginInvoke(() =>
            {
                if (seq != _ocrLoadSeq) return;
                if (problem is not null) { OcrTextBox.Text = problem; return; }
                if (!done) { OcrTextBox.Text = "No text yet — OCR is still catching up on this capture."; return; }
                if (string.IsNullOrWhiteSpace(text)) { OcrTextBox.Text = "No text found in this capture."; return; }
                _ocrRealText = text;
                OcrTextBox.Text = text;
            });
        });
    }

    private void OnOcrCopyAll(object sender, RoutedEventArgs e)
    {
        // The selectable box also holds status messages; only real text copies.
        if (_ocrRealText is null) { FlashTitle("no text to copy"); return; }
        try
        {
            _beforeClipboardWrite();
            Clipboard.SetText(_ocrRealText);
            FlashTitle("screen text copied");
        }
        catch (Exception ex)
        {
            Log.Warn($"archive: text copy failed: {ex.Message}");
            FlashTitle("copy failed — clipboard busy");
        }
    }

    /// <summary>Tile context menu: straight to the clipboard, no preview needed —
    /// the fast path for handing a screenshot's text to whatever needs it.</summary>
    private async void CopyOcrText(Entry entry)
    {
        var (done, text, problem) = await FetchOcrAsync(entry);
        if (problem is not null) { FlashTitle(problem); return; }
        if (!done) { FlashTitle("no text yet — OCR still catching up"); return; }
        if (string.IsNullOrWhiteSpace(text)) { FlashTitle("no text in this capture"); return; }
        try
        {
            _beforeClipboardWrite();
            Clipboard.SetText(text);
            FlashTitle("screen text copied");
        }
        catch (Exception ex)
        {
            Log.Warn($"archive: text copy failed: {ex.Message}");
            FlashTitle("copy failed — clipboard busy");
        }
    }

    /// <summary>One shot's OCR text, wherever it lives: the local index, or the
    /// peer's /meta (which already carries text for pull sidecars). Never
    /// throws — a peer problem comes back as a message instead.</summary>
    private async Task<(bool Done, string Text, string? Problem)> FetchOcrAsync(Entry entry)
    {
        try
        {
            if (entry is { IsShare: true, Share: { } share, ShareItem: { } item })
            {
                // The detail fetch carries the OCR sidecar (lists omit it).
                var full = await Task.Run(() => share.ItemAsync(item.Item));
                if (full is null) return (false, "", $"{share.Name} didn't answer for this item");
                return (full.OcrText is not null, full.OcrText ?? "", null);
            }

            if (entry is { IsRemote: true, Remote: { } remote, Dto: { } dto })
            {
                var meta = await Task.Run(() => remote.MetaAsync(dto.Id));
                if (meta is null) return (false, "", $"{remote.Peer.Name} didn't answer for this capture");
                return (meta.OcrText is not null, meta.OcrText ?? "", null);
            }

            var (done, text, _) = await Task.Run(() => _store.GetOcr(entry.Shot.Id));
            return (done, text ?? "", null);
        }
        catch (Exception ex)
        {
            Log.Warn($"archive: OCR fetch failed for shot {entry.Shot.Id}: {ex.Message}");
            return (false, "", "text unavailable — see log");
        }
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
        // Local rows only — a browsed entry's Path is cache, not archive.
        if (PreviewEntry is { IsRemote: false, IsShare: false } entry) Reveal(entry.Shot);
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
