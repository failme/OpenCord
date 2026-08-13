using System.Drawing;

namespace OpenCord;

// What the DM profile panel (MemberList) and the user popout (ProfileCard) share.
//
// In the live client they are one component shown two ways — the same themed card, the same name
// block, the same stack of written sections — so this is one description of it rather than two
// that drift apart the next time Discord moves something.
//
// Measured off the live DOM with both open at once: they are the same 304px card with the same
// 105px banner, the same 80px avatar and the same 268px body. Only the vertical origins and the
// footer differ, so those stay with each surface and everything else lives here.
//
// The two surfaces are *not* painted alike, because the live client does not paint them alike: the
// panel is pinned to `theme-dark` (scrim #00000099, light text) while the popout follows the
// profile's own theme and flips to `theme-light` (scrim #ffffff99, dark text) over a bright one.
// Verified on the same profile with both open at once — the popout's footer is black-at-8% where
// the panel's button is white-at-8%, which only makes sense on a light card.
static class ProfileSurface
{
    // Shared card geometry, design px.
    public const int CardW = 304, BannerH = 105, AvatarSize = 80, BodyPad = 18;
    // How far above the card's bottom the second gradient stop is reached. The panel holds flat
    // secondary under its footer button; the popout runs the gradient all the way down.
    public const int PanelFlatBottom = 72, PopoutFlatBottom = 0;

    /// The card's two gradient stops, plus the flat colour anything sitting on it is composited
    /// against. `Themed` is false when the user has neither a profile theme nor an accent colour,
    /// which is the plain dark card. `Light` means the scrim is white rather than black, which
    /// inverts every piece of text on the card.
    public readonly record struct Paint(Color Top, Color Bottom, Color Body, bool Themed, bool Light)
    {
        // Neutral darks rather than tints of the theme: a wash derived from the theme colour keeps
        // its hue, and a yellow-on-yellow name is exactly as unreadable as it sounds.
        public Color Strong => Light ? Color.FromArgb(0x2c, 0x2d, 0x32) : Theme.Strong;
        public Color Text => Light ? Color.FromArgb(0x3a, 0x3b, 0x41) : Theme.Text;
        public Color Muted => Light ? Color.FromArgb(0x5c, 0x5d, 0x66) : Theme.Muted;

        /// The scrim laid over the gradient — 60% either way.
        public Color Scrim(Color over) => Theme.Tint(Light ? Color.White : Color.Black, over, 0.4f);
    }

