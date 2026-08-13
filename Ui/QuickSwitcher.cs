using System.Drawing;
using System.Drawing.Drawing2D;

namespace OpenCord;

// Discord's Quick Switcher (Ctrl+K): a search box that jumps to any channel in any server, or any
// DM. Hosted in a ToolStripDropDown so click-away, Esc and edge-flipping come free; the search box
// is a real TextBox so typing, IME and the caret behave. Picking calls Session.GoTo, which does the
// guild switch + channel open in one place.
sealed class QuickSwitcher : Control
{
    readonly Session _session;
    readonly TextBox _search;
    readonly List<(string Name, string Sub, ulong Guild, ulong Channel, bool Dm, string? Icon)> _results = new();
    readonly Scroller _scroll;
    int _sel = -1;

    static ToolStripDropDown? _host;

    QuickSwitcher(Session session)
    {
        _session = session;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Size = new Size(Ui.S(460), Ui.S(430));
        BackColor = Theme.Floating;
        _scroll = new Scroller(this);

        _search = new TextBox
        {
            BorderStyle = BorderStyle.None,
            BackColor = Theme.InputBg,
            ForeColor = Theme.Text,
            Font = Theme.Body,
            PlaceholderText = "Where would you like to go?",
        };
        _search.SetBounds(Ui.S(20), Ui.S(44), Width - Ui.S(40), Ui.S(26));
        _search.TextChanged += (_, _) => { Build(_search.Text); _sel = -1; _scroll.Reset(); Invalidate(); };
        _search.KeyDown += OnKey;
        Controls.Add(_search);
        Build("");
    }

    public static void Show(Shell shell, Session session)
    {
        Pop.Close(_host);
        var q = new QuickSwitcher(session);
        var wa = Screen.FromControl(shell).WorkingArea;
        var pt = shell.PointToScreen(new Point((shell.ClientSize.Width - q.Width) / 2, Ui.S(60)));
        pt.X = Math.Clamp(pt.X, wa.Left + Ui.S(8), wa.Right - q.Width - Ui.S(8));
        pt.Y = Math.Clamp(pt.Y, wa.Top + Ui.S(8), wa.Bottom - q.Height - Ui.S(8));
        _host = Pop.Host(q, pt);
        q._search.Focus();
    }

    // ── results ─────────────────────────────────────────────────────────────────────────────────
    void Build(string q)
    {
        _results.Clear();
        var c = App.Client;
        if (c == null) return;
        q = q.Trim().ToLowerInvariant();

        foreach (var d in c.DMChannels)
        {
            if (d.DisplayName.Length == 0) continue;
            if (q.Length > 0 && !d.DisplayName.ToLowerInvariant().Contains(q)) continue;
            _results.Add((d.DisplayName, d.Subtitle, 0, d.Id, true, d.AvatarUrl));
        }
        foreach (var g in c.Guilds)
            foreach (var ch in g.Channels)
            {
                if (!ch.IsText && !ch.IsVoice) continue;
                if (q.Length > 0 && !ch.Name.ToLowerInvariant().Contains(q)
                    && !g.Name.ToLowerInvariant().Contains(q)) continue;
                _results.Add((ch.Name, g.Name, g.Id, ch.Id, false, null));
            }

        // Discord's list is capped; a wall of rows is noise, not navigation.
        if (_results.Count > 50) _results.RemoveRange(50, _results.Count - 50);
        _sel = _results.Count > 0 ? 0 : -1;
    }

    void Pick()
    {
        if (_sel < 0 || _sel >= _results.Count) return;
        var r = _results[_sel];
        Pop.Close(_host);
        _host = null;
        _session.GoTo(r.Guild, r.Channel);
    }

