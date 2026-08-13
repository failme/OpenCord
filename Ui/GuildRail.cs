using System.Drawing;
using System.Drawing.Drawing2D;

namespace OpenCord;

// The server rail: home button, a rule, one squircle per guild, then the add/explore actions.
//
// Geometry is measured, not guessed — slot tops in the live client step 32, 80, 128, so the pitch is
// 48 and the icon is 40 centred in a 72px column. The <svg> around each icon is 48px, which is bleed
// for the notification cutout; sizing icons to 48 makes the whole rail read too tight.
sealed class GuildRail : Control
{
    // The icon is a URL, not an Image: Media resolves it during paint and invalidates when it lands,
    // so the rail never blocks on the network and never holds a handle it has to dispose.
    public sealed record Slot(ulong Id, string Name, string? IconUrl = null, bool Unread = false, int Mentions = 0);

    const int Home = -1;                      // the home/DMs slot, drawn above the rule
    const int AddServer = -2;                 // the "+" action below the guilds
    const int Discover = -3;                  // the compass below it, as in the live rail
    readonly List<Slot> _guilds = new();
    int _hover = int.MinValue;
    readonly Scroller _scroll;
    // Discord eases the selection pill (8 unread -> 20 hover -> 40 selected) over ~120ms. State is
    // per-slot so a selected guild's pill stays up while the pointer rests on another, exactly like
    // the real rail.
    //
    // The icon shape does *not* animate: the classic rail morphed a circle into a squircle on
    // hover, but the redesign masks every tile with the same squircle whatever its state — verified
    // by reading the mask path off a selected and an unselected tile, which are byte-identical (and
    // identical to Icons.GuildSquircle). Keeping the morph is what made unselected servers round.
    readonly Dictionary<int, float> _pills = new();    // slot -> current pill height
    // Scratch buffer reused by the glide tick: ToList() per tick would allocate twice every 15ms.
    readonly List<int> _keys = new();
    readonly System.Windows.Forms.Timer _glide = new() { Interval = 15 };
    readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

    public ulong? Selected { get; private set; }   // null = home
    public int HomeMentions;
    public event Action<ulong?>? Picked;
    public event Action<Slot, Point>? GuildMenu;
    public event Action? AddServerClicked;
    public event Action? DiscoverClicked;

