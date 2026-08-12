using System.Drawing;

namespace ClaudeScord;

/// The frame the GIF, sticker and emoji pickers share.
///
/// In the live client these are not three popups — they are one 498x486 "expression picker" with a
/// tab row across the top, so switching between them keeps the panel exactly where it is. Measured
/// off it: the panel is --background-surface-high (#393a41) at radius 8, the tab row is 30 tall and
/// inset 16 from the top, the content starts at 58, and the selected tab is a #97979f-at-16% pill
/// at radius 8 in 14px/600.
///
/// ClaudeScord keeps three controls behind it — swapping tabs closes one popup and opens the next
/// at the same anchor, which looks the same from the outside and avoids merging three unrelated
/// content models into one class. `Open` is the hook the composer fills in, since it is the only
/// thing that knows how to construct each of them.
static class PickerChrome
{
    public enum Tab { Gifs, Stickers, Emoji }

    public const int W = 498, H = 486, TabTop = 16, TabH = 30, ContentTop = 58, PadX = 16, Gap = 8;

    public static Size Size => new(Ui.S(W), Ui.S(H));

    /// Set by the composer: opens the requested tab at the same anchor the current one uses.
    public static Action<Tab>? Open;

    static readonly (Tab T, string Label)[] Order =
        { (Tab.Gifs, "GIFs"), (Tab.Stickers, "Stickers"), (Tab.Emoji, "Emoji") };

    static Rectangle RectFor(int i)
    {
        int x = Ui.S(PadX);
        for (int k = 0; k < i; k++) x += Width(k) + Ui.S(Gap);
        return new Rectangle(x, Ui.S(TabTop), Width(i), Ui.S(TabH));
    }

    static int Width(int i) => Ui.Measure(Order[i].Label, Theme.Category).Width + Ui.S(18);

    /// Draws the panel background and the tab row. Call first from the picker's OnPaint; everything
    /// else belongs below ContentTop.
    public static void Paint(Graphics g, Control c, Tab active, Point mouse)
    {
        Ui.Fill(g, c.ClientRectangle, Theme.Field);
        for (int i = 0; i < Order.Length; i++)
        {
            var r = RectFor(i);
            bool on = Order[i].T == active;
            if (on) Ui.FillRound(g, r, Ui.S(8), Theme.Tint(Theme.Field, Color.FromArgb(151, 151, 159), 0.16f));
            else if (r.Contains(mouse))
                Ui.FillRound(g, r, Ui.S(8), Theme.Tint(Theme.Field, Color.FromArgb(151, 151, 159), 0.08f));
            Ui.Text(g, Order[i].Label, Theme.Category, r, on ? Theme.Strong : Theme.Text,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }

    /// The tab under a point, or null. Returns the *other* tabs too — the caller decides whether a
    /// click on the active one is a no-op.
    public static Tab? At(Point p)
    {
        for (int i = 0; i < Order.Length; i++)
            if (RectFor(i).Contains(p)) return Order[i].T;
        return null;
    }

    /// True if the click was on a tab and has been handled (the picker should stop processing it).
    public static bool Click(Point p, Tab active)
    {
        if (At(p) is not { } t) return false;
        if (t != active) Open?.Invoke(t);
        return true;
    }
}
