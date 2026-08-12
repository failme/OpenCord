using System.Drawing;
using System.Drawing.Drawing2D;

namespace ClaudeScord;

// A minimal SVG path renderer, so Discord's own icon geometry can be drawn directly instead of
// approximated with rounded rectangles or substituted with a lookalike from an icon font.
//
// Carried over from the predecessor unchanged: it handles M/L/H/V/C/S/Q/T/A/Z including the arc
// parameterisation, and correctly copes with arc flags packed without separators ("0 0 1-.0076"),
// which is how Discord's minified paths actually arrive.
static class Svg
{
    static readonly Dictionary<string, GraphicsPath> _svgCache = new();

    /// Parsed once per path string — the result is immutable and shared, so callers must not dispose it.
    public static GraphicsPath SvgGeometry(string d)
    {
        lock (_svgCache)
        {
            if (!_svgCache.TryGetValue(d, out var p)) _svgCache[d] = p = ParseSvg(d);
            return p;
        }
    }

    /// Fill an SVG path scaled to fit `box`, preserving aspect. `viewBox` is the source square size.
    public static void SvgFill(Graphics g, string d, RectangleF box, Color c, float viewBox = 24f)
    {
        float s = Math.Min(box.Width, box.Height) / viewBox;
        var st = g.Save();
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TranslateTransform(box.X + (box.Width - viewBox * s) / 2f, box.Y + (box.Height - viewBox * s) / 2f);
        g.ScaleTransform(s, s);
        using var br = new SolidBrush(c);
        g.FillPath(br, SvgGeometry(d));
        g.Restore(st);
    }

    /// Stroke an SVG path scaled to fit `box`. Most of Discord's chrome icons are line art; authoring
    /// them as a stroked centre-line is a fraction of the path data of the equivalent filled outline,
    /// and round caps/joins are what make them read as Discord's rather than as generic clip art.
    public static void SvgStroke(Graphics g, string d, RectangleF box, Color c, float width = 2f,
                                 float viewBox = 24f)
    {
        float s = Math.Min(box.Width, box.Height) / viewBox;
        var st = g.Save();
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TranslateTransform(box.X + (box.Width - viewBox * s) / 2f, box.Y + (box.Height - viewBox * s) / 2f);
        g.ScaleTransform(s, s);
        using var pen = new Pen(c, width) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        g.DrawPath(pen, SvgGeometry(d));
        g.Restore(st);
    }

    /// The path scaled to fit `box`, as a fresh path the caller owns — for clipping, or for filling
    /// with something other than a solid brush. Clones because the cached geometry is shared: any
    /// caller that transformed it in place would corrupt every later use of the same path string.
    public static GraphicsPath Fit(string d, RectangleF box, float viewBox = 24f)
    {
        float s = Math.Min(box.Width, box.Height) / viewBox;
        var p = (GraphicsPath)SvgGeometry(d).Clone();
        using var m = new Matrix();
        m.Translate(box.X + (box.Width - viewBox * s) / 2f, box.Y + (box.Height - viewBox * s) / 2f);
        m.Scale(s, s);
        p.Transform(m);
        return p;
    }

