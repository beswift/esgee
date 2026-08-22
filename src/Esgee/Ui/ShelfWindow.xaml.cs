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

    private readonly ClipboardService _clipboard;

    public TimeSpan Linger { get; set; } = TimeSpan.FromSeconds(8);
    public int MaxCards { get; set; } = 6;

    // Diagnostic visibility for `esgee --check-shelf`; not part of the UI API.
    internal int CardCount => Cards.Children.Count;
    internal int ActiveCardCount => Cards.Children.OfType<ShotCard>().Count(c => !c.IsLeaving);

    /// <summary>Set by the app when it owns a store; cards grow their share
    /// icon only while this is present AND shares are configured.</summary>
    public Esgee.Shares.SharePusher? SharePush { get; set; }

    public ShelfWindow(ClipboardService clipboard)
    {
        InitializeComponent();
        _clipboard = clipboard;
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
        // Leave() removes after a 200 ms animation. A while-loop over the raw
        // child count spins forever because its Completed callback needs this
        // dispatcher. Count only active cards and retire each victim once.
        var capacity = Math.Max(1, MaxCards);
        var active = Cards.Children.OfType<ShotCard>().Where(c => !c.IsLeaving).ToList();
        var retire = Math.Max(0, active.Count - capacity + 1);
        foreach (var victim in active.Take(retire)) victim.Leave();

        var card = new ShotCard(shot, Linger, Remove, _clipboard, SharePush);
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
