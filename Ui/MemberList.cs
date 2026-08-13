using System.Drawing;
using System.Drawing.Drawing2D;

namespace OpenCord;

// The 264px member column: role group headers, then one row per member.
//
// 264 is measured, not the classic 240 — rows are 248 wide with an 8px margin either side. Role
// headers keep the role's own casing; the refresh stopped upper-casing them.
sealed class MemberList : Control
{
    public sealed record Entry(bool IsGroup, string Name, string? AvatarUrl = null,
                               Presence Presence = Presence.Offline, Color? NameColor = null,
                               string? Activity = null, UserUser? User = null);

    /// A 1:1 DM's column: the recipient's profile instead of a roster — what the real client's
    /// "Show Member List" opens in a DM. Holds the live UserUser object, so presence and status
    /// updates paint through without a refetch.
    public sealed record Profile(UserUser User, UserProfile? Data);

    Profile? _profile;
    readonly List<Entry> _items = new();
    int _hover = -1;
    readonly Scroller _scroll;

    // Profile-mode hit boxes, filled by PaintProfile. The two banner buttons live inside the
    // scrolled content; the footer button is pinned, so only it is in plain client coordinates.
    Rectangle _friendBox, _moreBox, _viewBox;
    bool _viewHot;

    /// Right-click on a member. The session builds the menu — see MemberContextMenu.
    public event Action<UserUser, Point>? MemberMenu;

    /// (firstVisibleRow, lastVisibleRow) as the column is scrolled. The gateway only sends the
    /// member rows you are subscribed to, so without this a large server stopped at its first
    /// hundred rows no matter how far down you went.
    public event Action<int, int>? RangeNeeded;

    // The 100-block last asked for, so a scroll inside it costs nothing.
    int _askedBlock = -1;

