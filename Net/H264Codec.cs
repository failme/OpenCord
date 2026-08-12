using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace ClaudeScord;

// NV12 <-> RGB conversions and the NV12->JPEG encode used by both the webcam capture (display
// preview + our JPEG transport between two ClaudeScord clients) and the H264 decode path (peer
// frames land as NV12 and are turned into JPEG for the tile UI). BT.601, the colour matrix the
// video MFTs and Discord's encoders use for 4:2:0 video.
static class Nv12
{
    /// NV12 -> JPEG via a GDI+ 24bpp bitmap. The UV plane is 2x2 subsampled, rows 16-aligned in
    /// the buffer, so the stride must come from the buffer width, not the logical one. The MS
    /// H.264 decoder additionally pads the HEIGHT to a 16-row multiple (360 -> 368), so the UV
    /// plane offset is stride * paddedHeight — derived from the buffer length here, since the
    /// caller only knows the logical height. Reading UV at stride*h would shift the colour plane
    /// up by the pad rows and tint every frame green/magenta.
    public static byte[]? ToJpeg(byte[] nv12, int w, int h, int quality)
    {
        try
        {
            int stride = (w + 15) & ~15;
            int paddedH = nv12.Length / (stride + stride / 2);   // Y + half-height UV rows
            var rgb = ToRgb(nv12, w, h, stride, paddedH);
            using var bmp = new Bitmap(w, h, 3 * w, PixelFormat.Format24bppRgb, Marshal.UnsafeAddrOfPinnedArrayElement(rgb, 0));
            using var ms = new MemoryStream(65536);
            var codec = ImageCodecInfo.GetImageEncoders().FirstOrDefault(c => c.FormatID == ImageFormat.Jpeg.Guid);
            if (codec != null)
            {
                using var prm = new EncoderParameters(1);
                prm.Param[0] = new EncoderParameter(Encoder.Quality, quality);
                bmp.Save(ms, codec, prm);
            }
            else bmp.Save(ms, ImageFormat.Jpeg);
            return ms.ToArray();
        }
        catch { return null; }
    }

    /// RGB24 (tight stride) -> JPEG, for the self-preview tile from the screen-share source.
    public static byte[]? RgbToJpeg(byte[] rgb, int w, int h, int quality)
    {
        try
        {
            using var bmp = new Bitmap(w, h, w * 3, PixelFormat.Format24bppRgb,
                                       Marshal.UnsafeAddrOfPinnedArrayElement(rgb, 0));
            using var ms = new MemoryStream(65536);
            var codec = ImageCodecInfo.GetImageEncoders().FirstOrDefault(c => c.FormatID == ImageFormat.Jpeg.Guid);
            if (codec != null)
            {
                using var prm = new EncoderParameters(1);
                prm.Param[0] = new EncoderParameter(Encoder.Quality, quality);
                bmp.Save(ms, codec, prm);
            }
            else bmp.Save(ms, ImageFormat.Jpeg);
            return ms.ToArray();
        }
        catch { return null; }
    }

    /// NV12 -> packed 24bpp in **GDI+ byte order, which is B,G,R** — not R,G,B. Every consumer
    /// here wraps the result in a `PixelFormat.Format24bppRgb` Bitmap, and despite the name that
    /// format stores blue first. Writing R,G,B swapped red and blue in every rendered frame: our
    /// own preview and the peer's tile both came out wrong while the video we SENT (raw NV12
    /// straight into the encoder, no conversion) looked perfect on a real client.
    ///
    /// Y in [0,255] is expanded to [16,235] TV range, U/V centred on 128. BT.709 coefficients
    /// (Kr=0.2126, Kb=0.0722): the camera is a 1080p UVC device and peer frames decode at 720p —
    /// both HD, where BT.709 is the matrix the source uses.
    /// `paddedH` is the decoder's row-padded height (360 -> 368); the UV plane starts at
    /// stride*paddedH, not stride*h.
    public static byte[] ToRgb(byte[] nv12, int w, int h, int stride, int paddedH = 0)
    {
        var outp = new byte[w * h * 3];
        int yStride = stride, uvStride = stride;
        int uvOff = yStride * (paddedH > 0 ? paddedH : h);
        int po = 0;
        for (int y = 0; y < h; y++)
        {
            int rowBase = y * yStride;
            int uvRowBase = (y / 2) * uvStride;
            for (int x = 0; x < w; x++)
            {
                int yy = nv12[rowBase + x];
                int u = nv12[uvOff + uvRowBase + (x / 2) * 2] - 128;
                int v = nv12[uvOff + uvRowBase + (x / 2) * 2 + 1] - 128;
                // BT.709: c = Y*1.164 (TV range), then chroma with the 709 weights.
                int c = (yy - 16) * 298 + 128;
                int r = (c + 459 * v) >> 8;
                int g = (c - 55 * u - 136 * v) >> 8;
                int b = (c + 541 * u) >> 8;
                outp[po++] = (byte)(b < 0 ? 0 : b > 255 ? 255 : b);   // GDI+ 24bpp is B,G,R
                outp[po++] = (byte)(g < 0 ? 0 : g > 255 ? 255 : g);
                outp[po++] = (byte)(r < 0 ? 0 : r > 255 ? 255 : r);
            }
        }
        return outp;
    }

