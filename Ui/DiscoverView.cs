using System.Drawing;
using System.Drawing.Drawing2D;

namespace ClaudeScord;

/// Server discovery: the grid of joinable public servers behind the rail's compass.
///
/// Backed by the two calls the live client makes — a paged `discoverable-guilds` GET for the list
/// and a PUT to the guild's member collection to join. The web client fires that same PUT with
/// `lurker=true` the instant you click a card, which is a preview that leaves no membership; this
/// only ever sends the real one, and only when the Join button is pressed.
///
/// Search filters the page that has been fetched rather than querying the server: Discord's own
/// discovery search runs through a separate search host that a user token cannot reach.
sealed class DiscoverView : Control
{
    readonly List<UserDiscoverGuild> _all = new();
    readonly List<UserDiscoverGuild> _shown = new();
    readonly TextBox _search;
    readonly Scroller _scroll;
    readonly HashSet<ulong> _joining = new();
    int _hover = -1, _hoverJoin = -1;
    bool _busy;
    string? _error;

    /// Raised once a join succeeds, so the session can select the new server.
    public event Action<ulong>? Joined;

    // Design px. A card is the full art tile Discord uses: cover, then icon, name, description and
    // the two counts.
    // The card is tall enough for a footer row of its own: cover, then the overlapping icon, name,
    // a two-line description, and a 32px row holding the counts and the Join button side by side.
    // Sized so nothing has to overlap anything — the description used to run under the button.
    const int Pad = 40, CardW = 300, CardH = 268, CoverH = 120, Gap = 16, HeaderH = 116;
    const int JoinW = 76, JoinH = 32;

    public DiscoverView()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Chat;
        _scroll = new Scroller(this);

