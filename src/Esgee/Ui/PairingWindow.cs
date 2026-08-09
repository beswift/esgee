using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Esgee.Peers;

namespace Esgee.Ui;

/// <summary>
/// The host side of Bluetooth-style pairing: a small glass card showing a
/// large one-time PIN and a countdown. While this window is open — and only
/// then — the peer server answers POST /pair. It closes itself on success,
/// on the 5th wrong guess, or at the 2-minute mark, and closing it (any way)
/// takes /pair down with it.
/// </summary>
public sealed class PairingWindow : Window
{
    private readonly PairingSession _session;
    private readonly PeerServer _server;
    private readonly DispatcherTimer _tick;
    private readonly TextBlock _pin;
    private readonly TextBlock _headline;
    private readonly TextBlock _hint;
    private readonly TextBlock _status;
    private bool _settled;

    public PairingWindow(PairingSession session, PeerServer server)
    {
        _session = session;
        _server = server;

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = null;
        ShowInTaskbar = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Title = "esgee — pair a new machine";

        var res = Application.Current.Resources;
        var ink = (Brush)res["Ink"];
        var muted = (Brush)res["InkMuted"];

        _headline = new TextBlock
        {
            Text = "Pair a new machine",
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = ink,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var close = new Button { Content = "✕", Style = (Style)res["IconButton"] };
        close.Click += (_, _) => Close();

        var header = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(close, Dock.Right);
        header.Children.Add(close);
        header.Children.Add(_headline);

        _pin = new TextBlock
        {
            Text = $"{session.Pin[..3]} {session.Pin[3..]}",
            FontSize = 46,
            FontWeight = FontWeights.Bold,
            Foreground = ink,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 16, 0, 4),
        };
        AutomationProperties.SetAutomationId(_pin, "PairPin");

        _hint = new TextBlock
        {
            Text = "On your other machine, open the esgee tray menu → Peers → " +
                   "“Pair with another machine…” and enter this PIN.",
            FontSize = 12,
            Foreground = muted,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 300,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        _status = new TextBlock
        {
            FontSize = 12,
            Foreground = muted,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 14, 0, 0),
        };
        AutomationProperties.SetAutomationId(_status, "PairHostStatus");

        var stack = new StackPanel();
        stack.Children.Add(header);
        stack.Children.Add(_pin);
        stack.Children.Add(_hint);
        stack.Children.Add(_status);

        Content = new Border
        {
            Background = (Brush)res["Surface"],
            BorderBrush = (Brush)res["Hairline"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(24, 16, 24, 18),
            Margin = new Thickness(24), // room for the shadow
            Effect = (DropShadowEffect)res["CardLift"],
            Child = stack,
        };

        MouseLeftButtonDown += (_, _) => { try { DragMove(); } catch { } };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };

        // The window IS the /pair switch: registered on construction,
        // deregistered (and the session killed) the moment it closes.
        _server.BeginPairing(_session);
        _session.Succeeded += machine => Dispatcher.BeginInvoke(() => OnSucceeded(machine));
        _session.WrongGuess += n => Dispatcher.BeginInvoke(() => OnWrongGuess(n));
        _session.LockedOut += () => Dispatcher.BeginInvoke(OnLockedOut);
        Closed += (_, _) =>
        {
            _tick!.Stop();
            _session.Close();
            _server.EndPairing(_session);
        };

        _tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _tick.Tick += (_, _) => OnTick();
        _tick.Start();
        OnTick();
    }

    private void OnTick()
    {
        if (_settled) return;

        var left = _session.ExpiresAt - DateTimeOffset.UtcNow;
        if (left <= TimeSpan.Zero)
        {
            _status.Text = "PIN expired — close and try again.";
            SettleAndClose(TimeSpan.FromSeconds(2));
            return;
        }
        _status.Text = $"PIN expires in {(int)left.TotalMinutes}:{left.Seconds:D2}";
    }

    private void OnSucceeded(string machine)
    {
        if (_settled) return;
        _headline.Text = "Paired";
        _pin.Text = "✓";
        _hint.Text = $"{machine} can now browse and sync with this machine's archive.";
        _status.Text = "";
        SettleAndClose(TimeSpan.FromSeconds(2.5));
    }

    private void OnWrongGuess(int failures)
    {
        if (_settled || failures >= PairingSession.MaxAttempts) return;
        _status.Text = $"Wrong PIN received ({failures}/{PairingSession.MaxAttempts}) — " +
                       "PIN expires in " + Remaining();
    }

    private void OnLockedOut()
    {
        if (_settled) return;
        _headline.Text = "Pairing cancelled";
        _pin.Text = "✕";
        _hint.Text = "Too many wrong attempts. Open “Pair a new machine…” " +
                     "again for a fresh PIN.";
        _status.Text = "";
        SettleAndClose(TimeSpan.FromSeconds(3));
    }

    private string Remaining()
    {
        var left = _session.ExpiresAt - DateTimeOffset.UtcNow;
        if (left < TimeSpan.Zero) left = TimeSpan.Zero;
        return $"{(int)left.TotalMinutes}:{left.Seconds:D2}";
    }

    /// <summary>Terminal state reached: kill the session now (so /pair goes
    /// dark immediately), leave the outcome on screen briefly, then close.</summary>
    private void SettleAndClose(TimeSpan delay)
    {
        _settled = true;
        _session.Close();
        _server.EndPairing(_session);

        var t = new DispatcherTimer { Interval = delay };
        t.Tick += (_, _) => { t.Stop(); Close(); };
        t.Start();
    }
}
