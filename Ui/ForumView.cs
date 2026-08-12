using System.Drawing;
using System.Drawing.Drawing2D;

namespace ClaudeScord;

// A forum channel is a list of posts, not a message list. Discord replaces the chat pane with a
// column of post cards — title, applied tags, a preview of the opening message, and the reply count
// — and clicking one opens that post as a thread.
//
// Same "not docked" rule as FriendsView and VoiceView: it is re-bounded over the chat pane's
// rectangle when shown, because a second Fill would starve the chat.
sealed class ForumView : Control
{
    public sealed record Post(ulong Id, string Name, string Author, string? AvatarUrl,
                              string Preview, int Replies, string When, IReadOnlyList<string> Tags);

    readonly List<Post> _posts = new();
    readonly Scroller _scroll;
    string _channel = "";
    string? _topic;
    int _hover = -1;
    bool _loading;

    /// The post the user clicked — the session opens it as a thread.
    public event Action<ulong>? PostPicked;
    /// The "New Post" button.
    public event Action? NewPost;

    public ForumView()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Chat;
        Visible = false;
        _scroll = new Scroller(this);
    }

    public void SetLoading(string channel, string? topic)
    {
        _channel = channel;
        _topic = topic;
        _posts.Clear();
        _loading = true;
        _hover = -1;
        _scroll.Clamp(0);
        Invalidate();
    }

    public void Set(string channel, string? topic, IEnumerable<Post> posts)
    {
        _channel = channel;
        _topic = topic;
        _posts.Clear();
        _posts.AddRange(posts);
        _loading = false;
        _hover = -1;
        _scroll.Clamp(MaxScroll);
        Invalidate();
    }

    // ── geometry ────────────────────────────────────────────────────────────────────────────────
    static int HeaderH => Ui.S(76);
    static int CardH => Ui.S(96);
    static int Gap => Ui.S(8);
    int ContentH => _posts.Count * (CardH + Gap) + Ui.S(16);
    int MaxScroll => Math.Max(0, ContentH - (Height - HeaderH));

    Rectangle CardBox(int i) =>
        new(Ui.S(16), HeaderH + Ui.S(8) + i * (CardH + Gap) - _scroll.Value,
            Math.Max(Ui.S(80), Width - Ui.S(32)), CardH);

    Rectangle NewPostBox => new(Width - Ui.S(140), Ui.S(22), Ui.S(120), Ui.S(32));

    int HitTest(Point p)
    {
        if (p.Y < HeaderH) return -1;
        for (int i = 0; i < _posts.Count; i++)
            if (CardBox(i).Contains(p)) return i;
        return -1;
    }

    // ── input ───────────────────────────────────────────────────────────────────────────────────
    protected override void OnMouseMove(MouseEventArgs e)
    {
        int h = HitTest(e.Location);
        bool btn = NewPostBox.Contains(e.Location);
        if (h != _hover) { _hover = h; Invalidate(); }
        Cursor = h >= 0 || btn ? Cursors.Hand : Cursors.Default;
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        if (_hover != -1) { _hover = -1; Invalidate(); }
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        if (NewPostBox.Contains(e.Location)) { NewPost?.Invoke(); return; }
        int i = HitTest(e.Location);
        if (i >= 0) PostPicked?.Invoke(_posts[i].Id);
        base.OnMouseDown(e);
    }

    protected override void OnMouseWheel(MouseEventArgs e) => _scroll.Wheel(e.Delta, MaxScroll);

    // ── paint ───────────────────────────────────────────────────────────────────────────────────
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Ui.Fill(g, ClientRectangle, Theme.Chat);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        var clip = g.Save();
        g.SetClip(new Rectangle(0, HeaderH, Width, Math.Max(0, Height - HeaderH)));
        for (int i = 0; i < _posts.Count; i++)
        {
            var box = CardBox(i);
            if (box.Bottom < HeaderH || box.Top > Height) continue;
            PaintCard(g, box, _posts[i], _hover == i);
        }
        g.Restore(clip);

        PaintHeader(g);

        if (!_loading && _posts.Count == 0)
            Ui.Text(g, "No posts yet.", Theme.Body,
                    new Rectangle(0, HeaderH + Ui.S(40), Width, Ui.S(24)), Theme.Muted,
                    TextFormatFlags.HorizontalCenter);
        else if (_loading)
            Ui.Text(g, "Loading posts…", Theme.Body,
                    new Rectangle(0, HeaderH + Ui.S(40), Width, Ui.S(24)), Theme.Muted,
                    TextFormatFlags.HorizontalCenter);
    }

    void PaintHeader(Graphics g)
    {
        Ui.Fill(g, new Rectangle(0, 0, Width, HeaderH), Theme.Chat);
        int icon = Ui.S(20);
        Svg.SvgFill(g, Icons.ForumLine, new RectangleF(Ui.S(16), Ui.S(18), icon, icon), Theme.ChannelIcon);
        Ui.Text(g, _channel, Theme.H2, new Rectangle(Ui.S(44), Ui.S(12), Width - Ui.S(200), Ui.S(28)),
                Theme.Strong, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        if (!string.IsNullOrWhiteSpace(_topic))
            Ui.Text(g, Markdown.Flatten(_topic), Theme.Small,
                    new Rectangle(Ui.S(44), Ui.S(40), Width - Ui.S(200), Ui.S(18)), Theme.Faint,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

        var b = NewPostBox;
        Ui.FillRound(g, b, Ui.S(8), Theme.Blurple);
        Ui.Text(g, "New Post", Theme.SmallMedium, b, Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

        Ui.Fill(g, new Rectangle(0, HeaderH - 1, Width, 1), Theme.BorderSubtle);
    }

    void PaintCard(Graphics g, Rectangle box, Post p, bool hot)
    {
        Ui.FillRound(g, box, Ui.S(8), hot ? Theme.SurfaceHigh : Theme.Surface);

        int x = box.X + Ui.S(16);
        Ui.Text(g, p.Name, Theme.BodyMedium, new Rectangle(x, box.Y + Ui.S(12), box.Width - Ui.S(140), Ui.S(22)),
                Theme.Strong, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

        // Author avatar + the opening message, the way a forum card previews its post.
        int av = Ui.S(16);
        var ab = new Rectangle(x, box.Y + Ui.S(40), av, av);
        Ui.Avatar(g, Media.Get(p.AvatarUrl, this), ab, Theme.Field);
        int tx = ab.Right + Ui.S(6);
        var authorW = Ui.Measure(p.Author, Theme.SmallMedium).Width;
        Ui.Text(g, p.Author, Theme.SmallMedium,
                new Rectangle(tx, box.Y + Ui.S(40), Math.Min(authorW, Ui.S(160)), av), Theme.Muted,
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        Ui.Text(g, p.Preview, Theme.Small,
                new Rectangle(tx + Math.Min(authorW, Ui.S(160)) + Ui.S(6), box.Y + Ui.S(40),
                              Math.Max(Ui.S(20), box.Right - tx - Math.Min(authorW, Ui.S(160)) - Ui.S(140)), av),
                Theme.Faint, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

        // Tag pills along the bottom.
        int px = x;
        foreach (var t in p.Tags.Take(4))
        {
            int w = Ui.Measure(t, Theme.Small).Width + Ui.S(14);
            if (px + w > box.Right - Ui.S(120)) break;
            var pill = new Rectangle(px, box.Y + Ui.S(66), w, Ui.S(18));
            Ui.FillRound(g, pill, Ui.S(9), Theme.Field);
            Ui.Text(g, t, Theme.Small, pill, Theme.Muted,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            px += w + Ui.S(6);
        }

        // Reply count and last activity, right-aligned like the live client.
        Ui.Text(g, p.Replies == 1 ? "1 reply" : $"{p.Replies} replies", Theme.Small,
                new Rectangle(box.Right - Ui.S(120), box.Y + Ui.S(12), Ui.S(104), Ui.S(20)), Theme.Muted,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Right);
        Ui.Text(g, p.When, Theme.Small,
                new Rectangle(box.Right - Ui.S(120), box.Y + Ui.S(34), Ui.S(104), Ui.S(20)), Theme.Faint,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Right);
    }
}
