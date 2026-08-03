using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Esgee.Interop;
using Esgee.Store;

namespace Esgee.Ui;

/// <summary>
/// The payoff of the OCR index: type words that were on screen weeks ago, get
/// the screenshot back, drag it straight out as a file.
/// </summary>
public partial class ArchiveWindow : Window
{
    private const int PageSize = 200;

    private readonly ShotStore _store;
    private readonly Action _beforeClipboardWrite;
    private readonly DispatcherTimer _debounce;
    private readonly DispatcherTimer _livePoll;
    private string _lastToken = "";
    private bool _dragging;

    public ArchiveWindow(ShotStore store, Action beforeClipboardWrite)
    {
        InitializeComponent();

        _store = store;
        _beforeClipboardWrite = beforeClipboardWrite;

        // Search-as-you-type, but not query-per-keystroke.
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _debounce.Tick += (_, _) => { _debounce.Stop(); Refresh(); };

        // Live refresh. This window is often a SEPARATE process from the
        // resident app doing the capturing (taskbar pin launches --archive), so
        // no in-memory event can reach it — instead poll the index for a cheap
        // change token (WAL read, sub-ms) and refresh when it moves. Also picks
        // up OCR completions, so an open search gains matches as text lands.
        _livePoll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        _livePoll.Tick += (_, _) =>
        {
            if (!IsVisible || _dragging || _debounce.IsEnabled) return;
            try
            {
                var token = _store.ChangeToken();
                if (token == _lastToken) return;
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

        Loaded += (_, _) => { Refresh(); SearchBox.Focus(); };
        Closed += (_, _) => { _livePoll.Stop(); _debounce.Stop(); };
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

    // Bumped on every refresh so in-flight thumbnail decodes from a superseded
    // search can't paint into the new result set.
    private int _generation;

    // Shots behind the current tiles. Refresh() assigns a NEW list each time,
    // so a preview that grabbed the old reference keeps a stable snapshot to
    // navigate even if a live-poll refresh replaces the grid underneath it.
    private List<Shot> _currentShots = [];
    private List<Shot> _previewShots = [];
    private int _previewIndex = -1;

    private void Refresh()
    {
        var query = SearchBox.Text.Trim();

        // Any refresh observes the current index state; keep the poll's token
        // in step so it doesn't immediately re-refresh over us.
        try { _lastToken = _store.ChangeToken(); } catch { }

        List<Shot> shots;
        try
        {
            shots = query.Length == 0 ? _store.Recent(PageSize) : _store.Search(Fts(query), PageSize);
        }
        catch (Exception ex)
        {
            // An unbalanced quote in an FTS query throws; treat as no results
            // rather than a crash while the user is mid-keystroke.
            Log.Warn($"archive query failed: {ex.Message}");
            shots = [];
        }

        var gen = ++_generation;
        _currentShots = shots;
        Results.Items.Clear();
        foreach (var shot in shots)
            Results.Items.Add(BuildTile(shot, gen));

        Empty.Text = query.Length == 0
            ? "No captures yet — take one with the hotkey."
            : $"Nothing matching \"{query}\".";
        Empty.Visibility = shots.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Quotes each term so user text can't hit FTS5 operator syntax
    /// (AND/OR/NEAR, dashes, colons) by accident.</summary>
    private static string Fts(string query)
        => string.Join(" ", query
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(t => $"\"{t.Replace("\"", "\"\"")}\"*"));

    private UIElement BuildTile(Shot shot, int gen)
    {
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
        var path = shot.ThumbPath;
        _ = Task.Run(() =>
        {
            try
            {
                using var fs = System.IO.File.OpenRead(path);
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.StreamSource = fs;
                bmp.DecodePixelWidth = 448;
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                bmp.Freeze();

                Dispatcher.BeginInvoke(() =>
                {
                    if (gen == _generation) thumb.Source = bmp;
                });
            }
            catch (Exception ex)
            {
                Log.Warn($"archive thumb failed for {path}: {ex.Message}");
            }
        });

        var caption = new TextBlock
        {
            Text = shot.IsVideo
                ? $"{shot.TakenAt:MMM d, HH:mm}   ▶ {shot.DurationText}   {shot.Width}×{shot.Height}"
                : $"{shot.TakenAt:MMM d, HH:mm}   {shot.Width}×{shot.Height}",
            Foreground = (System.Windows.Media.Brush)FindResource("InkMuted"),
            FontSize = 11,
            Margin = new Thickness(2, 5, 2, 1),
        };

        var panel = new StackPanel { Children = { thumb, caption } };

        panel.ToolTip = "click to preview · drag out · right-click for more";

        Point pressAt = default;
        var pressed = false;

        panel.PreviewMouseLeftButtonDown += (_, e) =>
        {
            pressed = true;
            pressAt = e.GetPosition(panel);
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
                DragDrop.DoDragDrop(panel, DragSource.BuildDataObject(shot), DragDropEffects.Copy);
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
            if (pressed) OpenPreview(shot);
            pressed = false;
        };

        var menu = new ContextMenu();
        var copy = new MenuItem { Header = "Copy to clipboard" };
        copy.Click += (_, _) => Copy(shot);
        var reveal = new MenuItem { Header = "Show in folder" };
        reveal.Click += (_, _) => Reveal(shot);
        menu.Items.Add(copy);
        menu.Items.Add(reveal);
        panel.ContextMenu = menu;

        return panel;
    }

    // ---- lightbox preview ---------------------------------------------------

    private Shot? PreviewShot =>
        _previewIndex >= 0 && _previewIndex < _previewShots.Count
            ? _previewShots[_previewIndex] : null;

    private void OpenPreview(Shot shot)
    {
        _previewShots = _currentShots;
        _previewIndex = _previewShots.FindIndex(s => s.Id == shot.Id);
        if (_previewIndex < 0) { _previewShots = [shot]; _previewIndex = 0; }

        PreviewLayer.Visibility = Visibility.Visible;
        ShowPreviewContent(shot);
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

    private void ShowPreviewContent(Shot shot)
    {
        PreviewCaption.Text = shot.IsVideo
            ? $"{shot.TakenAt:MMM d, yyyy  HH:mm}   ▶ {shot.DurationText}   {shot.Width}×{shot.Height}"
            : $"{shot.TakenAt:MMM d, yyyy  HH:mm}   {shot.Width}×{shot.Height}";

        if (shot.IsVideo)
        {
            // Play the actual clip — muted loop; a frozen thumbnail would be a
            // letdown for the one media type whose point is motion.
            PreviewImage.Visibility = Visibility.Collapsed;
            PreviewImage.Source = null;
            PreviewVideo.Visibility = Visibility.Visible;
            try
            {
                PreviewVideo.Source = new Uri(shot.Path);
                PreviewVideo.Position = TimeSpan.Zero;
                PreviewVideo.Play();
            }
            catch (Exception ex)
            {
                Log.Warn($"preview video failed for {shot.Path}: {ex.Message}");
            }
            return;
        }

        PreviewVideo.Stop();
        PreviewVideo.Source = null; // release the file handle
        PreviewVideo.Visibility = Visibility.Collapsed;
        PreviewImage.Visibility = Visibility.Visible;

        // Full-quality decode, off the UI thread; guard against the user having
        // stepped on before a slow decode lands.
        var expected = shot.Id;
        var path = shot.Path;
        _ = Task.Run(() =>
        {
            try
            {
                using var fs = System.IO.File.OpenRead(path);
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.StreamSource = fs;
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                bmp.Freeze();
                Dispatcher.BeginInvoke(() =>
                {
                    if (PreviewShot?.Id == expected) PreviewImage.Source = bmp;
                });
            }
            catch (Exception ex)
            {
                Log.Warn($"preview decode failed for {path}: {ex.Message}");
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
        if (PreviewShot is { } shot) Copy(shot);
    }

    private void OnPreviewReveal(object sender, RoutedEventArgs e)
    {
        if (PreviewShot is { } shot) Reveal(shot);
    }

    private void OnPreviewVideoEnded(object sender, RoutedEventArgs e)
    {
        PreviewVideo.Position = TimeSpan.Zero;
        PreviewVideo.Play();
    }

    private void Copy(Shot shot)
    {
        try
        {
            _beforeClipboardWrite();
            Clipboard.SetDataObject(DragSource.BuildDataObject(shot), copy: true);
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
