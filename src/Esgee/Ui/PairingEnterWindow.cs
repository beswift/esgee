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
/// The joining side of pairing: type the 6-digit PIN shown on the other
/// machine. On submit it POSTs the PIN to every reachable candidate (online
/// tailnet nodes + manual Peers entries); the machine with an open pairing
/// window answers with the PeerToken, which the onPaired callback persists
/// and brings the peer layer up in-process — no restart, no settings editing.
/// </summary>
public sealed class PairingEnterWindow : Window
{
    private readonly Settings _settings;
    private readonly Action<PairResult> _onPaired;
    private readonly TextBox _box;
    private readonly Button _go;
    private readonly TextBlock _status;
    private bool _busy;

    public PairingEnterWindow(Settings settings, Action<PairResult> onPaired)
    {
        _settings = settings;
        _onPaired = onPaired;

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = null;
        ShowInTaskbar = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Title = "esgee — pair with another machine";

        var res = Application.Current.Resources;
        var ink = (Brush)res["Ink"];
        var muted = (Brush)res["InkMuted"];

        var headline = new TextBlock
        {
            Text = "Pair with another machine",
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
        header.Children.Add(headline);

        var hint = new TextBlock
        {
            Text = "On the other machine: tray → Peers → “Pair a new machine…”, " +
                   "then type the PIN it shows here.",
            FontSize = 12,
            Foreground = muted,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 300,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 12, 0, 12),
        };

        _box = new TextBox
        {
            FontSize = 30,
            FontWeight = FontWeights.Bold,
            MaxLength = 6,
            Width = 170,
            TextAlignment = TextAlignment.Center,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = ink,
            CaretBrush = ink,
        };
        AutomationProperties.SetAutomationId(_box, "PairPinBox");
        _box.PreviewTextInput += (_, e) => e.Handled = !e.Text.All(char.IsAsciiDigit);

        var boxChrome = new Border
        {
            Background = (Brush)res["SurfaceHover"],
            BorderBrush = (Brush)res["Hairline"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 6, 10, 6),
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = _box,
        };

        _go = MakeAccentButton("Pair");
        AutomationProperties.SetAutomationId(_go, "PairGoButton");
        _go.Margin = new Thickness(0, 14, 0, 0);
        _go.HorizontalAlignment = HorizontalAlignment.Center;
        _go.Click += async (_, _) => await SubmitAsync();

        _status = new TextBlock
        {
            FontSize = 12,
            Foreground = muted,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 300,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 12, 0, 0),
        };
        AutomationProperties.SetAutomationId(_status, "PairStatus");

        var stack = new StackPanel();
        stack.Children.Add(header);
        stack.Children.Add(hint);
        stack.Children.Add(boxChrome);
        stack.Children.Add(_go);
        stack.Children.Add(_status);

        Content = new Border
        {
            Background = (Brush)res["Surface"],
            BorderBrush = (Brush)res["Hairline"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(24, 16, 24, 18),
            Margin = new Thickness(24),
            Effect = (DropShadowEffect)res["CardLift"],
            Child = stack,
        };

        MouseLeftButtonDown += (_, e) =>
        {
            if (e.OriginalSource is not TextBox) { try { DragMove(); } catch { } }
        };
        PreviewKeyDown += async (_, e) =>
        {
            if (e.Key == Key.Escape) { Close(); e.Handled = true; }
            else if (e.Key == Key.Enter) { e.Handled = true; await SubmitAsync(); }
        };
        Loaded += (_, _) => _box.Focus();
    }

    private async Task SubmitAsync()
    {
        if (_busy) return;

        var pin = new string(_box.Text.Where(char.IsAsciiDigit).ToArray());
        if (pin.Length != 6)
        {
            _status.Text = "Enter the 6-digit PIN shown on the other machine.";
            return;
        }

        _busy = true;
        _box.IsEnabled = false;
        _go.IsEnabled = false;
        _status.Text = "Looking for machines on your tailnet…";

        try
        {
            var attempt = await Task.Run(() => PairWithAnyAsync(pin));
            if (attempt is { Outcome: PeerClient.PairOutcome.Paired, Result: { } paired })
            {
                Log.Info($"peers: paired with '{paired.Machine}' at " +
                         $"{attempt.Peer.Host}:{attempt.Peer.Port} — token adopted");
                _onPaired(paired);
                _status.Text = $"Paired with {paired.Machine} — peers are on.";
                var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                t.Tick += (_, _) => { t.Stop(); Close(); };
                t.Start();
                return; // stays busy: the window is about to close
            }

            _status.Text = attempt?.Outcome == PeerClient.PairOutcome.WrongPin
                ? "That PIN wasn’t accepted — double-check the digits and try again."
                : "No machine is offering a PIN right now. On the other machine: " +
                  "tray → Peers → “Pair a new machine…”, then retry.";
        }
        catch (Exception ex)
        {
            Log.Warn($"peers: pairing attempt failed: {ex.Message}");
            _status.Text = "Pairing failed — see esgee.log for details.";
        }

        _busy = false;
        _box.IsEnabled = true;
        _go.IsEnabled = true;
        _box.Focus();
        _box.SelectAll();
    }

    /// <summary>Posts the PIN to every candidate in parallel. At most one can
    /// accept (only one machine has a pairing window open); a 401 anywhere
    /// means a window WAS open and the PIN missed.</summary>
    private async Task<PeerClient.PairAttempt?> PairWithAnyAsync(string pin)
    {
        var candidates = PeerClient.CandidatePeers(_settings);
        Log.Info($"peers: pairing — probing {candidates.Count} candidate(s)");
        if (candidates.Count == 0) return null;

        var attempts = await Task.WhenAll(candidates.Select(c =>
            PeerClient.TryPairAsync(c, pin, TimeSpan.FromSeconds(4))));

        return attempts.FirstOrDefault(a => a.Outcome == PeerClient.PairOutcome.Paired)
            ?? attempts.FirstOrDefault(a => a.Outcome == PeerClient.PairOutcome.WrongPin)
            ?? attempts.First();
    }

    private static Button MakeAccentButton(string text)
    {
        var button = new Button
        {
            Content = text,
            Cursor = Cursors.Hand,
            Foreground = Brushes.White,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
        };

        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        border.SetValue(Border.BackgroundProperty,
            (Brush)Application.Current.Resources["Accent"]);
        border.SetValue(Border.PaddingProperty, new Thickness(22, 6, 22, 7));
        border.Name = "Bg";

        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);

        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
        var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(OpacityProperty, 0.88, "Bg"));
        template.Triggers.Add(hover);
        button.Template = template;
        return button;
    }
}
