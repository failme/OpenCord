using System.Drawing;
using System.Drawing.Drawing2D;

namespace OpenCord;

// The Inbox: what the title bar's tray button opens.
//
// Two tabs, both answering "what did I miss". Unreads is entirely local — the gateway's read states
// already know every channel with something new in it, so the list costs nothing to build and is
// correct the instant a message arrives. Mentions needs the server, because a mention from three
// days ago in a guild you have never opened this session is not in any local cache.
//
// Measured off the live popout: 480x640, radius 8 on Theme.Field (not the near-black Floating the
// menus use — this panel sits on a lighter surface), a 65px header, then a 36px tab strip whose only
// active-state indicator is the text colour: brand on the selected tab, subtle on the other.
sealed class InboxPopup : Control
{
    enum Tab { Unreads, Mentions }

    // One list entry. Unreads fill Title/Sub and leave Body empty; mentions fill all of them.
    readonly record struct Row(ulong Guild, ulong Channel, ulong MsgId, string Title, string Sub,
                               string Body, string When, int Mentions, string? Avatar, Color Colour);

    readonly Session _session;
    readonly List<Row> _rows = new();
    readonly Scroller _scroll;
    Tab _tab = Tab.Unreads;
    int _hover = -1;
    bool _busy, _markHot;
    string? _error;

    static ToolStripDropDown? _host;

    int HeaderH => Ui.S(65);
    int TabH => Ui.S(36);
    int ListTop => HeaderH + TabH;
    int RowH => _tab == Tab.Unreads ? Ui.S(44) : Ui.S(64);

    InboxPopup(Session session)
    {
        _session = session;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Size = new Size(Ui.S(480), Ui.S(640));
        BackColor = Theme.Field;
        _scroll = new Scroller(this);
    }

    /// Hangs under the title bar's Inbox button, right-aligned to it like the live client.
    public static void Show(Shell shell, Session session)
    {
        Pop.Close(_host);
        var p = new InboxPopup(session);
        var at = shell.PointToScreen(new Point(shell.ClientSize.Width - p.Width - Ui.S(60), Ui.S(M.TitleBar) + Ui.S(4)));
        _host = Pop.Host(p, at);
        p.Reload();
    }

    void Reload()
    {
        _scroll.Reset();
        _hover = -1;
        _error = null;
        _rows.Clear();

        if (_tab == Tab.Unreads)
        {
            foreach (var (g, c, gn, cn, m) in _session.UnreadChannels())
                _rows.Add(new Row(g, c, 0, cn, gn, "", "", m, null, Theme.Text));
            Invalidate();
            return;
        }

        _busy = true;
        Invalidate();
        _ = LoadMentions();
    }

    async Task LoadMentions()
    {
        var client = App.Client;
        if (client == null) { _busy = false; return; }
        try
        {
            var msgs = await client.Rest.GetRecentMentionsAsync(25);
            _rows.Clear();
            foreach (var m in msgs)
            {
                var guild = m.GuildId is { } gid ? client.GuildById.GetValueOrDefault(gid) : null;
                var author = m.Member?.DisplayName ?? m.Author?.DisplayName ?? "Unknown";
                var where = guild == null ? "Direct Message"
                          : guild.Name + " › #" + (guild.ChannelById.GetValueOrDefault(m.ChannelId)?.Name ?? "unknown");
                _rows.Add(new Row(m.GuildId ?? 0, m.ChannelId, m.Id, author, where,
                                  Markdown.Flatten(m.Content).Replace("\n", " "),
                                  MessageRow.Stamp(m.Timestamp), 0,
                                  m.Author?.GetAvatarUrl(32),
                                  guild?.NameColor(m.Author?.Id ?? 0) ?? Theme.Text));
            }
        }
        catch (Exception e) { _error = "Couldn't load mentions: " + e.Message; }
        _busy = false;
        if (!IsDisposed) Invalidate();
    }

    // ── input ───────────────────────────────────────────────────────────────────────────────────

