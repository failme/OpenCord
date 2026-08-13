using System.Drawing;
using System.Drawing.Drawing2D;

namespace OpenCord;

// The Friends page: the home tab that owns the friend list and the add-friend box.
//
// This sits over the chat pane rather than docking, for the reason CallBanner already documents —
// a second DockStyle.Fill starves the message list of its width. Session re-bounds it to the chat's
// rectangle when it is shown.
//
// The whole relationship layer (list, accept, decline, remove, block, add) already existed on
// UserClient; only this view was missing, so nothing here talks to the REST API directly beyond
// calling those methods.
sealed class FriendsView : Control
{
    public enum Tab { Online, All, Pending, Blocked, Add }

    readonly List<(Rectangle Box, Tab T, string Label)> _tabs = new();
    readonly List<(Rectangle Box, UserRelationship R, int Action)> _rows = new();
    // Add-friend is a real input, so it gets a real TextBox for the same reason the composer does.
    readonly HintBox _add = new()
    {
        BorderStyle = BorderStyle.None,
        BackColor = Theme.InputBg,
        ForeColor = Theme.Text,
        Font = Theme.Body,
        Hint = "You can add friends with their Discord username.",
        Visible = false,
    };

    readonly Scroller _scroll;
    Tab _tab = Tab.Online;
    int _hotTab = -1, _hotRow = -1;
    string _notice = "";
    Color _noticeColor = Theme.Danger;

