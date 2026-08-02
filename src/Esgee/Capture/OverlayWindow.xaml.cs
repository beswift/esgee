using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Esgee.Interop;
using DrawingBitmap = System.Drawing.Bitmap;
using DrawingRect = System.Drawing.Rectangle;

namespace Esgee.Capture;

/// <summary>
/// The frozen-frame capture surface. The screen is grabbed *before* this window
/// appears, so what the user aims at can't animate out from under the cursor —
/// the trick that makes every good screenshot tool feel instant.
/// </summary>
public partial class OverlayWindow : Window
{
    private readonly DrawingBitmap _frame;
    private readonly DrawingRect _virtualBounds;
    private readonly List<WindowFinder.WindowRect> _windows;

    private double _scale = 1.0;
    private Point _dragStart;
    private bool _dragging;
    private bool _committed;

    /// <summary>Fires with the cropped capture and the frame-relative rect it
    /// came from (so "repeat last region" can re-shoot the same spot).</summary>
    public event Action<DrawingBitmap, DrawingRect>? Captured;
    public event Action<int>? DelayRequested;
    public event Action? Cancelled;

    public OverlayWindow(DrawingBitmap frame, DrawingRect virtualBounds)
    {
        InitializeComponent();

        _frame = frame;
        _windows = WindowFinder.Snapshot();

        _virtualBounds = virtualBounds;

        // The frozen frame as the window background.
        var hbitmap = frame.GetHbitmap();
        try
        {
            var source = Imaging.CreateBitmapSourceFromHBitmap(
                hbitmap, IntPtr.Zero, Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            Frame.Source = source;
        }
        finally
        {
            DeleteObject(hbitmap);
        }

        MouseDown += OnMouseDown;
        MouseMove += OnMouseMove;
        MouseUp += OnMouseUp;
        Loaded += (_, _) => ResetDim();
    }

    [System.Runtime.InteropServices.LibraryImport("gdi32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static partial bool DeleteObject(IntPtr handle);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // Cover the virtual desktop exactly, in device pixels. WPF's DIP-based
        // Left/Top/Width/Height are unreliable for this under per-monitor DPI;
        // SetWindowPos sidesteps the conversion entirely.
        var hwnd = new WindowInteropHelper(this).Handle;
        Win32.SetWindowPos(hwnd, IntPtr.Zero,
            _virtualBounds.X, _virtualBounds.Y, _virtualBounds.Width, _virtualBounds.Height,
            Win32.SWP_NOZORDER | Win32.SWP_SHOWWINDOW);

        _scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        Activate(); // must own focus for Esc/Enter/digits
    }

    // ---- geometry ----------------------------------------------------------

    /// <summary>DIP point in this window → device-pixel point on the frame bitmap.</summary>
    private (int X, int Y) ToFrame(Point p)
    {
        var x = (int)Math.Round(p.X * _scale);
        var y = (int)Math.Round(p.Y * _scale);
        return (Math.Clamp(x, 0, _frame.Width - 1), Math.Clamp(y, 0, _frame.Height - 1));
    }

    /// <summary>Device-pixel rect on the frame → DIP rect for drawing.</summary>
    private Rect ToDip(DrawingRect r)
        => new(r.X / _scale, r.Y / _scale, r.Width / _scale, r.Height / _scale);

    private DrawingRect SelectionRect(Point a, Point b)
    {
        var (ax, ay) = ToFrame(a);
        var (bx, by) = ToFrame(b);
        // Exclusive bottom-right: a 600px drag yields exactly 600px, matching
        // what every other capture tool reports.
        var rect = DrawingRect.FromLTRB(
            Math.Min(ax, bx), Math.Min(ay, by),
            Math.Max(ax, bx), Math.Max(ay, by));
        if (rect.Width < 1) rect.Width = 1;
        if (rect.Height < 1) rect.Height = 1;
        return rect;
    }

    private DrawingRect? HoveredWindowRect(Point p)
    {
        var (x, y) = ToFrame(p);
        // Window rects are in screen coords; the frame starts at the virtual origin.
        var hit = WindowFinder.Hit(_windows, x + _virtualBounds.X, y + _virtualBounds.Y);
        if (hit is null) return null;

        var b = hit.Value.Bounds;
        var r = new DrawingRect(b.Left - _virtualBounds.X, b.Top - _virtualBounds.Y, b.Width, b.Height);
        r.Intersect(new DrawingRect(0, 0, _frame.Width, _frame.Height));
        return r.IsEmpty ? null : r;
    }

    // ---- input -------------------------------------------------------------

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Right) { Cancel(); return; }
        if (e.ChangedButton != MouseButton.Left) return;

        _dragStart = e.GetPosition(this);
        _dragging = true;
        CaptureMouse();
        FadeHints();
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        var p = e.GetPosition(this);

        if (_dragging)
        {
            var sel = SelectionRect(_dragStart, p);
            ShowRect(sel, showGuides: false);
            return;
        }

        // Idle hover: crosshair plus window snap preview.
        GuideV.X1 = GuideV.X2 = p.X;
        GuideV.Y1 = 0; GuideV.Y2 = ActualHeight;
        GuideH.Y1 = GuideH.Y2 = p.Y;
        GuideH.X1 = 0; GuideH.X2 = ActualWidth;

        var win = HoveredWindowRect(p);
        if (win is { } r) ShowRect(r, showGuides: true);
        else { Outline.Visibility = Visibility.Collapsed; Badge.Visibility = Visibility.Collapsed; ResetDim(); }
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || !_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();

        var p = e.GetPosition(this);
        var sel = SelectionRect(_dragStart, p);

        // A sub-threshold drag is a click: capture the window under the cursor.
        if (sel.Width < 5 && sel.Height < 5)
        {
            if (HoveredWindowRect(p) is { } win) Commit(win);
            else Cancel();
            return;
        }

        Commit(sel);
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                Cancel();
                break;

            case Key.Enter or Key.Space:
                Commit(new DrawingRect(0, 0, _frame.Width, _frame.Height));
                break;

            case >= Key.D1 and <= Key.D9:
                RequestDelay(e.Key - Key.D0);
                break;

            case >= Key.NumPad1 and <= Key.NumPad9:
                RequestDelay(e.Key - Key.NumPad0);
                break;
        }
    }

    // ---- drawing -----------------------------------------------------------

    private void ShowRect(DrawingRect frameRect, bool showGuides)
    {
        var dip = ToDip(frameRect);

        Canvas.SetLeft(Outline, dip.X);
        Canvas.SetTop(Outline, dip.Y);
        Outline.Width = dip.Width;
        Outline.Height = dip.Height;
        Outline.Visibility = Visibility.Visible;

        GuideV.Visibility = GuideH.Visibility =
            showGuides ? Visibility.Visible : Visibility.Collapsed;

        BadgeText.Text = $"{frameRect.Width} × {frameRect.Height}";
        var badgeY = dip.Y > 34 ? dip.Y - 30 : dip.Y + 8;
        Canvas.SetLeft(Badge, Math.Max(4, dip.X));
        Canvas.SetTop(Badge, badgeY);
        Badge.Visibility = Visibility.Visible;

        // Punch the hole in the dimming.
        var full = new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight));
        var hole = new RectangleGeometry(dip);
        Dim.Data = new CombinedGeometry(GeometryCombineMode.Exclude, full, hole);
    }

    private void ResetDim()
        => Dim.Data = new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight));

    private void FadeHints()
    {
        if (Hints.Opacity < 1) return;
        Hints.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, TimeSpan.FromMilliseconds(150)));
    }

    // ---- outcomes ----------------------------------------------------------

    private void Commit(DrawingRect frameRect)
    {
        if (_committed) return;
        _committed = true;

        // Clone the crop BEFORE closing: the frame is disposed by the controller
        // once the overlay goes away.
        var crop = _frame.Clone(frameRect, _frame.PixelFormat);
        Close();
        Captured?.Invoke(crop, frameRect);
    }

    private void RequestDelay(int seconds)
    {
        if (_committed) return;
        _committed = true;
        Close();
        DelayRequested?.Invoke(seconds);
    }

    private void Cancel()
    {
        if (_committed) return;
        _committed = true;
        Close();
        Cancelled?.Invoke();
    }
}