    public GuildRail()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Rail;
        Width = Ui.S(M.RailWidth);
        _scroll = new Scroller(this);
        _glide.Tick += (_, _) =>
        {
            float dt = (float)_clock.Elapsed.TotalSeconds;
            _clock.Restart();
            // The hovered and selected slots always have animation state; others only if they had
            // some (an unread nub glides up on hover and back down on leave).
            Ensure(_hover);
            Ensure(SelectedSlot());
            bool moving = false;
            _keys.Clear(); _keys.AddRange(_pills.Keys);
            foreach (var slot in _keys)
            {
                float cur = _pills[slot];
                float want = PillTarget(slot);
                if (Ease(ref cur, want, dt)) { _pills[slot] = cur; moving = true; }
                else if (want <= 1f) _pills.Remove(slot);   // settled at nothing: drop the entry
                else _pills[slot] = want;
            }
            if (!moving) _glide.Stop();
            Invalidate();
        };
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _glide.Dispose();
        base.Dispose(disposing);
    }

    // Seed animation state for a slot so the next tick can ease it from its resting value.
    void Ensure(int slot)
    {
        if (slot == int.MinValue) return;
        if (!_pills.ContainsKey(slot)) _pills[slot] = PillRest(slot);
    }

    // Settles the hover pill (8->20) in ~150ms, the same feel as Discord's ~120ms ease. Eased on
    // elapsed time rather than per tick so a dropped WinForms timer tick does not show up as a
    // jump — see Ui.Ease.
    static bool Ease(ref float cur, float want, float dt)
    {
        if (Math.Abs(want - cur) < 0.02f) { cur = want; return false; }
        cur = Ui.Ease(cur, want, dt, 40f);
        return true;
    }

    // The slot holding the selection: Home when Selected is null, else the guild's index.
    int SelectedSlot()
    {
        if (Selected == null) return Home;
        for (int i = 0; i < _guilds.Count; i++)
            if (_guilds[i].Id == Selected) return i;
        return int.MinValue;
    }

    // Resting pill height before any hover glide: a full bar for the selection, an 8px nub for
    // unread, nothing otherwise. Home follows the same rule — full when it holds the selection
    // (the DM screen is the default landing state, so the missing pill was very visible).
    float PillRest(int slot)
    {
        if (slot == Home) return Selected == null ? Icon : HomeMentions > 0 ? Ui.S(8) : 1f;
        if (slot < 0 || slot >= _guilds.Count) return 1f;
        if (Selected == _guilds[slot].Id) return Icon;
        return _guilds[slot].Unread ? Ui.S(8) : 1f;
    }

    // Hover grows the pill to half height, never below its resting value.
    float PillTarget(int slot) => slot == _hover ? Math.Max(Ui.S(20), PillRest(slot)) : PillRest(slot);

    public void SetGuilds(IEnumerable<Slot> s)
    {
        _guilds.Clear();
        _guilds.AddRange(s);
        Invalidate();
    }

    public void Select(ulong? id) { Selected = id; Ensure(SelectedSlot()); if (!_glide.Enabled) { _clock.Restart(); _glide.Start(); } Invalidate(); }

    int Pad => Ui.S(12);
    int Pitch => Ui.S(M.RailPitch);
    int Icon => Ui.S(M.RailIcon);
    int Sep => Ui.S(M.RailSeparator);
    int IconX => (Width - Icon) / 2;   // not "Left": that shadows Control.Left, the control's own X

    int SlotY(int i) => i switch
    {
        Home => Pad,
        AddServer => Pad + Pitch + Sep + _guilds.Count * Pitch - _scroll.Value,
        Discover => Pad + Pitch + Sep + (_guilds.Count + 1) * Pitch - _scroll.Value,
        _ => Pad + Pitch + Sep + i * Pitch - _scroll.Value,
    };

    int ContentH => Pad + Pitch + Sep + (_guilds.Count + 2) * Pitch + Pad;
    // The account panel is laid over the bottom of this column (see Shell.LayoutTray), so the
    // scrollable height stops above it or the last guild sits underneath the card.
    int ViewH => Math.Max(0, Height - Ui.S(AccountTray.TrayH));
    int MaxScroll => Math.Max(0, ContentH - ViewH);

    int HitTest(Point p)
    {
        if (p.X < IconX - Ui.S(8) || p.X > IconX + Icon + Ui.S(8)) return int.MinValue;
        if (p.Y >= SlotY(Home) && p.Y < SlotY(Home) + Icon) return Home;
        for (int i = 0; i < _guilds.Count; i++)
            if (p.Y >= SlotY(i) && p.Y < SlotY(i) + Icon) return i;
        if (p.Y >= SlotY(AddServer) && p.Y < SlotY(AddServer) + Icon) return AddServer;
        if (p.Y >= SlotY(Discover) && p.Y < SlotY(Discover) + Icon) return Discover;
        return int.MinValue;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int h = HitTest(e.Location);
        if (h != _hover)
        {
            _hover = h;
            if (!_glide.Enabled) { _clock.Restart(); _glide.Start(); }
            string? tip = h == Home ? "Direct Messages"
                        : h == AddServer ? "Add a Server"
                        : h == Discover ? "Discover"
                        : h >= 0 && h < _guilds.Count ? _guilds[h].Name : null;
            Tip.Show(this, tip, tip == null ? Rectangle.Empty : new Rectangle(IconX, SlotY(h), Icon, Icon));
            Invalidate();
        }
        Cursor = h != int.MinValue ? Cursors.Hand : Cursors.Default;
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        if (_hover != int.MinValue)
        {
            _hover = int.MinValue; Tip.Hide();
            if (!_glide.Enabled) { _clock.Restart(); _glide.Start(); }
            Invalidate();
        }
        base.OnMouseLeave(e);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        _scroll.Wheel(e.Delta, MaxScroll);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        int h = HitTest(e.Location);
        if (h == int.MinValue) return;
        if (e.Button == MouseButtons.Right)
        {
            if (h >= 0 && h < _guilds.Count) GuildMenu?.Invoke(_guilds[h], PointToScreen(e.Location));
            return;
        }
        if (e.Button != MouseButtons.Left) return;
        if (h == AddServer) { AddServerClicked?.Invoke(); return; }
        if (h == Discover) { DiscoverClicked?.Invoke(); return; }
        Selected = h == Home ? null : _guilds[h].Id;
        Invalidate();
        Picked?.Invoke(Selected);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Ui.Fill(g, ClientRectangle, Theme.Rail);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        DrawHome(g);

        // The rule between DMs and guilds: a short, thick, rounded line rather than a hairline.
        int ry = Pad + Pitch + Sep / 2;
        var rule = new Rectangle(IconX + Ui.S(4), ry, Icon - Ui.S(8), Math.Max(1, Ui.S(2)));
        Ui.FillRound(g, rule, Math.Max(1, Ui.S(1)), Theme.Border);

        var clip = g.Save();
        g.SetClip(new Rectangle(0, ry + Ui.S(4), Width, Math.Max(0, ViewH - ry - Ui.S(4))));
        for (int i = 0; i < _guilds.Count; i++) DrawGuild(g, i, _guilds[i]);
        DrawAdd(g);
        DrawDiscover(g);
        g.Restore(clip);
    }

    void DrawHome(Graphics g)
    {
        int y = SlotY(Home);
        bool sel = Selected == null, hot = _hover == Home;
        var box = new Rectangle(IconX, y, Icon, Icon);

        using (var p = Squircle(box, Home))
        using (var b = new SolidBrush(sel || hot ? Theme.Blurple : Theme.Surface))
            g.FillPath(b, p);

        // Clyde sits at ~60% of the tile, which is what the live client uses.
        int d = (int)(Icon * 0.6f);
        Svg.SvgFill(g, Icons.Clyde, new RectangleF(box.X + (Icon - d) / 2f, box.Y + (Icon - d) / 2f, d, d),
                    Color.White);

        DrawPill(g, y, Home);
        if (HomeMentions > 0) DrawBadge(g, box, HomeMentions);
    }

    void DrawAdd(Graphics g)
    {
        int y = SlotY(AddServer);
        bool hot = _hover == AddServer;
        var box = new Rectangle(IconX, y, Icon, Icon);
        using (var p = Squircle(box, AddServer))
        using (var b = new SolidBrush(hot ? Theme.Positive : Theme.Surface))
            g.FillPath(b, p);
        Svg.SvgFill(g, Icons.PlusLine, Rectangle.Inflate(box, -Ui.S(11), -Ui.S(11)),
                      hot ? Color.White : Theme.Positive);
    }

    // "Discover" — the compass tile the live rail puts under Add a Server.
    void DrawDiscover(Graphics g)
    {
        int y = SlotY(Discover);
        bool hot = _hover == Discover;
        var box = new Rectangle(IconX, y, Icon, Icon);
        using (var p = Squircle(box, Discover))
        using (var b = new SolidBrush(hot ? Theme.Positive : Theme.Surface))
            g.FillPath(b, p);
        Svg.SvgFill(g, Icons.Compass, Rectangle.Inflate(box, -Ui.S(9), -Ui.S(9)),
                    hot ? Color.White : Theme.Positive);
    }

    // Discord rests a guild icon as a *circle* and morphs it to the squircle on hover and selection
    // — not the other way round. The tween runs between two radii of the same rounded shape (the
    // circle's 50%, the squircle's ~22%), which is indistinguishable at 40px and far cheaper than
    // tweening the real squircle path; the settled end still uses the authored geometry.
    //
    /// Every tile, every state — see the note on _pills. `slot` is kept so call sites read the same
    /// as before and so a future per-slot shape has somewhere to go.
    static GraphicsPath Squircle(Rectangle box, int slot) =>
        Svg.Fit(Icons.GuildSquircle, box, Icons.GuildViewBox);

    void DrawGuild(Graphics g, int i, Slot s)
    {
        int y = SlotY(i);
        if (y + Icon < 0 || y > Height) return;
        bool sel = Selected == s.Id, hot = _hover == i;
        var box = new Rectangle(IconX, y, Icon, Icon);

        var icon = Media.Get(s.IconUrl, this);
        using (var path = Squircle(box, i))
        {
            if (icon != null)
            {
                // Clip to the squircle and draw the icon through it. Without the clip the corners
                // are square; without AntiAlias on the clip the rim comes out ragged.
                var st = g.Save();
                g.SetClip(path, CombineMode.Replace);
                if (Media.IsAnimated(icon)) Media.Animate(icon, this);
                g.DrawImage(icon, box);
                g.Restore(st);
            }
            else
            {
                using var b = new SolidBrush(sel || hot ? Theme.Blurple : Theme.Surface);
                g.FillPath(b, path);
                Ui.Text(g, Initials(s.Name), Theme.BodyMedium, box, Color.White,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
        }

        DrawPill(g, y, i);
        if (s.Mentions > 0) DrawBadge(g, box, s.Mentions);
    }

    // The selection pill on the left edge. Drawn as a full round-rect pushed half off-screen so the
    // visible half keeps its rounded right end — cheaper than building a two-corner path. The height
    // follows the slot's glide so an unread nub grows into the full selection pill on hover.
    void DrawPill(Graphics g, int slotY, int slot)
    {
        if (!_pills.TryGetValue(slot, out float h) || h <= 1f) return;
        int w = Ui.S(4);
        Ui.FillRound(g, new Rectangle(-w, slotY + (int)((Icon - h) / 2), w * 2, (int)h), w, Color.White);
    }

    void DrawBadge(Graphics g, Rectangle box, int n)
    {
        var label = n > 99 ? "99+" : n.ToString();
        var sz = Ui.Measure(label, Theme.SmallMedium);
        int w = Math.Max(Ui.S(16), sz.Width + Ui.S(8)), h = Ui.S(16);
        var badge = new Rectangle(box.Right - w + Ui.S(3), box.Bottom - h + Ui.S(2), w, h);
        // The badge punches a hole in the tile rather than sitting on top of it.
        using (var b = new SolidBrush(Theme.Rail))
            g.FillEllipse(b, Rectangle.Inflate(badge, Ui.S(3), Ui.S(3)));
        Ui.FillRound(g, badge, h / 2, Theme.Danger);
        Ui.Text(g, label, Theme.SmallMedium, badge, Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    /// Discord's fallback tile for a guild with no icon. Shared with the title bar, which shows the
    /// same 16px tile beside the guild's name.
    internal static string Initials(string name)
    {
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return "?";
        return string.Concat(words.Take(2).Select(w => char.ToUpperInvariant(w[0])));
    }
}
