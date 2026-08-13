using System.Drawing;
using System.Drawing.Drawing2D;

namespace OpenCord;

// The voice channel stage: what replaces the message list once you are connected.
//
// Discord's layout, measured off the live client: a near-black stage (the same
// --background-base-lowest the rail uses), a grid of 16:9 tiles each holding a centred circular
// avatar and a name pill in the bottom-left corner, and a floating control bar along the bottom.
// A muted participant gets a crossed-mic badge on the pill; whoever is talking gets a green ring.
//
// This is a *view*, not the transport. It renders whatever tiles Session hands it and raises events
// for the buttons — the voice connection itself lives in VoiceClient and the gateway.
sealed class VoiceView : Control
{
    /// `Screen` marks the tile as a member's SCREEN SHARE rather than the member themselves.
    /// Discord gives a share its own tile, and so must we: someone with both the camera and a
    /// share on has two live feeds, and pointing them at one tile just flickers between them.
    /// `Pending` is someone the call is still ringing — Discord puts them on the stage straight
    /// away, dimmed, so an outgoing call shows who you are calling rather than just yourself.
    public sealed record Tile(ulong UserId, string Name, string? Avatar,
                              bool Muted, bool Deafened, bool Streaming, bool Video,
                              bool Screen = false, bool Pending = false);

    // Who is talking right now, fed by VoiceClient's audio-driven detector. Kept out of Tile so a
    // ring turning on and off does not force the session to rebuild the whole tile list 10x a second.
    readonly HashSet<ulong> _speaking = new();

    public void SetSpeaking(ulong userId, bool on)
    {
        bool changed = on ? _speaking.Add(userId) : _speaking.Remove(userId);
        if (changed && Visible) Invalidate();
    }

    readonly List<Tile> _tiles = new();
    string _channel = "";
    string _guild = "";
    bool _muted, _deaf, _videoOn, _screenOn;
    int _hot = -1;

    // Latest frame per user (JPEG bytes), cameras and screen shares kept apart. Ours land here
    // too, keyed on our own user id.
    readonly Dictionary<ulong, byte[]> _frames = new();
    readonly Dictionary<ulong, byte[]> _screens = new();

    public event Action? Disconnect;
    public event Action? MuteToggled;
    public event Action? DeafenToggled;
    public event Action? ChatRequested;
    public event Action? VideoToggled;
    public event Action? ScreenToggled;

