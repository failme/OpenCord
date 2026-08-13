using System.Drawing;
using System.Drawing.Drawing2D;

namespace OpenCord;

// Floating surfaces: tooltips, the image lightbox, the user popout and the emoji picker.
//
// Everything that must dismiss when the user clicks elsewhere is hosted in a ToolStripDropDown.
// That is not a decorative choice — a plain borderless Form would need its own click-away tracking,
// screen-edge flipping, Esc handling and z-order dance, all of which the dropdown already does.
static class Pop
{
    /// Close a popup that may already be closed, disposed, or null.
    ///
    /// Every picker keeps a static handle to the one it last opened and closes it before opening
    /// another. That handle outlives the dropdown — disposal is deferred (see Host) and a popup also
    /// closes on its own when you click away — so a bare `_host?.Close()` is a disposed-object throw
    /// waiting for the second time you open anything.
    public static void Close(ToolStripDropDown? host)
    {
        if (host == null || host.IsDisposed) return;
        try { host.Close(); } catch (ObjectDisposedException) { }
    }

    /// Top-left for a popup that should sit *above* `anchor` and share its right edge — how Discord
    /// hangs the emoji/GIF/sticker pickers off the composer button that opened them.
    ///
    /// Host still clamps the result into the working area, so a narrow window slides it back inside
    /// rather than pushing it off-screen.
    public static Point Above(Rectangle anchor, Size popup) =>
        new(anchor.Right - popup.Width, anchor.Top - popup.Height - Ui.S(8));

    /// `onDismissClick` receives the screen point of a click that dismissed the popup.
    ///
    /// A ToolStripDropDown eats the click that closes it, so with a picker open every other picker
    /// button needs *two* clicks — one to dismiss, one to act. Discord swaps straight across. Handing
    /// the swallowed click back lets the opener decide whether it landed on something worth acting on.
    public static ToolStripDropDown Host(Control content, Point screen, Action<Point>? onDismissClick = null)
    {
        var dd = new ToolStripDropDown
        {
            Padding = Padding.Empty,
            Margin = Padding.Empty,
            AutoSize = false,
            Size = content.Size,
            BackColor = Theme.Floating,
            DropShadowEnabled = true,
        };
        dd.Items.Add(new ToolStripControlHost(content)
        {
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            AutoSize = false,
            Size = content.Size,
        });
        if (onDismissClick != null)
            dd.Closing += (_, e) =>
            {
                if (e.CloseReason != ToolStripDropDownCloseReason.AppClicked) return;
                var at = Cursor.Position;
                // Deferred for the same reason disposal is: the dropdown is mid-teardown here.
                try { dd.BeginInvoke(() => onDismissClick(at)); } catch { }
            };

        // Disposing straight out of Closed tears both down while WinForms is still unwinding the
        // click that closed them. ToolStripDropDown.AutoClose raises Closed *before* the hosted
        // control's own OnMouseDown runs, so a picker that closes itself on click — every one of
        // them — then called Close() on an already-disposed dropdown and took the process with it:
        //
        //   ObjectDisposedException: 'System.Windows.Forms.ToolStripDropDown'
        //     at ToolStripDropDown.Close()
        //     at StickerPicker.OnMouseDown(...)
        //
        // Deferring to the next message lets the click finish against a live control first.
        dd.Closed += (_, _) =>
        {
            void Free() { content.Dispose(); dd.Dispose(); }
            // No handle means nothing is mid-dispatch against it, so immediate disposal is safe.
            try { if (dd.IsHandleCreated) dd.BeginInvoke(Free); else Free(); }
            catch { Free(); }
        };

        // Keep the popup on screen: flip it back inside the working area rather than letting half of
        // it land off the edge, which is what happens for anything opened near the right or bottom.
        var wa = Screen.FromPoint(screen).WorkingArea;
        int x = Math.Min(screen.X, wa.Right - content.Width - 8);
        int y = Math.Min(screen.Y, wa.Bottom - content.Height - 8);
        dd.Show(new Point(Math.Max(wa.Left + 8, x), Math.Max(wa.Top + 8, y)));
        return dd;
    }
}

