using System.Drawing;
using System.Drawing.Drawing2D;

namespace OpenCord;

// Discord's right-click menu, built on ToolStripDropDown rather than hand-rolled.
//
// A custom popup would need its own hit-testing, keyboard loop, dismiss-on-click-away, screen-edge
// flipping and submenu placement — all of which WinForms already ships and gets right. Only the
// paint is wrong out of the box, so only the paint is replaced.
static class Menu
{
    public static ContextMenuStrip New() => new()
    {
        Renderer = new DiscordRenderer(),
        BackColor = Theme.Floating,
        ForeColor = Theme.Muted,
        Font = Theme.Body,
        ShowImageMargin = false,
        DropShadowEnabled = true,
        Padding = new Padding(Ui.S(8), Ui.S(6), Ui.S(8), Ui.S(6)),
    };

    /// A menu entry. `danger` paints Discord's red destructive style.
    public static ToolStripMenuItem Item(string text, Action click, bool danger = false, string? icon = null)
    {
        var it = new ToolStripMenuItem(text) { ForeColor = danger ? Theme.Danger : Theme.Muted };
        it.Padding = new Padding(0, Ui.S(3), 0, Ui.S(3));
        if (danger) it.Tag = "danger";
        if (icon != null) it.Tag = (it.Tag as string) + "|" + icon;
        it.Click += (_, _) => click();
        return it;
    }

    public static ToolStripSeparator Sep() => new();

    /// A submenu. WinForms nests and places these itself, which is most of why the menu is built on
    /// ToolStripDropDown rather than hand-rolled.
    public static ToolStripMenuItem Sub(string text, IEnumerable<ToolStripItem> items)
    {
        var it = new ToolStripMenuItem(text) { ForeColor = Theme.Muted };
        it.Padding = new Padding(0, Ui.S(3), 0, Ui.S(3));
        it.DropDown = New();
        foreach (var child in items) it.DropDownItems.Add(child);
        return it;
    }

    /// A toggle entry, for the roles submenu. The tick is part of the *text* rather than
    /// ToolStrip's Checked state: the strip is built with ShowImageMargin off (Discord's menus have
    /// no icon gutter), and that suppresses the check glyph entirely — a Checked item would look
    /// identical to an unchecked one.
    public static ToolStripMenuItem Toggle(string text, bool on, Action click) =>
        Item((on ? "✓  " : "   ") + text, click);

    /// Shorthand: build and show a menu at a screen point in one call.
    ///
    /// One strip, reused. A menu per right-click has to be disposed or it leaks a window handle, and
    /// there is no safe moment to do it: disposing out of Closed runs while WinForms is still
    /// unwinding the click, and deferring it one message is still too early — ToolStripManager's
    /// ModalMenuFilter goes on holding the strip as the active dropdown, so the *next* click anywhere
    /// in the app reaches into it and throws
    ///
    ///   ObjectDisposedException: 'System.Windows.Forms.ContextMenuStrip'
    ///     at Control.CreateHandle() … at ModalMenuFilter.ProcessMouseButtonPressed(…)
    ///
    /// from the message filter, where no handler of ours can see it coming. Never disposing the strip
    /// removes the race outright: one handle for the life of the app instead of one per right-click,
    /// and only the items — which the filter does not track — are replaced each time.
    static ContextMenuStrip? _strip;

    public static void Show(Control owner, Point screenPt, params ToolStripItem[] items)
    {
        _strip ??= New();
        // Right-clicking again while a menu is open must not swap the items out from under a visible
        // strip; close it first, then rebuild.
        if (_strip.Visible) _strip.Close();
        var old = _strip.Items.Cast<ToolStripItem>().ToArray();
        _strip.Items.Clear();
        foreach (var it in old) it.Dispose();
        _strip.Items.AddRange(items);
        _strip.Show(screenPt);
    }

    sealed class DiscordRenderer : ToolStripProfessionalRenderer
    {
        public DiscordRenderer() : base(new Colors()) { RoundedEdges = false; }

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            e.Graphics.Clear(Theme.Floating);
            Ui.FillRound(e.Graphics, e.AffectedBounds, Ui.S(8), Theme.Floating);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e) { }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected || !e.Item.Enabled) return;
            bool danger = (e.Item.Tag as string)?.StartsWith("danger") == true;
            var r = new Rectangle(Ui.S(2), Ui.S(1), e.Item.Width - Ui.S(4), e.Item.Height - Ui.S(2));
            Ui.FillRound(e.Graphics, r, Ui.S(4), danger ? Theme.Danger : Theme.Blurple);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            bool danger = (e.Item.Tag as string)?.StartsWith("danger") == true;
            e.TextColor = e.Item.Selected ? Color.White : danger ? Theme.Danger : Theme.Muted;
            e.TextFormat |= TextFormatFlags.NoPrefix;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            int y = e.Item.Height / 2;
            Ui.Fill(e.Graphics, new Rectangle(Ui.S(4), y, e.Item.Width - Ui.S(8), 1), Theme.Border);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            // Item is nullable here: the arrow is also rendered for the strip's own overflow chevron,
            // which belongs to no item.
            e.ArrowColor = e.Item?.Selected == true ? Color.White : Theme.Muted;
            base.OnRenderArrow(e);
        }
    }

    sealed class Colors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Theme.Floating;
        public override Color MenuBorder => Theme.Floating;
        public override Color MenuItemBorder => Theme.Floating;
        public override Color ImageMarginGradientBegin => Theme.Floating;
        public override Color ImageMarginGradientMiddle => Theme.Floating;
        public override Color ImageMarginGradientEnd => Theme.Floating;
        public override Color SeparatorDark => Theme.Border;
        public override Color SeparatorLight => Theme.Border;
    }
}
