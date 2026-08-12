using System.Drawing;
using System.Drawing.Imaging;
using System.Net.Http;
using SkiaSharp;
using SkiaSharp.Skottie;

namespace ClaudeScord;

// Lottie stickers, rasterised.
//
// Discord's default sticker packs are Lottie — vector animations shipped as JSON — and there is no
// raster form on the CDN at all: .png, .gif and .webp all 404 for them, with and without
// ?passthrough=false. So a client either renders Lottie or shows those stickers as nothing.
//
// Rendering is Skottie's job, not ours. A correct-enough renderer written by hand would have to
// resolve precomps, layer parenting chains, keyframed transforms, shape groups, trim paths and
// masks — the one sticker this was tested against has 37 parented layers — and the failure mode of
// getting it subtly wrong is a sticker that looks broken rather than one obviously missing.
//
// Frames are rendered *on demand*, not cached. The obvious design — rasterise every frame once into
// a sheet — costs ~5MB per sticker at 24 frames of 240px ARGB, so a scrolled sticker picker would
// run to hundreds of megabytes. Holding the parsed Animation instead and drawing the current frame
// each paint makes the cost O(1) in memory and a few hundred microseconds of CPU per visible
// sticker, which is the right trade for a client whose whole pitch is a small footprint.
static class Lottie
{
    /// Parsed animations, keyed by url. Small next to a frame sheet, but not free — a Lottie JSON is
    /// tens to hundreds of KB — so the least recently drawn are dropped past a cap.
    const int MaxAnimations = 48;

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    static readonly Dictionary<string, Entry?> _cache = new();
    static readonly HashSet<string> _inflight = new();
    static long _clock;

    sealed class Entry
    {
        public required Animation Anim;
        public long Used;
    }

    /// Draw the sticker at `url` into `box`, or return false while it is still loading.
    ///
    /// Because this is called *from* a paint, calling it is also the signal that the sticker is on
    /// screen: the playback tick repaints only what was drawn on the last frame and stops on its own
    /// once nothing asks any more. No separate visibility bookkeeping, and a sticker scrolled out of
    /// view stops costing anything.
    public static bool Draw(Graphics g, string url, Rectangle box, Control repaint)
    {
        Entry? e;
        lock (_cache)
        {
            if (!_cache.TryGetValue(url, out e))
            {
                if (_inflight.Add(url)) _ = Load(url, repaint);
                return false;
            }
            if (e == null) return false;          // tried and could not be rendered
            e.Used = ++_clock;
        }

        var frame = Render(e.Anim, box.Width, box.Height);
        if (frame == null) return false;
        g.DrawImage(frame, box);
        WantFrame(repaint, box);
        return true;
    }

    // One scratch bitmap, reused: the frame is drawn and handed straight to GDI+ within the same
    // paint, so there is never a second live frame to keep.
    static Bitmap? _scratch;
    static SKSurface? _surface;
    static SKImageInfo _info;

    static Bitmap? Render(Animation anim, int w, int h)
    {
        if (w <= 0 || h <= 0) return null;
        if (_scratch == null || _scratch.Width != w || _scratch.Height != h)
        {
            _scratch?.Dispose();
            _surface?.Dispose();
            _scratch = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
            _info = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
            _surface = SKSurface.Create(_info);
        }
        if (_surface == null) return null;

        // Every copy of a sticker plays off the same wall clock, so duplicates stay in step.
        double loop = Math.Max(0.1, anim.Duration.TotalSeconds);
        anim.SeekFrameTime(DateTime.UtcNow.TimeOfDay.TotalSeconds % loop);
        _surface.Canvas.Clear(SKColors.Transparent);
        anim.Render(_surface.Canvas, SKRect.Create(w, h));

        // Skia's pixels are BGRA premultiplied, which is exactly GDI+'s Format32bppPArgb — a straight
        // row copy rather than an encode/decode round trip through PNG.
        var locked = _scratch.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly,
                                       PixelFormat.Format32bppPArgb);
        try { _surface.ReadPixels(_info, locked.Scan0, locked.Stride, 0, 0); }
        finally { _scratch.UnlockBits(locked); }
        return _scratch;
    }

    static async Task Load(string url, Control repaint)
    {
        Entry? entry = null;
        try
        {
            var json = await Http.GetByteArrayAsync(url).ConfigureAwait(false);
            using var data = SKData.CreateCopy(json);
            if (Animation.TryCreate(data, out var anim) && anim != null)
                entry = new Entry { Anim = anim };
        }
        catch (Exception e) { Log.Write("lottie", "render failed: " + e.Message); }

        lock (_cache)
        {
            _cache[url] = entry;
            _inflight.Remove(url);
            Trim();
        }

        try
        {
            if (!repaint.IsDisposed && repaint.IsHandleCreated)
                repaint.BeginInvoke(() => { if (!repaint.IsDisposed) repaint.Invalidate(); });
        }
        catch { }
    }

    // Called with the lock held. Drops the least recently drawn animations past the cap.
    static void Trim()
    {
        if (_cache.Count <= MaxAnimations) return;
        foreach (var kv in _cache.OrderBy(k => k.Value?.Used ?? 0).ToList())
        {
            if (_cache.Count <= MaxAnimations) break;
            kv.Value?.Anim.Dispose();
            _cache.Remove(kv.Key);
        }
    }

    // ── playback ────────────────────────────────────────────────────────────────────────────────
    // 15fps. These are 160px stickers; the source is 60fps but the difference is not visible at that
    // size, and this repaint lands on the message list, which is the most expensive surface here.
    static readonly System.Windows.Forms.Timer _tick = new() { Interval = 66 };
    static readonly Dictionary<Control, Rectangle> _wants = new();
    static bool _hooked;

    static void WantFrame(Control c, Rectangle area)
    {
        _wants[c] = _wants.TryGetValue(c, out var had) ? Rectangle.Union(had, area) : area;
        if (!_hooked)
        {
            _hooked = true;
            _tick.Tick += (_, _) =>
            {
                if (_wants.Count == 0) { _tick.Stop(); return; }
                var due = _wants.ToArray();
                _wants.Clear();
                foreach (var (ctl, rect) in due)
                    if (!ctl.IsDisposed && ctl.IsHandleCreated) ctl.Invalidate(rect);
            };
        }
        if (!_tick.Enabled) _tick.Start();
    }
}
