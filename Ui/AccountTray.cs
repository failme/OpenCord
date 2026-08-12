using System.Drawing;

namespace ClaudeScord;

/// The account panel at the bottom left.
///
/// It is its own control rather than part of the channel sidebar because of where it sits: in the
/// live client the sidebar container starts at x=0 and this card is inset 8 from *that*, so it runs
/// underneath the guild rail rather than starting after it. Measured with the rail at 72 and the
/// sidebar at 374: the panel is at x=8 with width `sidebar - 16`, spanning both columns.
///
/// The card is 56 tall with 8 of margin all round. The avatar sits at 12,12, the two-line name
/// block at x=52, and the button strip is right-anchored 12 in from the card's right edge.
///
/// Mute and deafen are *split* buttons — a 32px toggle plus a 16px chevron for the device options,
/// joined into one 8-radius pill by a 1px seam. That strip is 146 wide against 112 without the
/// chevrons, so they only appear when the columns are wide enough to leave the name room; at
/// Discord's own default 240 sidebar they do not fit either.
sealed class AccountTray : Control
{
    // Hit box, where the glyph goes inside it, and which side of the pill is rounded.
    readonly List<(Rectangle Hit, Rectangle Glyph, string Icon, string Tip, Action Click,
                   bool Active, bool RoundLeft, bool RoundRight)> _buttons = new();
    int _hot = -1;
    bool _accountHot;

    public event Action? SettingsClicked;

    /// Design px: the card plus its margins. The rail and the channel list both reserve this much
    /// at their bottom so nothing runs underneath the card.
    public const int TrayH = 72;

