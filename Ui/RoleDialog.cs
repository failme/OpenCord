using System.Drawing;
using System.Drawing.Drawing2D;

namespace OpenCord;

// Create / edit a role: name, a colour from Discord's own role palette, display-separately (hoist)
// and allow-anyone-to-mention. The full permissions matrix stays in Server Settings territory;
// this covers what members actually reach for day to day.
sealed class RoleDialog : Form
{
    public sealed record Spec(string Name, int Color, bool Hoist, bool Mentionable);

    readonly TextBox _name = new();
    int _color;
    bool _hoist, _mentionable;
    int _hot = -1;          // 0 cancel, 1 save, 2 hoist toggle, 3 mentionable toggle, 100+i colour swatch
    readonly bool _isNew;
    readonly string _roleName;

    // Discord's default role palette, in picker order.
    static readonly int[] Palette =
    {
        0x1ABC9C, 0x11806A, 0x2ECC71, 0x1F8B4C, 0x3498DB, 0x206694, 0x9B59B6, 0x71368A,
        0xE91E63, 0xAD1457, 0xF1C40F, 0xC27C0E, 0xE67E22, 0xA84300, 0xE74C3C, 0x992D22,
        0x95A5A6, 0x607D8B, 0x979C9F, 0x546E7A,
    };

    RoleDialog(string name, int color, bool hoist, bool mentionable, bool isNew)
    {
        _isNew = isNew;
        _roleName = name;
        _color = color;
        _hoist = hoist;
        _mentionable = mentionable;

        Text = isNew ? "Create Role" : "Edit Role";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(Ui.S(420), Ui.S(430));
        DoubleBuffered = true;
        KeyPreview = true;
        BackColor = Theme.Floating;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

        _name.BorderStyle = BorderStyle.None;
        _name.BackColor = Theme.InputBg;
        _name.ForeColor = Theme.Text;
        _name.Font = Theme.Body;
        _name.Text = name;
        Controls.Add(_name);
        Shown += (_, _) => { _name.Focus(); _name.SelectAll(); };
    }

    Rectangle NameBox => new(Ui.S(24), Ui.S(84), ClientSize.Width - Ui.S(48), Ui.S(38));

    Rectangle ColorGrid => new(Ui.S(24), Ui.S(168), ClientSize.Width - Ui.S(48), Ui.S(96));
    Rectangle Swatch(int i)
    {
        const int PerRow = 10;
        int s = Ui.S(26);
        int gap = Ui.S(10);
        return new Rectangle(ColorGrid.X + i % PerRow * (s + gap), ColorGrid.Y + i / PerRow * (s + gap), s, s);
    }

    Rectangle HoistBox => new(Ui.S(24), ClientSize.Height - Ui.S(150), Ui.S(22), Ui.S(22));
    Rectangle MentionBox => new(Ui.S(24), ClientSize.Height - Ui.S(110), Ui.S(22), Ui.S(22));

    Rectangle FooterRect => new(0, ClientSize.Height - Ui.S(64), ClientSize.Width, Ui.S(64));

    Rectangle BtnRect(int i)
    {
        int w = Ui.S(i == 1 ? 96 : 72), h = Ui.S(34);
        int right = ClientSize.Width - Ui.S(16);
        int x = i == 1 ? right - w : right - Ui.S(96) - Ui.S(8) - w;
        return new Rectangle(x, FooterRect.Y + (FooterRect.Height - h) / 2, w, h);
    }

    const int HitCancel = 0, HitSave = 1, HitHoist = 2, HitMention = 3, HitColor = 100;

