using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Net.Http;
using System.Runtime;

namespace OpenCord;

// Image cache for avatars, emoji, GIFs and attachments.
//
// The hard part is not caching, it is *giving the memory back*. A decoded image is a few dozen bytes
// of managed object in front of a native GDI+ allocation that the GC cannot see, so dropping the
// reference does nothing until a gen-2 collection happens to run the finalizer — which, on a client
// whose managed heap barely moves, is close to never. Scrolling the GIF picker once left ~150MB
// resident for the rest of the session. Three things fix that, and all three are needed:
//
//   - Eviction *disposes*, it does not just drop (see Retire for why that is safe).
//   - GC.AddMemoryPressure tells the GC what an entry really costs, so it collects on image
//     pressure rather than on managed-heap pressure that never arrives.
//   - An idle sweep drops big images a while after they were last drawn, then compacts. Closing the
//     picker gives the memory back instead of holding it against a budget that is never reached.
//
// Other things learned the hard way:
//   - Image.FromStream does not copy: the stream has to outlive the Image. Only animated images keep
//     theirs — everything else is blitted into a Bitmap we own so the encoded bytes go free.
//   - Decode big pictures down. A 4000x3000 phone photo is 48MB of ARGB for a 550px-wide message.
//
// The CDN also wants a browser User-Agent; without one some assets come back 403.
static class Media
{
    // Live budget for decoded pixels. Small because the accounting below is now honest — the old
    // model billed a GIF for every frame it holds (560MB for 20 trending GIFs that really cost 100),
    // so a "64MB" budget was thrashing at a real 12MB.
    const long Budget = 48L * 1024 * 1024;
    // Nothing in the UI draws an image wider than the chat column, so anything past this is waste.
    const int MaxEdge = 1280;
    // An image bigger than this that has not been drawn for IdleMs is dropped even when the cache is
    // under budget. Avatars and emoji are far below it and stay put, so nothing flickers.
    const long IdleBytes = 512 * 1024;
    const long IdleMs = 10_000;
    // Compact only when a sweep actually returned something worth the pause.
    const long CompactAfter = 8L * 1024 * 1024;

    static readonly HttpClient Http = new();
    static readonly Dictionary<string, Entry> _cache = new();
    static readonly HashSet<string> _inflight = new();
    static long _bytes;
    static long _clock;

    sealed class Entry
    {
        public required Image Image;
        public long Bytes;
        public long Used;              // monotonic tick, for LRU
        public long AtMs;              // wall clock of last draw, for the idle sweep
        public bool Animated;
    }

    static Media()
    {
        Http.Timeout = TimeSpan.FromSeconds(20);
        Http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
    }

