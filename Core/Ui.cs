using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace OpenCord;

static class Ui
{
    // ── DPI ──────────────────────────────────────────────────────────────────────────────────────
    // The app runs SystemAware, so fonts — specified in points — already render at the system DPI,
    // while every constant in M is a design pixel written against 96 DPI. Mixing the two is what
    // clips text at 150%: Segoe UI 12pt draws 24px tall into a box someone sized as a raw 22.
    // S() converts a design pixel to a device pixel. SystemAware fixes DPI for the life of the
    // process, so this is a constant rather than per-control state.
    //
    // Read from the screen DC rather than a Control: field initialisers size half the window and
    // those run before any handle exists.
    public static int Dpi { get; } = ScreenDpi();
    /// System DPI times the user's zoom. Read once at startup: every laid-out size in the client
    /// derives from it, so changing it mid-run would leave half the UI measured at the old scale —
    /// which is why the settings page says a restart is needed.
    public static float Scale { get; } = Dpi / 96f * Math.Clamp(Prefs.Current.Zoom, 0.8f, 1.4f);

    public static int S(int designPx) => (int)Math.Round(designPx * Scale);

    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);
    [DllImport("gdi32.dll")] static extern int GetDeviceCaps(IntPtr hdc, int index);

    static int ScreenDpi()
    {
        try
        {
            var dc = GetDC(IntPtr.Zero);
            if (dc == IntPtr.Zero) return 96;
            int dpi = GetDeviceCaps(dc, 90);   // LOGPIXELSY
            ReleaseDC(IntPtr.Zero, dc);
            return dpi is >= 96 and <= 480 ? dpi : 96;
        }
        catch { return 96; }
    }

    // A text box must be at least as tall as the font in it, whatever the DPI. `pad` is the
    // design-pixel breathing room above + below the glyphs.
    public static int LineBox(Font f, int pad = 4) => f.Height + S(pad);

    // ── Wheel ────────────────────────────────────────────────────────────────────────────────────
    /// One wheel message in device pixels. Windows reports 120 units per notch; Chromium — so the
    /// real client — moves 100 CSS px for that. Scrolling by the raw delta instead is 20% fast at
    /// 96 DPI and 20% *slow* at 144, because the content scales with S() and the delta does not.
    public static float Wheel(int delta) => delta / 120f * S(100);

    /// Same, rounded, for the lists that keep an int offset. Rounding rather than truncating so a
    /// touchpad's smallest report still moves the list instead of stalling at zero.
    public static int WheelPx(int delta) => (int)MathF.Round(Wheel(delta));

    /// True when a wheel message is NOT a single physical notch. Only exact ±120 — one click of a
    /// mouse wheel — gets the glide. Everything else is either a precision touchpad report (sub-notch
    /// deltas that track the finger) or a COALESCED burst: Windows sums wheel messages when the UI
    /// thread is busy, so a fast trackpad flick can arrive as one ±240..±1440 that is a multiple of
    /// 120. Easing those with the fast-out curve is what made trackpad scrolling fling — a busy
    /// frame plus a quick scroll became one "notch" shot across in ~90ms. Sub-notch reports already
    /// tracked the finger directly; multi-notch bursts now do too, instead of gliding.
    public static bool Precise(int delta) => delta % 120 != 0 || Math.Abs(delta) > 120;

    // ── Easing ───────────────────────────────────────────────────────────────────────────────────
    /// Move `cur` toward `target` by an amount that depends on elapsed *time*, not on how often this
    /// happens to be called.
    ///
    /// The obvious `cur += (target - cur) * 0.28f` is frame-rate dependent, and System.Windows.Forms
    /// .Timer is the wrong clock to be frame-rate dependent against: WM_TIMER is low priority, its
    /// resolution is ~15.6ms, and it coalesces dropped ticks rather than making them up. So a scroll
    /// eased per-tick advances in uneven jumps exactly when the UI is busiest — which is the moment
    /// the stutter is most visible. Easing on elapsed seconds makes a late tick move proportionally
    /// further and the motion come out even regardless.
    ///
    /// `rate` is the exponential decay constant: 22 reproduces the old 0.28-per-15ms feel.
    public static float Ease(float cur, float target, float dt, float rate = 22f) =>
        cur + (target - cur) * (1f - MathF.Exp(-rate * Math.Clamp(dt, 0f, 0.1f)));

    /// Ease-out cubic over a fixed span: fast out of the blocks, settles gently. The profile the
    /// scroll glides use — a wheel notch moves immediately and coasts to a stop, and retargeting
    /// mid-flight re-uses the same curve from wherever the list is.
    public static float EaseOut(float k) => 1f - (1f - k) * (1f - k) * (1f - k);

    // ── Text ─────────────────────────────────────────────────────────────────────────────────────
    // Every drawn string goes through here rather than TextRenderer.DrawText, for two reasons.
    //
    // NoPrefix: DrawText treats "&" as a *mnemonic prefix* and swallows it, so "Audio & Video"
    // renders as "Audio  Video" in message bodies, embed titles, channel names and usernames alike.
    //
    // NoPadding: GDI otherwise reserves a few pixels of side bearing that MeasureText's default
    // (NoPadding, below) does not report. Layout here sizes a box from Measure and then fills it
    // with Text under EndEllipsis, so the mismatch made every tight box a few px narrower than the
    // string it was measured for — "#general" in the chat header rendered as "#gen...". The two must
    // agree on padding, and NoPadding is the side that also lets text sit flush at rect.X.
    // Centralising both means no call site can forget either.
    const TextFormatFlags Common = TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;

    public static void Text(Graphics g, string? s, Font f, Rectangle r, Color c,
                            TextFormatFlags flags = TextFormatFlags.Default) =>
        TextRenderer.DrawText(g, s, f, r, c, flags | Common);

    public static void Text(Graphics g, string? s, Font f, Point at, Color c,
                            TextFormatFlags flags = TextFormatFlags.Default) =>
        TextRenderer.DrawText(g, s, f, at, c, flags | Common);

    // MeasureText applies the same mnemonic rule, so a measured "&" width has to match the drawn one
    // or every layout built from it drifts.
    public static Size Measure(string? s, Font f, TextFormatFlags flags = TextFormatFlags.Default) =>
        TextRenderer.MeasureText(s, f, Size.Empty, flags | Common);

    // ── Painting ─────────────────────────────────────────────────────────────────────────────────
    /// `left`/`right` pick which side's pair of corners is rounded — a split button (the tray's
    /// mute and deafen toggles) is two of these meeting flat-to-flat with a 1px seam between.
    public static GraphicsPath RoundRect(Rectangle r, int radius, bool left = true, bool right = true)
    {
        var p = new GraphicsPath();
        if (radius <= 0 || r.Width <= 0 || r.Height <= 0 || (!left && !right)) { p.AddRectangle(r); return p; }
        int d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (left) p.AddArc(r.X, r.Y, d, d, 180, 90); else p.AddLine(r.X, r.Y, r.X, r.Y);
        if (right)
        {
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        }
        else p.AddLine(r.Right, r.Y, r.Right, r.Bottom);
        if (left) p.AddArc(r.X, r.Bottom - d, d, d, 90, 90); else p.AddLine(r.X, r.Bottom, r.X, r.Y);
        p.CloseFigure();
        return p;
    }

    public static void FillRound(Graphics g, Rectangle r, int radius, Color c, bool left, bool right)
    {
        var old = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var p = RoundRect(r, radius, left, right))
        using (var b = new SolidBrush(c))
            g.FillPath(b, p);
        g.SmoothingMode = old;
    }

    public static void FillRound(Graphics g, Rectangle r, int radius, Color c)
    {
        var old = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var p = RoundRect(r, radius))
        using (var b = new SolidBrush(c))
            g.FillPath(b, p);
        g.SmoothingMode = old;
    }

    public static void Fill(Graphics g, Rectangle r, Color c)
    {
        using var b = new SolidBrush(c);
        g.FillRectangle(b, r);
    }

    /// A rounded rect under a vertical gradient — how a themed profile surface is painted.
    ///
    /// Discord's profile gradient is not a plain two-stop: it holds the first colour flat for the
    /// banner's height before it starts interpolating, and the panel holds the second colour flat
    /// for the last 72px under the footer button. `solidTop` and `flatBottom` are those two runs,
    /// in device px. Getting them wrong is what makes two cards built from the same pair of colours
    /// read as different colours at the same row.
    public static void GradientRound(Graphics g, Rectangle r, int radius, Color top, Color bottom,
                                     int solidTop = 0, int flatBottom = 0)
    {
        if (r.Width <= 0 || r.Height <= 0) return;
        if (top == bottom) { FillRound(g, r, radius, top); return; }

        // Inflated by one: a gradient brush is undefined exactly on its end line, which paints the
        // last row of pixels in the *first* stop's colour.
        var box = Rectangle.Inflate(r, 1, 1);
        using var b = new LinearGradientBrush(box, top, bottom, 90f);

        float a = Math.Clamp(solidTop / (float)box.Height, 0f, 1f);
        float z = Math.Clamp(1f - flatBottom / (float)box.Height, 0f, 1f);
        // Only worth a blend when the two inner stops are ordered and actually inside the box; a
        // ColorBlend with equal or reversed positions throws rather than degrading.
        if (z - a > 0.001f && (a > 0.001f || z < 0.999f))
            b.InterpolationColors = new ColorBlend
            {
                Colors = new[] { top, top, bottom, bottom },
                Positions = new[] { 0f, a, z, 1f },
            };

        var sm = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var p = RoundRect(r, radius)) g.FillPath(b, p);
        g.SmoothingMode = sm;
    }

    // ── Server tags ──────────────────────────────────────────────────────────────────────────────
    // Discord's "server tag": the badge of the guild a user flies the colours of, then that guild's
    // short tag, in a translucent pill. Measured off the live client — beside a name it is 16 tall
    // with a 4px radius, on a profile panel 20 with an 8px radius; either way 4px of padding each
    // side, a 14px badge, a 2px gap, and 12px/600 text.
    //
    // The badge is left to the caller to fetch, because Media.Get needs the Control to repaint when
    // the download lands and this file has none. A null badge still reserves its slot, so the tag
    // does not jump sideways when the image arrives.
    const int TagBadge = 14, TagPad = 4, TagGap = 2;

    public static int TagChipWidth(string tag) =>
        S(TagPad + TagBadge + TagGap) + Measure(tag, Theme.SmallMedium).Width + S(TagPad);

    /// Draws the chiplet with its left edge at `x`, centred on `centerY`. `on` is the colour behind
    /// it — the pill is translucent and has to be composited against its own surface. `fg` overrides
    /// the label colour, which a light profile card needs: the default is white-ish and vanishes on
    /// one.
    public static void TagChip(Graphics g, int x, int centerY, string tag, Image? badge, Color on,
                               bool big = false, Color? fg = null)
    {
        int h = S(big ? 20 : 16), b = S(TagBadge);
        var pill = new Rectangle(x, centerY - h / 2, TagChipWidth(tag), h);
        FillRound(g, pill, S(big ? 8 : 4), Theme.TagPill(on));
        if (badge != null)
        {
            var im = g.InterpolationMode;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(badge, new Rectangle(pill.X + S(TagPad), pill.Y + (h - b) / 2, b, b));
            g.InterpolationMode = im;
        }
        Text(g, tag, Theme.SmallMedium,
             new Rectangle(pill.X + S(TagPad + TagBadge + TagGap), pill.Y, pill.Width, h),
             fg ?? Theme.Text, TextFormatFlags.VerticalCenter);
    }

    public static void OpenUrl(string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { }
    }

    /// The status dot in an avatar's bottom-right corner.
    ///
    /// Discord masks a *hole* in the avatar and drops the dot into it, so the ring around the dot is
    /// the background colour rather than a border on the dot — and idle/dnd/offline are themselves
    /// cut out of that dot in the same colour. Drawing them as strokes gets the shapes subtly wrong.
    public static void PresenceDot(Graphics g, Rectangle avatar, Presence p, Color background, int diameter = 0)
    {
        int d = diameter > 0 ? diameter : S(12), ring = Math.Max(1, d / 4);
        var dot = new Rectangle(avatar.Right - d, avatar.Bottom - d, d, d);
        var sm = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        using (var b = new SolidBrush(background))
            g.FillEllipse(b, Rectangle.Inflate(dot, ring, ring));
        using (var b = new SolidBrush(Theme.Dot(p)))
            g.FillEllipse(b, dot);

        using (var cut = new SolidBrush(background))
        {
            if (p == Presence.Idle)
                g.FillEllipse(cut, new Rectangle(dot.X - d / 4, dot.Y - d / 4, d, d));
            else if (p == Presence.Dnd)
                g.FillRectangle(cut, new Rectangle(dot.X + d / 5, dot.Y + d * 2 / 5, d * 3 / 5, Math.Max(2, d / 5)));
            else if (p == Presence.Offline)
                g.FillEllipse(cut, new Rectangle(dot.X + d / 4, dot.Y + d / 4, d / 2, d / 2));
            else if (p == Presence.Streaming)
            {
                var tri = new[]
                {
                    new PointF(dot.X + d * 0.38f, dot.Y + d * 0.3f),
                    new PointF(dot.X + d * 0.72f, dot.Y + d * 0.5f),
                    new PointF(dot.X + d * 0.38f, dot.Y + d * 0.7f),
                };
                g.FillPolygon(cut, tri);
            }
        }
        g.SmoothingMode = sm;
    }

    /// A circular avatar with a clean rim.
    ///
    /// Filling an ellipse with a scaled TextureBrush rather than SetClip+DrawImage is what makes the
    /// edge smooth: a clip region is not antialiased, so clipping a bitmap to a circle leaves the
    /// stair-stepped rim that reads as "glitchy" — it is edge sampling, not the decode. WrapMode.Clamp
    /// stops the brush tiling a fringe of the opposite edge around the outside.
    /// `host` opts the avatar into animation: an `a_` GIF avatar plays instead of showing its first
    /// frame. It is the control to repaint, so it is only ever passed from a real OnPaint — the
    /// registration is per image (see Media.Animate), not per draw, so calling this every frame is
    /// what it expects.
    public static void Avatar(Graphics g, Image? img, Rectangle box, Color placeholder,
                              Control? host = null)
    {
        var sm = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        if (img != null && host != null && Media.IsAnimated(img)) Media.Animate(img, host);

        if (img == null)
        {
            using var b = new SolidBrush(placeholder);
            g.FillEllipse(b, box);
        }
        else
        {
            var im = g.InterpolationMode;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            using (var tb = new TextureBrush(img, WrapMode.Clamp))
            {
                tb.ScaleTransform(box.Width / (float)img.Width, box.Height / (float)img.Height);
                tb.TranslateTransform(box.X, box.Y, MatrixOrder.Append);
                g.FillEllipse(tb, box);
            }
            g.InterpolationMode = im;
        }
        g.SmoothingMode = sm;
    }

    /// The same avatar at reduced opacity, for offline members. A ColorMatrix alpha is the only way
    /// to fade a TextureBrush fill — drawing a translucent scrim over it tints the ring as well.
    public static void AvatarDim(Graphics g, Image img, Rectangle box, Color placeholder, float alpha)
    {
        var sm = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var attrs = new System.Drawing.Imaging.ImageAttributes();
        attrs.SetColorMatrix(new System.Drawing.Imaging.ColorMatrix { Matrix33 = alpha });
        using var path = new GraphicsPath();
        path.AddEllipse(box);
        var st = g.Save();
        g.SetClip(path, CombineMode.Replace);
        g.DrawImage(img, box, 0, 0, img.Width, img.Height, GraphicsUnit.Pixel, attrs);
        g.Restore(st);
        g.SmoothingMode = sm;
    }
}