    Rectangle TabRect(int i) => new(i * Width / 2, HeaderH, Width / 2, TabH);
    Rectangle MarkRect => new(Width - Ui.S(16) - Ui.S(31), Ui.S(17), Ui.S(31), Ui.S(31));

    int RowAt(Point p)
    {
        if (p.Y < ListTop) return -1;
        int i = (p.Y - ListTop + _scroll.Value) / RowH;
        return i >= 0 && i < _rows.Count ? i : -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int h = RowAt(e.Location);
        bool mk = MarkRect.Contains(e.Location);
        bool tab = e.Y >= HeaderH && e.Y < HeaderH + TabH;
        if (h != _hover || mk != _markHot)
        {
            _hover = h; _markHot = mk;
            Tip.Show(this, mk ? "Mark All as Read" : null, mk ? MarkRect : Rectangle.Empty);
            Invalidate();
        }
        Cursor = h >= 0 || mk || tab ? Cursors.Hand : Cursors.Default;
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        if (_hover != -1 || _markHot) { _hover = -1; _markHot = false; Tip.Hide(); Invalidate(); }
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;

        if (MarkRect.Contains(e.Location)) { _session.MarkAllRead(); Reload(); return; }

        for (int i = 0; i < 2; i++)
            if (TabRect(i).Contains(e.Location))
            {
                var want = (Tab)i;
                if (want != _tab) { _tab = want; Reload(); }
                return;
            }

        int h = RowAt(e.Location);
        if (h < 0) return;
        var r = _rows[h];
        Pop.Close(_host);
        _host = null;
        // A mention jumps to the message itself; an unread channel just opens at the divider.
        _session.GoToMessage(r.Guild, r.Channel, r.MsgId);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        _scroll.Wheel(e.Delta, Math.Max(0, _rows.Count * RowH - (Height - ListTop)));
        base.OnMouseWheel(e);
    }

    // ── paint ───────────────────────────────────────────────────────────────────────────────────

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Ui.Fill(g, ClientRectangle, Theme.Field);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        PaintHeader(g);
        PaintTabs(g);

        string? empty = _busy ? "Loading…"
                      : _error != null ? _error
                      : _rows.Count > 0 ? null
                      : _tab == Tab.Unreads ? "You're all caught up." : "No recent mentions.";
        if (empty != null)
        {
            Ui.Text(g, empty, Theme.Body,
                    new Rectangle(Ui.S(16), ListTop + Ui.S(40), Width - Ui.S(32), Ui.S(24)),
                    Theme.Muted, TextFormatFlags.HorizontalCenter);
            return;
        }

