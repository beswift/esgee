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

    public ArchiveWindow(ShotStore store, Action beforeClipboardWrite)
    {
        InitializeComponent();

        _store = store;
        _beforeClipboardWrite = beforeClipboardWrite;

        // Search-as-you-type, but not query-per-keystroke.
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _debounce.Tick += (_, _) => { _debounce.Stop(); Refresh(); };

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
            if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
            {
                SearchBox.Focus();
                SearchBox.SelectAll();
            }
        };

        Loaded += (_, _) => { Refresh(); SearchBox.Focus(); };
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

    private void Refresh()
    {
        var query = SearchBox.Text.Trim();

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

        panel.ToolTip = "drag out · double-click to copy · right-click for more";

        Point pressAt = default;
        var pressed = false;

        panel.PreviewMouseLeftButtonDown += (_, e) =>
        {
            pressed = true;
            pressAt = e.GetPosition(panel);
            if (e.ClickCount == 2)
            {
                pressed = false;
                Copy(shot);
            }
        };
        panel.PreviewMouseMove += (_, e) =>
        {
            if (!pressed || e.LeftButton != MouseButtonState.Pressed) return;
            var p = e.GetPosition(panel);
            if (Math.Abs(p.X - pressAt.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(p.Y - pressAt.Y) < SystemParameters.MinimumVerticalDragDistance)
                return;

            pressed = false;
            try
            {
                DragDrop.DoDragDrop(panel, DragSource.BuildDataObject(shot), DragDropEffects.Copy);
            }
            catch (Exception ex)
            {
                Log.Error($"archive drag failed: {ex.Message}");
            }
        };
        panel.PreviewMouseLeftButtonUp += (_, _) => pressed = false;

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