    static GraphicsPath ParseSvg(string d)
    {
        // Even-odd, not nonzero.
        //
        // SVG's default is nonzero, and Discord's own exported icons rely on it: their cutouts are
        // counter-wound subpaths, which nonzero and even-odd both turn into holes. But several icons
        // here are hand-authored with the cutout wound the *same* way as the shape around it — under
        // nonzero those fill solid, which is why the emoji button's smiley had no eyes and no mouth.
        // Even-odd makes any enclosed subpath a hole regardless of direction, which is what every
        // icon in this set wants. The SelfTest hole checks pin it.
        var p = new GraphicsPath(FillMode.Alternate);
        PointF cur = default, sub = default, lastCubic = default, lastQuad = default;
        char cmd = ' ';
        int i = 0, n = d.Length;

        void Ws() { while (i < n && (d[i] == ',' || char.IsWhiteSpace(d[i]))) i++; }
        float Num()
        {
            Ws();
            int s = i;
            if (i < n && (d[i] == '-' || d[i] == '+')) i++;
            bool dot = false;
            while (i < n && (char.IsAsciiDigit(d[i]) || (d[i] == '.' && !dot))) { if (d[i] == '.') dot = true; i++; }
            if (i < n && (d[i] == 'e' || d[i] == 'E'))
            {
                i++;
                if (i < n && (d[i] == '-' || d[i] == '+')) i++;
                while (i < n && char.IsAsciiDigit(d[i])) i++;
            }
            return s == i ? 0f : float.Parse(d.AsSpan(s, i - s), System.Globalization.CultureInfo.InvariantCulture);
        }
        // Flags in an arc command may be packed without separators ("0 0 1-.0076" → 0, 1, -0.0076).
        bool Flag() { Ws(); return i < n && d[i++] == '1'; }

        while (true)
        {
            Ws();
            if (i >= n) break;
            if (char.IsLetter(d[i])) cmd = d[i++];
            else if (cmd == ' ') break;                       // stray number before any command
            bool rel = char.IsLower(cmd);
            PointF Abs(float x, float y) => rel ? new PointF(cur.X + x, cur.Y + y) : new PointF(x, y);

            switch (char.ToUpperInvariant(cmd))
            {
                case 'M':
                    cur = Abs(Num(), Num()); sub = cur; p.StartFigure();
                    cmd = rel ? 'l' : 'L';                    // implicit repeats of M are lineto
                    lastCubic = lastQuad = cur; break;
                case 'L': { var q = Abs(Num(), Num()); p.AddLine(cur, q); cur = q; lastCubic = lastQuad = cur; break; }
                case 'H': { float x = Num(); var q = new PointF(rel ? cur.X + x : x, cur.Y); p.AddLine(cur, q); cur = q; lastCubic = lastQuad = cur; break; }
                case 'V': { float y = Num(); var q = new PointF(cur.X, rel ? cur.Y + y : y); p.AddLine(cur, q); cur = q; lastCubic = lastQuad = cur; break; }
                case 'C':
                {
                    var c1 = Abs(Num(), Num()); var c2 = Abs(Num(), Num()); var e = Abs(Num(), Num());
                    p.AddBezier(cur, c1, c2, e); lastCubic = c2; cur = e; lastQuad = cur; break;
                }
                case 'S':
                {
                    var c1 = new PointF(2 * cur.X - lastCubic.X, 2 * cur.Y - lastCubic.Y);
                    var c2 = Abs(Num(), Num()); var e = Abs(Num(), Num());
                    p.AddBezier(cur, c1, c2, e); lastCubic = c2; cur = e; lastQuad = cur; break;
                }
                case 'Q':
                {
                    var q1 = Abs(Num(), Num()); var e = Abs(Num(), Num());
                    AddQuad(p, cur, q1, e); lastQuad = q1; cur = e; lastCubic = cur; break;
                }
                case 'T':
                {
                    var q1 = new PointF(2 * cur.X - lastQuad.X, 2 * cur.Y - lastQuad.Y);
                    var e = Abs(Num(), Num());
                    AddQuad(p, cur, q1, e); lastQuad = q1; cur = e; lastCubic = cur; break;
                }
                case 'A':
                {
                    float rx = Num(), ry = Num(), rot = Num();
                    bool laf = Flag(), sf = Flag();
                    var e = Abs(Num(), Num());
                    AddArc(p, cur, rx, ry, rot, laf, sf, e); cur = e; lastCubic = lastQuad = cur; break;
                }
                case 'Z': p.CloseFigure(); cur = sub; lastCubic = lastQuad = cur; break;
                default: return p;                            // unknown command: stop rather than loop
            }
        }
        return p;
    }

