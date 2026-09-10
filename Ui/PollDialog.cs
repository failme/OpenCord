using System.Drawing;

namespace OpenCord;

// Discord's "Create Poll" modal: a question, 2–10 answers you can add and remove, a multiple-choice
// toggle, and an expiry. Owner-drawn chrome like Prompt; real TextBoxes for the editable rows.
sealed class PollDialog : Form
{
    public sealed record Spec(string Question, IReadOnlyList<string> Answers, bool MultiSelect, int Hours);

    readonly TextBox _question = new();
    readonly List<TextBox> _answers = new();
    bool _multi;
    int _hours = 24;
    int _hot = -1;          // a Hit constant, or 100+i for answer i's remove button
    int _scrollY;

    static readonly (int Hours, string Label)[] Durations =
    { (1, "1h"), (4, "4h"), (8, "8h"), (24, "24h"), (72, "3d"), (168, "7d") };

    const int MaxAnswers = 10;

    PollDialog()
    {
        Text = "Create Poll";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(Ui.S(460), Ui.S(520));
        DoubleBuffered = true;
        KeyPreview = true;
        BackColor = Theme.Floating;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

        AddAnswer("Choice 1");
        AddAnswer("Choice 2");

        _question.BorderStyle = BorderStyle.None;
        _question.BackColor = Theme.InputBg;
        _question.ForeColor = Theme.Text;
        _question.Font = Theme.Body;
        Controls.Add(_question);
        LayoutRows();
        Shown += (_, _) => _question.Focus();
    }

    void AddAnswer(string text)
    {
        if (_answers.Count >= MaxAnswers) return;
        var b = new TextBox
        {
            BorderStyle = BorderStyle.None,
            BackColor = Theme.InputBg,
            ForeColor = Theme.Text,
            Font = Theme.Body,
            Text = text,
        };
        _answers.Add(b);
        Controls.Add(b);
        LayoutRows();
    }

    void RemoveAnswer(int i)
    {
        if (_answers.Count <= 2 || i < 0 || i >= _answers.Count) return;
        var b = _answers[i];
        if (ActiveControl == b) _question.Focus();
        Controls.Remove(b);
        b.Dispose();
        _answers.RemoveAt(i);
        LayoutRows();
    }

    // ── layout ──

    int AnswerTop => Ui.S(150);

    Rectangle RowBox(int i) => new(Ui.S(24), AnswerTop + i * Ui.S(40) - _scrollY,
                                   ClientSize.Width - Ui.S(96), Ui.S(32));

    Rectangle RemoveBox(int i) => new(RowBox(i).Right + Ui.S(6), RowBox(i).Y + Ui.S(8), Ui.S(18), Ui.S(18));

    Rectangle AddBox => new(Ui.S(24), AnswerTop + _answers.Count * Ui.S(40) + Ui.S(2) - _scrollY,
                            Ui.S(120), Ui.S(24));

    Rectangle MultiBox => new(Ui.S(24), ClientSize.Height - Ui.S(160), Ui.S(22), Ui.S(22));

    Rectangle DurationRow => new(Ui.S(24), ClientSize.Height - Ui.S(122), ClientSize.Width - Ui.S(48), Ui.S(26));

    Rectangle DurationBox(int i) =>
        new(DurationRow.X + i * Ui.S(52), DurationRow.Y, Ui.S(46), Ui.S(26));

    Rectangle FooterRect => new(0, ClientSize.Height - Ui.S(64), ClientSize.Width, Ui.S(64));

    Rectangle BtnRect(int i)
    {
        int w = Ui.S(i == 1 ? 96 : 72), h = Ui.S(34);
        int right = ClientSize.Width - Ui.S(16);
        int x = i == 1 ? right - w : right - Ui.S(96) - Ui.S(8) - w;
        return new Rectangle(x, FooterRect.Y + (FooterRect.Height - h) / 2, w, h);
    }

    void LayoutRows()
    {
        for (int i = 0; i < _answers.Count; i++)
        {
            var r = RowBox(i);
            if (_answers[i].IsDisposed) continue;
            _answers[i].SetBounds(r.X + Ui.S(10), r.Y + (r.Height - Theme.Body.Height) / 2,
                                  r.Width - Ui.S(20), Theme.Body.Height);
        }
        Invalidate();
    }

    // ── hit testing ──

    const int HitCancel = 0, HitCreate = 1, HitAdd = 2, HitMulti = 3, HitRemove = 100;

    int HitTest(Point p)
    {
        if (BtnRect(1).Contains(p)) return HitCreate;
        if (BtnRect(0).Contains(p)) return HitCancel;
        for (int i = 0; i < _answers.Count; i++)
            if (_answers.Count > 2 && RemoveBox(i).Contains(p)) return HitRemove + i;
        if (!_multi && AddBox.Contains(p)) return HitAdd;
        if (MultiBox.Contains(p)) return HitMulti;
        return -1;
    }

    int DurationAt(Point p)
    {
        for (int i = 0; i < Durations.Length; i++)
            if (DurationBox(i).Contains(p)) return i;
        return -1;
    }

