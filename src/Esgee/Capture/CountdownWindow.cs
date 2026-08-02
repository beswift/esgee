using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Esgee.Interop;

namespace Esgee.Capture;

/// <summary>
/// The delay-capture countdown: a small click-through pill top-center, so the
/// user can see the fuse burning while they arm the menu/tooltip/hover state
/// they're trying to photograph.
/// </summary>
public sealed class CountdownWindow : Window
{
    private readonly TextBlock _number;

    public CountdownWindow()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = null;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;

        _number = new TextBlock
        {
            FontSize = 34,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)Application.Current.Resources["Ink"],
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        Content = new Border
        {
            Background = (Brush)Application.Current.Resources["Surface"],
            BorderBrush = (Brush)Application.Current.Resources["Hairline"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(26, 10, 26, 12),
            Child = _number,
        };

        Loaded += (_, _) =>
        {
            var wa = SystemParameters.WorkArea;
            Left = wa.Left + (wa.Width - ActualWidth) / 2;
            Top = wa.Top + 24;
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Win32.MakeGhost(new WindowInteropHelper(this).Handle);
    }

    public void SetRemaining(int seconds) => _number.Text = seconds.ToString();
}
