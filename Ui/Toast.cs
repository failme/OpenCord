using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace OpenCord;

// A desktop notification, Discord-toast-style: a small dark card in the corner of the work area,
// shown without stealing focus, dismissed by click (which jumps to the channel) or after a few
// seconds. One at a time, like the real client.
static class Toast
{
    static ToastForm? _current;

    /// (guildId, channelId) — Session sets this so a click jumps into the right channel.
    public static Action<ulong, ulong>? OnClick;

    public static void Show(string title, string body, string? iconUrl, ulong guildId, ulong channelId)
    {
        _current?.Close();
        _current = new ToastForm(title, body, iconUrl, guildId, channelId);
        _current.Show();
    }
}

sealed class ToastForm : Form
{
    readonly string _title, _body, _iconUrl;
    readonly ulong _guild, _channel;
    readonly System.Windows.Forms.Timer _autoClose = new() { Interval = 5200 };

    const int WS_EX_TOOLWINDOW = 0x80, WS_EX_TOPMOST = 0x8, WS_EX_NOACTIVATE = 0x08000000;

    public ToastForm(string title, string body, string? iconUrl, ulong guildId, ulong channelId)
    {
        _title = title;
        _body = body;
        _iconUrl = iconUrl ?? "";
        _guild = guildId;
        _channel = channelId;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        DoubleBuffered = true;
        BackColor = Theme.Floating;
        Size = new Size(Ui.S(360), Ui.S(76));

        var wa = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1040);
        Location = new Point(wa.Right - Width - Ui.S(16), wa.Bottom - Height - Ui.S(16));

        _autoClose.Tick += (_, _) => Close();
        _autoClose.Start();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _autoClose.Dispose();
        base.Dispose(disposing);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_TOPMOST | WS_EX_NOACTIVATE;
            return cp;
        }
    }

    protected override bool ShowWithoutActivation => true;

    protected override void OnMouseDown(MouseEventArgs e)
    {
        Toast.OnClick?.Invoke(_guild, _channel);
        Close();
        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e) { Cursor = Cursors.Hand; base.OnMouseMove(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        // Card with a soft shadow + rounded corners, like a native toast.
        Ui.FillRound(g, new Rectangle(Ui.S(2), Ui.S(3), Width - Ui.S(5), Height - Ui.S(6)), Ui.S(8), Theme.Floating);
        Ui.FillRound(g, new Rectangle(0, 0, Width - 1, Height - 1), Ui.S(8), Theme.Surface);
        using (var pen = new Pen(Theme.Border))
        using (var path = Ui.RoundRect(new Rectangle(0, 0, Width - 2, Height - 2), Ui.S(8)))
            g.DrawPath(pen, path);

        int av = Ui.S(40);
        var ab = new Rectangle(Ui.S(14), (Height - av) / 2, av, av);
        Ui.Avatar(g, Media.Get(_iconUrl, this), ab, Theme.Sidebar);

        int tx = ab.Right + Ui.S(12);
        Ui.Text(g, _title, Theme.BodyMedium, new Rectangle(tx, Ui.S(10), Width - tx - Ui.S(16), Ui.S(22)),
                Theme.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        Ui.Text(g, _body, Theme.Body, new Rectangle(tx, Ui.S(34), Width - tx - Ui.S(16), Ui.S(30)),
                Theme.Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }
}
