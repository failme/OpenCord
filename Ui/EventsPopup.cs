using System.Drawing;
using System.Drawing.Drawing2D;

namespace ClaudeScord;

// The guild's scheduled events, opened from the Events row above the channel list. The live client
// gives this its own full page; a list popup is the same information without a second pane, and it
// matches the shape the pins and threads lists already use.
sealed class EventsPopup : Control
{
    readonly List<UserScheduledEvent> _events;
    readonly UserGuild _guild;
    readonly Scroller _scroll;
    int _hover = -1;

    static ToolStripDropDown? _host;

    EventsPopup(List<UserScheduledEvent> events, UserGuild guild)
    {
        _events = events;
        _guild = guild;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Size = new Size(Ui.S(440), Ui.S(320));
        BackColor = Theme.Floating;
        _scroll = new Scroller(this);
    }

    public static void Show(Shell shell, List<UserScheduledEvent> events, UserGuild guild)
    {
        Pop.Close(_host);
        var p = new EventsPopup(events, guild);
        _host = Pop.Host(p, shell.PointToScreen(new Point((shell.ClientSize.Width - p.Width) / 2, Ui.S(60))));
    }

    static int RowH => Ui.S(56);
    /// Where the list starts, below the header. Named ListTop, not Top: Control.Top is the
    /// control's own position and shadowing it here would be a trap for anyone reading this later.
    static int ListTop => Ui.S(56);

    int RowAt(Point p)
    {
        if (p.Y < ListTop) return -1;
        int i = (p.Y - ListTop + _scroll.Value) / RowH;
        return i >= 0 && i < _events.Count ? i : -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int h = RowAt(e.Location);
        if (h != _hover) { _hover = h; Invalidate(); }
        Cursor = h >= 0 ? Cursors.Hand : Cursors.Default;
        base.OnMouseMove(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        int i = RowAt(e.Location);
        if (i < 0) return;
        // Copying the link is the one action that works for every event type; joining a stage or
        // an external event is not something this client can do yet.
        try { Clipboard.SetText($"https://discord.com/events/{_guild.Id}/{_events[i].Id}"); } catch { }
        Pop.Close(_host);
        _host = null;
        base.OnMouseDown(e);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        _scroll.Wheel(e.Delta, Math.Max(0, _events.Count * RowH - (Height - ListTop)));
        base.OnMouseWheel(e);
    }

    /// "Today at 7:00 PM" / "Tomorrow at ..." / "August 14 at ..." — Discord's own event phrasing.
    static string When(DateTimeOffset? when)
    {
        if (when is not { } w) return "";
        var local = w.ToLocalTime();
        var d = local.Date;
        string time = local.ToString("h:mm tt");
        if (d == DateTime.Today) return "Today at " + time;
        if (d == DateTime.Today.AddDays(1)) return "Tomorrow at " + time;
        return local.ToString("MMMM d") + " at " + time;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Ui.Fill(g, ClientRectangle, Theme.Floating);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        Ui.Text(g, "Events", Theme.SmallMedium, new Rectangle(Ui.S(20), Ui.S(14), Width - Ui.S(40), Ui.S(20)),
                Theme.Faint, TextFormatFlags.NoPadding);

        var clip = g.Save();
        g.SetClip(new Rectangle(0, ListTop, Width, Height - ListTop));
        for (int i = 0; i < _events.Count; i++)
        {
            int y = ListTop + i * RowH - _scroll.Value;
            if (y + RowH < ListTop || y > Height) continue;
            var ev = _events[i];
            bool sel = _hover == i;
            var row = new Rectangle(Ui.S(8), y, Width - Ui.S(16), RowH - Ui.S(4));
            if (sel) Ui.FillRound(g, row, Ui.S(6), Theme.SidebarSelected);

            // A live event gets the red dot the real client uses; a scheduled one gets the date.
            bool live = ev.Status == 2;
            int icon = Ui.S(18);
            var ib = new RectangleF(row.X + Ui.S(12), row.Y + Ui.S(10), icon, icon);
            if (live)
            {
                using var b = new SolidBrush(Theme.Danger);
                g.FillEllipse(b, ib);
            }
            else Svg.SvgFill(g, Icons.Megaphone, ib, Theme.ChannelIcon);

            int tx = row.X + Ui.S(44);
            Ui.Text(g, ev.Name, Theme.BodyMedium,
                    new Rectangle(tx, row.Y + Ui.S(6), row.Width - tx - Ui.S(60), Ui.S(20)),
                    sel ? Theme.Strong : Theme.Text,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            Ui.Text(g, (live ? "Happening now" : When(ev.Start)) + "  ·  " + ev.Where, Theme.Small,
                    new Rectangle(tx, row.Y + Ui.S(26), row.Width - tx - Ui.S(60), Ui.S(16)),
                    live ? Theme.Danger : Theme.Faint,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

            if (ev.UserCount > 0)
                Ui.Text(g, ev.UserCount.ToString(), Theme.Small,
                        new Rectangle(row.Right - Ui.S(52), row.Y + Ui.S(6), Ui.S(40), Ui.S(20)),
                        Theme.Faint, TextFormatFlags.VerticalCenter | TextFormatFlags.Right);
        }
        g.Restore(clip);

        if (_events.Count == 0)
            Ui.Text(g, "No upcoming events", Theme.Body,
                    new Rectangle(Ui.S(16), Ui.S(80), Width - Ui.S(32), Ui.S(24)),
                    Theme.Muted, TextFormatFlags.HorizontalCenter);
    }
}
