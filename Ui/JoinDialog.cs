using System.Drawing;
using System.Drawing.Drawing2D;

namespace OpenCord;

// Discord's "Join a Server" modal: paste an invite, see the server's name / icon / member count,
// then accept. Accepting makes the gateway deliver GUILD_CREATE, which the session already turns
// into a new rail button — so this dialog only has to perform the REST accept and close.
sealed class JoinDialog : Form
{
    readonly JoinCard _card = new();
    TitleBar _title = null!;

    JoinDialog(UserClient client)
    {
        Text = "Join a Server";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(Ui.S(440), Ui.S(360));
        DoubleBuffered = true;
        KeyPreview = true;
        BackColor = Theme.Rail;

        _title = new TitleBar(this) { Dock = DockStyle.Top, Height = Ui.S(M.TitleBar) };
        Controls.Add(_card);
        Controls.Add(_title);

        _card.Join += async () =>
        {
            var code = UserRestClient.InviteCode(_card.Code);
            if (code.Length == 0) { _card.ShowError("Enter an invite code to join."); return; }
            _card.SetBusy(true);
            var (ok, error) = await client.Rest.AcceptInviteAsync(code);
            if (ok) { DialogResult = DialogResult.OK; Close(); return; }
            _card.SetBusy(false);
            _card.ShowError(error ?? "That invite didn't work.");
        };

        _card.PreviewRequested += async code =>
        {
            if (code.Length < 3) { _card.ShowPreview(null, 0, null); return; }
            var (name, members, error) = await client.Rest.PreviewInviteAsync(code);
            if (name != null) _card.ShowPreview(name, members, null);
            else _card.ShowPreview(null, 0, error);
        };
    }

    public static void Show(Shell shell, UserClient client)
    {
        using var dlg = new JoinDialog(client);
        dlg.ShowDialog(shell);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); }
        base.OnKeyDown(e);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.FrameColor(Handle, Theme.Rail);
    }

    protected override CreateParams CreateParams
    {
        get { var cp = base.CreateParams; cp.Style |= 0x00020000 /*WS_MINIMIZEBOX*/; return cp; }
    }
}

// The join card: title, the invite well, the live server preview, and the join button. The preview
// (icon + name + member count) is what makes the modal feel like Discord's rather than a bare form.
sealed class JoinCard : Panel
{
    const int Pad = 28;

    readonly TextBox _field = new();
    readonly LoginButton _button = new() { Text = "Join Server" };
    string? _error;
    string? _previewName;
    string? _previewIcon;
    int _previewMembers;
    string? _previewError;
    bool _busy;
    string _lastProbed = "";

    public string Code => _field.Text.Trim();

    public event Action? Join;
    public event Action<string>? PreviewRequested;

    public JoinCard()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Rail;

        _field.BorderStyle = BorderStyle.None;
        _field.BackColor = Theme.InputBg;
        _field.ForeColor = Theme.Text;
        _field.Font = Theme.Body;
        _field.PlaceholderText = "discord.gg/example";
        _field.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            Join?.Invoke();
        };
        _field.TextChanged += (_, _) =>
        {
            _error = null;
            _previewError = null;
            var code = UserRestClient.InviteCode(_field.Text);
            if (code.Length >= 3 && code != _lastProbed)
            {
                _lastProbed = code;
                PreviewRequested?.Invoke(code);
            }
            Invalidate();
        };
        _button.Click += (_, _) => Join?.Invoke();

        Controls.Add(_field);
        Controls.Add(_button);
    }

    public void ShowError(string message) { _error = message; Invalidate(); }
    public void ShowPreview(string? name, int members, string? error)
    {
        _previewName = name;
        _previewMembers = members;
        _previewError = error;
        if (name == null && error == null) { _previewIcon = null; _previewError = null; }
        Invalidate();
    }

    public void SetBusy(bool busy)
    {
        _busy = busy;
        _button.Busy = busy;
        _button.Enabled = !busy;
        _field.Enabled = !busy;
        Invalidate();
    }

    int P => Ui.S(Pad);

    protected override void OnSizeChanged(EventArgs e)
    {
        if (_field is null) return;
        int fieldY = Ui.S(96);
        _field.SetBounds(P, fieldY, Width - P * 2, Ui.S(42));
        int btnY = fieldY + _field.Height + Ui.S(16) + PreviewH + Ui.S(12);
        _button.SetBounds(P, btnY, Width - P * 2, Ui.S(44));
        base.OnSizeChanged(e);
    }

    int PreviewH => (_previewName != null || _previewError != null) ? Ui.S(64) : 0;

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var card = new Rectangle(0, 0, Width - 1, Height - 1);
        using (var shadow = new SolidBrush(Color.FromArgb(60, 0, 0, 0)))
        using (var sp = Ui.RoundRect(new Rectangle(Ui.S(2), Ui.S(4), card.Width, card.Height), Ui.S(8)))
            g.FillPath(shadow, sp);
        Ui.FillRound(g, card, Ui.S(8), Theme.Rail);

        var content = new Rectangle(P, P, Width - P * 2, Height - P * 2);

        Ui.Text(g, "Join a Server", Theme.H1, new Rectangle(content.X, content.Y, content.Width, Ui.S(30)),
                Theme.Strong, TextFormatFlags.HorizontalCenter);
        Ui.Text(g, "Enter an invite below to join a server", Theme.Body,
                new Rectangle(content.X, content.Y + Ui.S(34), content.Width, Ui.S(22)),
                Theme.Muted, TextFormatFlags.HorizontalCenter);

        int labelY = Ui.S(74);
        Ui.Text(g, "INVITE LINK", Theme.SmallMedium, new Rectangle(content.X, labelY, content.Width, Ui.S(16)),
                Theme.Muted, TextFormatFlags.NoPadding);

        if (PreviewH > 0) PaintPreview(g);
        if (_error != null)
        {
            Ui.Text(g, _error, Theme.Small, new Rectangle(content.X, _button.Top - Ui.S(20), content.Width, Ui.S(18)),
                    Theme.Danger, TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
        }
        else if (_previewError != null)
        {
            Ui.Text(g, _previewError, Theme.Small, new Rectangle(content.X, _button.Top - Ui.S(20), content.Width, Ui.S(18)),
                    Theme.Warning, TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
        }
    }

    void PaintPreview(Graphics g)
    {
        var r = new Rectangle(P, _field.Bottom + Ui.S(12), Width - P * 2, PreviewH - Ui.S(4));
        Ui.FillRound(g, r, Ui.S(8), Theme.Field);

        int av = Ui.S(44);
        var ab = new Rectangle(r.X + Ui.S(12), r.Y + (r.Height - av) / 2, av, av);
        if (_previewName != null)
        {
            Ui.Avatar(g, Media.Get(_previewIcon, this), ab, Theme.Surface);
            Ui.Text(g, _previewName, Theme.BodyMedium,
                    new Rectangle(ab.Right + Ui.S(12), r.Y, r.Width - ab.Right - Ui.S(24), Ui.S(22)),
                    Theme.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            Ui.Text(g, _previewMembers + " members", Theme.Small,
                    new Rectangle(ab.Right + Ui.S(12), r.Y + Ui.S(24), r.Width - ab.Right - Ui.S(24), Ui.S(18)),
                    Theme.Faint, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
        else
        {
            Ui.Text(g, _previewError ?? "Checking invite…", Theme.Small,
                    new Rectangle(ab.Right + Ui.S(12), r.Y, r.Width - ab.Right - Ui.S(24), r.Height),
                    Theme.Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
    }
}
