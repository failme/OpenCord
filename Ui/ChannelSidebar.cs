using System.Drawing;
using System.Drawing.Drawing2D;

namespace ClaudeScord;

// Guild header, the channel list, and the account tray — or, in home mode, the DM list.
//
// Scrolling is a plain integer offset rather than AutoScroll. AutoScroll computes its range from the
// child bounds *excluding* Padding, and its Layout fires before the children have been laid out, so
// the last row is unreachable until something forces a second pass. Owning the offset is less code
// and has none of that.
sealed class ChannelSidebar : Control
{
    public enum Kind { Category, Text, Voice, Announcement, Forum, Dm, GroupDm, Nav, VoiceMember, Thread }

    public sealed record Entry(Kind Kind, ulong Id, string Name, bool Unread = false, int Mentions = 0,
                               string? AvatarUrl = null, Presence Presence = Presence.Offline,
                               string? Subtitle = null, bool Muted = false,
                               UserPrimaryGuild? Tag = null);

    /// Home-mode rows that are destinations rather than channels. Real ids are snowflakes, so small
    /// integers can never collide with one; the DM list already uses 1 for its category header.
    public const ulong FriendsId = 2;
    /// The guild's "Events" row, which sits above the channel list like the live client's.
    public const ulong EventsId = 3;

    readonly List<Entry> _items = new();
    readonly HashSet<ulong> _collapsed = new();

    /// Collapse or expand every category at once — the live client's "Collapse All Categories".
    public void CollapseAll(bool on)
    {
        _collapsed.Clear();
        if (on)
            foreach (var it in _items)
                if (it.Kind == Kind.Category) _collapsed.Add(it.Id);
        Invalidate();
    }
    int _hover = -1;
    readonly Scroller _scroll;
    // Discord eases hover state over ~100ms instead of snapping, fading the row you leave out
    // while the new one fades in. State is per-row (index -> 0..1) so both can animate at once;
    // the selected row snaps, as the real client's selection is instant.
    readonly Dictionary<int, float> _fades = new();
    readonly List<int> _keys = new();
    readonly System.Windows.Forms.Timer _fade = new() { Interval = 15 };
    readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

    public string GuildName = "";
    public bool HomeMode;
    public ulong? SelectedChannel;
    public event Action<ulong>? ChannelPicked;
    public event Action<Entry, Point>? ChannelMenu;
    public event Action? GuildMenu;
    public event Action? InviteRequested;      // the person-plus button in the guild header

    Rectangle _inviteBox;
    bool _inviteHot;

    /// Transient feedback under the invite button — the same trick the composer uses to report a
    /// failed slash command, rather than a desktop toast, which is for messages.
    public void FlashInvite(string message) => Tip.Show(this, message, _inviteBox);
    public event Action? QuickSwitcher;