    public AccountTray()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Rail;
    }

    Rectangle Card => new(Ui.S(8), Ui.S(8), Width - Ui.S(16), Ui.S(56));

    /// Design px the name needs before the chevrons are worth their room.
    const int MinNameW = 64;

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Ui.Fill(g, ClientRectangle, Theme.Rail);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        var card = Card;
        Ui.FillRound(g, card, Ui.S(8), Theme.Tray);

        var me = App.Client?.CurrentUser;
        int av = Ui.S(32);
        var ab = new Rectangle(card.X + Ui.S(12), card.Y + Ui.S(12), av, av);
        Ui.Avatar(g, Media.Get(me?.GetDisplayAvatarUrl(64), this), ab, Theme.Surface, this);
        Ui.PresenceDot(g, ab, me?.Status switch
        {
            "online" => Presence.Online, "idle" => Presence.Idle, "dnd" => Presence.Dnd, _ => Presence.Offline,
        }, Theme.Tray, Ui.S(10));

        Build(card);
        int tx = card.X + Ui.S(52);
        // Built right-to-left, so the *last* entry is the leftmost button — measuring the name
        // against [0] (the gear) runs it underneath the whole strip.
        int tw = Math.Max(0, (_buttons.Count > 0 ? _buttons[^1].Hit.X : card.Right - Ui.S(12)) - tx - Ui.S(8));

        Ui.Text(g, me?.DisplayName ?? "not signed in", Theme.BodyMedium,
                new Rectangle(tx, card.Y + Ui.S(12), tw, Ui.S(18)), Theme.Strong,
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        // The live client rolls this line: the custom status normally, the @username while the
        // pointer is over the account area.
        var sub = _accountHot ? "@" + (me?.Username ?? "") : me?.CustomStatus;
        if (!string.IsNullOrEmpty(sub))
            Ui.Text(g, sub, Theme.SmallMedium, new Rectangle(tx, card.Y + Ui.S(32), tw, Ui.S(14)),
                    Theme.Subtle, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

        for (int i = 0; i < _buttons.Count; i++)
        {
            var b = _buttons[i];
            if (b.Active) Ui.FillRound(g, b.Hit, Ui.S(8), Theme.Tint(Theme.Tray, Theme.Danger, 0.12f),
                                       b.RoundLeft, b.RoundRight);
            else if (_hot == i) Ui.FillRound(g, b.Hit, Ui.S(8), Theme.Tint(Theme.Tray, Color.White, 0.06f),
                                             b.RoundLeft, b.RoundRight);
            Icons.Draw(g, b.Icon, b.Glyph, b.Active ? Theme.Danger : Theme.Subtle, 1.7f);
        }
    }

    void Build(Rectangle card)
    {
        _buttons.Clear();
        var c = App.Client;
        int y = card.Y + Ui.S(12), h = Ui.S(32), main = Ui.S(32), chev = Ui.S(16);

        int avail = card.Width - Ui.S(52) - Ui.S(12);
        bool split = avail - (main * 3 + Ui.S(16) + chev * 2 + Ui.S(2)) >= Ui.S(MinNameW);

        // Right to left: settings, then the deafen and mute pairs, 8 apart.
        int x = card.Right - Ui.S(12) - main;
        _buttons.Add((new Rectangle(x, y, main, h), Glyph(x, y, main, h, Ui.S(20)),
                      Icons.GearLine, "User Settings", () => SettingsClicked?.Invoke(), false, true, true));

        void Split(string icon, string tip, bool active, Action click, Action options)
        {
            if (split)
            {
                x -= Ui.S(8) + chev;
                _buttons.Add((new Rectangle(x, y, chev, h), Glyph(x, y, chev, h, Ui.S(16)),
                              Icons.ChevronDown, tip + " Options", options, active, false, true));
                x -= Ui.S(1) + main;
                _buttons.Add((new Rectangle(x, y, main, h), Glyph(x, y, main, h, Ui.S(20)),
                              icon, tip, click, active, true, false));
            }
            else
            {
                x -= Ui.S(8) + main;
                _buttons.Add((new Rectangle(x, y, main, h), Glyph(x, y, main, h, Ui.S(20)),
                              icon, tip, click, active, true, true));
            }
        }
        Split(c?.SelfDeaf == true ? Icons.HeadsetMutedLine : Icons.HeadsetLine, "Deafen",
              c?.SelfDeaf == true, ToggleDeaf, () => SettingsClicked?.Invoke());
        Split(c?.SelfMute == true ? Icons.MicMutedLine : Icons.MicLine, "Mute",
              c?.SelfMute == true, ToggleMute, () => SettingsClicked?.Invoke());
    }

    static Rectangle Glyph(int x, int y, int w, int h, int size) =>
        new(x + (w - size) / 2, y + (h - size) / 2, size, size);

    // The clickable "me" area (avatar + name). Discord opens the status menu there, and rolls the
    // subtext to the @username while the pointer is over it.
    Rectangle UserBox
    {
        get
        {
            var card = Card;
            int right = _buttons.Count > 0 ? _buttons[^1].Hit.X - Ui.S(8) : card.Right - Ui.S(12);
            return new Rectangle(card.X + Ui.S(8), card.Y + Ui.S(7),
                                 Math.Max(0, right - card.X - Ui.S(8)), Ui.S(42));
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int t = _buttons.FindIndex(b => b.Hit.Contains(e.Location));
        bool acc = UserBox.Contains(e.Location);
        if (t != _hot || acc != _accountHot)
        {
            _hot = t; _accountHot = acc;
            Tip.Show(this, t >= 0 ? _buttons[t].Tip : null, t >= 0 ? _buttons[t].Hit : Rectangle.Empty);
            Invalidate();
        }
        Cursor = t >= 0 || acc ? Cursors.Hand : Cursors.Default;
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        if (_hot != -1 || _accountHot) { _hot = -1; _accountHot = false; Tip.Hide(); Invalidate(); }
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) { base.OnMouseDown(e); return; }
        if (_hot >= 0) { _buttons[_hot].Click(); return; }
        if (UserBox.Contains(e.Location)) TrayPopover.Show(this, PointToScreen(new Point(0, 0)));
        base.OnMouseDown(e);
    }

    void ToggleMute()
    {
        var c = App.Client;
        if (c == null) return;
        _ = c.SetVoiceStateAsync(c.MyVoiceGuild, c.MyVoiceChannel, !c.SelfMute, c.SelfDeaf);
        Invalidate();
    }

    void ToggleDeaf()
    {
        var c = App.Client;
        if (c == null) return;
        bool on = !c.SelfDeaf;
        _ = c.SetVoiceStateAsync(c.MyVoiceGuild, c.MyVoiceChannel, on || c.SelfMute, on);
        Invalidate();
    }

    /// Fired from TrayPopover (a sibling Control) via this public entry point.
    public void OpenSettings() => SettingsClicked?.Invoke();
}
