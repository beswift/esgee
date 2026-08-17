using System.Windows;
using System.Windows.Interop;
using Esgee.Interop;
using Esgee.Store;

namespace Esgee.Ui;

/// <summary>
/// The corner shelf. Exists so captures stop competing for the clipboard's
/// single slot: several can sit here at once, each independently draggable.
/// </summary>
public partial class ShelfWindow : Window
{
    private const int EdgeGap = 18;

    private readonly Action _beforeClipboardWrite;

    public TimeSpan Linger { get; set; } = TimeSpan.FromSeconds(8);
    public int MaxCards { get; set; } = 6;

    /// <summary>Set by the app when it owns a store; cards grow their share
    /// icon only while this is present AND shares are configured.</summary>
    public Esgee.Shares.SharePusher? SharePush { get; set; }

    public ShelfWindow(Action beforeClipboardWrite)
    {
        InitializeComponent();
        _beforeClipboardWrite = beforeClipboardWrite;
        Closed += (_, _) => Log.Warn("shelf window CLOSED (hwnd destroyed)");
        Anchor();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // Never take focus. You should be able to keep typing in Claude Code
        // while shots land behind you.
        Win32.MakeNoActivate(new WindowInteropHelper(this).Handle);
    }

    /// <summary>Bottom-right of the working area, clear of the taskbar.</summary>
    private void Anchor()
    {
        var wa = SystemParameters.WorkArea;
        Left = wa.Right - Width - EdgeGap;
        Top = wa.Bottom - Height - EdgeGap;
    }

    public void Push(Shot shot)
    {
        // Oldest goes first once the shelf is full — the newest capture is
        // almost always the one being reached for.
        while (Cards.Children.Count >= MaxCards)
            ((ShotCard)Cards.Children[0]).Leave();

        var card = new ShotCard(shot, Linger, Remove, _beforeClipboardWrite, SharePush);
        Cards.Children.Add(card); // newest sits closest to the corner

        Anchor();
        if (!IsVisible) Show();
    }

    private void Remove(ShotCard card)
    {
        Cards.Children.Remove(card);

        // An empty transparent window still costs compositor work; hide it.
        if (Cards.Children.Count == 0) Hide();
    }

    public void ClearAll()
    {
        foreach (var card in Cards.Children.OfType<ShotCard>().ToList())
            card.Leave();
    }

    /// <summary>Tray exit needs the process to actually die, so real closes are
    /// routed through here while stray closes just hide.</summary>
    public bool AllowClose { get; set; }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!AllowClose) { e.Cancel = true; Hide(); }
        base.OnClosing(e);
    }
}