/// The clock an animation should run on.
///
/// Every step is eased on *elapsed time* rather than tick count, so the ticking itself barely
/// matters: a tick that lands late — WinForms' timer is quantised to the ~15.6ms system tick and
/// misses are coalesced away rather than made up — simply moves the animation proportionally
/// further, and the motion comes out even whatever the pacing. The step must invalidate the owner
/// (or the Scroller's apply hook must move it) or the loop will not be asked for more frames, and
/// a step that finishes the animation must Stop() or it will keep being called.
///
/// A short System.Windows.Forms.Timer rather than Application.Idle: the idle event is raised by
/// the main message pump and does not fire in every loop this app runs — the ToolStripDropDown
/// popups (pickers, search, inbox) pump their own — so a glide driven by it stalls inside a
/// picker. A timer works in every pump. Nothing runs at rest: Start only hooks while there is
/// distance left to cover.
sealed class Frames
{
    readonly Action<float> _step;                  // given seconds elapsed since the last frame
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 8 };
    readonly System.Diagnostics.Stopwatch _clock = new();

    public Frames(Action<float> step)
    {
        _step = step;
        _timer.Tick += (_, _) =>
        {
            float dt = (float)_clock.Elapsed.TotalSeconds;
            _clock.Restart();
            Log.Sample("frame-gap", dt * 1000);
            _step(dt);
        };
    }

    public bool Running => _timer.Enabled;

    public void Start()
    {
        if (_timer.Enabled) return;
        _clock.Restart();          // not from wherever it stopped, or frame one swallows the idle gap
        _timer.Start();
    }

    public void Stop() => _timer.Stop();
}

