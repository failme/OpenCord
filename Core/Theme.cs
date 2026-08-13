using System.Drawing;

namespace OpenCord;

// Discord's dark theme, measured off the live client rather than eyedropped from screenshots.
//
// Method (re-run it after any Discord redesign — see the notes at the bottom of this file):
// resolve each CSS custom property under a `theme-dark` container, paint it to a 1x1 canvas and read
// the pixel back. Canvas readback is what makes this reliable: the tokens resolve to
// `color-mix(in oklab, ...)`, which cannot be parsed by hand.
//
// Two things this file deliberately does NOT do:
//   - guess a colour from a token *name*. `--channel-background-default` is #323339, but the channel
//     sidebar paints #2c2d32 — it is transparent all the way up to the app root. Every value here
//     came from the element that actually paints it.
//   - store a translucent overlay. Discord's hover/selected states are greys at 12-20% alpha, so the
//     flat colour depends on the surface underneath. They are composited once, here, per surface.
static class Theme
{
    // ── Surfaces ────────────────────────────────────────────────────────────────────────────────
    // There is no divider between the rail and the sidebar: a horizontal scan at any y reads a flat
    // #2c2d32 from the rail's left edge to the chat's.
    public static readonly Color Rail = Color.FromArgb(44, 45, 50);      // --background-base-lowest  #2c2d32
    public static readonly Color Sidebar = Rail;                          // same surface, no seam
    public static readonly Color Chat = Color.FromArgb(50, 51, 57);      // --background-base-lower   #323339
    public static readonly Color Tray = Color.FromArgb(54, 55, 62);      // --background-base-low     #36373e
    public static readonly Color Field = Color.FromArgb(57, 58, 65);     // --background-surface-high #393a41
    public static readonly Color Surface = Color.FromArgb(60, 61, 69);   // --background-surface-higher
    public static readonly Color SurfaceHigh = Color.FromArgb(63, 64, 72);// --background-surface-highest
    public static readonly Color Floating = Color.FromArgb(17, 18, 20);  // popouts, menus, profile cards
    public static readonly Color Shadow = Color.FromArgb(15, 16, 18);

    // ── Text ────────────────────────────────────────────────────────────────────────────────────
    public static readonly Color Text = Color.FromArgb(243, 243, 244);   // --text-default  #f3f3f4
    public static readonly Color Strong = Color.FromArgb(255, 255, 255); // --text-strong   #ffffff
    public static readonly Color Muted = Color.FromArgb(171, 172, 178);  // --text-muted    #abacb2
    public static readonly Color Subtle = Color.FromArgb(197, 198, 202); // --text-subtle   #c5c6ca
    public static readonly Color Faint = Color.FromArgb(157, 158, 165);  // timestamps      #9d9ea5
    public static readonly Color Placeholder = Color.FromArgb(108, 109, 118); // composer hint   #6c6d76
    public static readonly Color ChannelIcon = Color.FromArgb(153, 154, 161); // --channel-icon #999aa1
    public static readonly Color SystemText = Color.FromArgb(153, 154, 161);  // system-message tail  #999aa1

    // Blend `into` toward `c` by amount (0..1). Lets a Nitro profile's accent colour tint the
    // popout background the way the live client's profile theme does — never more than a few
    // percent, or text stops separating from the surface behind it.
    public static Color Tint(Color into, Color c, float amount)
    {
        if (amount <= 0) return into;
        if (amount >= 1) return c;
        return Color.FromArgb(
            (byte)(into.R + (c.R - into.R) * amount),
            (byte)(into.G + (c.G - into.G) * amount),
            (byte)(into.B + (c.B - into.B) * amount));
    }

    // ── Brand ───────────────────────────────────────────────────────────────────────────────────
    public static readonly Color Blurple = Color.FromArgb(88, 101, 242);     // --background-brand #5865f2
    public static readonly Color BlurpleHover = Color.FromArgb(71, 82, 196);
    public static readonly Color BrandText = Color.FromArgb(148, 168, 255);  // --text-brand #94a8ff
    public static readonly Color Link = Color.FromArgb(118, 175, 246);       // --text-link  #76aff6

    // ── Interaction states ──────────────────────────────────────────────────────────────────────
    // Composited from the live CSS as  base + (overlay - base) * alpha. Sidebar rows sit on Rail,
    // chat rows on Chat, so the same rule yields two different flat colours.
    //   .wrapper:hover .link  #94949c @ .12     .modeSelected .link  #9696a0 @ .20
    //   .message.selected     #94949c @ .12     --border-normal      #9696a0 @ .20
    /// The server-tag chiplet's pill: #97979f at 20% over whatever it sits on. Composited per
    /// surface rather than stored flat, because the same pill appears on the sidebar, on a chat row
    /// and on a profile panel's own themed background.
    public static Color TagPill(Color on) => Tint(on, Color.FromArgb(151, 151, 159), 0.20f);