    public MemberList()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Chat;
        _scroll = new Scroller(this);
        Width = Ui.S(M.MembersWidth);
    }

    /// Called when the column starts over on a different channel, so the first scroll re-subscribes.
    public void ResetPaging() { _askedBlock = -1; }

    public void SetProfile(Profile? p)
    {
        _profile = p;
        _items.Clear();
        // The profile panel is a wider column than the roster — 320 against 264. Docked right, so
        // setting Width is the whole re-layout.
        Width = Ui.S(p != null ? M.DmProfileWidth : M.MembersWidth);
        BuildSections();
        _scroll.Clamp(MaxScroll);
        Invalidate();
    }

    public void SetMembers(IEnumerable<Entry> items)
    {
        _profile = null;
        _sections.Clear();
        Width = Ui.S(M.MembersWidth);
        _items.Clear();
        _items.AddRange(items);
        _scroll.Clamp(MaxScroll);   // a presence tick must not throw the list to the top
        Invalidate();
    }

    /// The profile fetch landed; refresh the panel's data without touching the live user object
    /// (presence/status keep painting through it). Ignored if the column moved to another DM.
    public void UpdateProfile(ulong userId, UserProfile? data)
    {
        if (_profile is { } p && p.User.Id == userId && !ReferenceEquals(p.Data, data))
        {
            _profile = p with { Data = data };
            BuildSections();
            _scroll.Clamp(MaxScroll);
            Invalidate();
        }
    }

    int RowH(Entry e) => e.IsGroup ? Ui.S(M.MemberGroupHeader) : Ui.S(M.MemberRow);
    int ContentH => _profile != null ? ProfileH : _items.Sum(RowH) + Ui.S(16);
    // In profile mode the footer button is pinned outside the scroller, so the viewport is the card
    // less that footer — 8px of margin each side of the card plus its own 72px footer.
    int MaxScroll => Math.Max(0, ContentH - (_profile != null ? Height - Ui.S(90) : Height));

    // The written sections, laid out once when the profile lands rather than per paint.
    List<ProfileSurface.Section> _sections = new();

    // The source text the cache was built from. A presence tick reaches this column as a bare
    // Invalidate() — the live user object paints straight through for the dot and the name, but
    // the status and activity are laid-out text and would otherwise repaint the old ones.
    (string?, string?, string?) _built;

    bool SectionsStale => _profile is { } pr
        && _built != (pr.User.CustomStatus, pr.User.ActivityLine, pr.Data?.Bio);

    void BuildSections()
    {
        if (_profile is not { } prof) { _sections = new(); _built = default; return; }
        var (u, p) = (prof.User, prof.Data);
        _built = (u.CustomStatus, u.ActivityLine, p?.Bio);
        // Width is the column, then the card's margin, then the body inset — all constants, so the
        // layout survives a resize (the column only ever changes height).
        _sections = ProfileSurface.Sections(u, p, Ui.S(M.DmProfileWidth) - Ui.S(16) - Ui.S(36));
    }

    // Design px, card-relative — the panel's own origins. Its avatar sits 4px higher and its body
    // 6px lower than the popout's; everything else is the shared card (ProfileSurface).
    const int AvatarY = 57, ButtonsY = 10, BodyTop = 154;

    int ProfileH
    {
        get
        {
            int h = Ui.S(BodyTop) + Ui.S(ProfileSurface.NameH);
            if (ProfileSurface.HasMutuals(_profile!.Data)) h += Ui.S(12) + Ui.S(16);
            return h + ProfileSurface.Height(_sections) + Ui.S(16);
        }
    }

    int EntryY(int i)
    {
        int y = Ui.S(8);
        for (int k = 0; k < i; k++) y += RowH(_items[k]);
        return y;
    }

    int HitTest(Point p)
    {
        int y = p.Y + _scroll.Value, acc = Ui.S(8);
        for (int i = 0; i < _items.Count; i++)
        {
            int h = RowH(_items[i]);
            if (y >= acc && y < acc + h) return _items[i].IsGroup ? -1 : i;
            acc += h;
        }
        return -1;
    }

    /// A point in the scrolled content's coordinates — the banner buttons are laid out there, the
    /// pinned footer button is not.
    Point Scrolled(Point p) => new(p.X, p.Y + _scroll.Value);

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_profile != null)
        {
            var sp = Scrolled(e.Location);
            bool hot = _viewBox.Contains(e.Location);
            if (hot != _viewHot) { _viewHot = hot; Invalidate(); }
            Cursor = hot || _moreBox.Contains(sp) || _friendBox.Contains(sp) ? Cursors.Hand : Cursors.Default;
            base.OnMouseMove(e);
            return;
        }
        int h = HitTest(e.Location);
        if (h != _hover) { _hover = h; Invalidate(); }
        Cursor = h >= 0 ? Cursors.Hand : Cursors.Default;
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        if (_hover != -1) { _hover = -1; Invalidate(); }
        if (_viewHot) { _viewHot = false; Invalidate(); }
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (_profile is { } prof)
        {
            var sp = Scrolled(e.Location);
            if (_viewBox.Contains(e.Location) || _friendBox.Contains(sp))
                ProfileCard.Show(this, prof.User, PointToScreen(new Point(-Ui.S(310), Ui.S(60))));
            else if (_moreBox.Contains(sp))
                MemberMenu?.Invoke(prof.User, PointToScreen(e.Location));
            base.OnMouseDown(e);
            return;
        }
        int i = HitTest(e.Location);
        if (i < 0 || _items[i].User == null) return;
        if (e.Button == MouseButtons.Left)
            ProfileCard.Show(this, _items[i].User!, PointToScreen(new Point(-Ui.S(310), e.Y - Ui.S(60))));
        else if (e.Button == MouseButtons.Right)
            // Built by the session, not here: the moderation entries depend on the client, the
            // guild and our own permissions, none of which the member list knows about.
            MemberMenu?.Invoke(_items[i].User!, PointToScreen(e.Location));
        base.OnMouseDown(e);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        _scroll.Wheel(e.Delta, MaxScroll);
        RequestVisibleRange();
    }

    /// Ask the gateway for the rows now on screen, but only when the scroll has crossed into a
    /// block we have not subscribed to — one op-14 per notch would flood the socket.
    void RequestVisibleRange()
    {
        if (_items.Count == 0) return;
        int first = RowIndexAt(_scroll.Value);
        int last = RowIndexAt(_scroll.Value + Height);
        int block = last / 100;
        if (block == _askedBlock) return;
        _askedBlock = block;
        RangeNeeded?.Invoke(first, last + 100);   // a block of lookahead, so scrolling stays smooth
    }

    // Which entry sits at a given content offset. Rows are not a fixed height (group headers are
    // taller), so this walks rather than divides.
    int RowIndexAt(int contentY)
    {
        int acc = Ui.S(8);
        for (int i = 0; i < _items.Count; i++)
        {
            acc += RowH(_items[i]);
            if (acc > contentY) return i;
        }
        return _items.Count - 1;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Ui.Fill(g, ClientRectangle, Theme.Chat);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        if (_profile != null) { PaintProfile(g); return; }

        for (int i = 0; i < _items.Count; i++)
        {
            int y = EntryY(i) - _scroll.Value;
            if (y + RowH(_items[i]) < 0 || y > Height) continue;
            if (_items[i].IsGroup) DrawGroup(g, y, _items[i]);
            else DrawMember(g, y, i, _items[i]);
        }
    }

    /// The 1:1 DM profile panel — the live client's "Show Member List" in a DM.
    ///
    /// Laid out against the real thing rather than by eye: a 304px card inset 8 from the column,
    /// a 105px banner, an 80px avatar hanging off it at 16,57, and a 268px body starting at 154.
    /// The vertical walk must match ProfileH, which is why both walk _sections.
    void PaintProfile(Graphics g)
    {
        if (SectionsStale) { BuildSections(); _scroll.Clamp(MaxScroll); }
        var (u, p) = (_profile!.User, _profile.Data);
        var card = new Rectangle(Ui.S(8), Ui.S(8), Width - Ui.S(16), Height - Ui.S(16));
        if (card.Width <= 0 || card.Height <= 0) return;

        // Discord builds the card out of the profile's own theme: a 2px frame carrying a vertical
        // primary -> secondary gradient, with the body the same gradient under a 60% black scrim.
        // That is why a themed profile reads as a dark saturated wash and not as a tinted grey —
        // the first version of this panel tinted the chat background 5% and came out near-neutral.
        // A user with no theme falls back to their accent colour, and then to the plain surface.
        var paint = ProfileSurface.Colors(p, Theme.Floating);
        var body = paint.Body;
        // The panel's gradient reaches its second colour 72px above the bottom, so the footer
        // button sits on flat secondary. Running it to the very bottom instead is what made this
        // card read as a different colour from the popout at the same row.
        var inner = ProfileSurface.PaintCard(g, card, paint, ProfileSurface.PanelFlatBottom);

        // Everything but the footer button scrolls, so the viewport is clipped before the transform
        // and the content drawn in card-relative coordinates from there on.
        var footer = new Rectangle(inner.X, inner.Bottom - Ui.S(72), inner.Width, Ui.S(72));
        var st = g.Save();
        g.SetClip(new Rectangle(inner.X, inner.Y, inner.Width, footer.Y - inner.Y), CombineMode.Intersect);
        g.TranslateTransform(0, -_scroll.Value);

        ProfileSurface.PaintBanner(g, inner, u, p, paint, this, Theme.Blurple);
        ProfileSurface.PaintAvatar(g, card, AvatarY, u, body, this);
        (_friendBox, _moreBox) = ProfileSurface.PaintBannerButtons(g, card, ButtonsY);

        int x = card.X + Ui.S(ProfileSurface.BodyPad);
        int w = card.Width - Ui.S(ProfileSurface.BodyPad * 2);
        int y = card.Y + Ui.S(BodyTop);

        y = ProfileSurface.PaintName(g, u, p, u.DisplayName, x, y, w, paint, this);
        if (ProfileSurface.HasMutuals(p))
        {
            y += Ui.S(12);
            ProfileSurface.PaintMutuals(g, x, y, w, p!, this, paint);
            y += Ui.S(16);
        }
        ProfileSurface.PaintSections(g, _sections, x, y, w, this, paint);
        g.Restore(st);

        // "View Full Profile", pinned below the scroller.
        _viewBox = new Rectangle(inner.X + Ui.S(16), footer.Y + Ui.S(16), inner.Width - Ui.S(32), Ui.S(39));
        Ui.FillRound(g, _viewBox, Ui.S(8), Theme.Tint(body, Color.White, _viewHot ? 0.16f : 0.08f));
        Ui.Text(g, "View Full Profile", Theme.H3, _viewBox, Theme.Strong,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    void DrawGroup(Graphics g, int y, Entry e)
    {
        var r = new Rectangle(Ui.S(16), y + Ui.S(20), Width - Ui.S(24), Ui.S(18));
        Ui.Text(g, e.Name, Theme.Category, r, Theme.ChannelIcon,
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }

    void DrawMember(Graphics g, int y, int i, Entry e)
    {
        int inset = Ui.S(8);
        var row = new Rectangle(inset, y, Width - inset * 2, Ui.S(M.MemberRow) - Ui.S(2));
        bool hot = _hover == i;
        if (hot) Ui.FillRound(g, row, Ui.S(8), Theme.RowHover);

        // Offline members are drawn at 30% opacity in the live client — name *and* avatar.
        bool dim = e.Presence == Presence.Offline;
        int av = Ui.S(M.MemberAvatar);
        var abox = new Rectangle(row.X + Ui.S(8), row.Y + (row.Height - av) / 2, av, av);
        var img = Media.Get(e.AvatarUrl, this);
        if (dim && img != null) Ui.AvatarDim(g, img, abox, Theme.Surface, 0.35f);
        else Ui.Avatar(g, img, abox, Theme.Surface, this);
        Ui.PresenceDot(g, abox, e.Presence, hot ? Theme.RowHover : Theme.Chat);

        int tx = abox.Right + Ui.S(12);
        int tw = row.Right - tx - Ui.S(8);
        bool two = !string.IsNullOrEmpty(e.Activity);

        // A member with no role colour is 16px/400 in --channel-icon, not the brighter --text-muted
        // at medium weight this used to draw — measured off the live roster.
        var fg = e.NameColor ?? Theme.ChannelIcon;
        if (dim) fg = Color.FromArgb(110, fg);

        // The server tag takes its width off the name's, so a long name ellipsises rather than
        // running underneath the chiplet.
        int tagW = e.User?.ServerTag is { Tag: { } tg } ? Ui.TagChipWidth(tg) + Ui.S(4) : 0;
        var nameBox = new Rectangle(tx, row.Y + (two ? Ui.S(2) : 0), tw - tagW,
                                    two ? row.Height / 2 : row.Height);
        Ui.Text(g, e.Name, Theme.Body, nameBox, fg,
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        if (tagW > 0 && e.User!.ServerTag is { Tag: { } tag } pg)
            Ui.TagChip(g, tx + Math.Min(nameBox.Width, Ui.Measure(e.Name, Theme.Body).Width) + Ui.S(4),
                       nameBox.Y + nameBox.Height / 2, tag, Media.Get(pg.BadgeUrl, this),
                       hot ? Theme.RowHover : Theme.Chat);

        if (two)
            Ui.Text(g, e.Activity, Theme.Small, new Rectangle(tx, row.Y + row.Height / 2, tw, row.Height / 2 - Ui.S(2)),
                    dim ? Color.FromArgb(90, Theme.Faint) : Theme.Faint,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }
}