    /// Packed 24bpp (GDI+ order: **B,G,R**) -> NV12, the screen-share encode path. The bytes come
    /// straight out of a Format24bppRgb bitmap, so reading them as R,G,B swapped red and blue in
    /// everything we broadcast. BT.709 to match ToRgb — a 601 encode decoded as 709 shifts hue on
    /// its own even with the channels in the right order.
    public static byte[] FromRgb(byte[] rgb, int w, int h, int stride)
    {
        var nv = new byte[((w + 15) & ~15) * h + ((w + 15) & ~15) * h / 2];
        int yStride = (w + 15) & ~15;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 3;
                int b = rgb[i], g = rgb[i + 1], r = rgb[i + 2];
                // TV-range luma: 16 + 0.2126R + 0.7152G + 0.0722B scaled to the 219-wide span.
                int yy = 16 + ((47 * r + 157 * g + 16 * b + 128) >> 8);
                nv[y * yStride + x] = (byte)(yy < 16 ? 16 : yy > 235 ? 235 : yy);
            }
        }
        for (int y = 0; y < h / 2; y++)
        {
            for (int x = 0; x < w / 2; x++)
            {
                int r = 0, g = 0, b = 0;
                for (int dy = 0; dy < 2; dy++)
                    for (int dx = 0; dx < 2; dx++)
                    {
                        int i = ((y * 2 + dy) * w + x * 2 + dx) * 3;
                        b += rgb[i]; g += rgb[i + 1]; r += rgb[i + 2];
                    }
                // Coefficients are 8-bit scale; the >>10 also averages the 2x2 block.
                int u = (-26 * r - 87 * g + 112 * b + 512) >> 10;
                int v = (112 * r - 102 * g - 10 * b + 512) >> 10;
                int o = yStride * h + y * yStride + x * 2;
                nv[o] = (byte)Math.Clamp(u + 128, 0, 255);
                nv[o + 1] = (byte)Math.Clamp(v + 128, 0, 255);
            }
        }
        return nv;
    }
}

// H.264 encoder: Media Foundation MFT. Input NV12 frames, output Annex-B access units (start-code
// delimited). The MFT may buffer a frame or two (B-frames / look-ahead), so Encode() can return
// zero or more AUs per input; call Flush() on teardown to drain the tail.
sealed class H264Encoder : IDisposable
{
    readonly int _width, _height, _fps, _bitrate;
    readonly long _hnsPerFrame;

    // The MS H.264 encoder MFT is NOT free-threaded: created on the (STA) UI/gateway thread and
    // then driven from a camera/screen capture thread, every ProcessInput call access-violates
    // (reproduced with a plain Task.Run call — the camera never hit it because the capture
    // failed earlier, so the screenshare was the first live encode). All MFT work therefore
    // happens on ONE dedicated "codec thread" that owns the MFT: setup + probe in the
    // constructor, then one Encode/Flush at a time. Callers post a frame and block for the AUs.
    Thread? _thread;
    readonly object _gate = new();
    readonly Queue<Request> _queue = new();
    bool _setupDone, _disposed;
    bool _codecOk;                       // true once the codec thread finishes a successful probe

    sealed class Request
    {
        public byte[]? Nv12;             // null => Flush
        public bool Keyframe;            // reset the encoder so the next frame is an IDR
        public List<byte[]>? Result;
        public Exception? Error;
    }

    // ── codec-thread state: touched only on _thread ───────────────────────────────────────────
    Mf.IMFTransform? _mft;
    // Start the sample clock at 1s (not 0): some builds of the MS encoder treat a 0 timestamp as
    // "unset" and silently drop the first frames, which reads as "accepted but never emits".
    long _hns = 10_000_000;
    // The MS H.264 encoder does NOT set MFT_OUTPUT_STREAM_PROVIDES_SAMPLES (0x20) in its output
    // stream info, so the CALLER must hand it an output sample in ProcessOutput — passing NULL
    // makes it return E_INVALIDARG. It also refuses ProcessInput until ProcessOutput has been
    // exercised once (pre-drain), which is done in Setup.
    bool _provideOutput = true;
    int _lastInputHr = Mf.S_OK;          // setup self-test diagnostics (codec thread only)
    int _lastOutputHr = Mf.S_OK;
    // The SPS+PPS+IDR keyframe the setup probe produced (see Setup). Emitted exactly once,
    // prepended to the first real frame, so the remote decoder can start.
    byte[]? _pendingKeyframe;
    // First-N-call production diagnostics: pin down the "screenshare previews but sends nothing"
    // failure (encoder Ready yet zero AUs in the live call). Logs pi/po HRESULTs + AU count.
    int _diagLeft = 5;

    public bool Ready => _codecOk;
    public string? Error { get; private set; }

    // Debug hook used by --mft to see inside the codec without touching production paths.
    public bool Diag;