    public static readonly Color SidebarHover = Color.FromArgb(56, 57, 63);
    public static readonly Color SidebarPressed = Color.FromArgb(61, 62, 68);
    public static readonly Color SidebarSelected = Color.FromArgb(65, 66, 72);
    public static readonly Color RowHover = Color.FromArgb(62, 63, 69);
    public static readonly Color Border = Color.FromArgb(70, 71, 78);        // --border-normal over Chat
    public static readonly Color BorderSubtle = Color.FromArgb(62, 63, 69);  // --border-subtle over Chat

    // ── Chat furniture ──────────────────────────────────────────────────────────────────────────
    public static readonly Color EmbedBg = Color.FromArgb(44, 45, 50);       // --embed-background #2c2d32
    // Blurple at 7.84% over the chat background, flattened — the live client paints
    // --background-code, which is a translucent brand tint, NOT the near-black this used to be.
    public static readonly Color CodeBg = Color.FromArgb(53, 55, 72);        // #353748
    public static readonly Color CodeBorder = Color.FromArgb(73, 74, 89);    // #494a59, 1px

    // Hidden-spoiler attachment: a light scrim with a dark round pill over it. Measured off the
    // live client — the first version used the dark embed colour and read as a broken image.
    // A call system message's gutter glyph is Discord's green, not the muted text colour.
    public static readonly Color CallGreen = Color.FromArgb(59, 165, 92);        // #3ba55c

    public static readonly Color SpoilerScrim = Color.FromArgb(125, 126, 135);   // #7d7e87
    public static readonly Color SpoilerPillBg = Color.FromArgb(50, 50, 54);     // #323236
    public static readonly Color SpoilerPillText = Color.FromArgb(228, 228, 230);// #e4e4e6
    public static readonly Color InputBg = Color.FromArgb(30, 31, 34);       // login/text field well, ~--input-background
    public static readonly Color SearchBg = Color.FromArgb(46, 47, 53);      // black @ .0784 over Chat - header search box
    public static readonly Color CodeText = Color.FromArgb(255, 255, 255);   // --text-code
    // Fenced-block syntax colours, matching the hljs theme the web client ships.
    // Measured off the live client's hljs stylesheet, not guessed. The first pass here used the
    // stock Atom One Dark palette (purple keywords, olive strings); Discord ships a *pastel*
    // theme, so every token was the wrong hue.
    public static readonly Color CodeKeyword = Color.FromArgb(255, 189, 185);   // #ffbdb9
    public static readonly Color CodeString = Color.FromArgb(150, 221, 169);    // #96dda9
    public static readonly Color CodeNumber = Color.FromArgb(252, 192, 161);    // #fcc0a1
    public static readonly Color CodeComment = Color.FromArgb(179, 179, 185);   // #b3b3b9
    public static readonly Color CodeType = Color.FromArgb(255, 182, 237);      // #ffb6ed  type/built_in/name
    public static readonly Color CodeTitle = Color.FromArgb(188, 202, 255);     // #bccaff  function names
    public static readonly Color CodeMeta = Color.FromArgb(255, 194, 82);       // #ffc252  meta/symbol
    public static readonly Color CodeAttr = Color.FromArgb(171, 207, 255);      // #abcfff  attr/link/bullet
    public static readonly Color CodeProperty = Color.FromArgb(142, 218, 225);  // #8edae1
    public static readonly Color CodeOperator = Color.FromArgb(255, 207, 205);  // #ffcfcd
    public static readonly Color MentionText = Color.FromArgb(205, 215, 255);// --mention-foreground #cdd7ff
    public static readonly Color MentionBg = Color.FromArgb(59, 63, 101);    // #5864f2 @ .24 over Chat
    // The same blurple lifted for hover — a reaction you have already added still has to respond
    // to the pointer, or it reads as disabled.
    public static readonly Color MentionBgHover = Color.FromArgb(74, 79, 126);
    // The count on a reaction you added. Much paler than --text-brand, which is what it used to use.
    public static readonly Color ReactionMeText = Color.FromArgb(223, 228, 255);   // #dfe4ff
    public static readonly Color MentionRow = Color.FromArgb(65, 60, 52);    // #f2a600 @ .08 over Chat
    public static readonly Color MentionRowBar = Color.FromArgb(242, 166, 0);
    public static readonly Color ReplyRow = Color.FromArgb(56, 59, 87);      // #5764f3 @ .16 over Chat
    public static readonly Color SpoilerBg = Color.FromArgb(36, 36, 41);
    public static readonly Color Selection = Color.FromArgb(63, 74, 165);