// ── tooltip ─────────────────────────────────────────────────────────────────────────────────────

static class Tip
{
    static readonly ToolTip _tt = new()
    {
        OwnerDraw = true,
        ShowAlways = true,
        UseAnimation = false,
        UseFading = false,
        InitialDelay = 250,
        ReshowDelay = 0,
        AutomaticDelay = 250,
    };
    static string? _current;

    static Tip()
    {
        _tt.Draw += (_, e) =>
        {
            Ui.FillRound(e.Graphics, e.Bounds, Ui.S(6), Theme.Floating);
            Ui.Text(e.Graphics, e.ToolTipText, Theme.SmallMedium, e.Bounds, Theme.Text,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        };
        _tt.Popup += (_, e) =>
        {
            var sz = Ui.Measure(_current, Theme.SmallMedium);
            e.ToolTipSize = new Size(sz.Width + Ui.S(16), sz.Height + Ui.S(10));
        };
    }

    public static void Show(Control owner, string? text, Rectangle anchor)
    {
        if (text == _current) return;
        _current = text;
        if (text == null) { _tt.Hide(owner); return; }
        _tt.Show(text, owner, anchor.X + anchor.Width / 2 - Ui.S(30), anchor.Y - Ui.S(32), 4000);
    }

    public static void Hide() { _current = null; }
}

// ── image lightbox ──────────────────────────────────────────────────────────────────────────────

sealed class Lightbox : Form
{
    readonly string _url, _openUrl;

    Lightbox(string url, string openUrl)
    {
        _url = url; _openUrl = openUrl;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
        Bounds = wa;
        BackColor = Color.Black;
        Opacity = 0.94;
        DoubleBuffered = true;
        KeyPreview = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }

    public static void Show(string url, string openUrl)
    {
        var f = new Lightbox(url, openUrl);
        f.Show();
        f.Activate();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape) Close();
        base.OnKeyDown(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (_link.Contains(e.Location)) { Ui.OpenUrl(_openUrl); return; }
        if (!_imgBox.Contains(e.Location)) Close();
        base.OnMouseDown(e);
    }

    Rectangle _imgBox, _link;

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Color.Black);
        var img = Media.Get(_url, this);
        if (img == null)
        {
            Ui.Text(g, "Loading…", Theme.Body, ClientRectangle, Theme.Muted,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            return;
        }
        if (Media.IsAnimated(img)) Media.Animate(img, this);

        float s = Math.Min(1f, Math.Min((Width - Ui.S(120)) / (float)img.Width,
                                        (Height - Ui.S(140)) / (float)img.Height));
        int w = (int)(img.Width * s), h = (int)(img.Height * s);
        _imgBox = new Rectangle((Width - w) / 2, (Height - h) / 2 - Ui.S(16), w, h);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.DrawImage(img, _imgBox);

        const string label = "Open in browser";
        var sz = Ui.Measure(label, Theme.Small);
        _link = new Rectangle((Width - sz.Width) / 2, _imgBox.Bottom + Ui.S(12), sz.Width, sz.Height + Ui.S(4));
        Ui.Text(g, label, Theme.Small, _link, Theme.Link);
    }
}

// ── user popout ─────────────────────────────────────────────────────────────────────────────────

sealed class ProfileCard : Control
{
    readonly UserUser _u;
    readonly UserMember? _m;
    readonly UserProfile? _p;
    Rectangle _msgBox;

    readonly List<UserRole> _roles = new();
    // The written sections, shared with the DM profile panel — see ProfileSurface. Laid out here so
    // the card can be sized to them before it opens.
    readonly List<ProfileSurface.Section> _sections;

