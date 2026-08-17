using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Esgee.Interop;
using Esgee.Shares;
using Esgee.Store;

namespace Esgee.Ui;

public partial class ShotCard : UserControl
{
    private const double TimerWidth = 184;

    private readonly Shot _shot;
    private readonly Action<ShotCard> _onGone;
    private readonly Action _beforeClipboardWrite;

    private Storyboard? _countdown;
    private Point _pressAt;
    private bool _pressed;
    private bool _pinned;
    private bool _leaving;

    public Shot Shot => _shot;

    public ShotCard(Shot shot, TimeSpan linger, Action<ShotCard> onGone,
        Action beforeClipboardWrite, SharePusher? sharePush = null)
    {
        InitializeComponent();

        _shot = shot;
        _onGone = onGone;
        _beforeClipboardWrite = beforeClipboardWrite;

        // Decode off the UI thread so the card's slide-in never stutters — an
        // ultrawide fullscreen PNG takes long enough to drop animation frames.
        var thumbPath = shot.ThumbPath;
        _ = Task.Run(() =>
        {
            var bmp = LoadThumb(thumbPath);
            if (bmp is not null) Dispatcher.BeginInvoke(() => Thumb.Source = bmp);
        });
        if (shot.IsVideo)
        {
            VideoBadge.Visibility = Visibility.Visible;
            VideoBadgeText.Text = $"▶ {shot.DurationText}{(shot.GifPath is not null ? "  GIF" : "")}";
        }

        CopyBtn.Click   += (_, _) => Copy();
        FolderBtn.Click += (_, _) => Reveal();
        PinBtn.Click    += (_, _) => TogglePin();
        CloseBtn.Click  += (_, _) => Leave();

        // Share icon only exists once a share is configured — the default
        // shelf renders exactly as it did before shares existed.
        if (sharePush is { Any: true })
        {
            ShareBtn.Visibility = Visibility.Visible;
            ShareBtn.Click += (_, _) => OnShareClick(sharePush);
        }

        MouseEnter += (_, _) => { Fade(Chrome, 1, 120); _countdown?.Pause(this); };
        MouseLeave += (_, _) => { Fade(Chrome, 0, 160); if (!_pinned) _countdown?.Resume(this); };

        PreviewMouseLeftButtonDown += OnPress;
        PreviewMouseLeftButtonUp   += OnRelease;
        PreviewMouseMove           += OnMove;
        MouseRightButtonUp         += (_, _) => Leave();

        Loaded += (_, _) => { PlayEnter(); StartCountdown(linger); };
    }

    /// <summary>Decodes at roughly 2x display width — enough for a crisp card on a
    /// HiDPI panel, but nowhere near the cost of decoding a full ultrawide grab.
    /// Runs on a worker thread — StreamSource (not UriSource) keeps the decode
    /// free of any dispatcher affinity, and the frozen result crosses threads.</summary>
    private static BitmapSource? LoadThumb(string path)
    {
        try
        {
            using var fs = System.IO.File.OpenRead(path);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.StreamSource = fs;
            bmp.DecodePixelWidth = 392;
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch (Exception ex)
        {
            Log.Warn($"thumbnail decode failed for {path}: {ex.Message}");
            return null;
        }
    }

    // ---- pointer -----------------------------------------------------------

    private void OnPress(object sender, MouseButtonEventArgs e)
    {
        // Let the hover toolbar handle its own clicks.
        if (e.OriginalSource is DependencyObject d && IsChrome(d)) return;
        _pressed = true;
        _pressAt = e.GetPosition(this);
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (!_pressed || e.LeftButton != MouseButtonState.Pressed) return;

        var now = e.GetPosition(this);
        if (Math.Abs(now.X - _pressAt.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(now.Y - _pressAt.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        _pressed = false;
        BeginDragOut();
    }

    private void OnRelease(object sender, MouseButtonEventArgs e)
    {
        if (!_pressed) return;
        if (e.OriginalSource is DependencyObject d && IsChrome(d)) return;
        _pressed = false;
        Copy(); // A plain click means "put it back on the clipboard".
    }

    private static bool IsChrome(DependencyObject d)
    {
        for (var node = d; node is not null; node = VisualTreeHelperParent(node))
            if (node is Button) return true;
        return false;
    }

    private static DependencyObject? VisualTreeHelperParent(DependencyObject d)
        => d is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
            ? System.Windows.Media.VisualTreeHelper.GetParent(d)
            : null;

    private void BeginDragOut()
    {
        // DoDragDrop pumps its own modal loop; the countdown must not expire
        // and yank the card out from under an in-flight drag.
        _countdown?.Pause(this);
        try
        {
            var data = DragSource.BuildDataObject(_shot);
            var effect = DragDrop.DoDragDrop(this, data, DragDropEffects.Copy);

            // Dropped somewhere real — its job is done, get it off the shelf.
            if (effect != DragDropEffects.None) { Leave(); return; }
        }
        catch (Exception ex)
        {
            Log.Error($"drag-out failed: {ex.Message}");
        }

        if (!_pinned) _countdown?.Resume(this);
    }

    // ---- actions -----------------------------------------------------------

    private void Copy()
    {
        try
        {
            _beforeClipboardWrite(); // suppress our own clipboard echo
            Clipboard.SetDataObject(DragSource.BuildDataObject(_shot), copy: true);
            FlashOnce();
        }
        catch (Exception ex)
        {
            Log.Error($"copy failed: {ex.Message}");
        }
    }

    private void Reveal()
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_shot.Path}\"")
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Log.Error($"reveal failed: {ex.Message}");
        }
    }