    // ── input ──

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int was = _hot;
        _hot = HitTest(e.Location);
        if (_hot != was) Invalidate();
        Cursor = _hot >= 0 || DurationAt(e.Location) >= 0 ? Cursors.Hand : Cursors.Default;
        base.OnMouseMove(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) { base.OnMouseDown(e); return; }
        switch (HitTest(e.Location))
        {
            case HitCancel: DialogResult = DialogResult.Cancel; Close(); break;
            case HitCreate: Submit(); break;
            case HitAdd: AddAnswer(""); break;
            case HitMulti:
                _multi = !_multi;
                Invalidate();
                break;
            default:
                if (_hot >= HitRemove) RemoveAnswer(_hot - HitRemove);
                break;
        }
        int d = DurationAt(e.Location);
        if (d >= 0) { _hours = Durations[d].Hours; Invalidate(); }
        base.OnMouseDown(e);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        int max = Math.Max(0, _answers.Count * Ui.S(40) + Ui.S(80) - (ClientSize.Height - AnswerTop - Ui.S(180)));
        _scrollY = Math.Clamp(_scrollY - e.Delta / 2, 0, max);
        LayoutRows();
        base.OnMouseWheel(e);
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
        var q = _question.Text.Trim();
        var answers = _answers.Select(a => a.Text.Trim()).Where(s => s.Length > 0).ToList();
        if (q.Length == 0 || answers.Count < 2) return;
        _spec = new Spec(q, answers, _multi, _hours);
        DialogResult = DialogResult.OK;
        Close();
    }

    public static Spec? Ask(IWin32Window owner)
    {
        using var d = new PollDialog();
        return d.ShowDialog(owner) == DialogResult.OK ? d._spec : null;
    }

    // ── paint ──

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        Ui.Fill(g, ClientRectangle, Theme.Floating);

        Ui.Text(g, "Create Poll", Theme.H2, new Rectangle(Ui.S(24), Ui.S(20), ClientSize.Width - Ui.S(48), Ui.S(28)),
                Theme.Strong, TextFormatFlags.NoPadding);

        Ui.Text(g, "Question", Theme.SmallMedium, new Rectangle(Ui.S(24), Ui.S(62), Ui.S(200), Ui.S(18)),
                Theme.Muted, TextFormatFlags.NoPadding);
        Ui.FillRound(g, new Rectangle(Ui.S(24), Ui.S(84), ClientSize.Width - Ui.S(48), Ui.S(38)), Ui.S(4), Theme.InputBg);

        Ui.Text(g, $"Answers ({_answers.Count}/{MaxAnswers})", Theme.SmallMedium,
                new Rectangle(Ui.S(24), Ui.S(128), Ui.S(240), Ui.S(18)),
                Theme.Muted, TextFormatFlags.NoPadding);
        for (int i = 0; i < _answers.Count; i++)
        {
            var row = RowBox(i);
            Ui.FillRound(g, row, Ui.S(4), Theme.InputBg);
            var x = RemoveBox(i);
            bool hotX = _answers.Count > 2 && _hot == HitRemove + i;
            Ui.Text(g, "×", Theme.SmallMedium, x, hotX ? Theme.Strong : Theme.Muted,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
        if (!_multi)
        {
            var add = AddBox;
            bool hot = _hot == HitAdd;
            Ui.Text(g, "+ Add answer", Theme.SmallMedium, add,
                    hot ? Theme.Strong : Theme.Link,
                    TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter);
        }

        var multi = MultiBox;
        Ui.FillRound(g, multi, Ui.S(3), _multi ? Theme.Blurple : Theme.Surface);
        if (_multi) Ui.Text(g, "✓", Theme.SmallMedium, multi, Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        Ui.Text(g, "Multiple choices", Theme.Body, new Rectangle(multi.Right + Ui.S(8), multi.Y - Ui.S(2), Ui.S(200), Ui.S(26)),
                Theme.Muted, TextFormatFlags.VerticalCenter);

        Ui.Text(g, "Ends after", Theme.SmallMedium, new Rectangle(Ui.S(24), DurationRow.Y - Ui.S(22), Ui.S(200), Ui.S(18)),
                Theme.Muted, TextFormatFlags.NoPadding);
        for (int i = 0; i < Durations.Length; i++)
        {
            var b = DurationBox(i);
            bool on = _hours == Durations[i].Hours;
            Ui.FillRound(g, b, Ui.S(13), on ? Theme.Blurple : Theme.Surface);
            Ui.Text(g, Durations[i].Label, Theme.Small, b, on ? Color.White : Theme.Muted,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        Ui.Fill(g, FooterRect, Theme.Rail);
        var cancel = BtnRect(0);
        Ui.Text(g, "Cancel", Theme.SmallMedium, cancel, _hot == 0 ? Theme.Strong : Theme.Text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        var ok = BtnRect(1);
        Ui.FillRound(g, ok, Ui.S(3), _hot == 1 ? Theme.BlurpleHover : Theme.Blurple);
        Ui.Text(g, "Create", Theme.SmallMedium, ok, Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }
}