    public H264Encoder(int width, int height, int fps = 30, int bitrate = 900_000)
    {
        _width = width;
        _height = height;
        _fps = fps;
        _bitrate = bitrate;
        _hnsPerFrame = 10_000_000L / fps;
        // The MS H.264 encoder MFT must be created AND driven from ONE thread that has called
        // MFStartup itself (see Mf.EnsureThreadStarted): driving an instance created elsewhere
        // access-violates in ProcessInput, and a thread that never called MFStartup gets S_OK
        // from ProcessInput but never any output. The codec thread satisfies both, and it is
        // explicit STA only because COM objects created on the app's STA main thread cannot be
        // driven from MTA threads safely; every MFT call happens on this thread itself.
        _thread = new Thread(CodecThreadMain) { IsBackground = true, Name = "h264-codec" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        // Block until setup (MFT create + probe) finishes so Ready/Error are valid on return —
        // the caller logs "armed" vs "unavailable" right after construction.
        lock (_gate)
            while (!_setupDone && !_disposed) Monitor.Wait(_gate);
    }

    void CodecThreadMain()
    {
        try { Setup(); }
        catch (Exception e) { Error = e.Message; }
        lock (_gate) { _setupDone = true; Monitor.PulseAll(_gate); }
        try
        {
            while (true)
            {
                Request req;
                lock (_gate)
                {
                    while (_queue.Count == 0 && !_disposed) Monitor.Wait(_gate);
                    if (_queue.Count == 0 && _disposed) return;
                    req = _queue.Dequeue();
                }
                try
                {
                    if (req.Keyframe) { ResetKeyframe(); req.Result = new List<byte[]>(); }
                    else req.Result = req.Nv12 == null ? FlushInternal() : EncodeInternal(req.Nv12);
                }
                catch (Exception e) { req.Error = e; }
                lock (_gate) { Monitor.PulseAll(_gate); }
            }
        }
        finally
        {
            // Answer any stragglers, then release the MFT on the thread that owns it.
            lock (_gate)
            {
                while (_queue.Count > 0)
                {
                    _queue.Dequeue().Result = new List<byte[]>();
                    Monitor.PulseAll(_gate);
                }
            }
            try { if (_mft != null) Marshal.ReleaseComObject(_mft); } catch { }
            _mft = null;
        }
    }

    void Setup() => Setup(probe: true);

    // Create + configure the encoder. Runs on the codec thread. `probe` runs the self-test
    // keyframe sweep; the reset path (RequestKeyframe) skips it so the encoder's FIRST real frame
    // is a true IDR of live content — the MS encoder only ever emits a keyframe on its very first
    // frame, so probing would consume it and leave only stale probe-pattern keyframes to replay.
    void Setup(bool probe)
    {
        int width = _width, height = _height, fps = _fps, bitrate = _bitrate;
        try
        {
            // The codec thread must call MFStartup itself or the MFT never emits (see
            // Mf.EnsureThreadStarted); the process-wide refcount guard alone is not enough.
            Mf.EnsureThreadStarted();
            _mft = FindMft();
            if (_mft == null) { Error = "no H264 encoder MFT"; return; }

            // Force the low-latency configuration BEFORE the output type is set (the encoder
            // ignores these afterwards): slice encoding (no multi-frame look-ahead buffering),
            // no B-frames, CBR rate control. Without these the MS encoder buffers every frame
            // and never emits an access unit for a live stream.
            bool hasCodecApi = _mft is Mf.ICodecAPI;
            bool okLL = Mf.CodecSetInt(_mft, Mf.CodecApiLowLatency, 1);
            bool okBp = Mf.CodecSetInt(_mft, Mf.CodecApiBpictureCount, 0);
            bool okRc = Mf.CodecSetInt(_mft, Mf.CodecApiRateControlMode, Mf.eAVEncCommonRateControlMode_CBR);
            if (Diag) Console.WriteLine($"  codecapi={hasCodecApi} lowlatency={okLL} bpcount={okBp} ratecontrol={okRc}");

            // Output type first. The encoder's available output type is an empty template the
            // caller completes. Baseline profile (66) forces no B-frames, so each input frame
            // yields one access unit immediately — the low-latency choice for live video.
            Mf.IMFMediaType? outType = null;
            for (uint i = 0; outType == null; i++)
            {
                if (_mft.GetOutputAvailableType(0, i, out var t) != Mf.S_OK) break;
                t.SetGUID(Mf.MtMajorType, Mf.MediaTypeVideo);
                t.SetGUID(Mf.MtSubtype, Mf.VideoFormatH264);
                Mf.MFSetAttributeSize(t, Mf.MtFrameSize, (uint)width, (uint)height);
                Mf.MFSetAttributeRatio(t, Mf.MtFrameRate, (uint)fps, 1);
                Mf.MFSetAttributeRatio(t, Mf.MtPixelAspectRatio, 1, 1);
                t.SetUINT32(Mf.MtInterlaceMode, Mf.MFVideoInterlace_Progressive);
                t.SetUINT32(Mf.MtAvgBitrate, (uint)bitrate);
                t.SetUINT32(Mf.MtMpeg2Profile, 66);   // eAVEncH264VProfile_Baseline
                t.SetUINT32(Mf.MtMpeg2Level, 31);     // Level 3.1 — 640x360@30 fits
                // A keyframe every 2s. Without this the MS encoder produces ONE IDR ever (its
                // first frame) and every frame after it is a P-slice: a subscriber that joins
                // after that — which is EVERY real Discord client, since it only subscribes once
                // it sees our op 12 — has nothing to start its decoder from and shows a black
                // tile forever. PLI handling alone can't cover it (the SFU does not always
                // forward one, and answering it costs an encoder teardown).
                t.SetUINT32(Mf.MtMaxKeyframeSpacing, (uint)Math.Max(1, fps * 2));
                outType = t;
            }
            if (outType == null) { Error = "H264 encoder offers no output type"; return; }
            int oh = _mft.SetOutputType(0, outType, 0);
            if (oh != Mf.S_OK) { Error = $"H264 set output type failed (0x{oh:X8})"; return; }

            var inType = Mf.MakeVideoType(Mf.VideoFormatNv12, width, height, fps);
            int ih = _mft.SetInputType(0, inType, 0);
            if (ih != Mf.S_OK) { Error = $"H264 set input type failed (0x{ih:X8})"; return; }

            _mft.ProcessMessage(Mf.MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, IntPtr.Zero);
            _mft.ProcessMessage(Mf.MFT_MESSAGE_NOTIFY_START_OF_STREAM, IntPtr.Zero);

            // Does the MFT allocate output samples itself? If not (the MS encoder), we must pass
            // our own sample into every ProcessOutput call.
            _mft.GetOutputStreamInfo(0, out var osi);
            _provideOutput = (osi.dwFlags & 0x100) == 0;   // MFT_OUTPUT_STREAM_PROVIDES_SAMPLES
            // The buffer the MFT wants for THIS output type. At 720p the MS encoder asks for more
            // than a megabyte, and a short buffer fails ProcessOutput — which left the finished
            // frame stuck inside, so the next ProcessInput came back MF_E_NOTACCEPTING and the
            // encoder was wedged for its whole life. 640x360 fit, which is why only the bigger
            // screen-share and 720p paths were dead.
            if (osi.cbSize > 0) _outBytes = Math.Max(osi.cbSize, 64 * 1024);
            // Prime the output path: the encoder rejects ProcessInput until ProcessOutput has
            // been called at least once (returns NEED_MORE_INPUT; that is expected).
            Mf.ProcessOutputOne(_mft, out var prime, out _, _provideOutput);
            if (prime != IntPtr.Zero) Marshal.Release(prime);

            // Self-test: some builds of the MS H.264 encoder MFT accept input but never emit an
            // access unit (async-like behaviour). Encode a continuous run of twenty frames now —
            // a frame-based encoder only emits after its internal look-ahead fills, so a one- or
            // two-frame probe falsely declares a healthy encoder broken (this exact bug shipped:
            // the 2-frame probe reported "MFT buffering" on a machine where a 60-frame feed
            // produced output from frame one). If nothing comes back even then, declare the codec
            // unusable so the caller falls back to the JPEG transport — and name the exact
            // failure (a ProcessInput reject is a config bug, not buffering).
            //
            // The probe has a second, critical job: the MS encoder delays its FIRST access unit
            // ~16 frames and that first AU is the stream's only SPS+PPS+IDR keyframe. If we
            // discarded it (the old code did), every real frame encoded afterwards was a P-slice
            // referencing a keyframe nobody ever received — no remote decoder could ever start.
            // So the probe's keyframe AU is kept and prepended to the first real Encode() result.
            if (probe)
            {
                var probeFrame = MakeProbeFrame(width, height);
                var probeAus = new List<byte[]>();
                // EncodeInternal, NOT Encode: the public wrapper bails while _codecOk is still false
                // (that's the setup state we're in), so it would return empty for every probe frame
                // and falsely declare the codec broken. We're already on the codec thread here.
                for (int f = 0; f < 20 && probeAus.Count == 0; f++)
                    probeAus.AddRange(EncodeInternal(probeFrame));
                if (probeAus.Count == 0)
                {
                    Error = _lastInputHr != Mf.S_OK
                        ? $"H264 ProcessInput failed 0x{_lastInputHr:X8}"
                        : $"H264 encoder emitted no access units (ProcessOutput 0x{_lastOutputHr:X8})";
                    _mft = null;
                    return;
                }
                _pendingKeyframe = probeAus.FirstOrDefault(au =>
                    VideoRtp.SplitNals(au).Any(n => (n[0] & 0x1F) == 5));   // NAL type 5 = IDR
                if (_pendingKeyframe == null)
                {
                    Error = "H264 probe produced no keyframe (IDR)";
                    _mft = null;
                    return;
                }
            }
            else _pendingKeyframe = null;
            _codecOk = true;
        }
        catch (Exception e) { Error = e.GetType().Name + ": " + e.Message; }
    }

    static byte[] MakeProbeFrame(int w, int h)
    {
        var nv = new byte[((w + 15) & ~15) * h + ((w + 15) & ~15) * h / 2];
        int stride = (w + 15) & ~15;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                nv[y * stride + x] = (byte)((x * 3 + y * 5) & 0xFF);
        return nv;
    }

    static Mf.IMFTransform? FindMft()
    {
        // The Microsoft H.264 encoder MFT (CLSID_CMSH264EncoderMFT) activates reliably via
        // CoCreateInstance; hardware MFTs (QSV/NVENC/AMF) usually need a D3D device, so they are
        // only a fallback (and their IMFActivate::ActivateObject often fails without one).
        if (Mf.CoCreateInstance(Mf.ClsidH264Encoder, IntPtr.Zero, Mf.CLSCTX_INPROC_SERVER,
                                typeof(Mf.IMFTransform).GUID, out var obj) == Mf.S_OK && obj != IntPtr.Zero)
            return (Mf.IMFTransform)Marshal.GetObjectForIUnknown(obj);
        foreach (uint flags in new[] { Mf.MFT_ENUM_FLAG_ALL,
                                       Mf.MFT_ENUM_FLAG_HARDWARE | Mf.MFT_ENUM_FLAG_SYNCMFT })
        {
            int hr = Mf.MFTEnumEx2(Mf.CategoryVideoEncoder, flags,
                                   Mf.MediaTypeVideo, Mf.VideoFormatNv12,
                                   Mf.MediaTypeVideo, Mf.VideoFormatH264,
                                   out var arr, out var count);
            if (hr == Mf.S_OK && count > 0)
            {
                try
                {
                    var p = Marshal.ReadIntPtr(arr);
                    var act = (Mf.IMFActivate)Marshal.GetObjectForIUnknown(p);
                    if (act.ActivateObject(typeof(Mf.IMFTransform).GUID, out var tobj) == Mf.S_OK
                        && tobj != IntPtr.Zero)
                        return (Mf.IMFTransform)Marshal.GetObjectForIUnknown(tobj);
                }
                finally { Mf.CoTaskMemFree(arr); }
            }
        }
        return null;
    }

    /// Ask the codec thread to tear down and re-create the encoder so the NEXT encoded frame is a
    /// fresh IDR. This is the only reliable way to force a keyframe from the MS H.264 encoder MFT
    /// (ICodecAPI's CODECAPI_AVEncVideoForceKeyFrame is not exposed on it) and matches how the
    /// real client answers PLI. Fire-and-forget; a frame already queued ahead of the reset just
    /// encodes with the old encoder.
    // PLI floods (a subscriber requests a keyframe every ~1.5s while it waits) must not stack
    // resets: at most one reset is queued or running at a time, AND at most one every 3s. The
    // rebuilt MS encoder buffers ~16 frames before it emits its fresh IDR (~1s at 15fps), so a
    // flood that resets faster than that guarantees the encoder NEVER emits — the peer sees no
    // keyframe, keeps requesting, and the call stays black in both directions (exactly the
    // death spiral in the live logs: 30+ PLIs while the encoder produced 0 AUs). Callers on hot
    // paths (the UDP receive thread) should wrap the call in Task.Run — the reset blocks until
    // it completes. Returns true when a reset was actually performed.
    int _resetPending;   // 1 while a reset is queued or running
    long _lastResetTicks;   // Stopwatch ticks of the last completed reset (PLI flood guard)
    const long ResetMinIntervalTicks = 3_000_000;   // 3s at Stopwatch.Frequency (~10MHz)

    public bool RequestKeyframe()
    {
        if (Interlocked.Exchange(ref _resetPending, 1) != 0) return false;   // already resetting
        try
        {
            // Flood guard: ignore requests inside the 3s window after the last reset — the
            // encoder is still working toward its fresh IDR, and a new reset would only push
            // the first emission further out.
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (now - Interlocked.Read(ref _lastResetTicks) < ResetMinIntervalTicks) return false;
            var req = new Request { Keyframe = true };
            lock (_gate)
            {
                if (!_codecOk || _disposed) return false;
                _queue.Enqueue(req);
                Monitor.PulseAll(_gate);
                // Block until the reset completes so the caller knows the encoder is ready to
                // encode a fresh keyframe again (frames fed during the reset are dropped).
                while (req.Result == null && req.Error == null && !_disposed) Monitor.Wait(_gate);
            }
            Interlocked.Exchange(ref _lastResetTicks, System.Diagnostics.Stopwatch.GetTimestamp());
            return true;
        }
        finally { Interlocked.Exchange(ref _resetPending, 0); }
    }

    // Runs on the codec thread: rebuild the MFT so the next frame is a keyframe. The setup skips
    // the probe deliberately — the MS encoder's only keyframe is its very first frame, and the
    // probe would consume it (the constructor probe's stale keyframe is fine at stream start but
    // useless for a mid-call PLI: it would flash a synthetic pattern the decoder can't reference).
    void ResetKeyframe()
    {
        try
        {
            if (_mft != null) { Marshal.ReleaseComObject(_mft); _mft = null; }
            _codecOk = false;
            // Fresh 1s start, NOT 0: some builds of the MS encoder treat a 0 sample timestamp as
            // "unset" and silently drop those frames (the constructor's _hns starts at 10ms for
            // the same reason). Resetting to 0 made the first post-PLI frame vanish and the fresh
            // encoder looked dead for an extra frame.
            _hns = 10_000_000;
            Setup(probe: false);
            if (Diag) Console.WriteLine($"  H264 reset: ok={_codecOk} err={Error}");
        }
        catch (Exception e) { Error = e.GetType().Name + ": " + e.Message; }
    }

    /// Encode one NV12 frame; returns the access unit(s) produced (one per frame on Baseline).
    /// The very first call prepends the constructor-probe keyframe so the stream starts decodable.
    public List<byte[]> Encode(byte[] nv12)
    {
        var req = new Request { Nv12 = nv12 };
        lock (_gate)
        {
            if (!_codecOk || _disposed) return new List<byte[]>();
            _queue.Enqueue(req);
            Monitor.PulseAll(_gate);
            while (req.Result == null && req.Error == null && !_disposed) Monitor.Wait(_gate);
        }
        // A codec-thread exception used to be swallowed here — Encode returned empty and the call
        // looked "camera on, nothing sent". Surface it so the live log names the real failure.
        if (req.Error != null && _diagLeft-- > 0)
            Log.Voice($"h264 diag: Encode threw {req.Error.GetType().Name}: {req.Error.Message}");
        return req.Result ?? new List<byte[]>();
    }

    // Runs on the codec thread.
    List<byte[]> EncodeInternal(byte[] nv12)
    {
        var outp = new List<byte[]>();
        if (_mft == null)
        {
            if (_diagLeft-- > 0) Log.Voice($"h264 diag: _mft null — encoder torn down before frames");
            return outp;
        }
        var sample = Mf.MakeSample(nv12, _hns);
        sample.SetSampleDuration(_hnsPerFrame);
        _hns += _hnsPerFrame;
        int ih = _mft.ProcessInput(0, sample, 0);
        // MF_E_NOTACCEPTING means the encoder is holding finished output. Drain it and re-submit
        // the same frame: bailing out here left the MFT permanently full, which is why a 720p
        // encoder (it fills sooner than 640x360) produced zero access units for its whole life.
        if (ih == Mf.MF_E_NOTACCEPTING)
        {
            Drain(outp);
            ih = _mft.ProcessInput(0, sample, 0);
        }
        _lastInputHr = ih;
        if (ih != Mf.S_OK)
        {
            if (_diagLeft-- > 0) Log.Voice($"h264 diag: ProcessInput 0x{ih:X8} (frame {_hns / _hnsPerFrame})");
            if (Diag) Console.WriteLine($"H264 ProcessInput 0x{ih:X8}");
            return outp;
        }
        Drain(outp);
        if (_diagLeft-- > 0)
            Log.Voice($"h264 diag: thread={Environment.CurrentManagedThreadId} pi=0x{_lastInputHr:X8} " +
                      $"po=0x{_lastOutputHr:X8} aus={outp.Count} frame={_hns / _hnsPerFrame}");
        if (_pendingKeyframe != null)
        {
            if (outp.Count > 0) outp.Insert(0, _pendingKeyframe);
            _pendingKeyframe = null;
        }
        return outp;
    }

    uint _outBytes = 1024 * 1024;   // ProcessOutput buffer, sized from MFT_OUTPUT_STREAM_INFO

    void Drain(List<byte[]> outp)
    {
        for (int guard = 0; guard < 200; guard++)
        {
            int hr = Mf.ProcessOutputOne(_mft!, out var pSample, out var status, _provideOutput, _outBytes);
            _lastOutputHr = hr;
            if (Diag) Console.WriteLine($"  H264 ProcessOutput 0x{hr:X8} sample={pSample} status={status}");
            if (hr == Mf.S_OK)
            {
                if (pSample != IntPtr.Zero)
                {
                    var s = (Mf.IMFSample)Marshal.GetObjectForIUnknown(pSample);
                    var bytes = Mf.SampleBytes(s);
                    if (bytes != null && bytes.Length > 0) outp.Add(bytes);
                    Marshal.Release(pSample);
                }
                else if ((status & Mf.MFT_OUTPUT_DATA_BUFFER_NO_SAMPLE) != 0) break;
                continue;
            }
            if (hr == Mf.MF_E_TRANSFORM_NEED_MORE_INPUT) break;
            if (hr == Mf.MF_E_TRANSFORM_STREAM_CHANGE)
            {
                // Output type changed (e.g. after the first frame negotiated a level); re-apply.
                if (_mft.GetOutputCurrentType(0, out var mt) == Mf.S_OK)
                    _mft.SetOutputType(0, mt, 0);
                if (_mft.GetOutputStreamInfo(0, out var osi2) == Mf.S_OK && osi2.cbSize > 0)
                    _outBytes = Math.Max(osi2.cbSize, 64 * 1024);
                continue;
            }
            break;
        }
    }

    /// Flush the encoder's tail and return whatever remains.
    public List<byte[]> Flush()
    {
        var req = new Request { Nv12 = null };
        lock (_gate)
        {
            if (!_codecOk || _disposed) return new List<byte[]>();
            _queue.Enqueue(req);
            Monitor.PulseAll(_gate);
            while (req.Result == null && req.Error == null && !_disposed) Monitor.Wait(_gate);
        }
        return req.Result ?? new List<byte[]>();
    }

    // Runs on the codec thread.
    List<byte[]> FlushInternal()
    {
        var outp = new List<byte[]>();
        if (_mft != null)
        {
            _mft.ProcessMessage(Mf.MFT_MESSAGE_COMMAND_DRAIN, IntPtr.Zero);
            Drain(outp);
        }
        return outp;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            Monitor.PulseAll(_gate);
        }
        _thread?.Join(3000);
    }
}

