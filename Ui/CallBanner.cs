using System.Drawing;
using System.Drawing.Drawing2D;

namespace ClaudeScord;

// The call overlay: a dimmed full-window layer with a centered card, exactly the shape of Discord's
// incoming-call UI. One control handles all three states — someone is ringing us (Answer/Decline),
// we are ringing them (Hang up), or we are in the call (mute / deafen / hang up).
//
// There is no audio path yet — the gateway state is real (ringing lists, participants, voice-state
// updates) so joining and leaving actually work; it is the RTP/opus part that is not wired up.
sealed class CallBanner : Control
{
    public enum State { Hidden, Incoming, Ringing, InCall }

    State _state = State.Hidden;
    ulong _channel;
    string _name = "";
    string? _avatar;
    int _hot = -1;
    bool _muted, _deaf;
    bool _isVideo;

    // InCall-only: current participants besides ourselves, for the "x others" line.
    int _others;
    bool _videoOn;                       // our camera toggle state
    bool _screenOn;                      // our screenshare toggle state
    byte[]? _peerFrame;                  // latest decrypted video frame from the peer (JPEG)
    byte[]? _selfFrame;                  // latest local capture (JPEG), the self preview tile
    readonly object _frameLock = new();

    public event Action<ulong>? Answer;
    public event Action<ulong>? Decline;
    public event Action<ulong>? HangUp;
    public event Action? ToggleMute;
    public event Action? ToggleDeaf;
    public event Action? ToggleVideo;
    public event Action? ToggleScreen;