    // Measured off the live popout with the DM panel open beside it: the same 304px card, the same
    // 105px banner, the avatar 4px lower and the body 6px higher than the panel's, and a 56px
    // footer holding a 44px message box.
    const int AvatarY = 61, ButtonsY = 8, BodyTop = 148, FooterH = 56, MsgBoxH = 44;

    ProfileCard(UserUser u, UserProfile? p)
    {
        _u = u;
        _p = p;
        _m = App.Guild?.GetMember(u.Id);

        // Resolved once, so the height calculation and the paint cannot disagree about whether this
        // member has any roles worth a row. @everyone shares the guild's id and is never shown.
        if (_m is { } mem && App.Guild is { } gld)
            _roles.AddRange(mem.RoleIds.Select(r => gld.RoleById.GetValueOrDefault(r))
                                       .Where(r => r != null && r.Id != gld.Id)
                                       .OrderByDescending(r => r!.Position).Take(6)!);

        // The popout follows the profile theme, so a bright one needs dark body text.
        var pal = ProfileSurface.Colors(p, Theme.Floating, followTheme: true);
        _sections = ProfileSurface.Sections(u, p, Ui.S(BodyW), pal.Text);

        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.UserPaint, true);
        Size = new Size(Ui.S(ProfileSurface.CardW), HeightPx());
        BackColor = Theme.Floating;
    }

    // 304 - 18 either side.
    const int BodyW = ProfileSurface.CardW - ProfileSurface.BodyPad * 2;

    static ToolStripDropDown? _host;

    // The whole profile modal's worth of data in one request; the card opens a beat later so the
    // height can fit everything Discord would show. One card at a time, and a previous card is
    // closed up front — the async fetch means the click-away dismissal can no longer be relied on.
    public static async void Show(Control owner, UserUser u, Point screen)
    {
        Pop.Close(_host);
        UserProfile? p = null;
        var c = App.Client;
        if (c != null)
        {
            try { p = await c.Rest.GetProfileAsync(u.Id, App.Guild?.Id ?? 0); }
            catch { }
        }
        _host = Pop.Host(new ProfileCard(u, p), screen);
    }

    // Device px — the section heights come back from RichText already scaled, so this cannot stay
    // in design px the way it used to. It must track OnPaint's advances exactly: they are two walks
    // over the same sections, and any drift shows up as content painted underneath the Message box.
    // It was once off by 12, which clipped the "N mutual servers" line clean in half.
    int HeightPx()
    {
        int h = Ui.S(BodyTop) + Ui.S(ProfileSurface.NameH);
        if (ProfileSurface.HasMutuals(_p)) h += Ui.S(12) + Ui.S(16);
        h += ProfileSurface.Height(_sections);
        if (_roles.Count > 0) h += Ui.S(ProfileSurface.Gap) + Ui.S(20) + RoleRows() * Ui.S(24);
        return h + Ui.S(FooterH);
    }

    /// How many lines the role pills wrap onto, using the same widths OnPaint lays them out with.
    int RoleRows()
    {
        int innerW = Ui.S(BodyW);
        int rows = 1, rx = 0;
        foreach (var r in _roles)
        {
            int w = Ui.Measure(r.Name, Theme.Small).Width + Ui.S(24);
            if (rx > 0 && rx + w > innerW) { rows++; rx = 0; }
            rx += w + Ui.S(4);
        }
        return rows;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        // The card lives in a ToolStripDropDown, which is a ToolStrip and not a Form — FindForm()
        // was always null here, so "Message" opened the DM behind a card that never went away.
        if (_msgBox.Contains(e.Location)) { App.OpenDm?.Invoke(_u.Id); Pop.Close(_host); }
        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        Cursor = _msgBox.Contains(e.Location) ? Cursors.Hand : Cursors.Default;
        base.OnMouseMove(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        // The same themed card the DM profile panel paints — the popout is that panel in a floating
        // container in the live client, so the two share their whole construction (ProfileSurface).
        // Its gradient runs the full height, where the panel's holds flat secondary under its footer.
        var paint = ProfileSurface.Colors(_p, Theme.Floating, followTheme: true);
        var body = paint.Body;
        var card = new Rectangle(0, 0, Width, Height);
        var inner = ProfileSurface.PaintCard(g, card, paint, ProfileSurface.PopoutFlatBottom);

        ProfileSurface.PaintBanner(g, inner, _u, _p, paint, this,
                                   App.Guild?.NameColor(_u.Id) ?? Theme.Blurple);
        ProfileSurface.PaintAvatar(g, card, AvatarY, _u, body, this);
        ProfileSurface.PaintBannerButtons(g, card, ButtonsY);

        int x = Ui.S(ProfileSurface.BodyPad);
        int w = Ui.S(BodyW);
        int y = Ui.S(BodyTop);

        y = ProfileSurface.PaintName(g, _u, _p, _m?.Nick ?? _u.DisplayName, x, y, w, paint, this);
        if (ProfileSurface.HasMutuals(_p))
        {
            y += Ui.S(12);
            ProfileSurface.PaintMutuals(g, x, y, w, _p!, this, paint);
            y += Ui.S(16);
        }
        y = ProfileSurface.PaintSections(g, _sections, x, y, w, this, paint);

        // Roles are the popout's own section: it is the only one of the two that has a guild.
        if (_roles.Count > 0)
        {
            y += Ui.S(ProfileSurface.Gap);
            Ui.Text(g, "Roles", Theme.SmallMedium, new Rectangle(x, y, w, Ui.S(16)), paint.Strong,
                    TextFormatFlags.VerticalCenter);
            y += Ui.S(20);
            int rx = x;
            foreach (var r in _roles)
            {
                var sz = Ui.Measure(r.Name, Theme.Small);
                int rw = sz.Width + Ui.S(24);
                if (rx > x && rx + rw > x + w) { rx = x; y += Ui.S(24); }
                var pill = new Rectangle(rx, y, rw, Ui.S(20));
                Ui.FillRound(g, pill, Ui.S(4), Theme.TagPill(body));
                using (var b = new SolidBrush(r.Rgb ?? Theme.Muted))
                    g.FillEllipse(b, pill.X + Ui.S(5), pill.Y + Ui.S(6), Ui.S(8), Ui.S(8));
                Ui.Text(g, r.Name, Theme.Small, new Rectangle(pill.X + Ui.S(17), pill.Y, sz.Width, pill.Height),
                        paint.Text, TextFormatFlags.VerticalCenter);
                rx += rw + Ui.S(4);
            }
        }

        // The popout's footer is a 44px message box at black 8%, where the panel has a button at
        // white 8% — the one place the live client's two profile surfaces genuinely differ.
        _msgBox = new Rectangle(x, Height - Ui.S(FooterH) + Ui.S(6), w, Ui.S(MsgBoxH));
        Ui.FillRound(g, _msgBox, Ui.S(8), Theme.Tint(body, Color.Black, 0.08f));
        Ui.Text(g, "Message  @" + _u.Username, Theme.Body14,
                new Rectangle(_msgBox.X + Ui.S(15), _msgBox.Y, _msgBox.Width - Ui.S(30), _msgBox.Height),
                paint.Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }
}

// ── channel notification settings ──────────────────────────────────────────────────────────────
// The bell in the chat header. For a guild channel: mute + message-notification level (all / only
// mentions / nothing / inherit). For a DM there is no notification level, just mute. Reads the
// client's authoritative state and pushes changes back through the same REST the web client uses.
sealed class NotifSettings : Control
{
    readonly ulong _channel;
    readonly bool _isDm;
    int _hot = -1;

    static readonly (string Label, int Level)[] Levels =
    {
        ("Inherit from server", 3), ("All messages", 0), ("Only mentions", 1), ("Nothing", 2),
    };

    static ToolStripDropDown? _host;

    NotifSettings(ulong channel, bool isDm)
    {
        _channel = channel;
        _isDm = isDm;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Size = new Size(Ui.S(300), Ui.S(isDm ? 168 : 306));
        BackColor = Theme.Floating;
    }

    public static void Show(Control owner, Point screen, ulong channel, bool isDm)
    {
        Pop.Close(_host);
        _host = Pop.Host(new NotifSettings(channel, isDm), screen);
    }

    bool Muted => App.Client?.MutedChannels.Contains(_channel) ?? false;
    int Level => App.Client?.ChannelNotifyLevels.GetValueOrDefault(_channel, 3) ?? 3;

    Rectangle ToggleBox => new(Ui.S(216), Ui.S(86), Ui.S(68), Ui.S(26));
    int LevelRow(int i) => Ui.S(150) + i * Ui.S(38);

    int HitTest(Point p)
    {
        if (ToggleBox.Contains(p)) return 0;
        if (_isDm) return -1;
        for (int i = 0; i < Levels.Length; i++)
            if (p.Y >= LevelRow(i) && p.Y < LevelRow(i) + Ui.S(36)) return i + 1;
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
        var c = App.Client;
        if (c == null) return;
        if (h == 0)
        {
            bool on = !Muted;
            if (on) c.MutedChannels.Add(_channel); else c.MutedChannels.Remove(_channel);
            c.NotifyMutesChanged();
            _ = c.Rest.SetChannelMutedAsync(App.Guild?.Id ?? 0, _channel, on);
            Invalidate();
        }
        else if (h >= 1 && h <= Levels.Length)
        {
            var lvl = Levels[h - 1].Level;
            c.ChannelNotifyLevels[_channel] = lvl;
            c.NotifyMutesChanged();
            _ = c.Rest.SetChannelNotifyLevelAsync(App.Guild?.Id ?? 0, _channel, lvl);
            Invalidate();
        }
        base.OnMouseDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Ui.Fill(g, ClientRectangle, Theme.Floating);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        Ui.Text(g, "Notification Settings", Theme.SmallMedium,
                new Rectangle(Ui.S(16), Ui.S(14), Width - Ui.S(32), Ui.S(18)),
                Theme.Faint, TextFormatFlags.NoPadding);

        // ── mute row ──
        Ui.Text(g, "Mute channel", Theme.Body,
                new Rectangle(Ui.S(16), Ui.S(90), Ui.S(190), Ui.S(22)),
                Theme.Text, TextFormatFlags.VerticalCenter);
        DrawToggle(g, ToggleBox, Muted);

        Ui.Fill(g, new Rectangle(Ui.S(16), Ui.S(130), Width - Ui.S(32), 1), Theme.Border);

        if (!_isDm)
        {
            Ui.Text(g, "Message notifications", Theme.SmallMedium,
                    new Rectangle(Ui.S(16), Ui.S(138), Width - Ui.S(32), Ui.S(16)),
                    Theme.Faint, TextFormatFlags.NoPadding);
            for (int i = 0; i < Levels.Length; i++)
            {
                bool sel = Level == Levels[i].Level;
                var row = new Rectangle(Ui.S(8), LevelRow(i), Width - Ui.S(16), Ui.S(36));
                if (_hot == i + 1) Ui.FillRound(g, row, Ui.S(6), Theme.SidebarHover);
                // Radio: selected paints blurple, hover outline otherwise.
                var dot = new Rectangle(row.X + Ui.S(12), row.Y + Ui.S(10), Ui.S(16), Ui.S(16));
                using (var b = new SolidBrush(sel ? Theme.Blurple : Theme.Surface))
                    g.FillEllipse(b, dot);
                using (var pen = new Pen(sel ? Theme.Blurple : Theme.Border))
                    g.DrawEllipse(pen, dot);
                if (sel)
                    using (var b = new SolidBrush(Color.White))
                        g.FillEllipse(b, new Rectangle(dot.X + Ui.S(4), dot.Y + Ui.S(4), Ui.S(8), Ui.S(8)));
                Ui.Text(g, Levels[i].Label, Theme.Body,
                        new Rectangle(dot.Right + Ui.S(10), row.Y, row.Width - dot.Right - Ui.S(18), row.Height),
                        sel ? Theme.Strong : Theme.Muted, TextFormatFlags.VerticalCenter);
            }
        }
    }

    static void DrawToggle(Graphics g, Rectangle box, bool on)
    {
        var track = new Rectangle(box.X, box.Y, Ui.S(44), Ui.S(24));
        Ui.FillRound(g, track, Ui.S(12), on ? Theme.Positive : Theme.Field);
        using (var pen = new Pen(Theme.Border))
        using (var path = Ui.RoundRect(new Rectangle(track.X, track.Y, track.Width - 1, track.Height - 1), Ui.S(12)))
            g.DrawPath(pen, path);
        int k = Ui.S(20);
        int kx = track.X + (on ? track.Width - k - Ui.S(2) : Ui.S(2));
        using (var b = new SolidBrush(on ? Color.White : Theme.Muted))
            g.FillEllipse(b, kx, track.Y + (track.Height - k) / 2, k, k);
    }
}

// ── emoji picker ────────────────────────────────────────────────────────────────────────────────

sealed class EmojiPicker : Control
{
    // A compact, frequency-ordered set. The full Unicode table is ~3600 entries and every one of
    // them is a separate sprite download; Discord's own picker is paged for the same reason.
    const string Common =
        "😀😃😄😁😆😅🤣😂🙂🙃😉😊😇🥰😍🤩😘😗😚😙😋😛😜🤪😝🤑🤗🤭🤫🤔🤐🤨😐😑😶😏😒🙄😬😮‍💨😌😔😪🤤😴😷🤒🤕🤢🤮🤧🥵🥶" +
        "🥴😵🤯🤠🥳😎🤓🧐😕😟🙁☹️😮😯😲😳🥺😦😧😨😰😥😢😭😱😖😣😞😓😩😫🥱😤😡😠🤬😈👿💀☠️💩🤡👹👺👻👽👾🤖" +
        "👍👎👌🤌🤏✌️🤞🤟🤘🤙👈👉👆👇☝️✋🤚🖐🖖👋🤝🙏💪🦾✍️💅🤳💄💋👀👁️👄🫦" +
        "❤️🧡💛💚💙💜🖤🤍🤎💔❣️💕💞💓💗💖💘💝💯💢💥💫💦💨🔥⭐🌟✨⚡☄️💥" +
        "🎉🎊🎈🎂🍰🍕🍔🍟🌭🍿🍩🍪🥤☕🍺🍻🥂🍷🍸🎮🎲🎯🎳🏀⚽🏈🎾🏐🥇🏆" +
        "✅❌⭕❗❓💤🔔🔕📌📍🔗📢📣💬💭🗯️♻️🆗🆕🔞🚫";

    readonly List<(string Key, string Url, string Name)> _items = new();
    readonly Action<string> _pick;
    readonly TextBox _search;
    readonly Scroller _scroll;
    int _hover = -1;
    string _filter = "";

    EmojiPicker(Action<string> pick)
    {
        _pick = pick;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.UserPaint, true);
        Size = PickerChrome.Size;
        BackColor = Theme.Field;
        _scroll = new Scroller(this);

        _search = new TextBox
        {
            BorderStyle = BorderStyle.None,
            BackColor = Theme.InputBg,
            ForeColor = Theme.Text,
            Font = Theme.Body,
            PlaceholderText = "Find the perfect emoji",
        };
        _search.SetBounds(Ui.S(PickerChrome.PadX + 12), Ui.S(PickerChrome.ContentTop + 11),
                          Width - Ui.S(PickerChrome.PadX * 2 + 24), Ui.S(22));
        _search.TextChanged += (_, _) => { _filter = _search.Text.Trim().ToLowerInvariant(); Build(); Invalidate(); };
        Controls.Add(_search);
        Build();
    }

    /// `anchor` is the screen rect of the control that opened this — the popup hangs above it.
    public static void Pick(Control owner, Rectangle anchor, Action<string> pick)
    {
        Pop.Close(_host);                       // one picker at a time, like Discord
        var p = new EmojiPicker(pick);
        _host = Pop.Host(p, Pop.Above(anchor, p.Size), owner is Composer cmp ? cmp.PickerDismissedAt : null);
        p._search.Focus();
    }

    static ToolStripDropDown? _host;

    void Build()
    {
        _items.Clear();
        foreach (var g in App.Client?.Guilds ?? new List<UserGuild>())
            foreach (var em in g.Emojis)
            {
                if (em.Name == null) continue;
                if (_filter.Length > 0 && !em.Name.ToLowerInvariant().Contains(_filter)) continue;
                _items.Add(($"<{(em.Animated ? "a" : "")}:{em.Name}:{em.Id}>",
                            $"https://cdn.discordapp.com/emojis/{em.Id}.{(em.Animated ? "gif" : "png")}?size=48",
                            em.Name));
                if (_items.Count > 400) break;
            }
        if (_filter.Length == 0 || "emoji smiley face".Contains(_filter))
            foreach (var (seq, isEmoji) in Twemoji.Split(Common))
                if (isEmoji) _items.Add((seq, Twemoji.Url(seq), seq));
        _scroll.Reset();
    }

    // Measured: 48px cells at radius 4 holding a 40px glyph, under a 40-tall search row.
    int Cell => Ui.S(48);
    int Cols => Math.Max(1, (Width - Ui.S(24)) / Cell);
    int GridTop => Ui.S(PickerChrome.ContentTop + 52);
    int ContentH => (_items.Count + Cols - 1) / Cols * Cell + Ui.S(8);
    int MaxScroll => Math.Max(0, ContentH - (Height - GridTop));

    int HitTest(Point p)
    {
        if (p.Y < GridTop) return -1;
        int col = (p.X - Ui.S(12)) / Cell, row = (p.Y - GridTop + _scroll.Value) / Cell;
        if (col < 0 || col >= Cols) return -1;
        int i = row * Cols + col;
        return i >= 0 && i < _items.Count ? i : -1;
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
        if (PickerChrome.Click(e.Location, PickerChrome.Tab.Emoji)) return;
        int i = HitTest(e.Location);
        if (i < 0) return;
        // A reaction wants "name:id" or the raw glyph; the composer wants the literal markup. The
        // callers differ, so hand back both and let them choose.
        _pick(_items[i].Key);
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
        PickerChrome.Paint(g, this, PickerChrome.Tab.Emoji, PointToClient(MousePosition));
        // The search well: black at 7.84% on the panel, 40 tall, radius 8.
        Ui.FillRound(g, new Rectangle(Ui.S(PickerChrome.PadX), Ui.S(PickerChrome.ContentTop),
                                      Width - Ui.S(PickerChrome.PadX * 2), Ui.S(40)), Ui.S(8),
                     Theme.Tint(Theme.Field, Color.Black, 0.0784f));

        var clip = g.Save();
        g.SetClip(new Rectangle(0, GridTop, Width, Height - GridTop));
        for (int i = 0; i < _items.Count; i++)
        {
            int col = i % Cols, row = i / Cols;
            var box = new Rectangle(Ui.S(12) + col * Cell, GridTop + row * Cell - _scroll.Value, Cell, Cell);
            if (box.Bottom < GridTop || box.Top > Height) continue;
            if (_hover == i) Ui.FillRound(g, box, Ui.S(4), Theme.SurfaceHigh);
            var img = Media.Get(_items[i].Url, this);
            var ib = Rectangle.Inflate(box, -Ui.S(4), -Ui.S(4));
            if (img != null) g.DrawImage(img, ib);
            else Ui.Text(g, _items[i].Name, Theme.Emoji, ib, Theme.Text, TextFormatFlags.HorizontalCenter);
        }
        g.Restore(clip);
    }
}