    int HitTest(Point p)
    {
        if (BtnRect(1).Contains(p)) return HitSave;
        if (BtnRect(0).Contains(p)) return HitCancel;
        for (int i = 0; i < Palette.Length; i++)
            if (Swatch(i).Contains(p)) return HitColor + i;
        if (HoistBox.Contains(p)) return HitHoist;
        if (MentionBox.Contains(p)) return HitMention;
        return -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int was = _hot;
        _hot = HitTest(e.Location);
        if (_hot != was) Invalidate();
        Cursor = _hot >= 0 ? Cursors.Hand : Cursors.Default;
        base.OnMouseMove(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) { base.OnMouseDown(e); return; }
        switch (HitTest(e.Location))
        {
            case HitCancel: DialogResult = DialogResult.Cancel; Close(); break;
            case HitSave: Submit(); break;
            case HitHoist: _hoist = !_hoist; Invalidate(); break;
            case HitMention: _mentionable = !_mentionable; Invalidate(); break;
            default:
                if (_hot >= HitColor)
                {
                    _color = Palette[_hot - HitColor];
                    Invalidate();
                }
                break;
        }
        base.OnMouseDown(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); }
        else if (e.KeyCode == Keys.Enter && !(ActiveControl is TextBox)) Submit();
        base.OnKeyDown(e);
    }

    Spec? _spec;

    void Submit()
    {
        var n = _name.Text.Trim();
        if (n.Length == 0) return;
        _spec = new Spec(n, _color, _hoist, _mentionable);
        DialogResult = DialogResult.OK;
        Close();
    }

    /// Returns the chosen spec, or null on cancel.
    public static Spec? Ask(IWin32Window owner, UserRole? role)
    {
        using var d = new RoleDialog(role?.Name ?? "new role", role?.Color ?? 0,
                                     role?.Hoist ?? false, role?.Mentionable ?? false, role == null);
        return d.ShowDialog(owner) == DialogResult.OK ? d._spec : null;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Ui.Fill(g, ClientRectangle, Theme.Floating);

        Ui.Text(g, _isNew ? "Create Role" : $"Edit @{_roleName}", Theme.H2,
                new Rectangle(Ui.S(24), Ui.S(20), ClientSize.Width - Ui.S(48), Ui.S(28)),
                Theme.Strong, TextFormatFlags.NoPadding);

        Ui.Text(g, "NAME", Theme.SmallMedium, new Rectangle(Ui.S(24), Ui.S(62), Ui.S(200), Ui.S(18)),
                Theme.Muted, TextFormatFlags.NoPadding);
        Ui.FillRound(g, NameBox, Ui.S(4), Theme.InputBg);

        Ui.Text(g, "COLOUR", Theme.SmallMedium, new Rectangle(Ui.S(24), Ui.S(146), Ui.S(200), Ui.S(18)),
                Theme.Muted, TextFormatFlags.NoPadding);
        for (int i = 0; i < Palette.Length; i++)
        {
            var b = Swatch(i);
            bool sel = _color == Palette[i];
            using var brush = new SolidBrush(System.Drawing.Color.FromArgb((int)(0xFF000000u | (uint)Palette[i])));
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.FillEllipse(brush, b);
            if (sel)
            {
                using var pen = new Pen(Theme.Text, Ui.S(2));
                g.DrawEllipse(pen, new Rectangle(b.X - Ui.S(3), b.Y - Ui.S(3), b.Width + Ui.S(6), b.Height + Ui.S(6)));
            }
        }
        if (_color == 0)
            Ui.Text(g, "No colour", Theme.Small,
                    new Rectangle(ColorGrid.X, ColorGrid.Bottom + Ui.S(4), Ui.S(200), Ui.S(18)),
                    Theme.Faint, TextFormatFlags.NoPadding);

        ToggleRow(g, HoistBox, "Display separately",
                  "Show members with this role grouped above the online list.", _hoist);
        ToggleRow(g, MentionBox, "Allow anyone to @mention",
                  "Members without permission can ping this role.", _mentionable);

        Ui.Fill(g, FooterRect, Theme.Rail);
        var cancel = BtnRect(0);
        Ui.Text(g, "Cancel", Theme.SmallMedium, cancel, _hot == 0 ? Theme.Strong : Theme.Text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        var ok = BtnRect(1);
        Ui.FillRound(g, ok, Ui.S(3), _hot == 1 ? Theme.BlurpleHover : Theme.Blurple);
        Ui.Text(g, _isNew ? "Create" : "Save", Theme.SmallMedium, ok, Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    void ToggleRow(Graphics g, Rectangle box, string title, string note, bool on)
    {
        bool hot = _hot == HitHoist && box == HoistBox || _hot == HitMention && box == MentionBox;
        Ui.FillRound(g, box, Ui.S(3), on ? Theme.Blurple : hot ? Theme.SurfaceHigh : Theme.Surface);
        if (on) Ui.Text(g, "✓", Theme.SmallMedium, box, Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        Ui.Text(g, title, Theme.BodyMedium,
                new Rectangle(box.Right + Ui.S(10), box.Y - Ui.S(4), ClientSize.Width - box.Right - Ui.S(30), Ui.S(20)),
                Theme.Text, TextFormatFlags.VerticalCenter);
        Ui.Text(g, note, Theme.Small,
                new Rectangle(box.Right + Ui.S(10), box.Y + Ui.S(16), ClientSize.Width - box.Right - Ui.S(30), Ui.S(18)),
                Theme.Faint);
    }
}