    private void TogglePin()
    {
        _pinned = !_pinned;
        if (_pinned)
        {
            _countdown?.Stop(this);
            TimerTrack.Visibility = Visibility.Collapsed;
            PinBtn.Foreground = (System.Windows.Media.Brush)FindResource("Accent");
        }
        else
        {
            TimerTrack.Visibility = Visibility.Visible;
            PinBtn.Foreground = (System.Windows.Media.Brush)FindResource("Ink");
            StartCountdown(TimeSpan.FromSeconds(8));
        }
    }

    // ---- share -------------------------------------------------------------

    /// <summary>One share: click pushes. Several: a tiny menu, last-used
    /// first — one extra click, still level with Slack's paste-and-enter.</summary>
    private void OnShareClick(SharePusher push)
    {
        var shares = push.Ordered();
        if (shares.Count == 0)
        {
            // The icon was wired when a share existed, but the last one was
            // removed from the tray while this card sat on the shelf. Retire
            // the stale icon instead of flashing an empty menu.
            ShareBtn.Visibility = Visibility.Collapsed;
            Log.Info("shares: card share icon clicked after the last share was removed; hiding it");
            return;
        }
        if (shares.Count == 1)
        {
            PushTo(push, shares[0]);
            return;
        }

        var menu = new ContextMenu
        {
            PlacementTarget = ShareBtn,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Top,
        };
        foreach (var share in shares)
        {
            var pick = share;
            var item = new MenuItem { Header = pick.Name };
            item.Click += (_, _) => PushTo(push, pick);
            menu.Items.Add(item);
        }
        // The pointer wanders into the popup, which counts as leaving the
        // card — don't let the countdown yank the card out mid-choice.
        menu.Opened += (_, _) => _countdown?.Pause(this);
        menu.Closed += (_, _) => { if (!_pinned && !IsMouseOver) _countdown?.Resume(this); };
        menu.IsOpen = true;
    }

    /// <summary>Fires the push and gets out of the way — the capture pipeline
    /// and the card's own lifecycle never wait on a share (same rule as
    /// SyncQueue). The badge reports the outcome if the card is still on the
    /// shelf when it lands; the log always has it either way.</summary>
    private void PushTo(SharePusher push, ShareEntry share)
    {
        ShareBadgeText.Text = $"→ {share.Name}…";
        ShareBadge.ToolTip = null;
        ShareBadge.Visibility = Visibility.Visible;

        _ = Task.Run(async () =>
        {
            try
            {
                var item = await push.PushAsync(_shot, share);
                await Dispatcher.BeginInvoke(() =>
                {
                    ShareBadgeText.Text = item.Duplicate == true
                        ? $"✓ {share.Name} — already there"
                        : $"✓ {share.Name}";
                    FlashOnce();
                });
            }
            catch (Exception ex)
            {
                Log.Error($"share {share.Name}: push of shot {_shot.Id} failed: {ex.Message}");
                await Dispatcher.BeginInvoke(() =>
                {
                    ShareBadgeText.Text = $"✕ {share.Name}";
                    ShareBadge.ToolTip = $"Push failed: {ex.Message}";
                });
            }
        });
    }

    // ---- animation ---------------------------------------------------------

    private void PlayEnter()
    {
        Slide.X = 44;
        Scale.ScaleX = Scale.ScaleY = 0.94;
        Opacity = 0;

        var ease = (IEasingFunction)FindResource("EaseOut");
        Slide.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty,
            new DoubleAnimation(44, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
        Scale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty,
            new DoubleAnimation(0.94, 1, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
        Scale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty,
            new DoubleAnimation(0.94, 1, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
        BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
    }

    private void StartCountdown(TimeSpan linger)
    {
        var shrink = new DoubleAnimation(TimerWidth, 0, linger);
        Storyboard.SetTarget(shrink, TimerTrack);
        Storyboard.SetTargetProperty(shrink, new PropertyPath(WidthProperty));

        _countdown = new Storyboard();
        _countdown.Children.Add(shrink);
        _countdown.Completed += (_, _) => Leave();
        _countdown.Begin(this, isControllable: true);

        if (IsMouseOver) _countdown.Pause(this);
    }

    private void FlashOnce()
    {
        var flash = new DoubleAnimationUsingKeyFrames();
        flash.KeyFrames.Add(new LinearDoubleKeyFrame(0.55, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(0))));
        flash.KeyFrames.Add(new LinearDoubleKeyFrame(0.0,  KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(340))));
        Flash.BeginAnimation(OpacityProperty, flash);
    }

    /// <summary>Collapses the card's height as it fades so the cards below it
    /// glide up instead of snapping.</summary>
    public void Leave()
    {
        if (_leaving) return;
        _leaving = true;

        _countdown?.Stop(this);
        Height = ActualHeight;

        var ease = (IEasingFunction)FindResource("EaseOut");
        var dur = TimeSpan.FromMilliseconds(200);

        Slide.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty,
            new DoubleAnimation(0, 36, dur) { EasingFunction = ease });
        BeginAnimation(OpacityProperty, new DoubleAnimation(Opacity, 0, dur));

        var collapse = new DoubleAnimation(ActualHeight, 0, dur) { EasingFunction = ease };
        collapse.Completed += (_, _) => _onGone(this);
        BeginAnimation(HeightProperty, collapse);
    }

    private static void Fade(UIElement el, double to, int ms)
        => el.BeginAnimation(OpacityProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)));
}