    public ChannelSidebar()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Sidebar;
        _scroll = new Scroller(this);
        Width = Ui.S(M.SidebarWidth);
        // Glide only runs while there is distance left, so it costs nothing at rest. Eased on
        // elapsed time rather than per tick — see Ui.Ease.
        _fade.Tick += (_, _) =>
        {
            float dt = (float)_clock.Elapsed.TotalSeconds;
            _clock.Restart();
            // The row under the pointer keeps its fade state so a fast sweep across rows reads
            // as one continuous pass instead of a per-row pop.
            if (_hover >= 0 && !_fades.ContainsKey(_hover)) _fades[_hover] = 0f;
            bool moving = false;
            _keys.Clear(); _keys.AddRange(_fades.Keys);
            foreach (var i in _keys)
            {
                float want = i == _hover ? 1f : 0f;
                float cur = _fades[i];
                if (Math.Abs(want - cur) < 0.02f)
                {
                    if (want == 0f) _fades.Remove(i);   // settled at none: drop the entry
                    else _fades[i] = want;
                }
                else { _fades[i] = Ui.Ease(cur, want, dt, 24f); moving = true; }
            }
            if (!moving) _fade.Stop();
            Invalidate();
        };
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _fade.Dispose();
        base.Dispose(disposing);
    }

    public void SetChannels(string guildName, IEnumerable<Entry> items, bool home = false)
    {
        GuildName = guildName;
        HomeMode = home;
        _items.Clear();
        _items.AddRange(items);
        _scroll.Reset();
        Invalidate();
    }

    /// Refresh row state (unread, mentions, presence) without resetting the scroll position.
    public void Refresh(IEnumerable<Entry> items)
    {
        _items.Clear();
        _items.AddRange(items);
        Invalidate();
    }

    // ── geometry ────────────────────────────────────────────────────────────────────────────────
    int HeaderH => Ui.S(M.HeaderHeight);
    // Space reserved at the bottom for the account panel, which is its own control spanning this
    // column and the guild rail — see AccountTray.
    int TrayH => Ui.S(AccountTray.TrayH);

    // ── voice status strip ──────────────────────────────────────────────────────────────────────
    // Sits directly above the user tray while connected, exactly as the live client does: a signal
    // icon, "Voice Connected" in green (amber while the transport is still negotiating), the
    // channel / server underneath, and a Disconnect button on the right.
    public string? VoiceChannelName { get; private set; }
    public string? VoiceGuildName { get; private set; }
    public bool VoiceConnecting { get; private set; }

    public event Action? VoiceDisconnect;

    public void SetVoiceStatus(string? channel, string? guild, bool connecting)
    {
        if (VoiceChannelName == channel && VoiceGuildName == guild && VoiceConnecting == connecting) return;
        VoiceChannelName = channel;
        VoiceGuildName = guild;
        VoiceConnecting = connecting;
        Invalidate();
    }

    int VoiceH => VoiceChannelName == null ? 0 : Ui.S(54);
    Rectangle VoiceBar => new(0, Height - TrayH - VoiceH, Width, VoiceH);
    Rectangle VoiceHangUp => new(Width - Ui.S(8) - Ui.S(32), VoiceBar.Y + (VoiceH - Ui.S(32)) / 2, Ui.S(32), Ui.S(32));
    bool _hangUpHot;
    int ListTop => HeaderH;
    int ListH => Math.Max(0, Height - HeaderH - TrayH - VoiceH);

    int RowH(Entry e) => e.Kind switch
    {
        Kind.Category => Ui.S(M.CategoryHeight),
        Kind.Dm or Kind.GroupDm => Ui.S(44),
        Kind.VoiceMember => Ui.S(26),
        Kind.Nav => Ui.S(M.NavRow + 2),
        _ => Ui.S(M.ChannelRow + 2),
    };

    // A thread row is a channel row pushed 12px deeper, with a smaller thread glyph.
    int ThreadInset => Ui.S(M.ChannelRowInset) + Ui.S(12);

    bool Hidden(int i)
    {
        // Everything under a collapsed category disappears, except channels that are unread —
        // which is exactly what the real client keeps visible.
        for (int k = i - 1; k >= 0; k--)
            if (_items[k].Kind == Kind.Category)
                return _collapsed.Contains(_items[k].Id) && !_items[i].Unread
                       && _items[i].Id != SelectedChannel && _items[i].Kind != Kind.VoiceMember;
        return false;
    }

    int ContentH
    {
        get
        {
            int h = Ui.S(8);
            for (int i = 0; i < _items.Count; i++) if (!Hidden(i)) h += RowH(_items[i]);
            return h + Ui.S(8);
        }
    }

    int MaxScroll => Math.Max(0, ContentH - ListH);

    int EntryY(int i)
    {
        int y = Ui.S(8);
        for (int k = 0; k < i; k++) if (!Hidden(k)) y += RowH(_items[k]);
        return y;
    }

    int HitTest(Point p)
    {
        if (p.Y < ListTop || p.Y >= ListTop + ListH) return -1;
        int y = p.Y - ListTop + _scroll.Value, acc = Ui.S(8);
        for (int i = 0; i < _items.Count; i++)
        {
            if (Hidden(i)) continue;
            int h = RowH(_items[i]);
            if (y >= acc && y < acc + h) return _items[i].Kind == Kind.VoiceMember ? -1 : i;
            acc += h;
        }
        return -1;
    }

    // ── input ───────────────────────────────────────────────────────────────────────────────────
    protected override void OnMouseMove(MouseEventArgs e)
    {
        int h = HitTest(e.Location);
        bool inv = !HomeMode && _inviteBox.Contains(e.Location);
        bool hang = VoiceChannelName != null && VoiceHangUp.Contains(e.Location);
        bool ng = !_newGroupBox.IsEmpty && _newGroupBox.Contains(e.Location);
        if (h != _hover || inv != _inviteHot || hang != _hangUpHot || ng != _newGroupHot)
        {
            _hover = h; _inviteHot = inv; _hangUpHot = hang; _newGroupHot = ng;
            if (!_fade.Enabled) { _clock.Restart(); _fade.Start(); }
            // The leaving row must ease back out, so it needs a seeded entry too.
            if (_hover >= 0 && !_fades.ContainsKey(_hover)) _fades[_hover] = 0f;
            Tip.Show(this, inv ? "Invite to Server" : null, inv ? _inviteBox : Rectangle.Empty);
            Invalidate();
        }
        Cursor = h >= 0 || e.Y < HeaderH ? Cursors.Hand : Cursors.Default;
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        if (_hover != -1 || _inviteHot)
        {
            _hover = -1; _inviteHot = false; _hangUpHot = false; Tip.Hide();
            if (!_fade.Enabled) { _clock.Restart(); _fade.Start(); }
            Invalidate();
        }
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Y < HeaderH && e.Button == MouseButtons.Left)
        {
            if (!HomeMode && _inviteBox.Contains(e.Location)) InviteRequested?.Invoke();
            else if (HomeMode) QuickSwitcher?.Invoke();
            else GuildMenu?.Invoke();
            return;
        }
        if (VoiceChannelName != null && VoiceHangUp.Contains(e.Location) && e.Button == MouseButtons.Left)
        { VoiceDisconnect?.Invoke(); return; }

        int i = HitTest(e.Location);
        if (i < 0) return;
        var it = _items[i];

        if (e.Button == MouseButtons.Right) { ChannelMenu?.Invoke(it, PointToScreen(e.Location)); return; }
        if (e.Button != MouseButtons.Left) return;
        // The "+" sits inside the header row, so it has to win before the row's own click.
        if (!_newGroupBox.IsEmpty && _newGroupBox.Contains(e.Location)) { NewGroupClicked?.Invoke(); return; }

        if (it.Kind == Kind.Category)
        {
            if (!_collapsed.Add(it.Id)) _collapsed.Remove(it.Id);
            Invalidate();
            return;
        }
        SelectedChannel = it.Id;
        Invalidate();
        ChannelPicked?.Invoke(it.Id);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        _scroll.Wheel(e.Delta, MaxScroll);
    }

    // ── paint ───────────────────────────────────────────────────────────────────────────────────
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Ui.Fill(g, ClientRectangle, Theme.Sidebar);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        DrawHeader(g);

        // Clip the list so rows cannot bleed into the header or the tray while scrolling.
        var st = g.Save();
        g.SetClip(new Rectangle(0, ListTop, Width, ListH));
        for (int i = 0; i < _items.Count; i++)
        {
            if (Hidden(i)) continue;
            int y = ListTop + EntryY(i) - _scroll.Value;
            if (y + RowH(_items[i]) < ListTop || y > ListTop + ListH) continue;   // offscreen
            switch (_items[i].Kind)
            {
                case Kind.Category: DrawCategory(g, y, _items[i], i); break;
                case Kind.Nav: DrawNav(g, y, i, _items[i]); break;
                case Kind.Dm or Kind.GroupDm: DrawDm(g, y, i, _items[i]); break;
                case Kind.VoiceMember: DrawVoiceMember(g, y, _items[i]); break;
                case Kind.Thread: DrawThread(g, y, i, _items[i]); break;
                default: DrawChannel(g, y, i, _items[i]); break;
            }
        }
        g.Restore(st);

        DrawVoiceBar(g);
    }

    void DrawVoiceBar(Graphics g)
    {
        if (VoiceChannelName == null) return;
        var r = VoiceBar;
        Ui.Fill(g, r, Theme.Tray);
        Ui.Fill(g, new Rectangle(r.X + Ui.S(8), r.Y, r.Width - Ui.S(16), 1), Theme.BorderSubtle);

        int ic = Ui.S(20);
        var status = VoiceConnecting ? Theme.Warning : Theme.Positive;
        Svg.SvgFill(g, Icons.Speaker, new RectangleF(Ui.S(10), r.Y + Ui.S(10), ic, ic), status);

        int tx = Ui.S(10) + ic + Ui.S(8);
        int tw = VoiceHangUp.X - tx - Ui.S(6);
        Ui.Text(g, VoiceConnecting ? "Voice Connecting" : "Voice Connected", Theme.SmallMedium,
                new Rectangle(tx, r.Y + Ui.S(6), tw, Ui.S(20)), status,
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        Ui.Text(g, VoiceChannelName + (VoiceGuildName == null ? "" : " / " + VoiceGuildName), Theme.Small,
                new Rectangle(tx, r.Y + Ui.S(26), tw, Ui.S(18)), Theme.Faint,
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

        var hb = VoiceHangUp;
        if (_hangUpHot) Ui.FillRound(g, hb, Ui.S(6), Theme.SidebarHover);
        Svg.SvgFill(g, Icons.PhoneLine, new RectangleF(hb.X + Ui.S(7), hb.Y + Ui.S(7), Ui.S(18), Ui.S(18)),
                    _hangUpHot ? Theme.Danger : Theme.ChannelIcon);
    }

    void DrawHeader(Graphics g)
    {
        var r = new Rectangle(0, 0, Width, HeaderH);
        Ui.Fill(g, r, Theme.Sidebar);

        if (HomeMode)
        {
            // Measured: a 32-tall button inset 8 either side of a 48-tall row, 8 radius, #97979f at
            // 12%, with the label *centred* in white at 14px/500 — not a left-aligned grey
            // placeholder, which is the classic client's search field and reads quite differently.
            var box = new Rectangle(Ui.S(8), (HeaderH - Ui.S(32)) / 2, Width - Ui.S(16), Ui.S(32));
            Ui.FillRound(g, box, Ui.S(8), Theme.TagPill(Theme.Sidebar));
            Ui.Text(g, "Find or start a conversation", Theme.Category, box, Theme.Strong,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
                  | TextFormatFlags.EndEllipsis);
        }
        else
        {
            // Name at x+16, chevron immediately after it, and "Invite to Server" pinned right —
            // measured off the live header (name 88..199, chevron 200, invite button 335..367 in a
            // sidebar ending at 375). Parking the chevron at the far right instead is the classic
            // client's layout and reads wrong next to a short server name.
            int chev = Ui.S(16), inv = Ui.S(32);
            int room = Width - Ui.S(M.SidebarPadX) - chev - Ui.S(1) - inv - Ui.S(8) - Ui.S(8);
            // Measured 16px/600 — the one place in the sidebar that is not the 500 body weight.
            int nameW = Math.Min(Ui.Measure(GuildName, Theme.BodySemibold).Width, room);
            var nr = new Rectangle(Ui.S(M.SidebarPadX), 0, Math.Max(0, nameW), HeaderH);

            Ui.Text(g, GuildName, Theme.BodySemibold, nr, Theme.Strong,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            Svg.SvgFill(g, Icons.ChevronDown,
                          new RectangleF(nr.Right + Ui.S(1), (HeaderH - chev) / 2f, chev, chev), Theme.Text);

            int ic = Ui.S(M.HeaderIcon);
            _inviteBox = new Rectangle(Width - Ui.S(8) - inv, (HeaderH - inv) / 2, inv, inv);
            Svg.SvgFill(g, Icons.PersonAdd,
                          new RectangleF(_inviteBox.X + (inv - ic) / 2f, _inviteBox.Y + (inv - ic) / 2f, ic, ic),
                          _inviteHot ? Theme.Text : Theme.ChannelIcon);
        }
        Ui.Fill(g, new Rectangle(0, HeaderH - 1, Width, 1), Theme.BorderSubtle);
    }

    // Categories are NOT upper-cased in the refresh — computed text-transform is `none`. The old
    // client shouted them, which is the single most obvious "built from an old screenshot" tell.
    //
    // The chevron follows the label rather than leading it, and it is 12px, not 14. Measured against
    // the live sidebar: label at x+16 in a 18px block, chevron at label-end + 3, centred on the
    // label's own centre line. Leading the label is the classic client's layout.
    void DrawCategory(Graphics g, int y, Entry e, int i)
    {
        int cx = Ui.S(M.SidebarPadX), chev = Ui.S(12);
        var col = _hover == i ? Theme.Text : Theme.ChannelIcon;

        int room = Width - cx * 2 - chev - Ui.S(3);
        int textW = Math.Min(Ui.Measure(e.Name, Theme.Category).Width, room);
        var r = new Rectangle(cx, y + Ui.S(M.CategoryPadTop), textW, Ui.S(18));

        Ui.Text(g, e.Name, Theme.Category, r, col,
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        Svg.SvgFill(g, _collapsed.Contains(e.Id) ? Icons.ChevronRight : Icons.ChevronDown,
                      new RectangleF(r.Right + Ui.S(3), r.Y + (r.Height - chev) / 2f, chev, chev), col);

        // The "+" the live client puts at the right of the Direct Messages header, for starting a
        // group. Only that header has one — a guild category's plus is a different action.
        if (e.Id == DmHeaderId)
        {
            int d = Ui.S(14);
            _newGroupBox = new Rectangle(Width - Ui.S(M.SidebarPadX) - d, r.Y + (r.Height - d) / 2, d, d);
            Svg.SvgFill(g, Icons.PlusLine, _newGroupBox,
                        _newGroupHot ? Theme.Text : Theme.ChannelIcon);
        }
    }

    /// The synthetic id BuildDmList gives the "Direct Messages" header row.
    public const ulong DmHeaderId = 1;
    Rectangle _newGroupBox;
    bool _newGroupHot;

    /// Raised by the "+" beside the Direct Messages header.
    public event Action? NewGroupClicked;

    void DrawChannel(Graphics g, int y, int i, Entry e)
    {
        bool sel = SelectedChannel == e.Id, hot = _hover == i;
        int inset = Ui.S(M.ChannelRowInset);
        var row = new Rectangle(inset, y, Width - inset * 2, Ui.S(M.ChannelRow));

        if (sel) Ui.FillRound(g, row, Ui.S(M.ChannelRowRadius), Theme.SidebarSelected);
        else if (hot) Ui.FillRound(g, row, Ui.S(M.ChannelRowRadius), Blend(Theme.Sidebar, Theme.SidebarHover, Fade(i)));

        // Unread is a white pill on the sidebar's left edge plus a brighter name — not a dot.
        if (e.Unread && !sel)
            Ui.FillRound(g, new Rectangle(0, row.Y + row.Height / 2 - Ui.S(4), Ui.S(8), Ui.S(8)), Ui.S(4), Color.White);

        var fg = sel ? Theme.Strong : e.Unread ? Theme.Text : hot ? Theme.Subtle : Theme.ChannelIcon;
        if (e.Muted && !sel && !e.Unread) fg = Color.FromArgb(120, fg);

        int icon = Ui.S(M.ChannelIcon);
        var ib = new RectangleF(row.X + Ui.S(M.ChannelPadX), row.Y + (row.Height - icon) / 2f, icon, icon);
        Svg.SvgFill(g, e.Kind switch
        {
            Kind.Voice => Icons.Speaker,
            Kind.Announcement => Icons.Megaphone,
            Kind.Forum => Icons.ForumLine,
            _ => Icons.Hash,
        }, ib, fg);

        int tx = row.X + Ui.S(M.ChannelPadX + M.ChannelIcon + M.ChannelIconGap);
        int tw = row.Right - tx - Ui.S(8) - (e.Mentions > 0 ? Ui.S(24) : 0);
        // Three measured states: read 16px/400 in --channel-icon, unread *and* selected both
        // 16px/500 in white. Selecting a read channel used to leave it at 400, so the row it was
        // sitting on read lighter than the same row does in the live client.
        Ui.Text(g, e.Name, sel || e.Unread ? Theme.BodyMedium : Theme.Body,
                new Rectangle(tx, row.Y, tw, row.Height),
                fg, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

        if (e.Mentions > 0) DrawBadge(g, row, e.Mentions);
    }

    // The Friends row above the DM list. Same 8px-inset rounded row as a channel, but with a filled
    // 20px glyph and no unread pill — it is a destination, not a channel.
    void DrawNav(Graphics g, int y, int i, Entry e)
    {
        bool sel = SelectedChannel == e.Id, hot = _hover == i;
        int inset = Ui.S(M.ChannelRowInset);
        var row = new Rectangle(inset, y, Width - inset * 2, Ui.S(M.NavRow));

        if (sel) Ui.FillRound(g, row, Ui.S(M.ChannelRowRadius), Theme.SidebarSelected);
        else if (hot) Ui.FillRound(g, row, Ui.S(M.ChannelRowRadius), Blend(Theme.Sidebar, Theme.SidebarHover, Fade(i)));

        var fg = sel ? Theme.Strong : hot ? Theme.Subtle : Theme.ChannelIcon;
        int icon = Ui.S(M.ChannelIcon);
        Svg.SvgFill(g, e.Id == EventsId ? Icons.Megaphone : Icons.People,
                    new RectangleF(row.X + Ui.S(M.ChannelPadX), row.Y + (row.Height - icon) / 2f, icon, icon), fg);

        int tx = row.X + Ui.S(M.ChannelPadX + M.ChannelIcon + M.ChannelIconGap);
        int tw = row.Right - tx - Ui.S(8) - (e.Mentions > 0 ? Ui.S(24) : 0);
        Ui.Text(g, e.Name, Theme.BodyMedium, new Rectangle(tx, row.Y, tw, row.Height), fg,
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

        if (e.Mentions > 0) DrawBadge(g, row, e.Mentions);
    }

    void DrawThread(Graphics g, int y, int i, Entry e)
    {
        bool sel = SelectedChannel == e.Id, hot = _hover == i;
        var row = new Rectangle(ThreadInset, y, Width - ThreadInset - Ui.S(M.ChannelRowInset), Ui.S(M.ChannelRow));

        if (sel) Ui.FillRound(g, row, Ui.S(M.ChannelRowRadius), Theme.SidebarSelected);
        else if (hot) Ui.FillRound(g, row, Ui.S(M.ChannelRowRadius), Blend(Theme.Sidebar, Theme.SidebarHover, Fade(i)));
        if (e.Unread && !sel)
            Ui.FillRound(g, new Rectangle(0, row.Y + row.Height / 2 - Ui.S(4), Ui.S(8), Ui.S(8)), Ui.S(4), Color.White);

        var fg = sel ? Theme.Strong : e.Unread ? Theme.Text : hot ? Theme.Subtle : Theme.ChannelIcon;
        if (e.Muted && !sel && !e.Unread) fg = Color.FromArgb(120, fg);

        int icon = Ui.S(18);
        var ib = new RectangleF(row.X + Ui.S(M.ChannelPadX), row.Y + (row.Height - icon) / 2f, icon, icon);
        Svg.SvgFill(g, Icons.ThreadLine, ib, fg);

        int tx = row.X + Ui.S(M.ChannelPadX + M.ChannelIcon + M.ChannelIconGap);
        int tw = row.Right - tx - Ui.S(8) - (e.Mentions > 0 ? Ui.S(24) : 0);
        Ui.Text(g, e.Name, sel || e.Unread ? Theme.BodyMedium : Theme.Body,
                new Rectangle(tx, row.Y, tw, row.Height),
                fg, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

        if (e.Mentions > 0) DrawBadge(g, row, e.Mentions);
    }

    // Who is talking, so the sidebar's voice rows ring green in step with the stage's tiles.
    readonly HashSet<ulong> _speaking = new();

    public void SetSpeaking(ulong userId, bool on)
    {
        if (on ? _speaking.Add(userId) : _speaking.Remove(userId)) Invalidate();
    }

    void DrawVoiceMember(Graphics g, int y, Entry e)
    {
        int av = Ui.S(20);
        var ab = new Rectangle(Ui.S(38), y + Ui.S(3), av, av);
        Ui.Avatar(g, Media.Get(e.AvatarUrl, this), ab, Theme.Surface, this);
        // Discord rings the small avatar while its owner is speaking, the same green as the stage.
        if (_speaking.Contains(e.Id))
        {
            using var pen = new Pen(Theme.Positive, Ui.S(2));
            g.DrawEllipse(pen, Rectangle.Inflate(ab, Ui.S(1), Ui.S(1)));
        }
        Ui.Text(g, e.Name, Theme.Small, new Rectangle(ab.Right + Ui.S(8), y, Width - ab.Right - Ui.S(20), Ui.S(24)),
                Theme.Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }

    void DrawDm(Graphics g, int y, int i, Entry e)
    {
        bool sel = SelectedChannel == e.Id, hot = _hover == i;
        int inset = Ui.S(M.ChannelRowInset);
        var row = new Rectangle(inset, y + Ui.S(1), Width - inset * 2, Ui.S(42));

        if (sel) Ui.FillRound(g, row, Ui.S(M.ChannelRowRadius), Theme.SidebarSelected);
        else if (hot) Ui.FillRound(g, row, Ui.S(M.ChannelRowRadius), Blend(Theme.Sidebar, Theme.SidebarHover, Fade(i)));
        if (e.Unread && !sel)
            Ui.FillRound(g, new Rectangle(0, row.Y + row.Height / 2 - Ui.S(4), Ui.S(8), Ui.S(8)), Ui.S(4), Color.White);

        int av = Ui.S(32);
        var ab = new Rectangle(row.X + Ui.S(8), row.Y + (row.Height - av) / 2, av, av);
        Ui.Avatar(g, Media.Get(e.AvatarUrl, this), ab, Theme.Surface, this);
        Ui.PresenceDot(g, ab, e.Presence, sel ? Theme.SidebarSelected : hot ? Theme.SidebarHover : Theme.Sidebar, Ui.S(11));

        var fg = sel || e.Unread ? Theme.Strong : hot ? Theme.Subtle : Theme.ChannelIcon;
        int tx = ab.Right + Ui.S(10);
        int tw = row.Right - tx - Ui.S(8) - (e.Mentions > 0 ? Ui.S(24) : 0);
        bool two = !string.IsNullOrEmpty(e.Subtitle);
        // The server tag claims its width up front so a long name ellipsises ahead of the chiplet
        // instead of being painted over by it.
        int tagW = e.Tag?.Tag is { Length: > 0 } tg ? Ui.TagChipWidth(tg) + Ui.S(4) : 0;
        var nameBox = new Rectangle(tx, row.Y + (two ? Ui.S(3) : 0), tw - tagW,
                                    two ? row.Height / 2 : row.Height);
        Ui.Text(g, e.Name, Theme.BodyMedium, nameBox, fg,
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        if (tagW > 0 && e.Tag?.Tag is { } tag)
            Ui.TagChip(g, tx + Math.Min(nameBox.Width, Ui.Measure(e.Name, Theme.BodyMedium).Width) + Ui.S(4),
                       nameBox.Y + nameBox.Height / 2, tag, Media.Get(e.Tag.BadgeUrl, this),
                       sel ? Theme.SidebarSelected : hot ? Theme.SidebarHover : Theme.Sidebar);
        if (two)
            Ui.Text(g, e.Subtitle, Theme.Small, new Rectangle(tx, row.Y + row.Height / 2, tw, row.Height / 2 - Ui.S(3)),
                    Theme.Faint, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

        if (e.Mentions > 0) DrawBadge(g, row, e.Mentions);
    }

    // The row's current fade, 0 when it has none. Seeded rows keep easing even after the pointer
    // leaves, so the exit fades out instead of snapping off.
    float Fade(int i) => _fades.TryGetValue(i, out var f) ? f : 0f;

    // Linear RGB blend used by the hover fade; Theme stores composited colours, so lerping them
    // directly is what the live client's 12% alpha overlay ends up looking like mid-transition.
    static Color Blend(Color a, Color b, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t),
                              (int)(a.B + (b.B - a.B) * t));
    }

    void DrawBadge(Graphics g, Rectangle row, int n)
    {
        var label = n > 99 ? "99+" : n.ToString();
        var sz = Ui.Measure(label, Theme.SmallMedium);
        int w = Math.Max(Ui.S(16), sz.Width + Ui.S(10));
        var box = new Rectangle(row.Right - w - Ui.S(6), row.Y + (row.Height - Ui.S(16)) / 2, w, Ui.S(16));
        Ui.FillRound(g, box, Ui.S(8), Theme.Danger);
        Ui.Text(g, label, Theme.SmallMedium, box, Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

}

// The account-tray popover: presence picker, custom status, user settings, log out. Same shape as
// the real client's menu that opens under your name in the corner.
sealed class TrayPopover : Control
{
    readonly AccountTray _owner;
    readonly TextBox _statusBox;
    int _hot = -1;
    bool _editing;

    static ToolStripDropDown? _host;

    static readonly (string Label, string Status, Presence Pres)[] Statuses =
    {
        ("Online", "online", Presence.Online),
        ("Idle", "idle", Presence.Idle),
        ("Do Not Disturb", "dnd", Presence.Dnd),
        ("Invisible", "invisible", Presence.Offline),
    };

    TrayPopover(AccountTray owner)
    {
        _owner = owner;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Size = new Size(Ui.S(240), Ui.S(300));
        BackColor = Theme.Floating;

        _statusBox = new TextBox
        {
            BorderStyle = BorderStyle.None,
            BackColor = Theme.InputBg,
            ForeColor = Theme.Text,
            Font = Theme.Body,
            PlaceholderText = "Set a custom status",
            Visible = false,
        };
        _statusBox.SetBounds(Ui.S(18), Row(0) + Ui.S(6), Width - Ui.S(36), Ui.S(22));
        _statusBox.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape) { _editing = false; _statusBox.Visible = false; Invalidate(); e.SuppressKeyPress = true; }
            else if (e.KeyCode == Keys.Enter)
            {
                var c = App.Client;
                if (c != null)
                {
                    var t = _statusBox.Text.Trim();
                    _ = c.Rest.SetCustomStatusAsync(t.Length == 0 ? null : t);
                }
                _editing = false; _statusBox.Visible = false;
                _owner.Invalidate(); Invalidate();
                e.SuppressKeyPress = true;
            }
        };
        Controls.Add(_statusBox);
    }

    public static void Show(AccountTray owner, Point screen)
    {
        Pop.Close(_host);
        var p = new TrayPopover(owner);
        _host = Pop.Host(p, new Point(screen.X, screen.Y - Ui.S(300)));
    }

    int Row(int i) => Ui.S(46) + i * Ui.S(40);

    int HitTest(Point p)
    {
        for (int i = 0; i < Statuses.Length; i++)
            if (p.Y >= Row(i) && p.Y < Row(i) + Ui.S(40)) return i;
        if (p.Y >= Ui.S(16) && p.Y < Ui.S(42)) return 4;              // custom status
        if (p.Y >= Height - Ui.S(110) && p.Y < Height - Ui.S(78)) return 5;   // settings
        if (p.Y >= Height - Ui.S(70) && p.Y < Height - Ui.S(38)) return 6;    // log out
        return -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int h = HitTest(e.Location);
        if (h != _hot) { _hot = h; Invalidate(); }
        Cursor = h >= 0 ? Cursors.Hand : Cursors.Default;
        base.OnMouseMove(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        int h = HitTest(e.Location);
        if (h < 0) return;
        var c = App.Client;
        switch (h)
        {
            case < 4 when c != null:
                _ = c.SetPresenceAsync(Statuses[h].Status);
                _owner.Invalidate();
                break;
            case 4:
                // Custom status opens an inline input, same as Discord's "Set custom status".
                _editing = true;
                _statusBox.Text = c?.CurrentUser?.CustomStatus ?? "";
                _statusBox.Visible = true;
                Invalidate();
                _statusBox.Focus();
                break;
            case 5:
                Pop.Close(_host); _host = null;
                _owner.OpenSettings();
                break;
            case 6:
                if (c != null) try { c.DisconnectAsync().GetAwaiter().GetResult(); } catch { }
                Prefs.ClearToken();
                Application.Restart();
                break;
        }
        base.OnMouseDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Ui.Fill(g, ClientRectangle, Theme.Floating);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var me = App.Client?.CurrentUser;
        int av = Ui.S(44);
        var ab = new Rectangle(Ui.S(14), Ui.S(16), av, av);
        Ui.Avatar(g, Media.Get(me?.GetDisplayAvatarUrl(96), this), ab, Theme.Surface, this);
        Ui.Text(g, me?.DisplayName ?? "not signed in", Theme.BodyMedium,
                new Rectangle(ab.Right + Ui.S(12), Ui.S(16), Width - ab.Right - Ui.S(28), Ui.S(22)),
                Theme.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        Ui.Text(g, me?.CustomStatus ?? me?.Status ?? "", Theme.Small,
                new Rectangle(ab.Right + Ui.S(12), Ui.S(38), Width - ab.Right - Ui.S(28), Ui.S(18)),
                Theme.Faint, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

        Ui.Fill(g, new Rectangle(Ui.S(8), Ui.S(78), Width - Ui.S(16), 1), Theme.Border);

        if (!_editing)
        {
            var cur = me?.Status ?? "online";
            for (int i = 0; i < Statuses.Length; i++)
            {
                bool sel = Statuses[i].Status == cur;
                var row = new Rectangle(Ui.S(8), Row(i), Width - Ui.S(16), Ui.S(40));
                if (_hot == i) Ui.FillRound(g, row, Ui.S(6), Theme.SidebarHover);
                if (sel) Ui.FillRound(g, row, Ui.S(6), Theme.SidebarSelected);
                var dot = new Rectangle(row.X + Ui.S(10), row.Y + Ui.S(12), Ui.S(16), Ui.S(16));
                using (var b = new SolidBrush(Theme.Dot(Statuses[i].Pres)))
                    g.FillEllipse(b, dot);
                Ui.Text(g, Statuses[i].Label, Theme.Body,
                        new Rectangle(dot.Right + Ui.S(10), row.Y, row.Width - dot.Right - Ui.S(18), row.Height),
                        sel ? Theme.Strong : Theme.Muted, TextFormatFlags.VerticalCenter);
            }
        }
        else
        {
            // Editing: the TextBox paints over this slot, so just draw its backdrop + helper text.
            Ui.FillRound(g, new Rectangle(Ui.S(8), Row(0), Width - Ui.S(16), Ui.S(36)), Ui.S(4), Theme.InputBg);
            Ui.Text(g, "Enter saves — Esc cancels", Theme.Small,
                    new Rectangle(Ui.S(8), Row(1), Width - Ui.S(16), Ui.S(22)), Theme.Faint);
        }

        Ui.Fill(g, new Rectangle(Ui.S(8), Height - Ui.S(122), Width - Ui.S(16), 1), Theme.Border);
        if (_hot == 5) Ui.FillRound(g, new Rectangle(Ui.S(8), Height - Ui.S(110), Width - Ui.S(16), Ui.S(32)), Ui.S(6), Theme.SidebarHover);
        Ui.Text(g, "User Settings", Theme.Body, new Rectangle(Ui.S(18), Height - Ui.S(110), Width - Ui.S(36), Ui.S(32)),
                _hot == 5 ? Theme.Text : Theme.Muted, TextFormatFlags.VerticalCenter);
        if (_hot == 6) Ui.FillRound(g, new Rectangle(Ui.S(8), Height - Ui.S(70), Width - Ui.S(16), Ui.S(32)), Ui.S(6), Theme.SidebarHover);
        Ui.Text(g, "Log Out", Theme.Body, new Rectangle(Ui.S(18), Height - Ui.S(70), Width - Ui.S(36), Ui.S(32)),
                Theme.Danger, TextFormatFlags.VerticalCenter);
    }
}