    void OnKey(object? s, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Down && _results.Count > 0)
        {
            _sel = (_sel + 1) % _results.Count;
            EnsureVisible();
            e.SuppressKeyPress = true;
        }
        else if (e.KeyCode == Keys.Up && _results.Count > 0)
        {
            _sel = _sel <= 0 ? _results.Count - 1 : _sel - 1;
            EnsureVisible();
            e.SuppressKeyPress = true;
        }
        else if (e.KeyCode == Keys.Enter)
        {
            e.SuppressKeyPress = true;
            Pick();
        }
    }

    void EnsureVisible()
    {
        int top = Ui.S(88), rowH = Ui.S(44);
        int max = Math.Max(0, _results.Count * rowH - (Height - top));
        if (_sel * rowH < _scroll.Value) _scroll.JumpTo(_sel * rowH, max);
        else if ((_sel + 1) * rowH > _scroll.Value + Height - top) _scroll.JumpTo((_sel + 1) * rowH - (Height - top), max);
        Invalidate();
    }

    // ── mouse ──────────────────────────────────────────────────────────────────────────────────
    int RowAt(Point p)
    {
        if (p.Y < Ui.S(88)) return -1;
        int i = (p.Y - Ui.S(88) + _scroll.Value) / Ui.S(44);
        return i >= 0 && i < _results.Count ? i : -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int h = RowAt(e.Location);
        if (h != _sel) { _sel = h; Invalidate(); }
        Cursor = h >= 0 ? Cursors.Hand : Cursors.Default;
        base.OnMouseMove(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (RowAt(e.Location) >= 0) Pick();
        base.OnMouseDown(e);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        _scroll.Wheel(e.Delta, Math.Max(0, _results.Count * Ui.S(44) - (Height - Ui.S(88))));
        base.OnMouseWheel(e);
    }

    // ── paint ──────────────────────────────────────────────────────────────────────────────────
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Ui.Fill(g, ClientRectangle, Theme.Floating);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        Ui.Text(g, "Quick Switcher", Theme.SmallMedium, new Rectangle(Ui.S(20), Ui.S(12), Width - Ui.S(40), Ui.S(18)),
                Theme.Faint, TextFormatFlags.NoPadding);
        Ui.FillRound(g, new Rectangle(Ui.S(16), Ui.S(36), Width - Ui.S(32), Ui.S(40)), Ui.S(6), Theme.InputBg);
        Ui.Text(g, "⌕", Theme.Body, new Rectangle(Ui.S(20), Ui.S(44), Ui.S(22), Ui.S(24)), Theme.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

        var clip = g.Save();
        g.SetClip(new Rectangle(0, Ui.S(88), Width, Height - Ui.S(88)));
        for (int i = 0; i < _results.Count; i++)
        {
            int y = Ui.S(88) + i * Ui.S(44) - _scroll.Value;
            if (y + Ui.S(44) < Ui.S(88) || y > Height) continue;
            DrawRow(g, y, i);
        }
        g.Restore(clip);

        if (_results.Count == 0)
            Ui.Text(g, "No results found", Theme.Body,
                    new Rectangle(Ui.S(16), Ui.S(120), Width - Ui.S(32), Ui.S(24)),
                    Theme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    void DrawRow(Graphics g, int y, int i)
    {
        var r = _results[i];
        bool sel = _sel == i;
        var row = new Rectangle(Ui.S(8), y, Width - Ui.S(16), Ui.S(40));
        if (sel) Ui.FillRound(g, row, Ui.S(6), Theme.SidebarSelected);

        int av = Ui.S(28);
        var ab = new Rectangle(row.X + Ui.S(8), row.Y + (row.Height - av) / 2, av, av);
        if (r.Dm) Ui.Avatar(g, Media.Get(r.Icon, this), ab, Theme.Surface);
        else
        {
            // Channels get their hash glyph on the rail colour, like the real switcher.
            using var b = new SolidBrush(Theme.Surface);
            g.FillEllipse(b, ab);
            Ui.Text(g, r.Dm ? "@" : "#", Theme.BodyMedium, ab, Theme.ChannelIcon,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        int tx = ab.Right + Ui.S(10);
        Ui.Text(g, r.Name, Theme.BodyMedium, new Rectangle(tx, row.Y + Ui.S(2), row.Width - tx - Ui.S(8), Ui.S(20)),
                sel ? Theme.Strong : Theme.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        Ui.Text(g, r.Sub, Theme.Small, new Rectangle(tx, row.Y + Ui.S(20), row.Width - tx - Ui.S(8), Ui.S(16)),
                Theme.Faint, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }
}
