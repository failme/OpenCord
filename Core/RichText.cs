using System.Drawing;

namespace OpenCord;

// Lays a message body out: markdown Runs in, positioned fragments out.
//
// Layout and painting are separate passes on purpose. A message row needs its height *before* it can
// be placed in the list, and re-running the whole parse on every paint is what makes a chat view
// stutter while scrolling. Layout once when the text or the width changes, paint from the result.
static class RichText
{
    public sealed class Piece
    {
        public required Run Run;
        public required string Text;
        public required Font Font;
        public required Color Color;
        public Rectangle Box;
        public bool Bg;      // the backing panel of a fenced code block, drawn before its text
    }

    static readonly Dictionary<(string, float, FontStyle), Font> _fonts = new();

    static Font Derive(Font b, FontStyle s)
    {
        if (s == FontStyle.Regular) return b;
        var key = (b.FontFamily.Name, b.Size, s);
        lock (_fonts)
        {
            if (!_fonts.TryGetValue(key, out var f)) _fonts[key] = f = new Font(b, s);
            return f;
        }
    }

    /// `body` is the base size for ordinary text. Chat messages are 16px, but a profile panel's bio
    /// and custom status are 14px in the live client — same markdown, smaller type — so the caller
    /// can substitute it. Headings, code and subtext keep their own sizes either way.
    static Font FontFor(Run r, Font? body = null)
    {
        if (r.Style.HasFlag(Style.Code)) return Theme.Mono;
        if (r.Style.HasFlag(Style.H1)) return Theme.H1;
        if (r.Style.HasFlag(Style.H2)) return Theme.H2;
        if (r.Style.HasFlag(Style.H3)) return Theme.H3;

        var fs = FontStyle.Regular;
        if (r.Style.HasFlag(Style.Bold)) fs |= FontStyle.Bold;
        if (r.Style.HasFlag(Style.Italic)) fs |= FontStyle.Italic;
        if (r.Style.HasFlag(Style.Underline)) fs |= FontStyle.Underline;
        if (r.Style.HasFlag(Style.Strike)) fs |= FontStyle.Strikeout;
        // A mention is 16px/500 in the live client — the pill's geometry and colours already
        // matched, but the text inside it was rendering a weight light. Only for the chat's own
        // size: a mention inside a 14px profile bio keeps that surface's font.
        if (r.Mention && body == null) return Derive(Theme.BodyMedium, fs);
        return Derive(r.Style.HasFlag(Style.Subtext) ? Theme.Small : body ?? Theme.Body, fs);
    }

    static Color ColorFor(Run r) =>
        r.Color ?? (r.Url != null ? Theme.Link
                  : r.Style.HasFlag(Style.Subtext) ? Theme.Muted
                  : r.Style.HasFlag(Style.Code) ? Theme.CodeText
                  : Theme.Text);

    // Words keep their trailing space so a wrap never loses or doubles one.
    static IEnumerable<string> Words(string s)
    {
        int i = 0;
        while (i < s.Length)
        {
            int j = s.IndexOf(' ', i);
            if (j < 0) { yield return s[i..]; break; }
            yield return s[i..(j + 1)];
            i = j + 1;
        }
    }

    /// `body` overrides the base text size — see FontFor. The line height follows it, so a 14px
    /// caller gets 14px lines rather than the chat's 22.
    public static List<Piece> Layout(IReadOnlyList<Run> runs, int maxWidth, out int height,
                                     Font? body = null)
    {
        var outp = new List<Piece>();
        int lh = body == null ? Ui.S(M.MessageLineHeight) : body.Height + Ui.S(2);
        int x = 0, y = 0, tallest = 0;

        void NewLine() { y += Math.Max(tallest, lh); x = 0; tallest = 0; }

        foreach (var r in runs)
        {
            if (r.Break) { NewLine(); continue; }

            if (r.Block) { LayoutBlock(r, maxWidth, outp, ref x, ref y, ref tallest); continue; }

            int indent = Ui.S(r.Indent * 16) + (r.Quote ? Ui.S(12) : 0);
            if (x == 0) x = indent;

            if (r.Emoji)
            {
                int sz = r.BigEmoji ? Ui.S(48) : Ui.S(22);
                if (x + sz > maxWidth && x > indent) { NewLine(); x = indent; }
                outp.Add(new Piece { Run = r, Text = "", Font = Theme.Body, Color = Theme.Text,
                                     Box = new Rectangle(x, y, sz, sz) });
                x += sz;
                tallest = Math.Max(tallest, sz);
                continue;
            }

            var f = FontFor(r, body);
            var c = ColorFor(r);
            foreach (var w in Words(r.Text))
            {
                if (w.Length == 0) continue;
                var word = w;
                var sz = Ui.Measure(word, f);
                if (x + sz.Width > maxWidth && x > indent)
                {
                    NewLine();
                    x = indent;
                    if (word.Trim().Length == 0) continue;   // never start a line with a stray space
                }

                // A "word" with no spaces in it — a long URL, a hash, a wall of one character —
                // can be wider than the whole message column. Word wrapping alone leaves it running
                // off the right edge and out from under the message; the browser breaks mid-word
                // here (overflow-wrap: break-word) and so must we. Fenced blocks already do this,
                // see LayoutBlock.
                while (sz.Width > maxWidth - indent && word.Length > 1)
                {
                    int cut = word.Length;
                    while (cut > 1 && Ui.Measure(word[..cut], f).Width > maxWidth - x) cut--;
                    var head = word[..cut];
                    var hs = Ui.Measure(head, f);
                    outp.Add(new Piece { Run = r, Text = head, Font = f, Color = c,
                                         Box = new Rectangle(x, y, hs.Width, hs.Height) });
                    tallest = Math.Max(tallest, hs.Height);
                    NewLine();
                    x = indent;
                    word = word[cut..];
                    sz = Ui.Measure(word, f);
                }

                outp.Add(new Piece { Run = r, Text = word, Font = f, Color = c,
                                     Box = new Rectangle(x, y, sz.Width, sz.Height) });
                x += sz.Width;
                tallest = Math.Max(tallest, sz.Height);
            }
        }

        height = y + Math.Max(tallest, lh);
        return outp;
    }