    // The refresh inverted the scrollbar: a light thumb on a dark track, where the classic theme had
    // a near-black thumb. Getting this backwards is very visible.
    public static readonly Color ScrollThumb = Color.FromArgb(118, 119, 128);// --scrollbar-thin-thumb
    public static readonly Color ScrollTrack = Color.FromArgb(43, 43, 49);   // #2b2b31

    // ── Status ──────────────────────────────────────────────────────────────────────────────────
    // Discord desaturated these in the refresh; the classic 35,165,90 green is stale.
    public static readonly Color Online = Color.FromArgb(61, 158, 96);       // --icon-status-online  #3d9e60
    public static readonly Color Idle = Color.FromArgb(255, 203, 110);       // --icon-status-idle    #ffcb6e
    public static readonly Color Dnd = Color.FromArgb(220, 66, 71);          // --icon-status-dnd     #dc4247
    public static readonly Color Offline = Color.FromArgb(157, 158, 165);    // --icon-status-offline #9d9ea5
    public static readonly Color Streaming = Color.FromArgb(137, 80, 245);
    public static readonly Color Danger = Color.FromArgb(218, 62, 68);       // --status-danger  #da3e44
    public static readonly Color Warning = Color.FromArgb(253, 184, 51);     // --status-warning #fdb833
    public static readonly Color Positive = Color.FromArgb(61, 158, 96);     // --status-positive

    // Presence -> dot colour. Lives here rather than on the model so the protocol layer stays free
    // of System.Drawing and can be exercised without a UI.
    public static Color Dot(Presence p) => p switch
    {
        Presence.Online => Online,
        Presence.Idle => Idle,
        Presence.Dnd => Dnd,
        Presence.Streaming => Streaming,
        _ => Offline,
    };

    // ── Type ────────────────────────────────────────────────────────────────────────────────────
    // Discord authors in CSS pixels at 1x; WinForms wants points. 16 CSS px = 12 pt, so the whole
    // scale is (px * 0.75). The predecessor used 10pt for body text and therefore rendered ~17%
    // smaller than Discord everywhere — every box in this project is sized against 12pt instead.
    //
    // Weights: Discord uses 400/500/600. gg sans isn't licensed for bundling, so the body family is
    // Nunito when the machine has it (a humanist sans close in width and roundness to gg sans) and
    // Segoe UI otherwise. Nunito ships *real* Medium/SemiBold families, so 500 no longer has to
    // masquerade as Semibold — usernames land on their true weight.
    static readonly string BodyFamily = Installed("Nunito") ? "Nunito" : "Segoe UI";
    static readonly string MediumFamily = Installed("Nunito Medium") ? "Nunito Medium"
                                       : Installed("Nunito SemiBold") ? "Nunito SemiBold"
                                       : "Segoe UI Semibold";
    // Discord uses 400/500/600, and 600 is a real weight in its type scale — the guild header, the
    // date divider and a reaction count all measure at it. Approximating those with the 500 family
    // is what made them read a step light next to the live client.
    static readonly string SemiFamily = Installed("Nunito SemiBold") ? "Nunito SemiBold"
                                      : Installed("Nunito Bold") ? "Nunito Bold"
                                      : "Segoe UI Semibold";