// H.264 decoder: Media Foundation MFT. Input access units (Annex-B), output NV12 frames. The
// decoder MFT switches output type when SPS/PPS arrive (it learns resolution there), so the
// stream-change path re-applies the current output type. One input AU may produce one frame.
//
// Threading: the MFT lives on ONE dedicated thread, exactly like H264Encoder. Its only caller is
// UdpVoice's receive loop, which is an `async` method — every `await ReceiveAsync` resumes on
// whatever threadpool thread the scheduler picks, so the decoder used to be CREATED on one thread
// and DRIVEN from a rotating set of others. Mf.EnsureThreadStarted is [ThreadStatic], so those
// other threads never called MFStartup, which is the documented "ProcessInput returns S_OK and
// ProcessOutput never yields a frame" failure: the peer's camera decrypted fine and then vanished
// with nothing in the log. Callers post an AU and block for the frames.
sealed class H264Decoder : IDisposable
{
    Mf.IMFTransform? _mft;                   // codec-thread only
    Thread? _thread;
    readonly object _gate = new();
    readonly Queue<Req> _queue = new();
    bool _setupDone, _disposed, _codecOk;

    sealed class Req
    {
        public byte[]? Au;
        public List<byte[]>? Result;
    }

    public bool Ready => _codecOk;
    public string? Error { get; private set; }
    public int Width { get; private set; } = 640;
    public int Height { get; private set; } = 360;