    // A fenced block owns its whole line box: full width, its own padded background, and hard
    // character wrapping rather than word wrapping — breaking code on spaces reflows indentation
    // and makes a pasted stack trace unreadable.
    static void LayoutBlock(Run r, int maxWidth, List<Piece> outp, ref int x, ref int y, ref int tallest)
    {
        if (x != 0) { y += Math.Max(tallest, Ui.S(M.MessageLineHeight)); x = 0; tallest = 0; }

        int pad = Ui.S(7), inner = Math.Max(Ui.S(40), maxWidth - pad * 2);   // measured: 7px, not 8
        var f = Theme.Mono;
        int lh = f.Height;
        var lines = new List<string>();
        foreach (var raw in r.Text.Replace("\t", "    ").Split('\n'))
        {
            var s = raw;
            while (true)
            {
                if (Ui.Measure(s, f).Width <= inner || s.Length <= 1) { lines.Add(s); break; }
                int cut = s.Length;
                while (cut > 1 && Ui.Measure(s[..cut], f).Width > inner) cut--;
                lines.Add(s[..cut]);
                s = s[cut..];
            }
        }

        int h = lines.Count * lh + pad * 2;
        outp.Add(new Piece { Run = r, Text = "", Font = f, Color = Theme.CodeText, Bg = true,
                             Box = new Rectangle(0, y, maxWidth, h) });
        for (int i = 0; i < lines.Count; i++)
        {
            // One piece per coloured span rather than one per line. The spans concatenate back to
            // the line, so selection and copy still see the original text.
            int cx = pad;
            foreach (var (text, kind) in Syntax.Line(lines[i], r.Lang))
            {
                if (text.Length == 0) continue;
                var w = Ui.Measure(text, f).Width;
                outp.Add(new Piece { Run = r, Text = text, Font = f, Color = Syntax.ColorOf(kind),
                                     Box = new Rectangle(cx, y + pad + i * lh, w, lh) });
                cx += w;
            }
        }
        y += h + Ui.S(4);
    }

    public static void Paint(Graphics g, IReadOnlyList<Piece> pieces, Point at, Control host,
                             HashSet<int>? revealedSpoilers = null)
    {
        foreach (var p in pieces)
        {
            var box = new Rectangle(at.X + p.Box.X, at.Y + p.Box.Y, p.Box.Width, p.Box.Height);
            var r = p.Run;

            if (p.Bg)
            {
                Ui.FillRound(g, box, Ui.S(4), Theme.CodeBg);
                using var pen = new Pen(Theme.CodeBorder);
                using var path = Ui.RoundRect(new Rectangle(box.X, box.Y, box.Width - 1, box.Height - 1), Ui.S(4));
                g.DrawPath(pen, path);
                continue;
            }

            if (r.Quote)
                Ui.Fill(g, new Rectangle(at.X, box.Y, Ui.S(4), box.Height), Theme.Border);

            if (r.Mention)
                Ui.FillRound(g, Rectangle.Inflate(box, Ui.S(2), 0), Ui.S(3), Theme.MentionBg);
            else if (r.Style.HasFlag(Style.Code))
                Ui.FillRound(g, box, Ui.S(3), Theme.CodeBg);

            // A spoiler stays a solid block until its group is revealed. Runs share a SpoilerId so
            // one click uncovers the whole span rather than a single word.
            if (r.SpoilerId != 0 && revealedSpoilers?.Contains(r.SpoilerId) != true)
            {
                Ui.FillRound(g, box, Ui.S(3), Theme.SpoilerBg);
                continue;
            }

            if (r.Emoji)
            {
                var img = Media.Get(r.Url, host);
                if (img != null)
                {
                    if (Media.IsAnimated(img)) Media.Animate(img, host);
                    g.DrawImage(img, box);
                }
                else Ui.FillRound(g, box, Ui.S(3), Theme.Surface);
                continue;
            }

            Ui.Text(g, p.Text, p.Font, box.Location, p.Color, TextFormatFlags.NoPadding);
        }
    }

    /// The spoiler group at a point, or 0. Lets a row turn a click into a reveal.
    public static int SpoilerAt(IReadOnlyList<Piece> pieces, Point at, Point p)
    {
        foreach (var pc in pieces)
        {
            if (pc.Run.SpoilerId == 0) continue;
            var box = new Rectangle(at.X + pc.Box.X, at.Y + pc.Box.Y, pc.Box.Width, pc.Box.Height);
            if (box.Contains(p)) return pc.Run.SpoilerId;
        }
        return 0;
    }

    /// The link at a point, or null.
    public static string? LinkAt(IReadOnlyList<Piece> pieces, Point at, Point p)
    {
        foreach (var pc in pieces)
        {
            if (pc.Run.Url == null || pc.Run.Emoji) continue;
            var box = new Rectangle(at.X + pc.Box.X, at.Y + pc.Box.Y, pc.Box.Width, pc.Box.Height);
            if (box.Contains(p)) return pc.Run.Url;
        }
        return null;
    }
}