    // A font name is only usable if the family actually resolves; probing through Font and checking
    // the reported name catches GDI+'s silent fallback to Microsoft Sans Serif for a missing face.
    static bool Installed(string family)
    {
        try { using var probe = new Font(family, 12f); return probe.Name.Equals(family, StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    public static readonly Font Body = new(BodyFamily, 12f);             // 16px/22 w400
    public static readonly Font BodyMedium = new(MediumFamily, 12f);      // 16px w500 - usernames, channel names
    public static readonly Font Small = new(BodyFamily, 9f);              // 12px - timestamps
    public static readonly Font SmallMedium = new(MediumFamily, 9f);
    public static readonly Font SmallSemibold = new(SemiFamily, 9f);      // 12px w600
    public static readonly Font BodySemibold = new(SemiFamily, 12f);      // 16px w600
    // 14px/18 w400 — the regular-weight partner to Category. The profile panel's username, custom
    // status and "Member Since" value are all this size; Body (16px) is a size too big for them.
    public static readonly Font Body14 = new(BodyFamily, 10.5f);
    // 15px semibold — the "SPOILER" pill over a hidden attachment.
    public static readonly Font SpoilerPill = new(MediumFamily, 11.25f, FontStyle.Bold);
    public static readonly Font Category = new(MediumFamily, 10.5f);      // 14px/18 w500 - category + member group headers
    public static readonly Font H1 = new(MediumFamily, 18f);              // 24px
    public static readonly Font H2 = new(MediumFamily, 15f);              // 20px
    public static readonly Font H3 = new(MediumFamily, 12f);              // 16px
    public static readonly Font Mono = new("Consolas", 11f);
    // The "GIF" lettering inside the composer's GIF-box icon. Static because it is drawn from
    // OnPaint: built inline it allocated an undisposed GDI font object on every single frame.
    public static readonly Font GifBadge = new("Segoe UI Semibold", 6.5f);
    public static readonly Font Emoji = new("Segoe UI Emoji", 12f);
    public static readonly Font EmojiJumbo = new("Segoe UI Emoji", 24f);

    // Discord's chrome icons are monochrome line art, not colour emoji. Segoe Fluent Icons ships
    // with Windows 11 and tints with ForeColor, which a colour emoji font can never do.
    public static readonly Font Icon = new("Segoe Fluent Icons", 12f);
    public static readonly Font IconSmall = new("Segoe Fluent Icons", 9f);
}

// Layout constants, in Discord's design pixels at 1x. Every consumer runs these through Ui.S().
//
// The predecessor scattered these as bare literals across 75 call sites, so "what is the rail
// width" had no answer you could grep for and the member list was 24px too narrow for a year.
static class M
{
    // Measured off the live rail: slot tops step 32, 80, 128... so the pitch is 48, and the icon is
    // 40 centred with 16 either side. The 48x48 <svg> wrapped around it is mask bleed for the
    // notification cutout, not the icon — reading that as the icon size is the easy mistake here.
    public const int RailWidth = 72;
    public const int RailIcon = 40;
    public const int RailPitch = 48;        // 40 icon + 8 gap
    public const int RailSeparator = 9;     // extra gap at the DM/guild rule (that slot steps 57)

    public const int SidebarWidth = 240;    // drag-resizable in the real client; 240 is the default
    public const int ChannelRow = 32;       // radius 8, inset 8 from each sidebar edge
    public const int ChannelRowInset = 8;
    public const int ChannelRowRadius = 8;
    public const int ChannelPadX = 8;       // inside the row, before the icon
    public const int ChannelIcon = 20;
    public const int ChannelIconGap = 8;    // icon -> name
    public const int NavRow = 38;           // Friends/Nitro/Shop rows above the DM list; pitch is 40
    public const int CategoryHeight = 40;   // 16px top pad + an 18px/14pt text block
    public const int CategoryPadTop = 16;
    public const int SidebarPadX = 16;      // category text, header text

    public const int HeaderHeight = 48;     // chat header, guild header, all 48
    public const int HeaderPadLeft = 16;
    // Measured off the live header at a 1280 viewport: buttons sit at 860/900/940/980, each a 32x32
    // hit box with a 20x20 icon centred in it, and the search box's right edge is 12 from the pane.
    // The icon is 20, not the 24 the button *looks* — using the hit box as the icon size draws every
    // chrome glyph 20% oversized, which is the most visible tell in the whole header.
    public const int HeaderBtn = 32;
    public const int HeaderIcon = 20;
    public const int HeaderBtnPitch = 40;
    public const int HeaderPadRight = 12;
    public const int HeaderSearchW = 244;   // 244x32, radius 8
    public const int HeaderSearchH = 32;

    public const int FriendRow = 62;        // friends-page row: 32px avatar with 14px either side
    public const int MembersWidth = 264;    // rows are 248 wide with an 8px margin either side
    // A 1:1 DM swaps the roster for the recipient's profile, and that panel is wider: a 320 column
    // holding a 304 card with 8px of margin all round.
    public const int DmProfileWidth = 320;
    public const int MemberRow = 44;
    public const int MemberAvatar = 32;
    public const int MemberGroupHeader = 42;

    public const int Avatar = 40;           // cozy-mode message avatar
    public const int MessagePadLeft = 16;   // avatar's left edge
    public const int MessageTextLeft = 72;  // avatar (40) + 16 left + 16 gap
    public const int MessagePadRight = 24;
    public const int MessageGroupGap = 17;  // margin above a new author group
    public const int MessageLineHeight = 22;

    public const int ComposerRadius = 8;
    public const int ComposerPad = 16;
    // The well is 56 tall for a single line — 17px of padding either side of one 22px line. The
    // refresh grew it from the 44 the classic client used, and 44 is the value that reads as "close
    // but not quite" against the real composer.
    public const int ComposerField = 56;
    // The refresh moved Inbox and Help out of the chat header and into the top bar, and put the
    // guild's icon and name in the middle of it. Measured at 1280: name block centred on the bar,
    // Inbox at 1208 and Help at 1244 — 24x24 each, pitch 36, 12 from the right edge.
    public const int TitleBar = 32;
    public const int TitleIcon = 24;
    public const int TitleIconPitch = 36;
    public const int TitleGuildIcon = 16;
    // The Clyde mark on the Direct Messages bar is 18, not the 16 a guild tile uses, and the label
    // beside either is 14px/500 with an 8px gap.
    public const int TitleLogo = 18;
}