    public VoiceView()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Rail;
        Visible = false;
    }

    public void Set(string channel, string guild, IEnumerable<Tile> tiles, bool muted, bool deaf,
                    bool videoOn = false, bool screenOn = false)
    {
        _channel = channel;
        _guild = guild;
        _muted = muted;
        _deaf = deaf;
        _videoOn = videoOn;
        _screenOn = screenOn;
        _tiles.Clear();
        _tiles.AddRange(tiles);
        // Frames for users no longer in the call — or shares that have ended — linger otherwise.
        foreach (var uid in _frames.Keys.ToList())
            if (!_tiles.Any(t => t.UserId == uid && !t.Screen)) _frames.Remove(uid);
        foreach (var uid in _screens.Keys.ToList())
            if (!_tiles.Any(t => t.UserId == uid && t.Screen)) _screens.Remove(uid);
        Invalidate();
    }

    public void SetVideoFrame(ulong userId, byte[]? jpeg)
    {
        if (jpeg == null) _frames.Remove(userId);
        else _frames[userId] = jpeg;
        Invalidate();
    }

    public void SetScreenFrame(ulong userId, byte[]? jpeg)
    {
        if (jpeg == null) _screens.Remove(userId);
        else _screens[userId] = jpeg;
        Invalidate();
    }


    // ── control bar ─────────────────────────────────────────────────────────────────────────────
    // Camera and screenshare are separate toggles exactly like Discord: the camera button drives
    // the webcam, the monitor button shares the screen.
    int BtnD => Ui.S(48);
    int BarY => Height - Ui.S(88);

    int BtnCount => 6;                  // chat, mic, camera, screenshare, headset, disconnect
    int BarW => BtnCount * BtnD + (BtnCount - 1) * Ui.S(12);

    Rectangle BtnRect(int i) =>
        new((Width - BarW) / 2 + i * (BtnD + Ui.S(12)), BarY, BtnD, BtnD);

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int h = -1;
        for (int i = 0; i < BtnCount; i++) if (BtnRect(i).Contains(e.Location)) { h = i; break; }
        if (h != _hot)
        {
            _hot = h;
            Tip.Show(this, h < 0 ? null : h switch
            {
                0 => "Open Chat",
                1 => _muted ? "Unmute" : "Mute",
                2 => _videoOn ? "Stop Video" : "Start Video",
                3 => _screenOn ? "Stop Sharing Screen" : "Share Screen",
                4 => _deaf ? "Undeafen" : "Deafen",
                _ => "Disconnect",
            }, h < 0 ? Rectangle.Empty : BtnRect(h));
            Invalidate();
        }
        Cursor = h >= 0 ? Cursors.Hand : Cursors.Default;
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        if (_hot != -1) { _hot = -1; Tip.Hide(); Invalidate(); }
        base.OnMouseLeave(e);
    }

    Rectangle[] _tileRects = Array.Empty<Rectangle>();

    /// Right-click on a participant — the per-user volume menu.
    public event Action<ulong, Point>? TileMenu;

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Right)
        {
            for (int i = 0; i < _tiles.Count && i < _tileRects.Length; i++)
                if (!_tiles[i].Screen && _tileRects[i].Contains(e.Location))
                {
                    TileMenu?.Invoke(_tiles[i].UserId, PointToScreen(e.Location));
                    return;
                }
            return;
        }
        if (e.Button != MouseButtons.Left || _hot < 0) return;
        switch (_hot)
        {
            case 0: ChatRequested?.Invoke(); break;
            case 1: MuteToggled?.Invoke(); break;
            case 2: VideoToggled?.Invoke(); break;
            case 3: ScreenToggled?.Invoke(); break;
            case 4: DeafenToggled?.Invoke(); break;
            default: Disconnect?.Invoke(); break;
        }
    }

    // ── paint ───────────────────────────────────────────────────────────────────────────────────

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Ui.Fill(g, ClientRectangle, Theme.Rail);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        Ui.Text(g, _channel, Theme.H3, new Rectangle(Ui.S(24), Ui.S(20), Width - Ui.S(48), Ui.S(24)),
                Theme.Strong, TextFormatFlags.HorizontalCenter);
        Ui.Text(g, _guild, Theme.Small, new Rectangle(Ui.S(24), Ui.S(44), Width - Ui.S(48), Ui.S(18)),
                Theme.Faint, TextFormatFlags.HorizontalCenter);

        PaintTiles(g);
        PaintBar(g);
    }

    // A grid that keeps 16:9 tiles: pick the column count that maximises tile size for the space,
    // which is what stops two people looking like postage stamps in a wide window.
    void PaintTiles(Graphics g)
    {
        // Our own camera / screen share goes in OUR tile in the grid, exactly like everyone
        // else's does — Session feeds it through SetVideoFrame keyed on our user id, so there is
        // no separate preview box to lay out around.
        if (_tiles.Count == 0)
        {
            Ui.Text(g, "Nobody else is here yet.", Theme.Body,
                    new Rectangle(0, Height / 2 - Ui.S(12), Width, Ui.S(24)), Theme.Muted,
                    TextFormatFlags.HorizontalCenter);
            return;
        }

        int top = Ui.S(80), bottom = BarY - Ui.S(20), gap = Ui.S(12);
        int availW = Width - Ui.S(48);
        int availH = Math.Max(Ui.S(80), bottom - top);

        int bestCols = 1, bestW = 0;
        for (int cols = 1; cols <= _tiles.Count; cols++)
        {
            int rows = (_tiles.Count + cols - 1) / cols;
            int w = (availW - (cols - 1) * gap) / cols;
            int h = (availH - (rows - 1) * gap) / rows;
            w = Math.Min(w, h * 16 / 9);
            if (w > bestW) { bestW = w; bestCols = cols; }
        }

        int tw = bestW, th = tw * 9 / 16;
        int totalRows = (_tiles.Count + bestCols - 1) / bestCols;
        int gridW = bestCols * tw + (bestCols - 1) * gap;
        int gridH = totalRows * th + (totalRows - 1) * gap;
        int ox = (Width - gridW) / 2, oy = top + Math.Max(0, (availH - gridH) / 2);

        // Rebuilt every paint so the right-click hit test always matches what is on screen.
        if (_tileRects.Length < _tiles.Count) _tileRects = new Rectangle[_tiles.Count];
        for (int i = 0; i < _tiles.Count; i++)
        {
            int c = i % bestCols, r = i / bestCols;
            var box = new Rectangle(ox + c * (tw + gap), oy + r * (th + gap), tw, th);
            _tileRects[i] = box;
            PaintTile(g, _tiles[i], box);
        }
    }

    void PaintTile(Graphics g, Tile t, Rectangle box)
    {
        Ui.FillRound(g, box, Ui.S(8), t.Screen ? Color.Black : Theme.Chat);

        // Discord rings the whole tile green while that person is talking. A screen share has no
        // voice of its own, so only the member tile lights up.
        bool talking = !t.Screen && _speaking.Contains(t.UserId);
        if (talking)
        {
            using var pen = new Pen(Theme.Positive, Ui.S(2));
            using var path = Ui.RoundRect(Rectangle.Inflate(new Rectangle(box.X, box.Y, box.Width - 1, box.Height - 1),
                                                            -Ui.S(1), -Ui.S(1)), Ui.S(8));
            g.DrawPath(pen, path);
        }

        // A member with the camera on shows their live frame instead of the avatar; a screen tile
        // shows the share. A camera fills the tile (cropping the edges of a face is fine); a
        // screen must NOT be cropped — losing the edges of someone's desktop loses the content —
        // so it is letterboxed at its own aspect ratio, which is what Discord does too.
        var src = t.Screen ? _screens : _frames;
        if (src.TryGetValue(t.UserId, out var jpeg) && jpeg != null)
        {
            var inner = Rectangle.Inflate(box, -Ui.S(3), -Ui.S(3));
            PaintClipped(g, inner, Ui.S(6), () =>
            {
                using var img = LoadJpeg(jpeg);
                if (img == null) return;
                if (t.Screen) PaintContain(g, img, inner);
                else PaintCover(g, img, inner);
            });
        }
        else if (t.Screen)
        {
            Ui.Text(g, "Waiting for stream…", Theme.Body, box, Theme.Muted,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
        else
        {
            int av = Math.Min(Ui.S(80), Math.Min(box.Width, box.Height) / 2);
            var ab = new Rectangle(box.X + (box.Width - av) / 2, box.Y + (box.Height - av) / 2, av, av);
            var img = Media.Get(t.Avatar, this);
            // Someone who has not picked up yet is faded, with the status underneath — the same
            // "waiting on them" treatment the real client gives an unanswered tile.
            if (t.Pending && img != null) Ui.AvatarDim(g, img, ab, Theme.Surface, 0.4f);
            else Ui.Avatar(g, img, ab, Theme.Surface, this);
            if (t.Pending)
                Ui.Text(g, "Ringing…", Theme.Small,
                        new Rectangle(box.X, ab.Bottom + Ui.S(10), box.Width, Ui.S(18)), Theme.Muted,
                        TextFormatFlags.HorizontalCenter);
        }

        // Name pill, bottom-left, with the muted badge Discord puts beside the name.
        var sz = Ui.Measure(t.Name, Theme.SmallMedium);
        bool badge = t.Muted || t.Deafened;
        int bw = Ui.S(14);
        int pw = sz.Width + Ui.S(16) + (badge ? bw + Ui.S(4) : 0);
        var pill = new Rectangle(box.X + Ui.S(8), box.Bottom - Ui.S(8) - Ui.S(22),
                                 Math.Min(pw, box.Width - Ui.S(16)), Ui.S(22));
        Ui.FillRound(g, pill, Ui.S(4), Color.FromArgb(160, 0, 0, 0));

        int tx = pill.X + Ui.S(8);
        if (badge)
        {
            Svg.SvgFill(g, t.Deafened ? Icons.HeadsetMutedLine : Icons.MicMutedLine,
                        new RectangleF(tx, pill.Y + (pill.Height - bw) / 2f, bw, bw), Theme.Danger);
            tx += bw + Ui.S(4);
        }
        Ui.Text(g, t.Name, Theme.SmallMedium, new Rectangle(tx, pill.Y, pill.Right - tx - Ui.S(8), pill.Height),
                Color.White, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

        // The LIVE badge belongs on the share itself, not on the person's own tile.
        if (t.Screen)
            Ui.Text(g, "LIVE", Theme.SmallMedium,
                    new Rectangle(box.Right - Ui.S(52), box.Y + Ui.S(8), Ui.S(44), Ui.S(18)),
                    Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    /// Fit the whole image inside the box, letterboxed — nothing cropped. A screen share must not
    /// lose its edges to a 16:9 tile: a 16:10 or ultrawide desktop would have its taskbar and
    /// window chrome cut off, which is the part you are usually sharing.
    static void PaintContain(Graphics g, Image img, Rectangle box)
    {
        float ia = img.Width / (float)img.Height, ba = box.Width / (float)box.Height;
        int w = ia > ba ? box.Width : (int)(box.Height * ia);
        int h = ia > ba ? (int)(box.Width / ia) : box.Height;
        g.DrawImage(img, new Rectangle(box.X + (box.Width - w) / 2, box.Y + (box.Height - h) / 2, w, h));
    }

    // A JPEG byte[] -> Image, or null. new Bitmap (not Image.FromStream) decodes eagerly so the
    // MemoryStream can die here; FromStream keeps it alive until the first DrawImage, which would
    // make the first paint throw once the stream is gone.
    static System.Drawing.Image? LoadJpeg(byte[] jpeg)
    {
        try
        {
            using var ms = new System.IO.MemoryStream(jpeg);
            return new System.Drawing.Bitmap(ms);
        }
        catch { return null; }
    }

    static void PaintCover(Graphics g, Image img, Rectangle box)
    {
        float ia = img.Width / (float)img.Height, ba = box.Width / (float)box.Height;
        Rectangle src;
        if (ia > ba)
        {
            int w = (int)(img.Height * ba);
            src = new Rectangle((img.Width - w) / 2, 0, w, img.Height);
        }
        else
        {
            int h = (int)(img.Width / ba);
            src = new Rectangle(0, (img.Height - h) / 2, img.Width, h);
        }
        g.DrawImage(img, box, src, GraphicsUnit.Pixel);
    }

    static void PaintClipped(Graphics g, Rectangle r, int radius, Action paint)
    {
        using var path = Ui.RoundRect(r, radius);
        var old = g.Clip;
        g.SetClip(path, System.Drawing.Drawing2D.CombineMode.Replace);
        try { paint(); }
        finally { g.Clip = old; }
    }

    void PaintBar(Graphics g)
    {
        for (int i = 0; i < BtnCount; i++)
        {
            var r = BtnRect(i);
            bool danger = i == 5;
            bool on = (i == 1 && _muted) || (i == 2 && _videoOn) || (i == 3 && _screenOn) || (i == 4 && _deaf);
            var fill = danger ? (_hot == i ? Color.FromArgb(166, 47, 52) : Theme.Danger)
                     : on && (i == 2 || i == 3) ? Theme.Positive
                     : on ? Theme.Danger
                     : _hot == i ? Theme.SurfaceHigh : Theme.Surface;
            Ui.FillRound(g, r, r.Width / 2, fill);

            string icon = i switch
            {
                0 => Icons.Hash,
                1 => _muted ? Icons.MicMutedLine : Icons.MicLine,
                2 => Icons.VideoLine,
                3 => Icons.MonitorLine,
                4 => _deaf ? Icons.HeadsetMutedLine : Icons.HeadsetLine,
                _ => Icons.PhoneLine,
            };
            Icons.Draw(g, icon, Rectangle.Inflate(r, -Ui.S(14), -Ui.S(14)), Color.White, 1.8f);
        }
    }
}
