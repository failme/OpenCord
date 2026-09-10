using System.Drawing;

namespace OpenCord;

// Discord's "Keyboard Shortcuts" modal (Ctrl+/): the full list of what the client actually answers
// to, in two columns. Owner-drawn like PollDialog — no interactive controls beyond the close hit.
sealed class ShortcutHelp : Form
{
    // (keys, description) pairs, grouped. Everything here is wired up in Shell.OnKeyDown or the
    // composer; nothing is aspirational.
    static readonly (string Group, (string Keys, string What)[] Rows)[] Sections =
    {
        ("Navigation", new[]
        {
            ("Ctrl K", "Quick switcher"),
            ("Alt ↑ / ↓", "Previous / next channel"),
            ("Ctrl Alt ↑ / ↓", "Previous / next server"),
            ("Ctrl 1 – 9", "Jump to server"),
        }),
        ("Messages", new[]
        {
            ("Ctrl F", "Search this channel"),
            ("Ctrl Shift F", "Search all servers"),
            ("Ctrl P", "Pinned messages"),
            ("Esc", "Mark channel as read"),
            ("Shift Esc", "Mark server as read"),
            ("↑", "Edit your last message"),
            ("Page ↑ / ↓", "Scroll messages"),
            ("Ctrl C", "Copy selected text"),
        }),
        ("Text", new[]
        {
            ("/", "Open slash commands"),
            ("Ctrl E", "Emoji picker"),
            ("Ctrl G", "GIF picker"),
            ("Ctrl B", "Bold"),
            ("Ctrl I", "Italic"),
            ("Ctrl U", "Underline"),
        }),
        ("Voice & video", new[]
        {
            ("Ctrl Shift M", "Mute / unmute"),
            ("Ctrl Shift D", "Deafen / undeafen"),
        }),
        ("App", new[]
        {
            ("Ctrl ,", "User settings"),
            ("Ctrl Shift N", "Join a server"),
            ("Ctrl /", "This list"),
        }),
    };

    const int ColW = 420;
    int _hotClose = -1;

    ShortcutHelp()
    {
        Text = "Keyboard Shortcuts";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(Ui.S(ColW * 2 + 64), Ui.S(560));
        DoubleBuffered = true;
        KeyPreview = true;
        BackColor = Theme.Floating;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
    }

    Rectangle CloseBox => new(ClientSize.Width - Ui.S(44), Ui.S(18), Ui.S(26), Ui.S(26));

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int was = _hotClose;
        _hotClose = CloseBox.Contains(e.Location) ? 0 : -1;
        if (_hotClose != was) Invalidate();
        Cursor = _hotClose >= 0 ? Cursors.Hand : Cursors.Default;
        base.OnMouseMove(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (CloseBox.Contains(e.Location)) { Close(); return; }
        base.OnMouseDown(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape) Close();
        base.OnKeyDown(e);
    }

    // The rows split down the middle at a group boundary: the first groups whose rows fit the left
    // column's height go left, the rest go right.
    static (List<(string, string)>, List<(string, string)>) Split()
    {
        var l = new List<(string, string)>(); var r = new List<(string, string)>();
        int left = 0, total = Sections.Sum(s => s.Rows.Length + 1);
        var target = total / 2;
        foreach (var (group, rows) in Sections)
        {
            var dest = left < target ? l : r;
            dest.Add((group, ""));
            foreach (var (k, w) in rows) dest.Add((k, w));
            left += rows.Length + 1;
        }
        return (l, r);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        Ui.Text(g, "Keyboard Shortcuts", Theme.H2, new Rectangle(Ui.S(28), Ui.S(16), Ui.S(500), Ui.S(32)),
                Theme.Strong, TextFormatFlags.NoPadding);
        var cb = CloseBox;
        if (_hotClose >= 0) Ui.FillRound(g, cb, Ui.S(4), Theme.RowHover);
        Ui.Text(g, "×", Theme.Body, cb, Theme.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

        var (l, r) = Split();
        int top = Ui.S(64), rowH = Ui.S(26);
        PaintCol(g, l, Ui.S(28), top, rowH);
        PaintCol(g, r, Ui.S(ColW + 36), top, rowH);
    }

    void PaintCol(Graphics g, List<(string, string)> rows, int x, int top, int rowH)
    {
        int y = top;
        foreach (var (keys, what) in rows)
        {
            if (what.Length == 0)
            {
                y += Ui.S(10);
                Ui.Text(g, keys, Theme.SmallSemibold, new Rectangle(x, y, Ui.S(380), rowH),
                        Theme.Muted, TextFormatFlags.NoPadding);
                y += rowH;
                continue;
            }
            Ui.Text(g, keys, Theme.SmallMedium, new Rectangle(x, y, Ui.S(150), rowH),
                    Theme.Strong, TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter);
            Ui.Text(g, what, Theme.Small, new Rectangle(x + Ui.S(156), y, Ui.S(224), rowH),
                    Theme.Muted, TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter);
            y += rowH;
        }
    }

    public static void Show(IWin32Window owner)
    {
        using var d = new ShortcutHelp();
        d.ShowDialog(owner);
    }
}
