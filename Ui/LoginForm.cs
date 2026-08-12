using System.Drawing;
using System.Drawing.Drawing2D;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace ClaudeScord;

// Discord's login screen, rebuilt for a token instead of email + password.
//
// Shown modally before the main window when there is no saved token. On a valid token it closes with
// DialogResult.OK and exposes it via Token; Program persists it (DPAPI) and starts the session.
//
// It validates the way the real login does — a REST call to /users/@me — so a bad token is caught
// here with an inline error rather than dumping you into an empty client that silently never
// connects.
sealed class LoginForm : Form
{
    readonly LoginCard _card = new();
    TitleBar _title = null!;

    public string? Token { get; private set; }

    public LoginForm()
    {
        Text = "ClaudeScord";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        // Same footprint as the Shell so the handoff to the main window is seamless, not a jump.
        ClientSize = new Size(Ui.S(1000), Ui.S(680));
        DoubleBuffered = true;
        KeyPreview = true;

        _title = new TitleBar(this) { Dock = DockStyle.Top, Height = Ui.S(M.TitleBar) };
        Controls.Add(_card);
        Controls.Add(_title);

        _card.Submit += TryLogin;
        CenterCard();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _card.FocusInput();     // Discord lands the caret in the first field; a token login has one
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.FrameColor(Handle, Theme.Blurple);
    }

    protected override CreateParams CreateParams
    {
        get { var cp = base.CreateParams; cp.Style |= 0x00020000 /*WS_MINIMIZEBOX*/; return cp; }
    }

    protected override void OnSizeChanged(EventArgs e) { CenterCard(); base.OnSizeChanged(e); }

    void CenterCard()
    {
        _card.Size = new Size(Ui.S(LoginCard.CardW), Ui.S(LoginCard.CardH));
        _card.Location = new Point((ClientSize.Width - _card.Width) / 2,
                                   Ui.S(M.TitleBar) + (ClientSize.Height - Ui.S(M.TitleBar) - _card.Height) / 2);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); }
        base.OnKeyDown(e);
    }

    // Discord's login art is a blurple gradient with soft blobs. A diagonal two-stop blurple → indigo
    // gradient is a clean approximation without shipping an image.
    protected override void OnPaintBackground(PaintEventArgs e)
    {
        using var brush = new LinearGradientBrush(ClientRectangle,
            Color.FromArgb(88, 101, 242), Color.FromArgb(58, 48, 140), 55f);
        e.Graphics.FillRectangle(brush, ClientRectangle);
    }

    async void TryLogin()
    {
        var token = _card.TokenValue;
        if (token.Length == 0) { _card.ShowError("Enter your account token."); return; }

        _card.SetBusy(true);
        var (ok, error) = await Validate(token);
        if (ok) { Token = token; DialogResult = DialogResult.OK; Close(); return; }

        _card.SetBusy(false);
        _card.ShowError(error!);
    }

    // GET /users/@me: 200 means the token authenticates, 401 means it does not. Anything else is
    // treated as a reachability problem so the message does not wrongly accuse the token.
    static async Task<(bool, string?)> Validate(string token)
    {
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri("https://discord.com/api/v9/") };
            http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", token);
            http.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");

            var resp = await http.GetAsync("users/@me");
            if (resp.IsSuccessStatusCode) return (true, null);
            if ((int)resp.StatusCode == 401) return (false, "Invalid token. Double-check it and try again.");
            return (false, $"Discord returned {(int)resp.StatusCode}. Try again in a moment.");
        }
        catch (Exception ex) { return (false, "Couldn't reach Discord. Check your connection.\n" + ex.Message); }
    }
}

// The centred login card: title, subtitle, the token well, the button, an error line, and the
// where-do-I-get-a-token footer. Static text is painted; the editable field is a real TextBox so
// paste, selection, caret and IME all behave.
sealed class LoginCard : Panel
{
    public const int CardW = 480, CardH = 300;
    const int Pad = 32;

    readonly TokenField _field = new();
    readonly LoginButton _button = new();
    string? _error;

    public event Action? Submit;

    public LoginCard()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Rail;

        _field.Submit += () => Submit?.Invoke();
        _field.Changed += () => { if (_error != null) { _error = null; Invalidate(); } };
        _button.Text = "Log In";
        _button.Click += (_, _) => Submit?.Invoke();

