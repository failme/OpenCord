using System.Drawing;

namespace ClaudeScord;

// Discord's small confirm modal: a title, one optional line of body text, an optional text field,
// then a footer with Cancel and a coloured confirm button.
//
// One dialog rather than one per action — "Change Nickname", "Kick", "Ban" and "Timeout" differ only
// in their strings, whether there is a field, and whether the confirm button is red.
sealed class Prompt : Form
{
    readonly TextBox? _input;
    readonly string _title, _body, _confirm;
    readonly bool _danger;
    int _hot = -1;   // 0 = cancel, 1 = confirm

    Prompt(string title, string body, string? initial, string confirm, bool danger)
    {
        _title = title;
        _body = body;
        _confirm = confirm;
        _danger = danger;

        Text = title;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(Ui.S(440), Ui.S(initial == null ? 176 : 216));
        DoubleBuffered = true;
        KeyPreview = true;
        BackColor = Theme.Floating;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

        if (initial != null)
        {
            _input = new TextBox
            {
                BorderStyle = BorderStyle.None,
                BackColor = Theme.InputBg,
                ForeColor = Theme.Text,
                Font = Theme.Body,
                Text = initial,
            };
            _input.SetBounds(Ui.S(24), Ui.S(112), ClientSize.Width - Ui.S(48), Ui.S(22));
            Controls.Add(_input);
            Shown += (_, _) => { _input.Focus(); _input.SelectAll(); };
        }
    }

    /// Returns the typed value, or null if cancelled. With `initial` null there is no field and the
    /// result is "" for confirm, null for cancel — which is what a plain yes/no needs.
    public static string? Ask(IWin32Window owner, string title, string body, string? initial,
                              string confirm = "Done", bool danger = false)
    {
        using var d = new Prompt(title, body, initial, confirm, danger);
        return d.ShowDialog(owner) == DialogResult.OK ? d._input?.Text ?? "" : null;
    }

    Rectangle FooterRect => new(0, ClientSize.Height - Ui.S(64), ClientSize.Width, Ui.S(64));

    Rectangle BtnRect(int i)
    {
        int w = Ui.S(i == 1 ? 96 : 72), h = Ui.S(34);
        int right = ClientSize.Width - Ui.S(16);
        int x = i == 1 ? right - w : right - Ui.S(96) - Ui.S(8) - w;
        return new Rectangle(x, FooterRect.Y + (FooterRect.Height - h) / 2, w, h);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int was = _hot;
        _hot = BtnRect(0).Contains(e.Location) ? 0 : BtnRect(1).Contains(e.Location) ? 1 : -1;
        if (_hot != was) Invalidate(FooterRect);
        Cursor = _hot >= 0 ? Cursors.Hand : Cursors.Default;
        base.OnMouseMove(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        if (_hot == 0) { DialogResult = DialogResult.Cancel; Close(); }
        else if (_hot == 1) { DialogResult = DialogResult.OK; Close(); }
        base.OnMouseDown(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); }
        else if (e.KeyCode == Keys.Enter) { DialogResult = DialogResult.OK; Close(); }
        base.OnKeyDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        Ui.Fill(g, ClientRectangle, Theme.Floating);

        Ui.Text(g, _title, Theme.H2, new Rectangle(Ui.S(24), Ui.S(24), ClientSize.Width - Ui.S(48), Ui.S(28)),
                Theme.Strong, TextFormatFlags.NoPadding);
        if (_body.Length > 0)
            Ui.Text(g, _body, Theme.Body,
                    new Rectangle(Ui.S(24), Ui.S(58), ClientSize.Width - Ui.S(48), Ui.S(44)),
                    Theme.Muted, TextFormatFlags.WordBreak);

        if (_input != null)
            Ui.FillRound(g, new Rectangle(Ui.S(20), Ui.S(104), ClientSize.Width - Ui.S(40), Ui.S(38)),
                         Ui.S(4), Theme.InputBg);

        // The footer sits on its own darker strip, like every Discord modal.
        Ui.Fill(g, FooterRect, Theme.Rail);
        var cancel = BtnRect(0);
        Ui.Text(g, "Cancel", Theme.SmallMedium, cancel, _hot == 0 ? Theme.Strong : Theme.Text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

        var ok = BtnRect(1);
        var fill = _danger ? (_hot == 1 ? Color.FromArgb(166, 47, 52) : Theme.Danger)
                           : (_hot == 1 ? Theme.BlurpleHover : Theme.Blurple);
        Ui.FillRound(g, ok, Ui.S(3), fill);
        Ui.Text(g, _confirm, Theme.SmallMedium, ok, Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }
}
