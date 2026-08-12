using System.Drawing;
using System.Drawing.Drawing2D;

namespace ClaudeScord;

// ── slash command autocomplete ─────────────────────────────────────────────────────────────────
//
// The "/" menu that Discord floats over the composer. It is deliberately NOT a ToolStripDropDown:
// the composer's TextBox has to keep focus (and keep receiving keystrokes) while the menu is open,
// so this is a borderless, non-activating, topmost Form — clicks still land on it, but typing,
// arrows and Esc all stay in the box. Same trick as ToastForm.
sealed class SlashMenu : Form
{
    readonly Action<UserAppCommand> _pick;
    readonly List<UserAppCommand> _all = new();
    readonly List<UserAppCommand> _shown = new();
    public int Selected = -1;

    const int RowH = 44;

    public SlashMenu(Action<UserAppCommand> pick)
    {
        _pick = pick;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        DoubleBuffered = true;
        BackColor = Theme.Floating;
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            // TOOLWINDOW keeps it off the taskbar, TOPMOST floats it over the shell, and NOACTIVATE
            // is what lets the composer textbox keep the caret while this window is on top.
            cp.ExStyle |= 0x80 | 0x8 | 0x08000000;
            return cp;
        }
    }

    public void SetCommands(IReadOnlyList<UserAppCommand> cmds)
    {
        _all.Clear();
        _all.AddRange(cmds);
    }

    public void ApplyFilter(string filter)
    {
        _shown.Clear();
        var f = filter.Trim().ToLowerInvariant();
        foreach (var c in _all)
            if (f.Length == 0 || c.Name.ToLowerInvariant().Contains(f)) _shown.Add(c);
        // Discord's own ordering: popularity rank first, then alphabetical.
        _shown.Sort((a, b) => a.Popularity != b.Popularity ? a.Popularity.CompareTo(b.Popularity)
                                                        : string.CompareOrdinal(a.Name, b.Name));
        if (_shown.Count > 8) _shown.RemoveRange(8, _shown.Count - 8);
        Selected = _shown.Count > 0 ? 0 : -1;
        Size = new Size(Ui.S(440), Math.Max(Ui.S(88), _shown.Count * RowH + Ui.S(12)));
        Invalidate();
    }

    public void MoveSel(int dir)
    {
        if (_shown.Count == 0) return;
        Selected = (Selected + dir + _shown.Count) % _shown.Count;
        Invalidate();
    }

    public UserAppCommand? Current => Selected >= 0 && Selected < _shown.Count ? _shown[Selected] : null;

    /// Show just above `anchor` (a screen point at the composer's top-left), flipping below it if
    /// there is no room on screen.
    public void ShowAbove(Point anchor)
    {
        var wa = Screen.FromPoint(anchor).WorkingArea;
        int x = Math.Max(wa.Left + Ui.S(8), Math.Min(anchor.X, wa.Right - Width - Ui.S(8)));
        int y = anchor.Y - Height - Ui.S(6);
        if (y < wa.Top + Ui.S(8)) y = anchor.Y + Ui.S(40);
        Location = new Point(x, y);
        Show();
        BringToFront();
    }

    int RowAt(Point p)
    {
        if (p.Y < Ui.S(6) || p.Y > Height - Ui.S(6)) return -1;
        int i = (p.Y - Ui.S(6)) / RowH;
        return i >= 0 && i < _shown.Count ? i : -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int h = RowAt(e.Location);
        if (h != Selected) { Selected = h; Invalidate(); }
        Cursor = h >= 0 ? Cursors.Hand : Cursors.Default;
        base.OnMouseMove(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        int h = RowAt(e.Location);
        if (h >= 0) _pick(_shown[h]);
        base.OnMouseDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Ui.FillRound(g, new Rectangle(1, 1, Width - 3, Height - 3), Ui.S(8), Theme.Floating);
        using (var pen = new Pen(Theme.Border))
        using (var path = Ui.RoundRect(new Rectangle(1, 1, Width - 3, Height - 3), Ui.S(8)))
            g.DrawPath(pen, path);

        for (int i = 0; i < _shown.Count; i++)
        {
            var c = _shown[i];
            bool sel = i == Selected;
            int y = Ui.S(6) + i * RowH;
            var row = new Rectangle(Ui.S(6), y, Width - Ui.S(12), RowH - Ui.S(4));
            if (sel) Ui.FillRound(g, row, Ui.S(6), Theme.SidebarSelected);

            int av = Ui.S(28);
            var ab = new Rectangle(row.X + Ui.S(8), row.Y + (row.Height - av) / 2, av, av);
            var icon = Media.Get(c.IconUrl, this);
            if (icon != null) Ui.Avatar(g, icon, ab, Theme.Surface);
            else
            {
                using var b = new SolidBrush(sel ? Theme.Blurple : Theme.Surface);
                g.FillEllipse(b, ab);
                Ui.Text(g, "/", Theme.BodyMedium, ab, sel ? Color.White : Theme.ChannelIcon,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }

            int tx = ab.Right + Ui.S(10);
            int rw = row.Right - tx - Ui.S(120);
            Ui.Text(g, "/" + c.Name, Theme.BodyMedium,
                    new Rectangle(tx, row.Y + Ui.S(3), rw, Ui.S(20)),
                    sel ? Theme.Strong : Theme.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            Ui.Text(g, c.Description, Theme.Small,
                    new Rectangle(tx, row.Y + Ui.S(21), rw, Ui.S(17)),
                    Theme.Faint, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            Ui.Text(g, c.AppName.Length > 0 ? c.AppName : "Slash Command", Theme.Small,
                    new Rectangle(row.Right - Ui.S(112), row.Y, Ui.S(104), row.Height),
                    Theme.Faint, TextFormatFlags.VerticalCenter | TextFormatFlags.Right | TextFormatFlags.EndEllipsis);
        }

        if (_shown.Count == 0)
            Ui.Text(g, "No commands match", Theme.Body, ClientRectangle, Theme.Muted,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }
}

// ── slash command options panel ────────────────────────────────────────────────────────────────
//
// After a command is picked, Discord shows the option fields it takes. This floats over the
// composer while the user types values — the positional / name:value grammar is parsed by
// Composer.ParseSlashOptions (pure, pinned by SelfTest); this form only renders the result: the
// command header, one row per option with a required-star, the current value, and the row being
// edited highlighted. The composer drives it on every keystroke and blocks Enter until every
// required option has a value.
sealed class SlashOptionsForm : Form
{
    readonly Action<int> _focusRow;
    string _cmd = "", _desc = "", _app = "";
    // (Name, TypeLabel, Required, Value, Active, Missing, Subcommand)
    readonly List<(string Name, string Type, bool Required, string Value, bool Active, bool Missing, bool Sub)> _rows = new();
    bool _blocked;   // a subcommand must be chosen first — nothing is fillable yet

    const int RowH = 44;

    public SlashOptionsForm(Action<int> focusRow)
    {
        _focusRow = focusRow;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        DoubleBuffered = true;
        BackColor = Theme.Floating;
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80 | 0x8 | 0x08000000;
            return cp;
        }
    }

    /// Rows are the option list; `values` are the name→raw pairs parsed from the box so far; the
    /// composer passes which row the caret is in (active) and which required option is missing
    /// (red), plus whether a subcommand still has to be chosen.
    public void Set(string cmd, string desc, string app,
                    IReadOnlyList<UserAppCommandOption> opts,
                    IReadOnlyList<(string Name, string Raw)> values,
                    int activeIdx, int missingIdx, bool blocked)
    {
        _cmd = cmd; _desc = desc; _app = app; _blocked = blocked;
        _rows.Clear();
        for (int i = 0; i < opts.Count; i++)
        {
            var o = opts[i];
            var v = values.FirstOrDefault(x => x.Name == o.Name);
            _rows.Add((o.Name, TypeLabel(o.Type), o.Required,
                       v.Name != null ? v.Raw : "",
                       i == activeIdx, i == missingIdx, o.Type is 1 or 2));
        }
        _hover = -1;   // rows were rebuilt; a stale hover would light the wrong one
        Size = new Size(Ui.S(440), Ui.S(64) + Math.Max(_rows.Count, 1) * RowH + Ui.S(10));
        Invalidate();
    }

    public static string TypeLabel(int type) => type switch
    {
        1 or 2 => "Subcommand",
        3 => "Text",
        4 => "Integer",
        5 => "Boolean",
        6 => "User",
        7 => "Channel",
        8 => "Role",
        9 => "Mentionable",
        10 => "Number",
        _ => "Option",
    };

    /// Show just above `anchor` (a screen point at the composer's top-left), flipping below it if
    /// there is no room on screen.
    public void ShowAbove(Point anchor)
    {
        var wa = Screen.FromPoint(anchor).WorkingArea;
        int x = Math.Max(wa.Left + Ui.S(8), Math.Min(anchor.X, wa.Right - Width - Ui.S(8)));
        int y = anchor.Y - Height - Ui.S(6);
        if (y < wa.Top + Ui.S(8)) y = anchor.Y + Ui.S(40);
        Location = new Point(x, y);
        Show();
        BringToFront();
    }

    int RowAt(Point p)
    {
        if (p.Y < Ui.S(64) || p.Y >= Height - Ui.S(10)) return -1;
        int i = (p.Y - Ui.S(64)) / RowH;
        return i >= 0 && i < _rows.Count ? i : -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int h = RowAt(e.Location);
        if (h != _hover)
        {
            _hover = h;
            Cursor = h >= 0 ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }
        base.OnMouseMove(e);
    }

    int _hover = -1;

    protected override void OnMouseLeave(EventArgs e)
    {
        if (_hover != -1) { _hover = -1; Cursor = Cursors.Default; Invalidate(); }
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        int h = RowAt(e.Location);
        if (h >= 0) _focusRow(h);   // clicking a field focuses it; subcommand rows pick the sub
        base.OnMouseDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Ui.FillRound(g, new Rectangle(1, 1, Width - 3, Height - 3), Ui.S(8), Theme.Floating);
        using (var pen = new Pen(Theme.Border))
        using (var path = Ui.RoundRect(new Rectangle(1, 1, Width - 3, Height - 3), Ui.S(8)))
            g.DrawPath(pen, path);

        // Header: the command with its app on the right, description under it.
        Ui.Text(g, _cmd, Theme.BodyMedium, new Rectangle(Ui.S(14), Ui.S(8), Width - Ui.S(130), Ui.S(20)),
                Theme.Strong, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        if (_app.Length > 0)
            Ui.Text(g, _app, Theme.Small, new Rectangle(Width - Ui.S(122), Ui.S(8), Ui.S(108), Ui.S(20)),
                    Theme.Faint, TextFormatFlags.VerticalCenter | TextFormatFlags.Right | TextFormatFlags.EndEllipsis);
        Ui.Text(g, _desc, Theme.Small, new Rectangle(Ui.S(14), Ui.S(28), Width - Ui.S(28), Ui.S(18)),
                Theme.Faint, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        Ui.Fill(g, new Rectangle(Ui.S(10), Ui.S(56), Width - Ui.S(20), 1), Theme.BorderSubtle);

        if (_rows.Count == 0)
        {
            Ui.Text(g, "This command takes no options.", Theme.Body, new Rectangle(Ui.S(16), Ui.S(80), Width - Ui.S(32), Ui.S(24)),
                    Theme.Muted, TextFormatFlags.HorizontalCenter);
            return;
        }

        for (int i = 0; i < _rows.Count; i++)
        {
            var (name, type, required, value, active, missing, sub) = _rows[i];
            int y = Ui.S(64) + i * RowH;
            var row = new Rectangle(Ui.S(6), y, Width - Ui.S(12), RowH - Ui.S(4));

            if (active) Ui.FillRound(g, row, Ui.S(6), Theme.SidebarSelected);
            else if (_hover == i) Ui.FillRound(g, row, Ui.S(6), Theme.SurfaceHigh);
            if (missing) Ui.FillRound(g, row, Ui.S(6), Color.FromArgb(40, Theme.Danger.R, Theme.Danger.G, Theme.Danger.B));

            int tx = row.X + Ui.S(10);
            var nc = sub ? Theme.Text : active ? Theme.Strong : Theme.Text;
            Ui.Text(g, name + (required ? " *" : ""), Theme.BodyMedium,
                    new Rectangle(tx, row.Y + Ui.S(4), row.Width / 2 - Ui.S(10), Ui.S(20)),
                    required ? Theme.Danger : nc,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            Ui.Text(g, type, Theme.Small, new Rectangle(tx, row.Y + Ui.S(24), row.Width / 2 - Ui.S(10), Ui.S(15)),
                    Theme.Faint, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

            // Value column: the parsed raw text, or a placeholder telling the user what to do.
            int vx = row.X + row.Width / 2 + Ui.S(6);
            var vr = new Rectangle(vx, row.Y + Ui.S(4), row.Width / 2 - Ui.S(16), row.Height - Ui.S(8));
            if (value.Length > 0)
            {
                Ui.FillRound(g, Rectangle.Inflate(vr, -Ui.S(2), -Ui.S(2)), Ui.S(5), Theme.InputBg);
                Ui.Text(g, value, Theme.Body, Rectangle.Inflate(vr, Ui.S(4), 0),
                        active ? Theme.Strong : Theme.Text,
                        TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            }
            else
            {
                var placeholder = _blocked ? "Pick a subcommand"
                                  : required ? "Required" : "Optional";
                Ui.Text(g, placeholder, Theme.Body, vr,
                        missing ? Theme.Danger : Theme.Faint,
                        TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            }
        }
    }
}

// ── mention / emoji autocomplete ──────────────────────────────────────────────────────────────
//
// The @-mention and :emoji: menus that float over the composer while the user types. Same window
// trick as SlashMenu (borderless, non-activating, topmost) so the composer's TextBox keeps the
// caret and every keystroke. One form serves both modes: rows are members, roles, @everyone, or
// emoji, and picking one hands the composer its insert text.
sealed class AutoMenu : Form
{
    public enum Kind { Member, Role, Everyone, Emoji, Channel }
    public sealed record Item(Kind Kind, string? Icon, string Name, string Sub, Color Color, string Insert);

    readonly Action<Item> _pick;
    readonly List<Item> _all = new();
    readonly List<Item> _shown = new();
    public int Selected = -1;

    const int RowH = 44;

    public AutoMenu(Action<Item> pick)
    {
        _pick = pick;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        DoubleBuffered = true;
        BackColor = Theme.Floating;
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80 | 0x8 | 0x08000000;
            return cp;
        }
    }

    public void SetItems(IReadOnlyList<Item> items)
    {
        _all.Clear();
        _all.AddRange(items);
    }

    public void ApplyFilter(string filter)
    {
        _shown.Clear();
        var f = filter.Trim().ToLowerInvariant();
        // Members match on display name *or* @username (nickname ≠ username is common), roles and
        // emoji on their own name. Prefix on both, like Discord's autocomplete.
        foreach (var it in _all)
            if (f.Length == 0
                || it.Name.ToLowerInvariant().StartsWith(f)
                || (it.Sub.Length > 0 && it.Sub.ToLowerInvariant().StartsWith(f)))
                _shown.Add(it);
        if (_shown.Count > 8) _shown.RemoveRange(8, _shown.Count - 8);
        Selected = _shown.Count > 0 ? 0 : -1;
        Size = new Size(Ui.S(340), Math.Max(Ui.S(88), _shown.Count * RowH + Ui.S(12)));
        Invalidate();
    }

    public void MoveSel(int dir)
    {
        if (_shown.Count == 0) return;
        Selected = (Selected + dir + _shown.Count) % _shown.Count;
        Invalidate();
    }

    public Item? Current => Selected >= 0 && Selected < _shown.Count ? _shown[Selected] : null;

    /// Show just above `anchor` (a screen point at the composer's top-left), flipping below it if
    /// there is no room on screen.
    public void ShowAbove(Point anchor)
    {
        var wa = Screen.FromPoint(anchor).WorkingArea;
        int x = Math.Max(wa.Left + Ui.S(8), Math.Min(anchor.X, wa.Right - Width - Ui.S(8)));
        int y = anchor.Y - Height - Ui.S(6);
        if (y < wa.Top + Ui.S(8)) y = anchor.Y + Ui.S(40);
        Location = new Point(x, y);
        Show();
        BringToFront();
    }

    int RowAt(Point p)
    {
        if (p.Y < Ui.S(6) || p.Y > Height - Ui.S(6)) return -1;
        int i = (p.Y - Ui.S(6)) / RowH;
        return i >= 0 && i < _shown.Count ? i : -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int h = RowAt(e.Location);
        if (h != Selected) { Selected = h; Invalidate(); }
        Cursor = h >= 0 ? Cursors.Hand : Cursors.Default;
        base.OnMouseMove(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        int h = RowAt(e.Location);
        if (h >= 0) _pick(_shown[h]);
        base.OnMouseDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Ui.FillRound(g, new Rectangle(1, 1, Width - 3, Height - 3), Ui.S(8), Theme.Floating);
        using (var pen = new Pen(Theme.Border))
        using (var path = Ui.RoundRect(new Rectangle(1, 1, Width - 3, Height - 3), Ui.S(8)))
            g.DrawPath(pen, path);

        for (int i = 0; i < _shown.Count; i++)
        {
            var it = _shown[i];
            bool sel = i == Selected;
            int y = Ui.S(6) + i * RowH;
            var row = new Rectangle(Ui.S(6), y, Width - Ui.S(12), RowH - Ui.S(4));
            if (sel) Ui.FillRound(g, row, Ui.S(6), Theme.SidebarSelected);

            int av = Ui.S(32);
            var ab = new Rectangle(row.X + Ui.S(8), row.Y + (row.Height - av) / 2, av, av);
            switch (it.Kind)
            {
                case Kind.Member:
                    Ui.Avatar(g, it.Icon == null ? null : Media.Get(it.Icon, this), ab, Theme.Surface);
                    break;
                case Kind.Role:
                    using (var b = new SolidBrush(it.Color))
                        g.FillEllipse(b, new Rectangle(ab.X + Ui.S(8), ab.Y + Ui.S(8), Ui.S(16), Ui.S(16)));
                    break;
                case Kind.Everyone:
                    Ui.FillRound(g, ab, Ui.S(6), Theme.Surface);
                    Ui.Text(g, "@", Theme.BodyMedium, ab, Theme.ChannelIcon,
                            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                    break;
                case Kind.Channel:
                    Ui.FillRound(g, ab, Ui.S(6), Theme.Surface);
                    Ui.Text(g, "#", Theme.BodyMedium, ab, Theme.ChannelIcon,
                            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                    break;
                case Kind.Emoji:
                    var img = it.Icon == null ? null : Media.Get(it.Icon, this);
                    if (img != null) g.DrawImage(img, ab);
                    break;
            }

            int tx = ab.Right + Ui.S(10);
            int rw = row.Right - tx - Ui.S(8);
            if (it.Kind == Kind.Emoji)
            {
                // Emoji rows are a single line: image + the :name: it inserts.
                Ui.Text(g, it.Name, Theme.BodyMedium,
                        new Rectangle(tx, row.Y, rw, row.Height),
                        sel ? Theme.Strong : Theme.Text,
                        TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            }
            else
            {
                Ui.Text(g, it.Name, Theme.BodyMedium,
                        new Rectangle(tx, row.Y + Ui.S(3), rw, Ui.S(20)),
                        it.Kind == Kind.Role ? it.Color : sel ? Theme.Strong : Theme.Text,
                        TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                Ui.Text(g, it.Sub, Theme.Small,
                        new Rectangle(tx, row.Y + Ui.S(21), rw, Ui.S(17)),
                        Theme.Faint, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            }
        }

        if (_shown.Count == 0)
            Ui.Text(g, "No matches", Theme.Body, ClientRectangle, Theme.Muted,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }
}

// ── forward picker ─────────────────────────────────────────────────────────────────────────────
// Right-click → Forward on a message: pick any channel or DM, and the message is forwarded with the
// same reference-type-1 payload the web client uses. The result lands in the target channel as a
// normal MESSAGE_CREATE — this picker only has to pick and fire.
sealed class ForwardPicker : Control
{
    readonly UserMessage _msg;
    readonly TextBox _search;
    readonly List<(ulong Guild, ulong Channel, string Name, string Sub, bool Dm, string? Icon)> _results = new();
    readonly Scroller _scroll;
    int _sel = -1;

    static ToolStripDropDown? _host;

    ForwardPicker(UserMessage msg)
    {
        _msg = msg;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Size = new Size(Ui.S(420), Ui.S(400));
        BackColor = Theme.Floating;
        _scroll = new Scroller(this);

        _search = new TextBox
        {
            BorderStyle = BorderStyle.None,
            BackColor = Theme.InputBg,
            ForeColor = Theme.Text,
            Font = Theme.Body,
            PlaceholderText = "Choose a destination",
        };
        _search.SetBounds(Ui.S(20), Ui.S(44), Width - Ui.S(40), Ui.S(26));
        _search.TextChanged += (_, _) => { Build(_search.Text); _sel = -1; _scroll.Reset(); Invalidate(); };
        _search.KeyDown += OnKey;
        Controls.Add(_search);
        Build("");
    }

    public static void Pick(Control owner, Point screen, UserMessage msg)
    {
        Pop.Close(_host);
        var p = new ForwardPicker(msg);
        _host = Pop.Host(p, screen);
        p._search.Focus();
    }

    void Build(string q)
    {
        _results.Clear();
        var c = App.Client;
        if (c == null) return;
        q = q.Trim().ToLowerInvariant();

        foreach (var d in c.DMChannels)
        {
            if (d.DisplayName.Length == 0 || d.Id == _msg.ChannelId) continue;
            if (q.Length > 0 && !d.DisplayName.ToLowerInvariant().Contains(q)) continue;
            _results.Add((0, d.Id, d.DisplayName, d.Subtitle, true, d.AvatarUrl));
        }
        foreach (var g in c.Guilds)
            foreach (var ch in g.Channels)
            {
                if (!ch.IsPostable || ch.Id == _msg.ChannelId || !g.CanView(c.CurrentUser?.Id ?? 0, ch)) continue;
                if (q.Length > 0 && !ch.Name.ToLowerInvariant().Contains(q) && !g.Name.ToLowerInvariant().Contains(q)) continue;
                _results.Add((g.Id, ch.Id, ch.Name, g.Name, false, null));
            }
        _results.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        if (_results.Count > 60) _results.RemoveRange(60, _results.Count - 60);
        _sel = _results.Count > 0 ? 0 : -1;
    }

    void Pick()
    {
        if (_sel < 0 || _sel >= _results.Count) return;
        var r = _results[_sel];
        Pop.Close(_host);
        _host = null;
        _ = DoForward(r);
    }

    async Task DoForward((ulong Guild, ulong Channel, string Name, string Sub, bool Dm, string? Icon) r)
    {
        var c = App.Client;
        if (c == null) return;
        try
        {
            await c.Rest.ForwardMessageAsync(r.Channel, _msg.ChannelId, _msg.Id);
            Toast.Show("Message forwarded", r.Sub + " → " + r.Name, null, r.Guild, r.Channel);
        }
        catch (Exception e)
        {
            Log.Write("chat", "forward failed: " + e.Message);
            Toast.Show("Forward failed", "Couldn't forward the message.", null, r.Guild, r.Channel);
        }
    }

    void OnKey(object? s, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Down && _results.Count > 0)
        {
            _sel = (_sel + 1) % _results.Count; EnsureVisible(); e.SuppressKeyPress = true;
        }
        else if (e.KeyCode == Keys.Up && _results.Count > 0)
        {
            _sel = _sel <= 0 ? _results.Count - 1 : _sel - 1; EnsureVisible(); e.SuppressKeyPress = true;
        }
        else if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Pick(); }
    }

    void EnsureVisible()
    {
        int top = Ui.S(88), rowH = Ui.S(44);
        int max = Math.Max(0, _results.Count * rowH - (Height - top));
        if (_sel * rowH < _scroll.Value) _scroll.JumpTo(_sel * rowH, max);
        else if ((_sel + 1) * rowH > _scroll.Value + Height - top) _scroll.JumpTo((_sel + 1) * rowH - (Height - top), max);
        Invalidate();
    }

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

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Ui.Fill(g, ClientRectangle, Theme.Floating);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        Ui.Text(g, "Forward message", Theme.SmallMedium, new Rectangle(Ui.S(20), Ui.S(12), Width - Ui.S(40), Ui.S(18)),
                Theme.Faint, TextFormatFlags.NoPadding);
        Ui.FillRound(g, new Rectangle(Ui.S(16), Ui.S(36), Width - Ui.S(32), Ui.S(40)), Ui.S(6), Theme.InputBg);

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
            Ui.Text(g, "No channels match", Theme.Body, new Rectangle(Ui.S(16), Ui.S(120), Width - Ui.S(32), Ui.S(24)),
                    Theme.Muted, TextFormatFlags.HorizontalCenter);
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
            using var b = new SolidBrush(Theme.Surface);
            g.FillEllipse(b, ab);
            Ui.Text(g, "#", Theme.BodyMedium, ab, Theme.ChannelIcon,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        int tx = ab.Right + Ui.S(10);
        Ui.Text(g, r.Name, Theme.BodyMedium, new Rectangle(tx, row.Y + Ui.S(2), row.Width - tx - Ui.S(8), Ui.S(20)),
                sel ? Theme.Strong : Theme.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        Ui.Text(g, r.Sub, Theme.Small, new Rectangle(tx, row.Y + Ui.S(20), row.Width - tx - Ui.S(8), Ui.S(16)),
                Theme.Faint, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }
}

// ── GIF picker ──────────────────────────────────────────────────────────────────────────────────
// Discord's GIF search is a picker over its own Tenor/Klipy proxy; the REST side already exists
// (TrendingGifsAsync / SearchGifsAsync), this is the grid over it. Selecting pastes the gif URL into
// the composer, which the embed pipeline then unfurls into an animated GIF message — exactly how the
// web client's picker behaves.
sealed class GifPicker : Control
{
    readonly Action<string> _pick;
    readonly TextBox _search;
    readonly List<(string Url, string Prev, int W, int H)> _items = new();
    readonly System.Windows.Forms.Timer _debounce = new() { Interval = 380 };
    readonly Scroller _scroll;
    int _hover = -1;
    bool _busy;
    string _query = "";

    static ToolStripDropDown? _host;

    GifPicker(Action<string> pick)
    {
        _pick = pick;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Size = PickerChrome.Size;
        BackColor = Theme.Field;
        _scroll = new Scroller(this);

        _search = new TextBox
        {
            BorderStyle = BorderStyle.None,
            BackColor = Theme.InputBg,
            ForeColor = Theme.Text,
            Font = Theme.Body,
            PlaceholderText = "Search Tenor",
        };
        _search.SetBounds(Ui.S(PickerChrome.PadX + 12), Ui.S(PickerChrome.ContentTop + 11),
                          Width - Ui.S(PickerChrome.PadX * 2 + 24), Ui.S(22));
        _search.TextChanged += (_, _) => { _debounce.Stop(); _debounce.Start(); };
        _debounce.Tick += (_, _) => { _debounce.Stop(); _ = Run(_search.Text.Trim()); };
        Controls.Add(_search);
        _ = Run("");
    }

    /// `anchor` is the screen rect of the control that opened this — the popup hangs above it.
    public static void Pick(Control owner, Rectangle anchor, Action<string> pick)
    {
        Pop.Close(_host);
        var p = new GifPicker(pick);
        _host = Pop.Host(p, Pop.Above(anchor, p.Size), owner is Composer cmp ? cmp.PickerDismissedAt : null);
        p._search.Focus();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _debounce.Dispose();
        base.Dispose(disposing);
    }

    async Task Run(string q)
    {
        _query = q;
        _busy = true;
        Invalidate();
        var c = App.Client;
        if (c == null) { _busy = false; return; }
        List<UserRestClient.GifResult> gifs;
        try { gifs = q.Length == 0 ? await c.Rest.TrendingGifsAsync() : await c.Rest.SearchGifsAsync(q); }
        catch { gifs = new(); }
        _items.Clear();
        foreach (var g in gifs)
            _items.Add((g.Url, string.IsNullOrEmpty(g.Preview) ? g.Url : g.Preview, g.Width, g.Height));
        _scroll.Reset();
        _busy = false;
        Invalidate();
    }

    int CellW => (Width - Ui.S(24) - Ui.S(8)) / 2;
    int CellH => Ui.S(96);
    int GridTop => Ui.S(PickerChrome.ContentTop + 52);
    int ContentH => ((_items.Count + 1) / 2) * (CellH + Ui.S(8)) + Ui.S(8);
    int MaxScroll => Math.Max(0, ContentH - (Height - GridTop));

    int HitTest(Point p)
    {
        if (p.Y < GridTop) return -1;
        int col = (p.X - Ui.S(12)) / (CellW + Ui.S(8));
        int row = (p.Y - GridTop + _scroll.Value) / (CellH + Ui.S(8));
        int i = row * 2 + col;
        return i >= 0 && i < _items.Count && col < 2 ? i : -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int h = HitTest(e.Location);
        if (h != _hover) { _hover = h; Invalidate(); }
        Cursor = h >= 0 ? Cursors.Hand : Cursors.Default;
        base.OnMouseMove(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (PickerChrome.Click(e.Location, PickerChrome.Tab.Gifs)) return;
        int i = HitTest(e.Location);
        if (i < 0) return;
        _pick(_items[i].Url);
        Pop.Close(_host);
        _host = null;
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        _scroll.Wheel(e.Delta, MaxScroll);
        base.OnMouseWheel(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        PickerChrome.Paint(g, this, PickerChrome.Tab.Gifs, PointToClient(MousePosition));
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Ui.FillRound(g, new Rectangle(Ui.S(PickerChrome.PadX), Ui.S(PickerChrome.ContentTop),
                                      Width - Ui.S(PickerChrome.PadX * 2), Ui.S(40)), Ui.S(8),
                     Theme.Tint(Theme.Field, Color.Black, 0.0784f));

        if (_busy && _items.Count == 0)
        {
            Ui.Text(g, "Loading…", Theme.Body, new Rectangle(Ui.S(16), Ui.S(80), Width - Ui.S(32), Ui.S(24)),
                    Theme.Muted, TextFormatFlags.HorizontalCenter);
            return;
        }

        var clip = g.Save();
        g.SetClip(new Rectangle(0, GridTop, Width, Height - GridTop));
        for (int i = 0; i < _items.Count; i++)
        {
            int col = i % 2, row = i / 2;
            var box = new Rectangle(Ui.S(12) + col * (CellW + Ui.S(8)), GridTop + row * (CellH + Ui.S(8)) - _scroll.Value,
                                    CellW, CellH);
            if (box.Bottom < GridTop || box.Top > Height) continue;
            using (var path = Ui.RoundRect(box, Ui.S(6)))
            {
                var st = g.Save();
                g.SetClip(path, CombineMode.Replace);
                var img = Media.Get(_items[i].Prev, this);
                if (img != null)
                {
                    if (Media.IsAnimated(img)) Media.Animate(img, this);
                    DrawCover(g, img, box);
                }
                else Ui.Fill(g, box, Theme.Surface);
                g.Restore(st);
            }
            if (_hover == i)
            {
                using var pen = new Pen(Theme.Blurple, Math.Max(1, Ui.S(2)));
                using var path = Ui.RoundRect(box, Ui.S(6));
                g.DrawPath(pen, path);
            }
        }
        g.Restore(clip);

        if (_query.Length > 0 && _items.Count == 0 && !_busy)
            Ui.Text(g, "No GIFs found", Theme.Body, new Rectangle(Ui.S(16), Ui.S(80), Width - Ui.S(32), Ui.S(24)),
                    Theme.Muted, TextFormatFlags.HorizontalCenter);
    }

    // Scale-to-cover, centred: a portrait gif must fill a landscape cell without squashing.
    static void DrawCover(Graphics g, Image img, Rectangle box)
    {
        float s = Math.Max(box.Width / (float)img.Width, box.Height / (float)img.Height);
        int w = (int)(img.Width * s), h = (int)(img.Height * s);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.DrawImage(img, new Rectangle(box.X + (box.Width - w) / 2, box.Y + (box.Height - h) / 2, w, h));
    }
}

// ── sticker picker ──────────────────────────────────────────────────────────────────────────────
// Standard packs (every account has them) plus this server's custom stickers, sent with the REST
// sticker send. Lottie stickers have no raster fallback, so they show a name tile instead.
sealed class StickerPicker : Control
{
    readonly ulong _channel;
    readonly List<(string Pack, List<UserSticker> Stickers)> _packs = new();
    readonly Scroller _scroll;
    int _hover = -1;

    static ToolStripDropDown? _host;

    StickerPicker(ulong channel)
    {
        _channel = channel;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Size = PickerChrome.Size;
        BackColor = Theme.Field;
        _scroll = new Scroller(this);
        _ = Load();
    }

    /// `anchor` is the screen rect of the control that opened this — the popup hangs above it.
    public static void Pick(Control owner, Rectangle anchor, ulong channel)
    {
        Pop.Close(_host);
        var p = new StickerPicker(channel);
        _host = Pop.Host(p, Pop.Above(anchor, p.Size), owner is Composer cmp ? cmp.PickerDismissedAt : null);
    }

    async Task Load()
    {
        var c = App.Client;
        if (c == null) return;
        try
        {
            var packs = await c.Rest.GetStickerPacksAsync();
            foreach (var (name, stickers) in packs) _packs.Add((name, stickers));
            if (App.Guild is { } g && g.Stickers.Count > 0)
                _packs.Add((g.Name + " stickers", g.Stickers));
        }
        catch (Exception e) { Log.Write("sticker", e.Message); }
        Invalidate();
    }

    int Cell => Ui.S(76);
    int Gap => Ui.S(8);
    int Cols => Math.Max(1, (Width - Ui.S(24) + Gap) / (Cell + Gap));
    int HeaderH => Ui.S(28);

    int RowHeights()
    {
        int y = Ui.S(12);
        foreach (var (_, list) in _packs) y += HeaderH + (((list.Count + Cols - 1) / Cols) * (Cell + Gap));
        return y;
    }

    int MaxScroll => Math.Max(0, RowHeights() - (Height - Ui.S(PickerChrome.ContentTop + 8)));

    // (packIndex, stickerIndex) under a point, or (-1, -1).
    (int, int) HitTest(Point p)
    {
        // Must track OnPaint's origin, which starts below the shared tab row.
        int y = Ui.S(PickerChrome.ContentTop + 12) - _scroll.Value;
        for (int pi = 0; pi < _packs.Count; pi++)
        {
            var list = _packs[pi].Stickers;
            y += HeaderH;
            int rows = (list.Count + Cols - 1) / Cols;
            if (p.Y >= y - HeaderH && p.Y < y)
                continue;   // header rows aren't clickable
            if (p.Y >= y && p.Y < y + rows * (Cell + Gap))
            {
                int col = (p.X - Ui.S(12)) / (Cell + Gap);
                int row = (p.Y - y) / (Cell + Gap);
                int i = row * Cols + col;
                if (col < Cols && i < list.Count) return (pi, i);
            }
            y += rows * (Cell + Gap);
        }
        return (-1, -1);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var (pi, si) = HitTest(e.Location);
        int h = pi >= 0 ? pi * 1000 + si : -1;
        if (h != _hover) { _hover = h; Invalidate(); }
        Cursor = pi >= 0 ? Cursors.Hand : Cursors.Default;
        base.OnMouseMove(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (PickerChrome.Click(e.Location, PickerChrome.Tab.Stickers)) return;
        var (pi, si) = HitTest(e.Location);
        if (pi < 0 || _channel == 0) return;
        var s = _packs[pi].Stickers[si];
        Pop.Close(_host);
        _host = null;
        var c = App.Client;
        if (c != null) _ = c.Rest.SendStickerAsync(_channel, s.Id);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        _scroll.Wheel(e.Delta, MaxScroll);
        base.OnMouseWheel(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        PickerChrome.Paint(g, this, PickerChrome.Tab.Stickers, PointToClient(MousePosition));
        g.SmoothingMode = SmoothingMode.AntiAlias;

        int y = Ui.S(PickerChrome.ContentTop + 12);
        var clip = g.Save();
        g.SetClip(new Rectangle(0, Ui.S(PickerChrome.ContentTop), Width, Height - Ui.S(PickerChrome.ContentTop)));
        for (int pi = 0; pi < _packs.Count; pi++)
        {
            var (pack, list) = _packs[pi];
            if (y - _scroll.Value > Height) break;
            Ui.Text(g, pack, Theme.SmallMedium, new Rectangle(Ui.S(16), y - _scroll.Value, Width - Ui.S(32), HeaderH - Ui.S(4)),
                    Theme.Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            y += HeaderH;

            for (int i = 0; i < list.Count; i++)
            {
                int col = i % Cols, row = i / Cols;
                var box = new Rectangle(Ui.S(12) + col * (Cell + Gap), y + row * (Cell + Gap) - _scroll.Value, Cell, Cell);
                if (box.Bottom < 0 || box.Top > Height) continue;
                bool hot = _hover == pi * 1000 + i;
                var s = list[i];
                if (s.Renderable)
                {
                    using (var path = Ui.RoundRect(box, Ui.S(6)))
                    {
                        var st = g.Save();
                        g.SetClip(path, CombineMode.Replace);
                        // Lottie is rasterised by Skottie; everything else is an ordinary image.
                        if (s.IsLottie)
                        {
                            if (!Lottie.Draw(g, s.Url, box, this)) Ui.Fill(g, box, Theme.Surface);
                        }
                        else if (Media.Get(s.Url, this) is { } img)
                        {
                            if (Media.IsAnimated(img)) Media.Animate(img, this);
                            g.DrawImage(img, box);
                        }
                        else Ui.Fill(g, box, Theme.Surface);
                        g.Restore(st);
                    }
                }
                else
                {
                    Ui.FillRound(g, box, Ui.S(6), Theme.Surface);
                    Ui.Text(g, s.Name, Theme.Small, new Rectangle(box.X + Ui.S(4), box.Y, box.Width - Ui.S(8), box.Height),
                            Theme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
                                       | TextFormatFlags.EndEllipsis);
                }
                if (hot)
                {
                    using var pen = new Pen(Theme.Blurple, Math.Max(1, Ui.S(2)));
                    using var path = Ui.RoundRect(box, Ui.S(6));
                    g.DrawPath(pen, path);
                }
            }
            y += ((list.Count + Cols - 1) / Cols) * (Cell + Gap);
        }
        g.Restore(clip);

        if (_packs.Count == 0)
            Ui.Text(g, "Loading stickers…", Theme.Body, new Rectangle(Ui.S(16), Ui.S(60), Width - Ui.S(32), Ui.S(24)),
                    Theme.Muted, TextFormatFlags.HorizontalCenter);
    }
}