    /// Rec. 709 luminance. Discord picks the light treatment for a bright profile theme; this is
    /// the same call, made once here.
    public static bool IsLight(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0 > 0.5;

    /// One written section: an optional header, its text already laid out, and that text's height.
    public sealed record Section(string? Header, List<RichText.Piece> Text, int H);

    /// Design px between two sections, and between a section's header and its text.
    public const int Gap = 20, HeaderH = 24;

    public static Color Rgb(int c) => Color.FromArgb(c | unchecked((int)0xFF000000));

    /// Discord builds a profile surface out of the user's own theme: a 2px frame carrying a
    /// vertical primary -> secondary gradient, with the body that same gradient under a 60% black
    /// scrim. That is why a themed profile reads as a dark saturated wash and not as a tinted grey.
    /// A user with no theme falls back to their accent colour, and then to `plain`.
    /// `followTheme` is what separates the popout from the panel: the popout takes the light
    /// treatment over a bright theme, the panel is always dark.
    public static Paint Colors(UserProfile? p, Color plain, bool followTheme = false)
    {
        var accent = p?.ProfileColor is { } pc ? Rgb(pc) : (Color?)null;
        bool themed = p?.ThemeColors != null || accent != null;
        var (top, bot) = p?.ThemeColors is { } tc
            ? (Rgb(tc.Primary), Rgb(tc.Secondary))
            : (accent ?? plain, accent ?? plain);
        bool light = themed && followTheme && IsLight(top);
        var probe = new Paint(top, bot, plain, themed, light);
        return probe with { Body = themed ? probe.Scrim(top) : plain };
    }

    /// The card itself: a 2px frame of the raw gradient with the body laid inside it under the
    /// scrim. Returns the inner rect everything else is placed against.
    public static Rectangle PaintCard(Graphics g, Rectangle card, Paint p, int flatBottom)
    {
        int solid = Ui.S(BannerH), flat = Ui.S(flatBottom);
        var frame = p.Themed ? p.Top : Theme.Border;
        Ui.GradientRound(g, card, Ui.S(8), frame, p.Themed ? p.Bottom : Theme.Border, solid, flat);
        var inner = Rectangle.Inflate(card, -Ui.S(2), -Ui.S(2));
        if (p.Themed) Ui.GradientRound(g, inner, Ui.S(6), p.Body, p.Scrim(p.Bottom), solid, flat);
        else Ui.FillRound(g, inner, Ui.S(6), p.Body);
        return inner;
    }

    /// The banner strip: the user's own image when they have one — animated GIF banners play — else
    /// the theme colour. Clipped to the card's rounded top rather than drawn square into the corner.
    public static void PaintBanner(Graphics g, Rectangle inner, UserUser u, UserProfile? p,
                                   Paint paint, Control host, Color fallback)
    {
        var img = p?.BannerUrl(u.Id) is { } url ? Media.Get(url, host) : null;
        var banner = new Rectangle(inner.X, inner.Y, inner.Width, Ui.S(BannerH));
        var st = g.Save();
        using (var round = Ui.RoundRect(inner, Ui.S(6)))
            g.SetClip(round, System.Drawing.Drawing2D.CombineMode.Intersect);
        if (img != null)
        {
            if (Media.IsAnimated(img)) Media.Animate(img, host);
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.DrawImage(img, banner);
        }
        else Ui.Fill(g, banner, paint.Themed ? paint.Top : fallback);
        g.Restore(st);
    }

    /// The avatar hanging off the banner, with its ring in the card's own colour.
    public static Rectangle PaintAvatar(Graphics g, Rectangle card, int avatarY, UserUser u,
                                        Color body, Control host)
    {
        int av = Ui.S(AvatarSize);
        var box = new Rectangle(card.X + Ui.S(16), card.Y + Ui.S(avatarY), av, av);
        using (var ring = new SolidBrush(body)) g.FillEllipse(ring, Rectangle.Inflate(box, Ui.S(6), Ui.S(6)));
        Ui.Avatar(g, Media.Get(u.GetAvatarUrl(160), host), box, Theme.Surface, host);
        // The live client's hole sits 12 into the avatar's corner, not the 8 PresenceDot would use
        // for a 16px dot — so it is handed a box 4px short in each direction.
        Ui.PresenceDot(g, new Rectangle(box.X, box.Y, box.Width - Ui.S(4), box.Height - Ui.S(4)),
                       u.Presence, body, Ui.S(16));
        return box;
    }

    /// The two round buttons over the banner — "Friend" and "More", 32px at 52% black, 8 apart and
    /// 10 in from the card's right edge. Returns (friend, more) for hit-testing.
    public static (Rectangle Friend, Rectangle More) PaintBannerButtons(Graphics g, Rectangle card, int y)
    {
        var more = new Rectangle(card.Right - Ui.S(42), card.Y + Ui.S(y), Ui.S(32), Ui.S(32));
        var friend = more.WithX(more.X - Ui.S(40));
        void Button(Rectangle box, string icon)
        {
            using (var b = new SolidBrush(Color.FromArgb(133, 0, 0, 0))) g.FillEllipse(b, box);
            Svg.SvgFill(g, icon, new RectangleF(box.X + Ui.S(8), box.Y + Ui.S(8), Ui.S(16), Ui.S(16)),
                        Color.White);
        }
        Button(friend, Icons.PersonCheck);
        Button(more, Icons.DotsHorizontal);
        return (friend, more);
    }

    /// The written sections under the name block, in the live client's order, laid out to `width`.
    ///
    /// A bio or a custom status carries markdown and custom emoji, so both go through the chat's
    /// own text pipeline instead of being painted as the raw `<a:name:id>` the API sends.
    ///
    /// Only "Member Since" gets a header: the redesign dropped the "About Me" label and prints the
    /// bio bare, directly under the mutuals line.
    public static List<Section> Sections(UserUser u, UserProfile? p, int width, Color? textColor = null)
    {
        var outp = new List<Section>();
        void Add(string? header, string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            // 14px, not the chat's 16 — a profile's bio and custom status are a size down.
            var pieces = RichText.Layout(Markdown.Parse(text), width, out int h, Theme.Body14);
            // A light card needs dark body text. Markdown bakes the colour into each run at parse
            // time, so the plain ones are re-coloured here rather than at paint time.
            if (textColor is { } c)
                foreach (var piece in pieces)
                    if (piece.Run.Color == Theme.Text) piece.Run.Color = c;
            outp.Add(new Section(header, pieces, h));
        }
        Add(null, u.CustomStatus);
        Add(null, u.ActivityLine);
        Add(null, p?.Bio);
        // Snowflake time is UTC; the live client prints it local, which is a whole day out either
        // side of midnight.
        Add("Member Since", u.CreatedAt.ToLocalTime().ToString("MMM d, yyyy"));
        return outp;
    }

    /// The stack's total height, gaps included — what a surface adds to its own content height.
    public static int Height(List<Section> sections)
    {
        int h = 0;
        foreach (var s in sections) h += Ui.S(Gap) + (s.Header != null ? Ui.S(HeaderH) : 0) + s.H;
        return h;
    }

    /// Paints the stack from `y` and returns the y below it. Mirrors Height exactly.
    public static int PaintSections(Graphics g, List<Section> sections, int x, int y, int w, Control host, Paint paint)
    {
        foreach (var s in sections)
        {
            y += Ui.S(Gap);
            if (s.Header != null)
            {
                Ui.Text(g, s.Header, Theme.SmallMedium, new Rectangle(x, y, w, Ui.S(16)), paint.Strong,
                        TextFormatFlags.VerticalCenter);
                y += Ui.S(HeaderH);
            }
            RichText.Paint(g, s.Text, new Point(x, y), host);
            y += s.H;
        }
        return y;
    }

    public static bool HasMutuals(UserProfile? p) =>
        p != null && ((p.MutualFriendsCount ?? p.MutualFriends.Count) > 0 || p.MutualGuilds.Count > 0);

    /// The "N Mutual Friends • N Mutual Servers" line, with the friends' avatars stacked ahead of
    /// it — 16px avatars on a 13px pitch, then a 4px gap, then 12px text.
    public static void PaintMutuals(Graphics g, int x, int y, int w, UserProfile p, Control host, Paint paint)
    {
        int n = p.MutualFriendsCount ?? p.MutualFriends.Count, guilds = p.MutualGuilds.Count;
        int cx = x;
        foreach (var f in p.MutualFriends.Take(3))
        {
            Ui.Avatar(g, Media.Get(f.GetAvatarUrl(32), host), new Rectangle(cx, y, Ui.S(16), Ui.S(16)),
                      Theme.Surface);
            cx += Ui.S(13);
        }
        if (cx > x) cx += Ui.S(3) + Ui.S(4);        // the last avatar's own width, then the gap

        void Label(string s)
        {
            var sz = Ui.Measure(s, Theme.Small);
            Ui.Text(g, s, Theme.Small, new Rectangle(cx, y, Math.Max(0, x + w - cx), Ui.S(16)),
                    paint.Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            cx += sz.Width;
        }
        if (n > 0) Label($"{n} Mutual Friend{(n == 1 ? "" : "s")}");
        if (n > 0 && guilds > 0)
        {
            cx += Ui.S(6);
            using (var b = new SolidBrush(paint.Muted)) g.FillEllipse(b, cx, y + Ui.S(6), Ui.S(4), Ui.S(4));
            cx += Ui.S(4) + Ui.S(6);
        }
        if (guilds > 0) Label($"{guilds} Mutual Server{(guilds == 1 ? "" : "s")}");
    }

    /// The name block: display name, then the username row carrying the server tag and badges.
    /// Returns the y below it.
    public static int PaintName(Graphics g, UserUser u, UserProfile? p, string display,
                                int x, int y, int w, Paint paint, Control host)
    {
        Ui.Text(g, display, Theme.H2, new Rectangle(x, y, w, Ui.S(24)), paint.Strong,
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        y += Ui.S(24);

        int row = y + Ui.S(11);
        var name = u.Username + (p?.Pronouns is { Length: > 0 } pr ? "  •  " + pr : "");
        Ui.Text(g, name, Theme.Body14, new Rectangle(x, y, w, Ui.S(22)), paint.Text,
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        int nx = x + Math.Min(w, Ui.Measure(name, Theme.Body14).Width) + Ui.S(8);

        if (u.ServerTag is { Tag: { } tag } pg)
        {
            Ui.TagChip(g, nx, row, tag, Media.Get(pg.BadgeUrl, host), paint.Body, big: true,
                       fg: paint.Strong);
            nx += Ui.TagChipWidth(tag) + Ui.S(8);
        }
        foreach (var b in p?.Badges ?? new List<UserBadge>())
        {
            if (b.IconUrl == null || nx + Ui.S(20) > x + w) break;
            if (Media.Get(b.IconUrl, host) is { } img)
                g.DrawImage(img, new Rectangle(nx, row - Ui.S(10), Ui.S(20), Ui.S(20)));
            nx += Ui.S(22);
        }
        return y + Ui.S(22);
    }

    /// Design px the name block occupies — display name plus the username row.
    public const int NameH = 24 + 22;
}