    // Debug hook used by --mft / --selftest to see inside the codec.
    public bool Diag;

    public H264Decoder()
    {
        _thread = new Thread(CodecThreadMain) { IsBackground = true, Name = "h264-decode" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        lock (_gate)
            while (!_setupDone && !_disposed) Monitor.Wait(_gate);
    }

    void CodecThreadMain()
    {
        try { Setup(); }
        catch (Exception e) { Error = e.Message; }
        // PulseAll, never Pulse: the codec thread and every blocked caller wait on the same
        // monitor, so a single Pulse can wake the wrong one and lose the wakeup for good.
        lock (_gate) { _setupDone = true; Monitor.PulseAll(_gate); }
        try
        {
            while (true)
            {
                Req req;
                lock (_gate)
                {
                    while (_queue.Count == 0 && !_disposed) Monitor.Wait(_gate);
                    if (_queue.Count == 0 && _disposed) return;
                    req = _queue.Dequeue();
                }
                var outp = new List<byte[]>();
                try { DecodeInternal(req.Au!, outp); } catch { }
                lock (_gate) { req.Result = outp; Monitor.PulseAll(_gate); }
            }
        }
        finally
        {
            lock (_gate)
            {
                while (_queue.Count > 0) _queue.Dequeue().Result = new List<byte[]>();
                Monitor.PulseAll(_gate);
            }
            try { if (_mft != null) Marshal.ReleaseComObject(_mft); } catch { }
            _mft = null;
        }
    }

    void Setup()
    {
        try
        {
            // The codec thread must call MFStartup itself or the MFT accepts input and never
            // emits (same rule as the encoder — see Mf.EnsureThreadStarted).
            Mf.EnsureThreadStarted();
            _mft = FindMft();
            if (_mft == null) { Error = "no H264 decoder MFT"; return; }

            // MF_LOW_LATENCY: the MS H.264 decoder otherwise fills a reorder buffer (~30 frames)
            // before its first output — two seconds of black at 15fps on every camera toggle, and
            // a permanent lag afterwards. A live call has no B-frames to reorder anyway.
            if (_mft.GetAttributes(out var attrs) == Mf.S_OK && attrs != null)
                try { attrs.SetUINT32(Mf.CodecApiLowLatency, 1); } catch { }

            var inType = Mf.MakeVideoType(Mf.VideoFormatH264, 640, 360, 15);
            int ih = _mft.SetInputType(0, inType, 0);
            if (Diag) Console.WriteLine($"  H264Dec set in=0x{ih:X8}");
            if (ih != Mf.S_OK) { Error = "H264 set input type failed"; return; }
            var outType = Mf.MakeVideoType(Mf.VideoFormatNv12, 640, 360, 15);
            int oh = _mft.SetOutputType(0, outType, 0);
            if (Diag) Console.WriteLine($"  H264Dec set out=0x{oh:X8}");
            if (oh != Mf.S_OK) { Error = "H264 set output type failed"; return; }
            RefreshOutputSize();

            // The MS software decoder reports flags=0 in its output stream info: it does NOT
            // allocate output samples, so the caller must provide one in every ProcessOutput
            // (passing NULL makes ProcessOutput return E_INVALIDARG and no frame ever comes out).

            _mft.ProcessMessage(Mf.MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, IntPtr.Zero);
            _mft.ProcessMessage(Mf.MFT_MESSAGE_NOTIFY_START_OF_STREAM, IntPtr.Zero);
            _codecOk = true;
        }
        catch (Exception e) { Error = e.Message; }
    }

    static Mf.IMFTransform? FindMft()
    {
        // Prefer the Microsoft software H.264 decoder (CLSID_CMSH264DecoderMFT) exactly like the
        // encoder: hardware decoders (QSV/NVDEC) usually require a D3D device manager and would
        // silently accept input while never producing a frame. The software one decodes
        // standalone (verified: 83 frames from a 120-AU feed).
        if (Mf.CoCreateInstance(Mf.ClsidH264Decoder, IntPtr.Zero, Mf.CLSCTX_INPROC_SERVER,
                                typeof(Mf.IMFTransform).GUID, out var obj) == Mf.S_OK && obj != IntPtr.Zero)
            return (Mf.IMFTransform)Marshal.GetObjectForIUnknown(obj);
        Console.WriteLine("  H264Dec: CoCreateInstance(ClsidH264Decoder) FAILED, enumerating");
        foreach (uint flags in new[] { Mf.MFT_ENUM_FLAG_ALL,
                                       Mf.MFT_ENUM_FLAG_HARDWARE | Mf.MFT_ENUM_FLAG_SYNCMFT })
        {
            int hr = Mf.MFTEnumEx2(Mf.CategoryVideoDecoder, flags,
                                   Mf.MediaTypeVideo, Mf.VideoFormatH264,
                                   Mf.MediaTypeVideo, Mf.VideoFormatNv12,
                                   out var arr, out var count);
            if (hr == Mf.S_OK && count > 0)
            {
                try
                {
                    var p = Marshal.ReadIntPtr(arr);
                    var act = (Mf.IMFActivate)Marshal.GetObjectForIUnknown(p);
                    if (act.ActivateObject(typeof(Mf.IMFTransform).GUID, out var obj2) == Mf.S_OK
                        && obj2 != IntPtr.Zero)
                        return (Mf.IMFTransform)Marshal.GetObjectForIUnknown(obj2);
                }
                finally { Mf.CoTaskMemFree(arr); }
            }
        }
        return null;
    }

    // The MS H.264 decoder does NOT set MFT_OUTPUT_STREAM_PROVIDES_SAMPLES (flags=0), so the
    // caller must hand it an output sample in ProcessOutput — exactly like the encoder. The old
    // Drain passed NULL, which made ProcessOutput return E_INVALIDARG and every decoded frame
    // was silently dropped (the decoder reported 0 frames even for a valid keyframe AU).
    bool _provideOutput = true;
    // One 100ns tick per 15fps frame, starting at 1s like the encoder. The decoder MFT is NOT
    // safe to drive on the same thread as an actively-encoding H264Encoder MFT: interleaving
    // Encode()/Decode() calls makes every Drain return MF_E_TRANSFORM_NEED_MORE_INPUT forever
    // (0 frames from 121 AUs, verified in MftDebug), while the same feed driven non-interleaved
    // — encode all, then decode, which is exactly how production separates the camera thread
    // from the UDP receive thread — decodes ~80. Keep the two codecs on separate threads.
    long _hns = 10_000_000;
    long _hnsPerFrame = 666_667;   // 15 fps, matching the encoder's cadence

    /// Feed one access unit; returns any decoded NV12 frames (usually one, possibly zero for a
    /// parameter-set-only AU). Posts to the codec thread and blocks for the result.
    public List<byte[]> Decode(byte[] au)
    {
        if (au.Length == 0) return new List<byte[]>();
        var req = new Req { Au = au };
        lock (_gate)
        {
            if (!_codecOk || _disposed) return new List<byte[]>();
            _queue.Enqueue(req);
            Monitor.PulseAll(_gate);
            while (req.Result == null && !_disposed) Monitor.Wait(_gate);
        }
        return req.Result ?? new List<byte[]>();
    }

    // Runs on the codec thread.
    void DecodeInternal(byte[] au, List<byte[]> outp)
    {
        if (_mft == null) return;
        // Match the verified raw-drive sequence exactly: NO SetSampleDuration (the MS decoder
        // behaves differently with a duration set — the working drive omits it), timestamps
        // starting at 1s.
        var sample = Mf.MakeSample(au, _hns);
        _hns += _hnsPerFrame;
        int hr = _mft.ProcessInput(0, sample, 0);
        if (hr == Mf.MF_E_NOTACCEPTING) { Drain(outp); hr = _mft.ProcessInput(0, sample, 0); }
        if (Diag) Console.WriteLine($"  H264Dec ProcessInput 0x{hr:X8} len={au.Length}");
        if (hr != Mf.S_OK && hr != Mf.MF_E_TRANSFORM_NEED_MORE_INPUT) return;
        Drain(outp);
    }

    // The output buffer the MFT needs for the CURRENT output type. Re-read on every stream change:
    // it is 0.34MB at 640x360 and 1.32MB at 720p, and a short buffer fails ProcessOutput on every
    // single frame.
    uint _outBytes = 1024 * 1024;

    void RefreshOutputSize()
    {
        if (_mft == null) return;
        if (_mft.GetOutputStreamInfo(0, out var osi) == Mf.S_OK && osi.cbSize > 0)
            _outBytes = osi.cbSize;
    }

    /// Adopt whichever output type the decoder now offers, preferring NV12 (what Nv12.ToJpeg
    /// expects), and pick up the resolution + buffer size that come with it.
    void Renegotiate()
    {
        if (_mft == null) return;
        Mf.IMFMediaType? chosen = null;
        for (uint i = 0; i < 16; i++)
        {
            if (_mft.GetOutputAvailableType(0, i, out var t) != Mf.S_OK || t == null) break;
            chosen ??= t;
            if (t.GetGUID(Mf.MtSubtype, out var sub) == Mf.S_OK && sub == Mf.VideoFormatNv12)
            { chosen = t; break; }
        }
        if (chosen == null) return;
        if (_mft.SetOutputType(0, chosen, 0) != Mf.S_OK) return;
        if (Mf.MFGetAttributeSize(chosen, Mf.MtFrameSize, out var fw, out var fh) == Mf.S_OK
            && fw != 0 && fh != 0) { Width = (int)fw; Height = (int)fh; }
        RefreshOutputSize();
    }

    void Drain(List<byte[]> outp)
    {
        for (int guard = 0; guard < 200; guard++)
        {
            int hr = Mf.ProcessOutputOne(_mft!, out var pSample, out var status, _provideOutput, _outBytes);
            if (Diag) Console.WriteLine($"  H264Dec Drain 0x{hr:X8} sample={pSample} status={status}");
            if (hr == Mf.S_OK)
            {
                if (pSample != IntPtr.Zero)
                {
                    var s = (Mf.IMFSample)Marshal.GetObjectForIUnknown(pSample);
                    var bytes = Mf.SampleBytes(s);
                    if (bytes != null && bytes.Length > 0) outp.Add(bytes);
                    Marshal.Release(pSample);
                }
                else if ((status & Mf.MFT_OUTPUT_DATA_BUFFER_NO_SAMPLE) != 0) break;
                continue;
            }
            if (hr == Mf.MF_E_TRANSFORM_NEED_MORE_INPUT) break;
            if (hr == Mf.MF_E_TRANSFORM_STREAM_CHANGE || hr == Mf.MF_E_TRANSFORM_TYPE_NOT_SET)
            {
                // The SPS told the decoder the stream's real resolution, so it dropped the output
                // type we guessed at construction. Both HRESULTs mean the same thing here: pick a
                // fresh output type. Handling only STREAM_CHANGE left a 720p peer decoding nothing
                // at all, because this decoder answers TYPE_NOT_SET instead.
                Renegotiate();
                continue;
            }
            // Anything else is a real failure and used to end the drain in silence — which is
            // exactly how an undersized output buffer read as "the peer's camera just doesn't
            // work". Name it once so the next log says which HRESULT.
            LastDrainError ??= $"0x{hr:X8}";
            break;
        }
    }

    /// First non-recoverable ProcessOutput HRESULT, for the live log.
    public string? LastDrainError { get; private set; }

    public void Dispose()
    {
        lock (_gate) { _disposed = true; Monitor.PulseAll(_gate); }
        _thread?.Join(3000);
    }
}