        _search = new TextBox
        {
            BorderStyle = BorderStyle.None,
            BackColor = Theme.InputBg,
            ForeColor = Theme.Text,
            Font = Theme.Body,
            PlaceholderText = "Search for a server",
            Visible = false,
        };
        _search.TextChanged += (_, _) => { Filter(); Invalidate(); };
        Controls.Add(_search);
    }

    /// Fetches the first page. Called every time the pane is opened; the list is small and the
    /// counts go stale quickly.
    public async void Load()
    {
        _search.Visible = true;
        LayoutSearch();
        if (_busy) return;
        _busy = true; _error = null; Invalidate();
        var c = App.Client;
        if (c == null) { _busy = false; return; }
        try
        {
            var (guilds, _) = await c.Rest.DiscoverGuildsAsync(0, 48);
            _all.Clear();
            _all.AddRange(guilds);
            if (_all.Count == 0) _error = "Discovery is unavailable right now.";
        }
        catch (Exception e) { _error = e.Message; }
        _busy = false;
        Filter();
        _scroll.Reset();
        Invalidate();
    }

    void Filter()
    {
        var q = _search.Text.Trim();
        _shown.Clear();
        _shown.AddRange(q.Length == 0
            ? _all
            : _all.Where(g => g.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                           || (g.Description ?? "").Contains(q, StringComparison.OrdinalIgnoreCase)));
        _hover = _hoverJoin = -1;
    }

    void LayoutSearch() =>
        _search.SetBounds(Ui.S(Pad + 12), Ui.S(70), Math.Max(Ui.S(120), Width - Ui.S(Pad * 2 + 24)), Ui.S(22));

    protected override void OnSizeChanged(EventArgs e) { LayoutSearch(); base.OnSizeChanged(e); }

    int Cols => Math.Max(1, (Width - Ui.S(Pad * 2) + Ui.S(Gap)) / Ui.S(CardW + Gap));
    int Rows => (_shown.Count + Cols - 1) / Cols;
    int ContentH => Ui.S(HeaderH) + Rows * Ui.S(CardH + Gap) + Ui.S(Pad);
    int MaxScroll => Math.Max(0, ContentH - Height);

    Rectangle CardAt(int i)
    {
        int col = i % Cols, row = i / Cols;
        return new Rectangle(Ui.S(Pad) + col * Ui.S(CardW + Gap),
                             Ui.S(HeaderH) + row * Ui.S(CardH + Gap) - _scroll.Value,
                             Ui.S(CardW), Ui.S(CardH));
    }

    /// The Join button sits in the card's bottom-right.
    Rectangle JoinAt(int i)
    {
        var c = CardAt(i);
        return new Rectangle(c.Right - Ui.S(12 + JoinW), c.Bottom - Ui.S(12 + JoinH), Ui.S(JoinW), Ui.S(JoinH));
    }

    int HitCard(Point p)
    {
        for (int i = 0; i < _shown.Count; i++) if (CardAt(i).Contains(p)) return i;
        return -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int h = HitCard(e.Location);
        int j = h >= 0 && JoinAt(h).Contains(e.Location) ? h : -1;
        if (h != _hover || j != _hoverJoin) { _hover = h; _hoverJoin = j; Invalidate(); }
        Cursor = j >= 0 ? Cursors.Hand : Cursors.Default;
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        if (_hover != -1 || _hoverJoin != -1) { _hover = _hoverJoin = -1; Invalidate(); }
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        int i = HitCard(e.Location);
        // Only the button joins. Clicking the card body does nothing — the live client lurks there,
        // which is a write to the account, and a stray click should never cost a membership.
        if (i >= 0 && JoinAt(i).Contains(e.Location)) Join(_shown[i]);
        base.OnMouseDown(e);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        _scroll.Wheel(e.Delta, MaxScroll);
        base.OnMouseWheel(e);
    }

    async void Join(UserDiscoverGuild g)
    {
        var c = App.Client;
        if (c == null || !_joining.Add(g.Id)) return;
        Invalidate();
        var (ok, err) = await c.Rest.JoinDiscoverableGuildAsync(g.Id);
        _joining.Remove(g.Id);
        if (ok) Joined?.Invoke(g.Id);
        else { _error = err; Invalidate(); }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Ui.Fill(g, ClientRectangle, Theme.Chat);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        int x = Ui.S(Pad);
        Ui.Text(g, "Discover", Theme.H1, new Rectangle(x, Ui.S(24), Width - Ui.S(Pad * 2), Ui.S(32)),
                Theme.Strong, TextFormatFlags.VerticalCenter);
        Ui.FillRound(g, new Rectangle(x, Ui.S(64), Math.Max(Ui.S(120), Width - Ui.S(Pad * 2)), Ui.S(34)),
                     Ui.S(8), Theme.InputBg);

        if (_busy && _shown.Count == 0)
        {
            Ui.Text(g, "Loading…", Theme.Body, new Rectangle(x, Ui.S(HeaderH), Width - Ui.S(Pad * 2), Ui.S(24)),
                    Theme.Muted);
            return;
        }
        if (_error != null && _shown.Count == 0)
        {
            Ui.Text(g, _error, Theme.Body, new Rectangle(x, Ui.S(HeaderH), Width - Ui.S(Pad * 2), Ui.S(48)),
                    Theme.Danger, TextFormatFlags.WordBreak);
            return;
        }

        for (int i = 0; i < _shown.Count; i++)
        {
            var card = CardAt(i);
            if (card.Bottom < 0 || card.Top > Height) continue;
            PaintCard(g, _shown[i], card, i);
        }
    }

    void PaintCard(Graphics g, UserDiscoverGuild s, Rectangle card, int i)
    {
        Ui.FillRound(g, card, Ui.S(8), _hover == i ? Theme.Surface : Theme.EmbedBg);

        // Cover art, clipped to the card's rounded top.
        var cover = new Rectangle(card.X, card.Y, card.Width, Ui.S(CoverH));
        var st = g.Save();
        using (var round = Ui.RoundRect(card, Ui.S(8))) g.SetClip(round, CombineMode.Intersect);
        if (Media.Get(s.CoverUrl, this) is { } art)
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(art, cover);
        }
        else Ui.Fill(g, cover, Theme.Surface);
        g.Restore(st);

        // The server's icon overlaps the cover's bottom edge, ringed in the card colour.
        int av = Ui.S(48);
        var ab = new Rectangle(card.X + Ui.S(12), cover.Bottom - av / 2, av, av);
        using (var ring = new SolidBrush(_hover == i ? Theme.Surface : Theme.EmbedBg))
            g.FillEllipse(ring, Rectangle.Inflate(ab, Ui.S(3), Ui.S(3)));
        Ui.Avatar(g, Media.Get(s.IconUrl, this), ab, Theme.SurfaceHigh, this);

        int tx = card.X + Ui.S(12), tw = card.Width - Ui.S(24);
        int y = ab.Bottom + Ui.S(8);
        Ui.Text(g, s.Name, Theme.H3, new Rectangle(tx, y, tw, Ui.S(22)), Theme.Strong,
                TextFormatFlags.EndEllipsis);
        y += Ui.S(24);
        var join = JoinAt(i);
        // Two lines, ending above the footer row rather than sliding under the button.
        Ui.Text(g, s.Description ?? "", Theme.Small,
                new Rectangle(tx, y, tw, Math.Max(0, join.Top - Ui.S(6) - y)), Theme.Muted,
                TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);

        // Online / member counts, as two dotted stats, sharing the footer row with the button.
        int cy = join.Top + (join.Height - Ui.S(16)) / 2;
        int cw = Math.Max(0, join.X - Ui.S(8) - (tx + Ui.S(12)));
        using (var b = new SolidBrush(Theme.Online)) g.FillEllipse(b, tx, cy + Ui.S(5), Ui.S(8), Ui.S(8));
        var online = UserDiscoverGuild.Compact(s.OnlineCount) + " Online";
        Ui.Text(g, online, Theme.Small, new Rectangle(tx + Ui.S(12), cy, cw, Ui.S(16)), Theme.Muted,
                TextFormatFlags.EndEllipsis);
        int mx = tx + Ui.S(12) + Ui.Measure(online, Theme.Small).Width + Ui.S(10);
        if (mx + Ui.S(60) < join.X)
        {
            using var b = new SolidBrush(Theme.Offline);
            g.FillEllipse(b, mx, cy + Ui.S(5), Ui.S(8), Ui.S(8));
            Ui.Text(g, UserDiscoverGuild.Compact(s.MemberCount) + " Members", Theme.Small,
                    new Rectangle(mx + Ui.S(12), cy, join.X - Ui.S(8) - mx - Ui.S(12), Ui.S(16)),
                    Theme.Muted, TextFormatFlags.EndEllipsis);
        }

        bool busy = _joining.Contains(s.Id);
        Ui.FillRound(g, join, Ui.S(8), busy ? Theme.Surface
                                     : _hoverJoin == i ? Theme.BlurpleHover : Theme.Blurple);
        Ui.Text(g, busy ? "…" : "Join", Theme.Category, join, Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }
}