    public FriendsView()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Chat;
        Visible = false;
        _scroll = new Scroller(this);
        Controls.Add(_add);
        _add.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            Submit();
        };
    }

    UserClient? C => App.Client;

    public void Reload() { _scroll.Reset(); Invalidate(); }

    // ── data ────────────────────────────────────────────────────────────────────────────────────
    IEnumerable<UserRelationship> Listed() =>
        (C?.Relationships ?? new List<UserRelationship>()).Where(r => _tab switch
        {
            Tab.Online => r.Type == 1 && r.User is { } u && u.IsOnline,
            Tab.All => r.Type == 1,
            Tab.Pending => r.Type is 3 or 4,
            Tab.Blocked => r.Type == 2,
            _ => false,
        }).OrderBy(r => r.User?.DisplayName ?? "", StringComparer.OrdinalIgnoreCase);

    int PendingCount => C?.Relationships.Count(r => r.Type == 3) ?? 0;

    string Heading() => _tab switch
    {
        Tab.Online => $"Online — {Listed().Count()}",
        Tab.All => $"All Friends — {Listed().Count()}",
        Tab.Pending => $"Pending — {Listed().Count()}",
        Tab.Blocked => $"Blocked — {Listed().Count()}",
        _ => "",
    };

    string EmptyText() => _tab switch
    {
        Tab.Online => "There are no friends online at this time. Check back later!",
        Tab.All => "You haven't added any friends yet.",
        Tab.Pending => "There are no pending friend requests. Here's a cricket instead \U0001F997",
        Tab.Blocked => "You can't unblock the Wumpus.",
        _ => "",
    };

    // ── geometry ────────────────────────────────────────────────────────────────────────────────
    int HeaderH => Ui.S(M.HeaderHeight);
    int BodyTop => HeaderH + Ui.S(16);

    /// Where the title ends and the divider before the tabs sits. The header is icon + "Friends",
    /// so the tabs have to clear both — measuring only the word left them on top of the icon.
    int TitleRight => Ui.S(M.HeaderPadLeft) + Ui.S(24) + Ui.S(8)
                    + Ui.Measure("Friends", Theme.BodyMedium).Width;

    void BuildTabs()
    {
        _tabs.Clear();
        int x = TitleRight + Ui.S(17);
        // A vertical rule separates the title from the tabs in the live client; the tabs then run
        // left to right with 8px between them.
        foreach (var (t, label) in new[]
                 {
                     (Tab.Online, "Online"), (Tab.All, "All"),
                     (Tab.Pending, PendingCount > 0 ? $"Pending  {PendingCount}" : "Pending"),
                     (Tab.Blocked, "Blocked"), (Tab.Add, "Add Friend"),
                 })
        {
            int w = Ui.Measure(label, Theme.Body).Width + Ui.S(24);
            _tabs.Add((new Rectangle(x, (HeaderH - Ui.S(32)) / 2, w, Ui.S(32)), t, label));
            x += w + Ui.S(8);
        }
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        BuildTabs();
        LayoutAdd();
        base.OnSizeChanged(e);
    }

    void LayoutAdd()
    {
        // The add box is a 48px well inset 30px from either side, with room for the button.
        var well = AddWell;
        _add.SetBounds(well.X + Ui.S(12), well.Y + (well.Height - Theme.Body.Height) / 2,
                       Math.Max(1, well.Width - Ui.S(24) - AddButtonW), Theme.Body.Height);
    }

    int AddButtonW => Ui.S(150);
    Rectangle AddWell => new(Ui.S(30), BodyTop + Ui.S(52), Math.Max(1, Width - Ui.S(60)), Ui.S(48));
    Rectangle AddButton => new(AddWell.Right - AddButtonW - Ui.S(8), AddWell.Y + Ui.S(8),
                               AddButtonW, AddWell.Height - Ui.S(16));

    // ── input ───────────────────────────────────────────────────────────────────────────────────
    public void Select(Tab t)
    {
        _tab = t;
        _notice = "";
        _scroll.Reset();
        _add.Visible = t == Tab.Add;
        _add.Text = "";
        if (t == Tab.Add) { LayoutAdd(); _add.Focus(); }
        BuildTabs();
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int t = _tabs.FindIndex(x => x.Box.Contains(e.Location));
        int r = _rows.FindIndex(x => x.Box.Contains(e.Location));
        if (t != _hotTab || r != _hotRow) { _hotTab = t; _hotRow = r; Invalidate(); }
        Cursor = t >= 0 || r >= 0 || (_tab == Tab.Add && AddButton.Contains(e.Location))
                 ? Cursors.Hand : Cursors.Default;
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        if (_hotTab != -1 || _hotRow != -1) { _hotTab = -1; _hotRow = -1; Invalidate(); }
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        if (_hotTab >= 0) { Select(_tabs[_hotTab].T); return; }
        if (_tab == Tab.Add && AddButton.Contains(e.Location)) { Submit(); return; }
        if (_hotRow >= 0) { RowClick(_rows[_hotRow].R, _rows[_hotRow].Action); return; }
        base.OnMouseDown(e);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        _scroll.Wheel(e.Delta, Math.Max(0, _rows.Count * Ui.S(M.FriendRow) + Ui.S(80) - Height));
    }

    // Action 0 opens a DM (or accepts, on an incoming request); 1 removes/declines/unblocks.
    void RowClick(UserRelationship r, int action)
    {
        if (action == 0)
        {
            if (r.Type == 3) { _ = Run(C!.Rest.RelateAsync(r.Id, null)); return; }
            if (r.Type == 1) { App.OpenDm?.Invoke(r.Id); return; }
            return;
        }
        _ = Run(C!.Rest.UnrelateAsync(r.Id));
    }

    void Submit()
    {
        var name = _add.Text.Trim();
        if (name.Length == 0 || C == null) return;
        _ = Run(C.Rest.AddFriendAsync(name), ok: $"Success! Your friend request to {name} was sent.");
    }

    async Task Run(Task<string?> call, string? ok = null)
    {
        string? err;
        try { err = await call; }
        catch (Exception ex) { err = ex.Message; }
        if (IsDisposed) return;
        BeginInvoke(() =>
        {
            _notice = err ?? ok ?? "";
            _noticeColor = err == null ? Theme.Positive : Theme.Danger;
            if (err == null && ok != null) _add.Text = "";
            Invalidate();
        });
    }

    // ── paint ───────────────────────────────────────────────────────────────────────────────────
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Ui.Fill(g, ClientRectangle, Theme.Chat);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        if (_tabs.Count == 0) BuildTabs();
        DrawHeader(g);
        if (_tab == Tab.Add) DrawAdd(g); else DrawList(g);
    }

    void DrawHeader(Graphics g)
    {
        int icon = Ui.S(24);
        Svg.SvgFill(g, Icons.People,
                    new RectangleF(Ui.S(M.HeaderPadLeft), (HeaderH - icon) / 2f, icon, icon), Theme.ChannelIcon);
        Ui.Text(g, "Friends", Theme.BodyMedium,
                new Rectangle(Ui.S(M.HeaderPadLeft) + icon + Ui.S(8), 0, Ui.S(120), HeaderH),
                Theme.Strong, TextFormatFlags.VerticalCenter);
        Ui.Fill(g, new Rectangle(TitleRight + Ui.S(8), (HeaderH - Ui.S(24)) / 2, 1, Ui.S(24)), Theme.Border);

        for (int i = 0; i < _tabs.Count; i++)
        {
            var (box, t, label) = _tabs[i];
            bool sel = t == _tab;
            // Add Friend is a solid blurple button in the live client, not a tab.
            if (t == Tab.Add) Ui.FillRound(g, box, Ui.S(8), sel || _hotTab == i ? Theme.BlurpleHover : Theme.Blurple);
            else if (sel) Ui.FillRound(g, box, Ui.S(8), Theme.SurfaceHigh);
            else if (_hotTab == i) Ui.FillRound(g, box, Ui.S(8), Theme.Surface);

            Ui.Text(g, label, Theme.Body, box,
                    t == Tab.Add || sel ? Theme.Strong : Theme.Subtle,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
        Ui.Fill(g, new Rectangle(0, HeaderH - 1, Width, 1), Theme.BorderSubtle);
    }

    void DrawAdd(Graphics g)
    {
        Ui.Text(g, "ADD FRIEND", Theme.Category,
                new Rectangle(Ui.S(30), BodyTop, Width - Ui.S(60), Ui.S(20)), Theme.Strong);
        Ui.Text(g, "You can add friends with their Discord username.", Theme.Small,
                new Rectangle(Ui.S(30), BodyTop + Ui.S(24), Width - Ui.S(60), Ui.S(20)), Theme.Faint);

        var well = AddWell;
        Ui.FillRound(g, well, Ui.S(8), Theme.InputBg);
        _add.BackColor = Theme.InputBg;

        var btn = AddButton;
        bool on = _add.Text.Trim().Length > 0;
        Ui.FillRound(g, btn, Ui.S(8), on ? Theme.Blurple : Color.FromArgb(60, Theme.Blurple));
        Ui.Text(g, "Send Friend Request", Theme.SmallMedium, btn,
                on ? Color.White : Color.FromArgb(120, Color.White),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

        if (_notice.Length > 0)
            Ui.Text(g, _notice, Theme.Small,
                    new Rectangle(Ui.S(30), well.Bottom + Ui.S(8), Width - Ui.S(60), Ui.S(20)), _noticeColor);
    }

    void DrawList(Graphics g)
    {
        _rows.Clear();
        var items = Listed().ToList();

        if (items.Count == 0)
        {
            Ui.Text(g, EmptyText(), Theme.Body,
                    new Rectangle(0, BodyTop + Ui.S(60), Width, Ui.S(24)), Theme.Faint,
                    TextFormatFlags.HorizontalCenter);
            return;
        }

        Ui.Text(g, Heading(), Theme.Category,
                new Rectangle(Ui.S(30), BodyTop, Width - Ui.S(60), Ui.S(20)), Theme.ChannelIcon);

        int y = BodyTop + Ui.S(28) - _scroll.Value;
        int rowH = Ui.S(M.FriendRow);
        var st = g.Save();
        g.SetClip(new Rectangle(0, HeaderH, Width, Height - HeaderH));

        foreach (var r in items)
        {
            var row = new Rectangle(Ui.S(30), y, Math.Max(1, Width - Ui.S(60)), rowH - Ui.S(2));
            if (row.Bottom >= HeaderH && row.Top <= Height)
            {
                bool hot = _hotRow >= 0 && _hotRow < _rows.Count && _rows[_hotRow].R == r;
                if (hot) Ui.FillRound(g, row, Ui.S(8), Theme.RowHover);
                DrawFriend(g, row, r);
            }
            y += rowH;
        }
        g.Restore(st);
    }

    void DrawFriend(Graphics g, Rectangle row, UserRelationship r)
    {
        var u = r.User;
        int av = Ui.S(32);
        var ab = new Rectangle(row.X + Ui.S(8), row.Y + (row.Height - av) / 2, av, av);
        Ui.Avatar(g, Media.Get(u?.GetAvatarUrl(64), this), ab, Theme.Surface, this);
        Ui.PresenceDot(g, ab, u?.Presence ?? Presence.Offline, Theme.Chat, Ui.S(11));

        // Two action circles on the right: primary (message / accept) then dismiss (remove, decline,
        // unblock). A blocked or outgoing row has nothing useful for the primary slot.
        int b = Ui.S(32), gap = Ui.S(8);
        int bx = row.Right - Ui.S(8) - b;
        bool primary = r.Type == 1 || r.Type == 3;

        void Circle(string icon, int action, Color fg)
        {
            var box = new Rectangle(bx, row.Y + (row.Height - b) / 2, b, b);
            bool hot = _hotRow >= 0 && _hotRow < _rows.Count
                       && _rows[_hotRow].R == r && _rows[_hotRow].Action == action;
            Ui.FillRound(g, box, b / 2, hot ? Theme.SurfaceHigh : Theme.Rail);
            Icons.Draw(g, icon, Rectangle.Inflate(box, -Ui.S(9), -Ui.S(9)), hot ? Theme.Text : fg);
            _rows.Add((box, r, action));
            bx -= b + gap;
        }

        Circle(Icons.CloseLine, 1, Theme.ChannelIcon);
        if (primary) Circle(r.Type == 3 ? Icons.CheckLine : Icons.InboxLine, 0, Theme.ChannelIcon);

        int tx = ab.Right + Ui.S(12);
        int tw = bx - tx;
        string sub = r.Type switch
        {
            3 => "Incoming Friend Request",
            4 => "Outgoing Friend Request",
            2 => "Blocked",
            _ => u?.StatusText ?? "Offline",
        };
        Ui.Text(g, u?.DisplayName ?? "unknown", Theme.BodyMedium,
                new Rectangle(tx, row.Y + Ui.S(3), tw, row.Height / 2), Theme.Text,
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        Ui.Text(g, sub, Theme.Small,
                new Rectangle(tx, row.Y + row.Height / 2, tw, row.Height / 2 - Ui.S(3)), Theme.Faint,
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }
}