    static void AddQuad(GraphicsPath p, PointF a, PointF q, PointF b) =>
        p.AddBezier(a,
            new PointF(a.X + 2f / 3f * (q.X - a.X), a.Y + 2f / 3f * (q.Y - a.Y)),
            new PointF(b.X + 2f / 3f * (q.X - b.X), b.Y + 2f / 3f * (q.Y - b.Y)), b);

    // SVG endpoint-parameterised arc → up-to-90° bezier segments (W3C implementation notes F.6).
    static void AddArc(GraphicsPath p, PointF from, float rx, float ry, float rot, bool laf, bool sf, PointF to)
    {
        if (rx == 0 || ry == 0 || (from.X == to.X && from.Y == to.Y)) { if (from != to) p.AddLine(from, to); return; }
        rx = Math.Abs(rx); ry = Math.Abs(ry);
        double phi = rot * Math.PI / 180.0, cosf = Math.Cos(phi), sinf = Math.Sin(phi);
        double hx = (from.X - to.X) / 2.0, hy = (from.Y - to.Y) / 2.0;
        double x1 = cosf * hx + sinf * hy, y1 = -sinf * hx + cosf * hy;
        double rxs = (double)rx * rx, rys = (double)ry * ry, x1s = x1 * x1, y1s = y1 * y1;
        double lam = x1s / rxs + y1s / rys;
        if (lam > 1) { double k = Math.Sqrt(lam); rx = (float)(rx * k); ry = (float)(ry * k); rxs = (double)rx * rx; rys = (double)ry * ry; }
        double den = rxs * y1s + rys * x1s;
        double co = (laf == sf ? -1 : 1) * Math.Sqrt(Math.Max(0, (rxs * rys - rxs * y1s - rys * x1s) / (den == 0 ? 1 : den)));
        double cx1 = co * rx * y1 / ry, cy1 = -co * ry * x1 / rx;
        double cx = cosf * cx1 - sinf * cy1 + (from.X + to.X) / 2.0;
        double cy = sinf * cx1 + cosf * cy1 + (from.Y + to.Y) / 2.0;

        static double Ang(double ux, double uy, double vx, double vy)
        {
            double len = Math.Sqrt((ux * ux + uy * uy) * (vx * vx + vy * vy));
            double a = Math.Acos(Math.Clamp(len == 0 ? 1 : (ux * vx + uy * vy) / len, -1, 1));
            return ux * vy - uy * vx < 0 ? -a : a;
        }
        double ux0 = (x1 - cx1) / rx, uy0 = (y1 - cy1) / ry, vx0 = (-x1 - cx1) / rx, vy0 = (-y1 - cy1) / ry;
        double th1 = Ang(1, 0, ux0, uy0), dth = Ang(ux0, uy0, vx0, vy0);
        if (!sf && dth > 0) dth -= 2 * Math.PI;
        else if (sf && dth < 0) dth += 2 * Math.PI;

        int segs = Math.Max(1, (int)Math.Ceiling(Math.Abs(dth) / (Math.PI / 2)));
        double step = dth / segs, t = 4.0 / 3.0 * Math.Tan(step / 4);
        PointF At(double a) => new((float)(cx + rx * Math.Cos(a) * cosf - ry * Math.Sin(a) * sinf),
                                   (float)(cy + rx * Math.Cos(a) * sinf + ry * Math.Sin(a) * cosf));
        PointF Dt(double a) => new((float)(-rx * Math.Sin(a) * cosf - ry * Math.Cos(a) * sinf),
                                   (float)(-rx * Math.Sin(a) * sinf + ry * Math.Cos(a) * cosf));
        var cp = from;
        for (int k = 0; k < segs; k++)
        {
            double a1 = th1 + k * step, a2 = a1 + step;
            PointF s1 = At(a1), d1 = Dt(a1), s2 = At(a2), d2 = Dt(a2);
            p.AddBezier(cp,
                new PointF((float)(s1.X + t * d1.X), (float)(s1.Y + t * d1.Y)),
                new PointF((float)(s2.X - t * d2.X), (float)(s2.Y - t * d2.Y)), s2);
            cp = s2;
        }
    }
}
