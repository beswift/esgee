using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Esgee.Interop;
using DrawingRect = System.Drawing.Rectangle;

namespace Esgee.Capture;

/// <summary>
/// The while-recording pill: pulsing red dot + elapsed clock + stop square.
/// NoActivate (clicking it must not steal focus from what's being recorded) but
/// NOT click-through — the click IS the stop button. Excluded from screen
/// capture via WDA_EXCLUDEFROMCAPTURE so it never appears in the recording.
/// </summary>
public sealed class RecordingIndicatorWindow : Window
{
    private readonly TextBlock _elapsed;
    private readonly DispatcherTimer _tick;
    private readonly Func<TimeSpan> _clock;
    private readonly DrawingRect _region;

    /// <summary>Raised when the pill is clicked.</summary>
    public event Action? StopRequested;

    public RecordingIndicatorWindow(DrawingRect recordedRegion, Func<TimeSpan> clock)
    {
        _region = recordedRegion;
        _clock = clock;

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = null;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        Cursor = Cursors.Hand;

        var dot = new Ellipse { Width = 10, Height = 10, Fill = Brushes.OrangeRed, VerticalAlignment = VerticalAlignment.Center };
        var pulse = new DoubleAnimation(1.0, 0.35, TimeSpan.FromMilliseconds(700))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
        };
        dot.BeginAnimation(OpacityProperty, pulse);

        _elapsed = new TextBlock
        {
            Text = "0:00",
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)Application.Current.Resources["Ink"],
            Margin = new Thickness(8, 0, 10, 1),
            VerticalAlignment = VerticalAlignment.Center,
        };

        var stopGlyph = new System.Windows.Shapes.Rectangle
        {
            Width = 10,
            Height = 10,
            RadiusX = 2,
            RadiusY = 2,
            Fill = (Brush)Application.Current.Resources["Ink"],
            VerticalAlignment = VerticalAlignment.Center,
        };

        Content = new Border
        {
            Background = (Brush)Application.Current.Resources["Surface"],
            BorderBrush = (Brush)Application.Current.Resources["Hairline"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(13, 7, 13, 8),
            ToolTip = "Recording — click to stop",
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children = { dot, _elapsed, stopGlyph },
            },
        };

        MouseLeftButtonUp += (_, _) => StopRequested?.Invoke();

        _tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _tick.Tick += (_, _) =>
        {
            var t = _clock();
            _elapsed.Text = t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
        };
        _tick.Start();

        Loaded += (_, _) => Place();
        Closed += (_, _) => _tick.Stop();
    }

    /// <summary>Prefers hovering just above the recorded region (clear of the
    /// pixels being captured — belt to the display-affinity braces), else just
    /// below it, else top-center of the work area for fullscreen recordings.</summary>
    private void Place()
    {
        var wa = SystemParameters.WorkArea;
        var centerOverRegion = _region.X + (_region.Width - ActualWidth) / 2;
        var clampedLeft = Math.Clamp(centerOverRegion, wa.Left + 8, wa.Right - ActualWidth - 8);

        if (_region.Y - ActualHeight - 12 >= wa.Top)
        {
            Left = clampedLeft;
            Top = _region.Y - ActualHeight - 12;
        }
        else if (_region.Bottom + ActualHeight + 12 <= wa.Bottom)
        {
            Left = clampedLeft;
            Top = _region.Bottom + 12;
        }
        else
        {
            Left = wa.Left + (wa.Width - ActualWidth) / 2;
            Top = wa.Top + 24;
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        Win32.MakeNoActivate(hwnd);
        if (!Win32.SetWindowDisplayAffinity(hwnd, Win32.WDA_EXCLUDEFROMCAPTURE))
            Log.Warn("recording pill: SetWindowDisplayAffinity failed; pill may appear in the recording");
    }
}