        Controls.Add(_field);
        Controls.Add(_button);
    }

    public string TokenValue => _field.Value;
    public void FocusInput() => _field.FocusInput();

    public void ShowError(string message) { _error = message; Invalidate(); }

    public void SetBusy(bool busy)
    {
        _button.Busy = busy;
        _button.Enabled = !busy;
        _field.Enabled = !busy;
        Invalidate();
    }

    int P => Ui.S(Pad);

    protected override void OnSizeChanged(EventArgs e)
    {
        int fieldY = Ui.S(112);
        _field.SetBounds(P, fieldY, Width - P * 2, Ui.S(42));
        int btnY = fieldY + _field.Height + Ui.S(24);
        _button.SetBounds(P, btnY, Width - P * 2, Ui.S(44));
        base.OnSizeChanged(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        // The card: rounded, with a soft drop shadow so it lifts off the gradient.
        var card = new Rectangle(0, 0, Width - 1, Height - 1);
        using (var shadow = new SolidBrush(Color.FromArgb(60, 0, 0, 0)))
        using (var sp = Ui.RoundRect(new Rectangle(Ui.S(2), Ui.S(4), card.Width, card.Height), Ui.S(8)))
            g.FillPath(shadow, sp);
        Ui.FillRound(g, card, Ui.S(8), Theme.Rail);

        var content = new Rectangle(P, P, Width - P * 2, Height - P * 2);

        Ui.Text(g, "Welcome back!", Theme.H1, new Rectangle(content.X, content.Y, content.Width, Ui.S(30)),
                Theme.Strong, TextFormatFlags.HorizontalCenter);
        Ui.Text(g, "Log in with your account token", Theme.Body,
                new Rectangle(content.X, content.Y + Ui.S(34), content.Width, Ui.S(22)),
                Theme.Muted, TextFormatFlags.HorizontalCenter);

        // Field label. The refresh no longer upper-cases these in CSS, but the login label still is,
        // and the required asterisk is red.
        int labelY = Ui.S(90);
        Ui.Text(g, "TOKEN", Theme.SmallMedium, new Rectangle(content.X, labelY, content.Width, Ui.S(16)),
                Theme.Muted, TextFormatFlags.NoPadding);
        var lw = Ui.Measure("TOKEN", Theme.SmallMedium).Width;
        Ui.Text(g, " *", Theme.SmallMedium, new Point(content.X + lw, labelY), Theme.Danger);

        if (_error != null)
        {
            int ey = _button.Top - Ui.S(20);
            Ui.Text(g, _error, Theme.Small, new Rectangle(content.X, ey, content.Width, Ui.S(18)),
                    Theme.Danger, TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
        }

        // Footer: where a token comes from, and that it stays on this machine.
        int fy = _button.Bottom + Ui.S(14);
        Ui.Text(g, "Stored encrypted on this PC only. Never entered anywhere but here.", Theme.Small,
                new Rectangle(content.X, fy, content.Width, Ui.S(16)), Theme.Faint,
                TextFormatFlags.HorizontalCenter);
    }
}

// The token well: a rounded dark field with a real TextBox and a click-to-reveal eye. Masked by
// default because a token is credential-equivalent; revealable because you cannot proofread a pasted
// 70-character string through dots.
sealed class TokenField : Panel
{
    readonly TextBox _box;
    bool _revealed;

    public event Action? Submit;
    public event Action? Changed;

    public TokenField()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.InputBg;

        _box = new TextBox
        {
            BorderStyle = BorderStyle.None,
            BackColor = Theme.InputBg,
            ForeColor = Theme.Text,
            Font = Theme.Body,
            UseSystemPasswordChar = true,
            PlaceholderText = "Paste your token",
        };
        _box.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            Submit?.Invoke();
        };
        _box.TextChanged += (_, _) => Changed?.Invoke();
        Controls.Add(_box);
    }

    public string Value => _box.Text.Trim();
    public void FocusInput() => _box.Focus();

    public new bool Enabled
    {
        get => _box.Enabled;
        set { _box.Enabled = value; base.Enabled = value; }
    }

    int EyeW => Ui.S(40);
    Rectangle EyeRect => new(Width - EyeW, 0, EyeW, Height);

    protected override void OnSizeChanged(EventArgs e)
    {
        if (_box is not null)
            _box.SetBounds(Ui.S(12), (Height - _box.PreferredHeight) / 2,
                           Math.Max(1, Width - EyeW - Ui.S(12)), _box.PreferredHeight);
        base.OnSizeChanged(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (EyeRect.Contains(e.Location))
        {
            _revealed = !_revealed;
            _box.UseSystemPasswordChar = !_revealed;
            Invalidate();
        }
        else _box.Focus();
        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        Cursor = EyeRect.Contains(e.Location) ? Cursors.Hand : Cursors.IBeam;
        base.OnMouseMove(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Ui.FillRound(g, new Rectangle(0, 0, Width - 1, Height - 1), Ui.S(4), Theme.InputBg);

        // Segoe Fluent: RedEye (E7B3) to reveal, Hide (ED1A) once shown.
        Ui.Text(g, _revealed ? "" : "", Theme.Icon, EyeRect, Theme.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }
}

// Full-width blurple button, painted so hover/pressed/disabled match the rest of the client rather
// than the default WinForms button chrome.
sealed class LoginButton : Control
{
    bool _hover, _down;
    public bool Busy { get; set; }

    public LoginButton()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = _down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var c = !Enabled || Busy ? Theme.BlurpleHover
              : _down ? Color.FromArgb(62, 71, 176)
              : _hover ? Theme.BlurpleHover
              : Theme.Blurple;
        Ui.FillRound(g, new Rectangle(0, 0, Width - 1, Height - 1), Ui.S(4), c);

        Ui.Text(g, Busy ? "Logging in…" : Text, Theme.BodyMedium, ClientRectangle,
                Enabled || Busy ? Color.White : Color.FromArgb(200, 255, 255, 255),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }
}