    /// The cached image, or null while it loads. `repaint` is invalidated once it arrives, so a
    /// caller just draws what it gets and redraws when told.
    public static Image? Get(string? url, Control repaint)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        Housekeeping();
        lock (_cache)
        {
            // A URL that already failed must not start another request here: Get is called from
            // every paint, so a list full of dead avatars used to open a fresh 20s-timeout GET per
            // repaint — a storm of sockets and failed decodes while just hovering or typing.
            // FailedRetryMs gives it one retry window so a transient outage recovers on its own.
            if (_failed.TryGetValue(url, out var at) && Environment.TickCount64 - at < FailedRetryMs)
                return null;
            if (_cache.TryGetValue(url, out var e))
            {
                e.Used = ++_clock;
                e.AtMs = Environment.TickCount64;
                return e.Image;
            }
            if (!_inflight.Add(url)) return null;      // already downloading
        }
        _ = Load(url, repaint);
        return null;
    }

    /// True once the url has been tried and failed, so a caller can stop showing a spinner.
    public static bool Failed(string? url) => url != null && _failed.ContainsKey(url);

    /// Forget every failure so the next paint retries. Called when the gateway reconnects: a blip
    /// that killed a handful of avatar downloads otherwise left them as default avatars for the
    /// full FailedRetryMs, long after the connection came back.
    public static void RetryFailed()
    {
        lock (_cache) _failed.Clear();
    }

    // url -> TickCount64 of its last failure. Long enough that a dead CDN link stops being retried
    // on every repaint, short enough that a temporary outage (logged out of a gateway, brief DNS
    // blip) heals without a restart.
    const long FailedRetryMs = 10 * 60 * 1000;
    static readonly Dictionary<string, long> _failed = new();

    // How many images may be downloading and decoding at once.
    //
    // This is a memory control, not a politeness one. Every visible tile calls Get from the same
    // paint, so opening the emoji picker used to start ~56 loads in the same instant; each one holds
    // a socket, and each one finishes with a CPU-bound decode, so the thread pool's hill-climbing
    // injected a thread per item. Measured: 35 threads before opening the picker, 147 after, still
    // 134 a minute later — the pool retires threads slowly and their committed stacks are ~15MB of
    // private bytes that never came back. Six at a time fills the grid just as fast, because the
    // bottleneck was never the client.
    static readonly SemaphoreSlim _slots = new(6);

    static async Task Load(string url, Control repaint)
    {
        await _slots.WaitAsync().ConfigureAwait(false);
        try
        {
            var bytes = await Http.GetByteArrayAsync(url).ConfigureAwait(false);
            var (img, animated) = Decode(bytes);
            if (img != null)
                lock (_cache)
                {
                    long cost = Cost(img, animated, bytes.Length);
                    _cache[url] = new Entry
                    {
                        Image = img, Bytes = cost, Used = ++_clock,
                        AtMs = Environment.TickCount64, Animated = animated,
                    };
                    _bytes += cost;
                    _failed.Remove(url);             // a retry succeeded; let a future failure re-arm it
                    GC.AddMemoryPressure(cost);
                    Trim();
                }
        }
        catch { lock (_cache) _failed[url] = Environment.TickCount64; }   // a broken avatar must never take the client down
        finally { lock (_cache) _inflight.Remove(url); _slots.Release(); }

        try
        {
            if (!repaint.IsDisposed && repaint.IsHandleCreated)
                repaint.BeginInvoke(() => { if (!repaint.IsDisposed) repaint.Invalidate(); });
        }
        catch { }
    }

    // Animated images stay as GDI+ loaded them: ImageAnimator drives the frames and re-encoding
    // would flatten the animation, so the MemoryStream has to stay alive underneath.
    //
    // ponytail: that means the GIF picker holds full-size GIFs (~500px) to draw ~150px cells, and
    // there is no smaller variant to ask for — Discord's GIF proxy returns `preview` == `src`, and
    // the only other field is a webp GDI+ cannot decode. Cutting the peak needs our own GIF decoder
    // writing downscaled frames, which only wins if it also drops frames: at full frame count a
    // 40-frame GIF costs more as decoded 150px frames than it does as encoded bytes. Worth doing if
    // the picker's ~28MB peak ever matters; the memory does come back either way.
    //
    // Everything else is blitted into a Bitmap this cache owns, and the source plus its stream are
    // released straight away. That copy is not busywork — GDI+ holds the *encoded* bytes for the
    // life of the Image, so a 2MB JPEG avatar was costing 2MB of PNG/JPEG on top of its pixels
    // forever. It also normalises to 32bppPArgb, which is the format DrawImage blits without a
    // per-draw conversion.
    internal static (Image?, bool) DecodeFor(byte[] bytes) => Decode(bytes);   // SelfTest

    static (Image?, bool) Decode(byte[] bytes)
    {
        var stream = new MemoryStream(bytes);
        var src = Image.FromStream(stream);
        if (ImageAnimator.CanAnimate(src)) return (src, true);

        int w = src.Width, h = src.Height;
        if (w > MaxEdge || h > MaxEdge)
        {
            float s = MaxEdge / (float)Math.Max(w, h);
            w = Math.Max(1, (int)(w * s));
            h = Math.Max(1, (int)(h * s));
        }

        var copy = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(copy))
        {
            if (w == src.Width && h == src.Height)
            {
                // Exact 1:1. Source rect in pixels rather than DrawImageUnscaled, which honours the
                // image's DPI metadata and quietly rescales anything not authored at 96dpi.
                g.CompositingMode = CompositingMode.SourceCopy;
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = PixelOffsetMode.Half;
            }
            else
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            }
            g.DrawImage(src, new Rectangle(0, 0, w, h), 0, 0, src.Width, src.Height, GraphicsUnit.Pixel);
        }
        src.Dispose();
        stream.Dispose();   // nothing has seen either yet, so both are safe to release eagerly
        return (copy, false);
    }

    // What an entry actually costs this process, measured against real trending GIFs rather than
    // reasoned about (`--memtest` prints the comparison):
    //
    //   an animated GIF  ≈ its encoded bytes + about three frames' worth of pixels
    //   anything else    = its pixels, because Decode already threw the encoded bytes away
    //
    // Both of the obvious models are badly wrong. One frame under-counts a GIF ~8x; a frame per
    // frame over-counts it ~6x — GDI+ decodes frames from the retained stream on demand and keeps
    // only a couple, so 20 trending GIFs weigh 100MB, not the 560MB the old model billed. Getting
    // this wrong in either direction breaks the cache: too low and it grows without bound, too high
    // and it evicts images that are still on screen and re-downloads them on the next paint.
    internal static long CostOf(Image img, int encoded = 0) => Cost(img, ImageAnimator.CanAnimate(img), encoded);

    static long Cost(Image img, bool animated, int encoded)
    {
        long px = (long)img.Width * img.Height * 4;
        return animated ? encoded + px * 3 : px;
    }

    // Evict least-recently-drawn entries until back under budget. Called with the _cache lock held.
    static void Trim()
    {
        if (_bytes <= Budget) return;
        foreach (var kv in _cache.OrderBy(k => k.Value.Used).ToList())
        {
            if (_bytes <= Budget) break;
            Evict(kv.Key, kv.Value);
        }
    }

    // Called with the _cache lock held.
    static void Evict(string key, Entry e)
    {
        _cache.Remove(key);
        _bytes -= e.Bytes;
        GC.RemoveMemoryPressure(e.Bytes);
        Unanimate(e.Image);
        Retire(e.Image, e.Bytes);
    }

    // ── Releasing ───────────────────────────────────────────────────────────────────────────────
    // Disposing an evicted image on the spot is not safe: a paint handler may be part-way through
    // drawing it, and a disposed Image throws "Parameter is not valid" from inside OnPaint — a crash
    // whose stack points nowhere near the cause. That hazard is why this used to leak by design.
    //
    // Deferring removes it. Retired images are disposed from a WinForms timer tick, which the message
    // loop only dispatches between messages, so it can never land inside anyone's WM_PAINT. The one
    // tick of delay also covers ImageAnimator's worker, which may still be advancing a frame at the
    // moment StopAnimate is called.
    static readonly List<(Image Image, long Bytes)> _retired = new();

    static void Retire(Image img, long bytes)
    {
        lock (_retired) _retired.Add((img, bytes));
    }

    static System.Windows.Forms.Timer? _keeper;

    // Started from the first Get, which is always a paint and so always the UI thread.
    static void Housekeeping()
    {
        if (_keeper != null) return;
        _keeper = new System.Windows.Forms.Timer { Interval = 5000 };
        _keeper.Tick += (_, _) => Sweep();
        _keeper.Start();
    }

    static void Sweep()
    {
        (Image Image, long Bytes)[] due;
        lock (_retired) { due = _retired.ToArray(); _retired.Clear(); }
        long freed = 0;
        foreach (var (img, bytes) in due)
        {
            // Unanimate again, not just at eviction. A paint can hold an image across the moment a
            // background load evicts it — Get hands the reference out, Trim runs on the loader
            // thread, and the *same* paint then calls Animate and re-registers what was just
            // unregistered. Disposing after that leaves ImageAnimator advancing frames on a freed
            // GDI+ bitmap, which is an access violation in native code: no managed exception, no
            // crash.log, the process simply vanishes. Doing it here closes the window, because
            // eviction and this loop are both on the UI thread and no paint can run between them.
            Unanimate(img);
            try { img.Dispose(); } catch { }
            freed += bytes;
        }
        // Drop the references before collecting. Disposing an Image frees the native bitmap but the
        // object still roots the byte[] it was decoded from, and this array is a live local for the
        // rest of the method — so the compaction below ran with all six still reachable and reclaimed
        // 23MB of the 28. The memory only came back on the *next* tick, once this array was garbage.
        Array.Clear(due);

        // Anything big that nothing has drawn for a while: the GIF picker's contents a few seconds
        // after it closes, an attachment scrolled far out of view. This is what makes the memory come
        // back down rather than sit at the budget until the app exits. What is evicted here is only
        // *retired* — it gets disposed by the next tick, which is also the tick that compacts for it.
        long now = Environment.TickCount64;
        int dropped = 0;
        lock (_cache)
        {
            // Expired failures: once the retry window has passed, forget them so the next Get tries
            // again instead of treating the url as dead forever.
            foreach (var url in _failed.Where(kv => now - kv.Value >= FailedRetryMs).Select(kv => kv.Key).ToList())
                _failed.Remove(url);
            foreach (var kv in _cache.ToList())
                if (kv.Value.Bytes >= IdleBytes && now - kv.Value.AtMs > IdleMs)
                { Evict(kv.Key, kv.Value); dropped++; }
        }

        // Most of an animated GIF's weight turns out to be *managed*: GDI+ keeps the byte[] the
        // MemoryStream wraps, so the encoded data is on the heap, not in the native bitmap. Worth
        // knowing when reading this line — `held` is the cache's own accounting, `managed` is where
        // the GIFs actually sit.
        if (Log.Sink != null && (due.Length > 0 || dropped > 0))
            Log.Write("media", $"sweep: disposed {due.Length} ({freed >> 20}MB), idle-dropped {dropped}, "
                             + $"held {Held >> 20}MB in {Count} entries, managed {GC.GetTotalMemory(false) >> 20}MB");

        // Disposing gave the native GDI+ bitmaps back, but the encoded byte[] each animated image was
        // decoded from is a large-object allocation and the LOH is not compacted by default — that
        // alone was ~40MB of managed heap still resident after the GIF picker's images were gone.
        //
        // This deliberately keys off what was *disposed this tick*, not what was evicted. Evicting
        // only retires an image; compacting in that tick runs while every one of them is still alive
        // and reclaims nothing, which is exactly the bug the log above was added to catch.
        if (freed < CompactAfter) return;
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
    }

    internal static int Count { get { lock (_cache) return _cache.Count; } }

    /// Total decoded bytes the cache is holding — SelfTest pins the accounting, and the settings
    /// page shows it.
    internal static long Held { get { lock (_cache) return _bytes; } }

    // ── Animation ───────────────────────────────────────────────────────────────────────────────
    // ImageAnimator advances a GIF's current frame on its own thread; the control just has to ask
    // for the update and repaint. One registration per image, not per draw call, or the frame rate
    // multiplies with the number of paints. The handler is kept so the registration can be undone —
    // ImageAnimator.StopAnimate matches on the delegate, so it cannot be released without it, and
    // without releasing it ImageAnimator's own strong reference keeps every GIF alive for good.
    static readonly Dictionary<Image, EventHandler> _animating = new();

    /// Call from OnPaint before drawing `img`: selects the current frame and keeps it ticking.
    public static void Animate(Image img, Control repaint)
    {
        lock (_animating)
        {
            if (!_animating.ContainsKey(img))
            {
                EventHandler h = (_, _) =>
                {
                    try
                    {
                        if (!repaint.IsDisposed && repaint.IsHandleCreated)
                            repaint.BeginInvoke(() => { if (!repaint.IsDisposed) repaint.Invalidate(); });
                    }
                    catch { }
                };
                _animating[img] = h;
                ImageAnimator.Animate(img, h);
            }
        }
        ImageAnimator.UpdateFrames(img);
    }

    static void Unanimate(Image img)
    {
        lock (_animating)
            if (_animating.Remove(img, out var h))
                try { ImageAnimator.StopAnimate(img, h); } catch { }
    }

    public static bool IsAnimated(Image img) => ImageAnimator.CanAnimate(img);
}