        var clip = g.Save();
        g.SetClip(new Rectangle(0, ListTop, Width, Height - ListTop));
        for (int i = 0; i < _rows.Count; i++)
        {
            int y = ListTop + i * RowH - _scroll.Value;
            if (y + RowH < ListTop || y > Height) continue;
            if (_tab == Tab.Unreads) PaintUnread(g, y, i);
            else PaintMention(g, y, i);
        }
        g.Restore(clip);
    }

    void PaintHeader(Graphics g)
    {
        int ic = Ui.S(20);
        Svg.SvgFill(g, Icons.InboxLine, new RectangleF(Ui.S(16), (HeaderH - ic) / 2f, ic, ic), Theme.Muted);
        Ui.Text(g, "Inbox", Theme.H3, new Rectangle(Ui.S(44), 0, Width - Ui.S(120), HeaderH),
                Theme.Strong, TextFormatFlags.VerticalCenter);

        var mk = MarkRect;
        if (_markHot) Ui.FillRound(g, mk, Ui.S(6), Theme.SurfaceHigh);
        int mi = Ui.S(18);
        Svg.SvgFill(g, Icons.CheckLine,
                    new RectangleF(mk.X + (mk.Width - mi) / 2f, mk.Y + (mk.Height - mi) / 2f, mi, mi),
                    _markHot ? Theme.Text : Theme.Muted);
    }

    // The only indicator of the active tab is its text colour — no underline, no fill. Measured:
    // brand text on the selected tab, subtle on the other.
    void PaintTabs(Graphics g)
    {
        for (int i = 0; i < 2; i++)
        {
            var r = TabRect(i);
            bool on = (int)_tab == i;
            Ui.Text(g, i == 0 ? "Unreads" : "Mentions", Theme.SmallMedium, r,
                    on ? Theme.BrandText : Theme.Subtle,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
        Ui.Fill(g, new Rectangle(0, HeaderH + TabH - 1, Width, 1), Theme.BorderSubtle);
    }

    void PaintUnread(Graphics g, int y, int i)
    {
        var r = _rows[i];
        var row = new Rectangle(Ui.S(8), y + Ui.S(2), Width - Ui.S(16), RowH - Ui.S(4));
        if (_hover == i) Ui.FillRound(g, row, Ui.S(6), Theme.SurfaceHigh);

        int x = row.X + Ui.S(10), ic = Ui.S(16);
        Svg.SvgFill(g, r.Guild == 0 ? Icons.People : Icons.Hash,
                    new RectangleF(x, row.Y + (row.Height - ic) / 2f, ic, ic), Theme.ChannelIcon);
        x += ic + Ui.S(8);

        int badge = r.Mentions > 0 ? Ui.S(28) : 0;
        int nameW = Math.Min(Ui.Measure(r.Title, Theme.BodyMedium).Width, row.Right - x - Ui.S(12) - badge - Ui.S(90));
        Ui.Text(g, r.Title, Theme.BodyMedium, new Rectangle(x, row.Y, Math.Max(0, nameW), row.Height),
                Theme.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        Ui.Text(g, r.Sub, Theme.Small,
                new Rectangle(x + nameW + Ui.S(8), row.Y, row.Right - Ui.S(12) - badge - x - nameW - Ui.S(8), row.Height),
                Theme.Faint, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

        if (r.Mentions > 0) Badge(g, row, r.Mentions);
    }

    void PaintMention(Graphics g, int y, int i)
    {
        var r = _rows[i];
        var row = new Rectangle(Ui.S(8), y + Ui.S(2), Width - Ui.S(16), RowH - Ui.S(4));
        if (_hover == i) Ui.FillRound(g, row, Ui.S(6), Theme.SurfaceHigh);

        int av = Ui.S(32);
        var ab = new Rectangle(row.X + Ui.S(10), row.Y + (row.Height - av) / 2, av, av);
        Ui.Avatar(g, Media.Get(r.Avatar, this), ab, Theme.Surface, this);

        int x = ab.Right + Ui.S(10), right = row.Right - Ui.S(12);
        var stamp = Ui.Measure(r.When, Theme.Small);
        int nameW = Math.Min(Ui.Measure(r.Title, Theme.BodyMedium).Width, right - x - stamp.Width - Ui.S(12));
        Ui.Text(g, r.Title, Theme.BodyMedium, new Rectangle(x, row.Y + Ui.S(4), Math.Max(0, nameW), Ui.S(20)),
                r.Colour, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        Ui.Text(g, r.When, Theme.Small, new Rectangle(right - stamp.Width, row.Y + Ui.S(4), stamp.Width, Ui.S(20)),
                Theme.Faint, TextFormatFlags.VerticalCenter);
        Ui.Text(g, r.Sub, Theme.Small, new Rectangle(x, row.Y + Ui.S(22), right - x, Ui.S(16)),
                Theme.Faint, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        Ui.Text(g, r.Body, Theme.Body, new Rectangle(x, row.Y + Ui.S(38), right - x, Ui.S(20)),
                _hover == i ? Theme.Text : Theme.Muted,
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }

    void Badge(Graphics g, Rectangle row, int n)
    {
        var label = n > 99 ? "99+" : n.ToString();
        var sz = Ui.Measure(label, Theme.SmallMedium);
        int w = Math.Max(Ui.S(16), sz.Width + Ui.S(8)), h = Ui.S(16);
        var b = new Rectangle(row.Right - Ui.S(12) - w, row.Y + (row.Height - h) / 2, w, h);
        Ui.FillRound(g, b, h / 2, Theme.Danger);
        Ui.Text(g, label, Theme.SmallMedium, b, Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }
}