    public CallBanner()
    {
        // Not docked: a second Dock.Fill would fight ChatView for the fill space and one of them
        // ends up 0x0. The shell re-bounds this over the client area when shown/resized.
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, false);
        BackColor = Color.Black;
        Visible = false;
    }

    public bool Active => Visible;

    public void Show(State st, ulong channel, string name, string? avatar, bool isVideo = false,
                     bool muted = false, bool deaf = false, int others = 0, bool videoOn = false,
                     bool screenOn = false)
    {
        _state = st;
        _channel = channel;
        _name = name;
        _avatar = avatar;
        _isVideo = isVideo;
        _muted = muted;
        _deaf = deaf;
        _others = others;
        _videoOn = videoOn;
        _screenOn = screenOn;
        if (st != State.Hidden)
        {
            Visible = true;
            // No BringToFront: the Shell adds the TitleBar after this control, so it already paints
            // above the dim layer and its window buttons stay clickable during a call.
            if (Parent != null) Bounds = Parent.ClientRectangle;
        }
        Invalidate();
    }

    public new void Hide() { if (Visible) { Visible = false; Invalidate(); } }

    /// Latest decrypted peer video frame (JPEG bytes), from VoiceClient. Null clears the tile.
    public void SetPeerFrame(byte[]? jpeg)
    {
        lock (_frameLock)
        {
            _peerFrame = jpeg;
            // Keep the InCall title readable on top of live video: fade the avatar/name block when
            // a frame is actually on screen.
        }
        Invalidate();
    }

    public void SetSelfFrame(byte[]? jpeg)
    {
        lock (_frameLock) _selfFrame = jpeg;
        Invalidate();
    }

    // Who is talking, from VoiceClient's audio-driven detector.
    readonly HashSet<ulong> _speaking = new();

    public void SetSpeaking(ulong userId, bool on)
    {
        if ((on ? _speaking.Add(userId) : _speaking.Remove(userId)) && Visible) Invalidate();
    }

    // ── geometry ────────────────────────────────────────────────────────────────────────────────
    int CardW => Ui.S(360);
    int CardH => Ui.S(_state == State.InCall ? 250 : 230);
    Rectangle Card => new((Width - CardW) / 2, (Height - CardH) / 2, CardW, CardH);

    Rectangle Btn(int i)   // 0 = left (decline/hangup), 1 = right (answer)
    {
        int d = Ui.S(64);
        int y = Card.Y + (CardH - Ui.S(96));
        int cx = Card.X + CardW / 2 + (i == 0 ? -Ui.S(48) - d / 2 : Ui.S(48) + d / 2);
        return new Rectangle(cx - d / 2, y, d, d);
    }

    Rectangle LabelFor(Rectangle b) =>
        new(b.X - Ui.S(40), b.Bottom + Ui.S(4), b.Width + Ui.S(80), Ui.S(18));

    // InCall: Discord's call controls in one centered row at the card's bottom — mute, deafen,
    // camera, screenshare, then the red hang-up. The four square toggles are the same size; the
    // hang-up is a bigger circle, offset up to share the row's vertical centre. (The earlier
    // top-right corner row ran out of room once the screenshare button was added — a 5th button
    // there collided with the centred avatar.)
    int CtrlD => Ui.S(34);
    int HangD => Ui.S(56);
    int CtrlGap => Ui.S(12);

    Rectangle Ctrl(int i)   // 0 mute, 1 deafen, 2 camera, 3 screenshare, 4 hang-up
    {
        int total = 4 * CtrlD + HangD + 4 * CtrlGap;
        int x = Card.X + (CardW - total) / 2;
        // 48px below the card's bottom edge leaves room for the hang-up label while keeping the
        // row clear of the "+N others" sub text (which ends ~ab.Bottom+30) for long names.
        int y = Card.Y + CardH - Ui.S(48) - CtrlD;
        if (i == 4)
            return new Rectangle(x + 4 * (CtrlD + CtrlGap), y - (HangD - CtrlD) / 2, HangD, HangD);
        return new Rectangle(x + i * (CtrlD + CtrlGap), y, CtrlD, CtrlD);
    }

    int HitTest(Point p)
    {
        if (_state == State.Incoming)
        {
            if (Btn(0).Contains(p)) return 0;   // decline
            if (Btn(1).Contains(p)) return 1;   // answer
        }
        else if (_state == State.Ringing)
        {
            if (Btn(0).Contains(p)) return 2;   // hang up
        }
        else
        {
            for (int i = 0; i < 5; i++)
                if (Ctrl(i).Contains(p)) return 10 + i;   // 10 mute .. 14 hang-up
        }
        return -1;
    }

    // ── input ───────────────────────────────────────────────────────────────────────────────────
    protected override void OnMouseMove(MouseEventArgs e)
    {
        int h = HitTest(e.Location);
        if (h != _hot) { _hot = h; Invalidate(); }
        Cursor = h >= 0 ? Cursors.Hand : Cursors.Default;
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        if (_hot != -1) { _hot = -1; Invalidate(); }
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        switch (_hot)
        {
            case 0: Decline?.Invoke(_channel); break;
            case 1: Answer?.Invoke(_channel); break;
            case 2: HangUp?.Invoke(_channel); break;
            case 10: ToggleMute?.Invoke(); break;
            case 11: ToggleDeaf?.Invoke(); break;
            case 12: ToggleVideo?.Invoke(); break;
            case 13: ToggleScreen?.Invoke(); break;
            case 14: HangUp?.Invoke(_channel); break;
        }
        base.OnMouseDown(e);
    }

    // ── paint ───────────────────────────────────────────────────────────────────────────────────
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        // Dim everything behind the card, like the real client's call backdrop.
        using (var b = new SolidBrush(Color.FromArgb(190, 13, 14, 16)))
            g.FillRectangle(b, ClientRectangle);

        var card = Card;
        Ui.FillRound(g, card, Ui.S(12), Theme.Floating);
        using (var pen = new Pen(Theme.Border))
        using (var path = Ui.RoundRect(new Rectangle(card.X, card.Y, card.Width - 1, card.Height - 1), Ui.S(12)))
            g.DrawPath(pen, path);

        // In a video call the peer's live frame becomes the card's background (a Discord-style video
        // tile); without one we fall back to the avatar layout.
        byte[]? peer;
        lock (_frameLock) peer = _peerFrame;
        bool showingVideo = _state == State.InCall && peer != null;
        int av = Ui.S(76);
        var ab = showingVideo
            ? new Rectangle(card.X + Ui.S(14), card.Y + Ui.S(14), av - Ui.S(10), av - Ui.S(10))
            : new Rectangle(card.X + (card.Width - av) / 2, card.Y + Ui.S(26), av, av);

        if (showingVideo)
        {
            var inner = Rectangle.Inflate(card, -Ui.S(10), -Ui.S(10));
            PaintClipped(g, inner, Ui.S(10), () =>
            {
                using var img = LoadJpeg(peer);
                if (img != null) PaintCover(g, img, inner);
            });
            // Peer name pill, TOP-left over the video (Discord's video-tile layout) — the bottom
            // of the card is the control row now.
            Ui.FillRound(g, new Rectangle(card.X + Ui.S(16), card.Y + Ui.S(16), Ui.S(190), Ui.S(24)),
                         Ui.S(4), Color.FromArgb(160, 0, 0, 0));
            Ui.Text(g, _name, Theme.SmallMedium,
                    new Rectangle(card.X + Ui.S(24), card.Y + Ui.S(16), Ui.S(174), Ui.S(24)),
                    Color.White, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
        else
        {
            Ui.Avatar(g, Media.Get(_avatar, this), ab, Theme.Surface);
            // Discord rings the caller's avatar while they are talking. Anyone who is not us counts
            // as "them" here — a DM call has exactly one other person on the card.
            if (_state == State.InCall && _speaking.Any(u => u != (App.Client?.CurrentUser?.Id ?? 0)))
            {
                using var pen = new Pen(Theme.Positive, Ui.S(3));
                g.DrawEllipse(pen, Rectangle.Inflate(ab, Ui.S(3), Ui.S(3)));
            }
        }

        // Self preview: a floating picture-in-picture tile at the window's bottom-right corner (the
        // way the real client shows it), well clear of the card and its control buttons. Shown for
        // BOTH the camera and the screenshare — the user needs to see what they are broadcasting.
        byte[]? self;
        lock (_frameLock) self = _selfFrame;
        if (_state == State.InCall && (_videoOn || _screenOn) && self != null)
        {
            int sw = Ui.S(176);
            int sh = Ui.S(99);
            var tile = new Rectangle(Width - sw - Ui.S(24), Height - sh - Ui.S(24), sw, sh);
            PaintClipped(g, tile, Ui.S(8), () =>
            {
                using var img = LoadJpeg(self);
                if (img != null) PaintCover(g, img, tile);
            });
            using (var pen = new Pen(Color.FromArgb(90, 255, 255, 255), 1f)) g.DrawRectangle(pen, tile);
            // A subtle "You" label so the corner tile reads as the local camera, not a stray peer.
            Ui.FillRound(g, new Rectangle(tile.X, tile.Bottom - Ui.S(20), tile.Width, Ui.S(20)),
                         Ui.S(4), Color.FromArgb(120, 0, 0, 0));
            Ui.Text(g, "You", Theme.Small, new Rectangle(tile.X, tile.Bottom - Ui.S(20), tile.Width, Ui.S(20)),
                    Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        // The title/name block only appears over the static avatar layout; live video carries the
        // peer's name pill instead. The control row keeps the card's bottom, so the text sits in
        // the middle band (above the hang-up circle, below the avatar).
        if (!showingVideo)
        {
            var title = _state switch
            {
                State.Incoming => _isVideo ? "Incoming Video Call" : "Incoming Call",
                State.Ringing => _isVideo ? "Calling…" : "Ringing…",
                _ => _isVideo ? "In Video Call" : "In Call",
            };
            Ui.Text(g, title, Theme.H2,
                    new Rectangle(card.X, ab.Bottom + Ui.S(8), card.Width, Ui.S(24)), Theme.Strong,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            var sub = _state == State.InCall && _others > 0 ? $"{_name} +{_others} other" : _name;
            Ui.Text(g, sub, Theme.BodyMedium,
                    new Rectangle(card.X + Ui.S(16), ab.Bottom + Ui.S(30), card.Width - Ui.S(32), Ui.S(20)),
                    Theme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }

        DrawButtons(g);
    }

    // Run an action under a rounded clip path (used for video tiles).
    static void PaintClipped(Graphics g, Rectangle r, int radius, Action paint)
    {
        using var path = Ui.RoundRect(r, radius);
        var old = g.Clip;
        g.SetClip(path, System.Drawing.Drawing2D.CombineMode.Replace);
        try { paint(); }
        finally { g.Clip = old; }
    }

    // A JPEG byte[] -> Image, or null. The frame is our own capture or a decrypted peer frame;
    // System.Drawing already ships the GDI+ JPEG decoder so no dependency is added.
    //
    // new Bitmap(stream) (NOT Image.FromStream) is deliberate: FromStream decodes lazily and keeps
    // the stream alive until the first DrawImage, so disposing the MemoryStream here would make the
    // first paint throw "Parameter is not valid". new Bitmap reads the whole frame eagerly.
    static System.Drawing.Image? LoadJpeg(byte[] jpeg)
    {
        try
        {
            using var ms = new System.IO.MemoryStream(jpeg);
            return new System.Drawing.Bitmap(ms);
        }
        catch { return null; }
    }

    // Draw a 16:9-ish image to cover the box, preserving aspect (letterboxing would look unlike
    // Discord's video tiles, which always fill).
    static void PaintCover(Graphics g, Image img, Rectangle box)
    {
        float ia = img.Width / (float)img.Height, ba = box.Width / (float)box.Height;
        Rectangle src;
        if (ia > ba)   // image wider: crop the sides
        {
            int w = (int)(img.Height * ba);
            src = new Rectangle((img.Width - w) / 2, 0, w, img.Height);
        }
        else           // image taller: crop top/bottom
        {
            int h = (int)(img.Width / ba);
            src = new Rectangle(0, (img.Height - h) / 2, img.Width, h);
        }
        g.DrawImage(img, box, src, GraphicsUnit.Pixel);
    }

    void DrawButtons(Graphics g)
    {
        if (_state == State.Incoming)
        {
            // Red decline on the left, green answer on the right — the arrangement everyone
            // recognises from the mobile UI.
            DrawCircleBtn(g, Btn(0), Theme.Danger, Color.White, hangUp: true, "Decline");
            DrawCircleBtn(g, Btn(1), Theme.Positive, Color.White, hangUp: false, "Answer");
        }
        else if (_state == State.Ringing)
        {
            DrawCircleBtn(g, Btn(0), Theme.Danger, Color.White, hangUp: true, "Hang Up");
        }
        else if (_state == State.InCall)
        {
            // The four square toggles first, then the red hang-up circle — Discord's call row.
            DrawSquareBtn(g, Ctrl(0), _muted ? Theme.Danger : Theme.SurfaceHigh, _muted,
                          _muted ? Icons.MicMutedLine : Icons.MicLine);
            DrawSquareBtn(g, Ctrl(1), _deaf ? Theme.Danger : Theme.SurfaceHigh, _deaf,
                          _deaf ? Icons.HeadsetMutedLine : Icons.HeadsetLine);
            // Camera and screenshare are SEPARATE toggles, exactly like Discord's call controls:
            // the camera button drives the webcam, the monitor button shares the screen. Green
            // while the respective stream is live.
            DrawSquareBtn(g, Ctrl(2), _videoOn ? Theme.Positive : Theme.SurfaceHigh, _videoOn,
                          Icons.VideoLine);
            DrawSquareBtn(g, Ctrl(3), _screenOn ? Theme.Positive : Theme.SurfaceHigh, _screenOn,
                          Icons.MonitorLine);
            DrawCircleBtn(g, Ctrl(4), Theme.Danger, Color.White, hangUp: true, "Hang Up");
        }
    }

    void DrawCircleBtn(Graphics g, Rectangle b, Color bg, Color fg, bool hangUp, string label)
    {
        using (var br = new SolidBrush(bg)) g.FillEllipse(br, Rectangle.Inflate(b, Ui.S(2), Ui.S(2)));
        using (var p = new Pen(Color.FromArgb(110, Color.White), 2f))
            g.DrawEllipse(p, b);
        DrawPhone(g, Rectangle.Inflate(b, -Ui.S(20), -Ui.S(20)), fg, hangUp);
        Ui.Text(g, label, Theme.SmallMedium, LabelFor(b), Theme.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    void DrawSquareBtn(Graphics g, Rectangle b, Color bg, bool active, string icon)
    {
        Ui.FillRound(g, b, Ui.S(8), bg);
        Icons.Draw(g, icon, Rectangle.Inflate(b, -Ui.S(8), -Ui.S(8)), Color.White, 1.8f);
    }

    // The phone glyph, flipped so the handset points down for decline/hang-up. The rotation is a
    // transform because the path is authored one way up.
    void DrawPhone(Graphics g, RectangleF r, Color c, bool hangUp)
    {
        var st = g.Save();
        if (hangUp)
        {
            g.TranslateTransform(r.X + r.Width / 2, r.Y + r.Height / 2);
            g.RotateTransform(180);
            g.TranslateTransform(-(r.X + r.Width / 2), -(r.Y + r.Height / 2));
        }
        Svg.SvgFill(g, Icons.PhoneLine, r, c);
        g.Restore(st);
    }
}