/// The one scroll-offset model for every list in the app.
///
/// The physics are a browser's, not a spring's. A wheel notch moves the content exactly one notch
/// (Ui.Wheel) with a short eased glide (~90ms) that re-targets from wherever the list currently
/// is: slow scrolling tracks the wheel 1:1, a fast flick composes into continuous motion, and the
/// list stops ~90ms after the last notch. Chasing an *accumulated* target — the previous model —
/// is what felt broken: during a spin the visible offset fell behind the wheel by more and more
/// notches, and after the wheel stopped the list kept sliding for ~300ms, like it was on a spring.
/// A precision touchpad is followed directly, finger to pixel, with no glide.
///
/// The glide runs off Frames' timer rather than Application.Idle so it works inside the
/// ToolStripDropDown message loop the pickers and popups live in.
sealed class Scroller
{
    const float Duration = 0.09f;                  // per-notch glide, ~Chromium's scroll animation
    readonly Control _owner;
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 8 };
    readonly System.Diagnostics.Stopwatch _clock = new();
    readonly Action<int>? _apply;                  // surfaces that must move child controls (AutoScroll)
    float _value, _from, _to, _t;

    public Scroller(Control owner, Action<int>? apply = null)
    {
        _owner = owner;
        _apply = apply;
        _timer.Tick += (_, _) =>
        {
            // A tick after the control died must not paint into it; the timer holds the owner.
            if (_owner.IsDisposed) { _timer.Stop(); return; }
            float dt = (float)_clock.Elapsed.TotalSeconds;
            _clock.Restart();
            Step(dt);
        };
    }

    void Step(float dt)
    {
        if (_t < Duration)
        {
            _t += dt;
            _value = _t >= Duration ? _to
                     : _from + (_to - _from) * Ui.EaseOut(Math.Min(1f, _t / Duration));
        }
        _apply?.Invoke((int)_value);
        _owner.Invalidate();
        if (_t >= Duration) _timer.Stop();
    }

    /// Where to draw from, in whole pixels.
    public int Value => (int)_value;

    /// A wheel event. `max` is the control's current MaxScroll. True if the offset will change.
    public bool Wheel(int delta, int max)
    {
        float px = Ui.Wheel(delta);
        if (Ui.Precise(delta))
        {
            // Trackpad: track the finger exactly, and cancel any glide in flight.
            _timer.Stop();
            float nv = Math.Clamp(_value - px, 0, max);
            bool moved = Math.Abs(nv - _value) >= 0.5f;
            _value = nv;
            _from = _to = nv;
            if (moved) { _apply?.Invoke((int)_value); _owner.Invalidate(); }
            return moved;
        }
        return ScrollTo(_value - px, max);
    }

    /// An eased move to `v` (clamped to `max`) — a wheel notch, a page key, ScrollToBottom.
    /// True if the list will move.
    public bool ScrollTo(float v, int max)
    {
        float to = Math.Clamp(v, 0, max);
        _from = _value;
        if (to == _from && !_timer.Enabled) { _to = to; return false; }
        _to = to;
        _t = 0;
        _timer.Start();
        return to != _from;
    }

    /// An instant move — the scrollbar thumb, keyboard-selection follow, a channel switch.
    public void JumpTo(float v, int max)
    {
        _value = _from = _to = Math.Clamp(v, 0, max);
        _t = Duration;
        _timer.Stop();
        _apply?.Invoke((int)_value);
        _owner.Invalidate();
    }

    /// The content changed height: keep the offset legal without animating to it.
    public void Clamp(int max)
    {
        _value = Math.Clamp(_value, 0, max);
        _from = Math.Clamp(_from, 0, max);
        _to = Math.Clamp(_to, 0, max);
    }

    public void Reset() { _value = _from = _to = 0; _t = Duration; _timer.Stop(); }

    /// Where the list is heading, in whole pixels — pinned-at-bottom checks and the selftest.
    public float Target => _to;
}
