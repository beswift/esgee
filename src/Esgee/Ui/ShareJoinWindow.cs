using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Esgee.Shares;

namespace Esgee.Ui;

/// <summary>
/// "Join a team share…": paste the invite the share's operator sent — the
/// esgee-share:// URL, or the bare code plus the node's address — pick a
/// display name, in. Redeeming mints THIS member's own token
/// (docs/SHARES.md "Joining"); the onJoined callback persists the entry.
/// Retryable outcomes (no name hint, name taken) keep the window open — the
/// server did not consume the invite for those.
/// </summary>
public sealed class ShareJoinWindow : Window
{
    /// <summary>The node's default port (docs/SHARES.md), assumed when the
    /// pasted address names no port of its own.</summary>
    private const int ShareDefaultPort = 43118;

    private readonly Action<ShareEntry> _onJoined;
    private readonly TextBox _invite;
    private readonly TextBox _host;
    private readonly TextBox _name;
    private readonly Button _go;
    private readonly TextBlock _status;
    private bool _busy;

    public ShareJoinWindow(Action<ShareEntry> onJoined)
    {
        _onJoined = onJoined;

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = null;
        ShowInTaskbar = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Title = "esgee — join a team share";

        var res = Application.Current.Resources;
        var ink = (Brush)res["Ink"];
        var muted = (Brush)res["InkMuted"];

        var headline = new TextBlock
        {
            Text = "Join a team share",
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
            Text = "Paste the invite you were sent (esgee-share://…), or its " +
                   "code plus the share's address.",
            FontSize = 12,
            Foreground = muted,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 320,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 12, 0, 12),
        };

        _invite = MakeBox(ink, "ShareInviteBox");
        _host = MakeBox(ink, "ShareHostBox");
        _name = MakeBox(ink, "ShareNameBox");

        // The address row only matters for bare codes; a full URL fills it.
        _invite.TextChanged += (_, _) =>
        {
            if (ShareClient.ParseInviteUrl(_invite.Text) is { } parsed)
            {
                _host.Text = parsed.BaseUrl;
                _host.IsEnabled = false;
            }
            else
            {
                _host.IsEnabled = true;
            }
        };

        var stack = new StackPanel();
        stack.Children.Add(header);
        stack.Children.Add(hint);
        stack.Children.Add(Labeled("Invite link or code", _invite, muted));
        stack.Children.Add(Labeled("Share address (host:port)", _host, muted));
        stack.Children.Add(Labeled("Your name, as teammates will see it", _name, muted));

        _go = MakeAccentButton("Join");
        AutomationProperties.SetAutomationId(_go, "ShareJoinGoButton");
        _go.Margin = new Thickness(0, 14, 0, 0);
        _go.HorizontalAlignment = HorizontalAlignment.Center;
        _go.Click += async (_, _) => await SubmitAsync();
        stack.Children.Add(_go);

        _status = new TextBlock
        {
            FontSize = 12,
            Foreground = muted,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 320,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 12, 0, 0),
        };
        AutomationProperties.SetAutomationId(_status, "ShareJoinStatus");
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
        Loaded += (_, _) => _invite.Focus();
    }

    private async Task SubmitAsync()
    {
        if (_busy) return;

        // A full invite URL carries the address; a bare code needs the box.
        string baseUrl, code;
        if (ShareClient.ParseInviteUrl(_invite.Text) is { } parsed)
        {
            (baseUrl, code) = parsed;
        }
        else
        {
            code = _invite.Text.Trim();
            if (code.Length == 0)
            {
                _status.Text = "Paste the invite link or code first.";
                return;
            }
            var addr = Peers.PeerClient.ToBaseUrl(_host.Text, ShareDefaultPort);
            if (_host.Text.Trim().Length == 0 || addr is null)
            {
                _status.Text = "That code needs the share's address too — " +
                               "host:port, from whoever invited you.";
                return;
            }
            baseUrl = addr;
        }

        var displayName = _name.Text.Trim();

        _busy = true;
        SetBusy(true);
        _status.Text = "Joining…";

        try
        {
            var attempt = await ShareClient.JoinAsync(baseUrl, code, displayName);
            if (attempt is { Status: ShareJoinStatus.Joined, Result: { } joined })
            {
                // The share names itself (GET /share); the URL is the fallback
                // when that fetch fails — the entry still works either way.
                string? shareName = null;
                try
                {
                    using var client = new ShareClient(baseUrl, joined.Token);
                    shareName = (await client.InfoAsync())?.Name;
                }
                catch { /* name is cosmetic; the join already succeeded */ }

                var entry = new ShareEntry
                {
                    Name = string.IsNullOrWhiteSpace(shareName)
                        ? new Uri(baseUrl + "/").Authority : shareName!,
                    BaseUrl = baseUrl,
                    MemberToken = joined.Token,
                    MemberId = joined.MemberId,
                };
                _onJoined(entry);

                _status.Text = $"Joined “{entry.Name}” — it's in the archive's " +
                               "machine list and on every card's share icon.";
                var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                t.Tick += (_, _) => { t.Stop(); Close(); };
                t.Start();
                return; // stays busy: the window is about to close
            }

            _status.Text = attempt.Status switch
            {
                // Neither of these consumed the invite — retry with a name.
                ShareJoinStatus.NeedDisplayName =>
                    "This invite has no name hint — enter the name teammates should see.",
                ShareJoinStatus.NameTaken =>
                    "Someone on this share already goes by that name — pick another " +
                    "(the invite is still good).",
                ShareJoinStatus.BadInvite =>
                    "That invite wasn't accepted — it may be spent or expired. " +
                    "Ask the operator for a fresh one.",
                _ =>
                    "Can't reach the share. Check Tailscale is up and the address " +
                    "is right, then try again.",
            };
        }
        catch (Exception ex)
        {
            Log.Warn($"shares: join attempt failed: {ex.Message}");
            _status.Text = "Joining failed — see esgee.log for details.";
        }

        _busy = false;
        SetBusy(false);
    }

    private void SetBusy(bool busy)
    {
        _invite.IsEnabled = !busy;
        _host.IsEnabled = !busy && ShareClient.ParseInviteUrl(_invite.Text) is null;
        _name.IsEnabled = !busy;
        _go.IsEnabled = !busy;
    }

    private static TextBox MakeBox(Brush ink, string automationId)
    {
        var box = new TextBox
        {
            FontSize = 13,
            Width = 320,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = ink,
            CaretBrush = ink,
        };
        AutomationProperties.SetAutomationId(box, automationId);
        return box;
    }

    private static UIElement Labeled(string label, TextBox box, Brush muted)
    {
        var res = Application.Current.Resources;
        var stack = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        stack.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 11,
            Foreground = muted,
            Margin = new Thickness(2, 0, 0, 3),
        });
        stack.Children.Add(new Border
        {
            Background = (Brush)res["SurfaceHover"],
            BorderBrush = (Brush)res["Hairline"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 6, 10, 6),
            Child = box,
        });
        return stack;
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
