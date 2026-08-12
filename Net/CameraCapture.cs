using System.Runtime.InteropServices;
using System.Threading;

namespace ClaudeScord;

// The webcam camera: Media Foundation source reader on the first video capture device, samples
// read on a background thread, handed to a callback at ~15 fps as raw NV12 (the caller owns the
// H.264 encode AND the preview JPEG — passing JPEG here would double-encode and feed JPEG bytes
// into the H.264 encoder as if they were NV12). Same event shape as ScreenCapture so VoiceClient
// can swap the two sources without caring which is live. Uses only MF (ships with Windows), so
// the no-packaged-native rule holds.
//
// Threading: EVERY MF object (activate, media source, source reader) is created AND used on the
// capture thread. Creating the reader on the UI thread and calling ReadSample from the capture
// thread used to throw "Unable to cast COM object ... to interface type 'IMFSourceReader'"
// (E_NOINTERFACE) — a cross-apartment marshal failure, since the reader RCW lived in the UI
// thread's STA. The whole open therefore happens inside Loop(), and Start() waits for it.
sealed class CameraCapture : IDisposable
{
    readonly int _outW, _outH, _fps;
    readonly CancellationTokenSource _cts = new();
    readonly ManualResetEventSlim _openDone = new(false);
    Thread? _thread;
    bool _running, _openOk;
    Mf.IMFSourceReader? _reader;
    Mf.IMFMediaSource? _source;

    public event Action<byte[] /*nv12*/, int /*width*/, int /*height*/>? Frame;
    public event Action? Stopped;
    public string? DeviceName { get; private set; }

    public CameraCapture(int width = 640, int height = 360, int fps = 15)
    {
        _outW = width;
        _outH = height;
        _fps = fps;
    }

    /// Names of every connected video capture device (for the settings page / diagnostics).
    public static string[] DeviceNames()
    {
        var names = new List<string>();
        Mf.EnsureThreadStarted();
        try
        {
            Mf.MFCreateAttributes(out var attrs, 2);
            attrs.SetGUID(Mf.DevSourceType, Mf.DevSourceTypeVidCap);
            if (Mf.MFEnumDeviceSources(attrs, out var arr, out var count) != Mf.S_OK) return names.ToArray();
            try
            {
                for (uint i = 0; i < count; i++)
                {
                    var ptr = Marshal.ReadIntPtr(arr, (int)(i * IntPtr.Size));
                    var act = (Mf.IMFActivate)Marshal.GetObjectForIUnknown(ptr);
                    var n = Mf.DeviceName(act);
                    if (n != null) names.Add(n);
                }
            }
            finally { Mf.CoTaskMemFree(arr); }
        }
        catch { }
        return names.ToArray();
    }

    /// Open the first camera and start sampling. Returns false when no camera is present or the
    /// open fails (the caller then reverts the camera toggle).
    public bool Start()
    {
        if (_running) return true;
        _running = true;
        _thread = new Thread(Loop) { IsBackground = true, Name = "camera-capture" };
        _thread.Start();
        // Block until the capture thread finishes the open (enumeration + source reader creation
        // + first stream setup) so success/failure is known synchronously, exactly like the old
        // inline open. The old code opened on the UI thread, which is what E_NOINTERFACE'd the
        // reader when the capture thread later used it.
        if (!_openDone.Wait(8000))
        {
            Log.Voice("camera open timed out");
            _running = false;
            return false;
        }
        return _openOk;
    }

    public void Stop()
    {
        if (!_running && _reader == null) return;
        _running = false;
        _cts.Cancel();
        _thread?.Join(2000);
        try { _reader?.Flush(0); } catch { }
        try { _source?.Stop(); } catch { }
        try { _source?.Shutdown(); } catch { }
        _openDone.Dispose();
        Stopped?.Invoke();
    }

    void Loop()
    {
        // The source reader internally drives MFTs, which need this thread to have called
        // MFStartup itself (same rule as the H.264 codecs). All MF work below happens on this
        // thread: the source reader is NOT thread-safe, and driving an RCW created on the UI
        // thread's STA from here throws E_NOINTERFACE on first use.
        Mf.EnsureThreadStarted();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long next = 0;
        long lastFrameMs = -1;
        try
        {
            // ── open (on this thread) ──────────────────────────────────────────────────────────
            Mf.MFCreateAttributes(out var attrs, 2);
            attrs.SetGUID(Mf.DevSourceType, Mf.DevSourceTypeVidCap);
            // Shared (frame-server) capture: without this the open fails while another app — the
            // real Discord client — is already using the webcam (MF_E_DEVICE_IN_USE). Windows 11
            // routes shared opens through the OS camera frame server; both apps stream from it.
            attrs.SetUINT32(Mf.DevSourceShareCapture, 1);
            if (Mf.MFEnumDeviceSources(attrs, out var arr, out var count) != Mf.S_OK)
            {
                Log.Voice("camera enumeration failed");
                return;
            }
            if (count == 0)
            {
                Log.Voice("no camera device found");
                return;
            }
            try
            {
                var ptr = Marshal.ReadIntPtr(arr, 0);
                var act = (Mf.IMFActivate)Marshal.GetObjectForIUnknown(ptr);
                DeviceName = Mf.DeviceName(act);
                Log.Voice("camera device: " + (DeviceName ?? "unknown"));
                if (act.ActivateObject(typeof(Mf.IMFMediaSource).GUID, out var srcPtr) != Mf.S_OK) return;
                _source = (Mf.IMFMediaSource)Marshal.GetObjectForIUnknown(srcPtr);
            }
            finally { Mf.CoTaskMemFree(arr); }

            if (Mf.MFCreateSourceReaderFromMediaSource(_source, null!, out _reader) != Mf.S_OK) return;

            // Stream index 0, NOT MF_SOURCE_READER_FIRST_VIDEO_STREAM (0xFFFFFFFB): the sentinel
            // returns MF_E_INVALIDMEDIATYPE for every reader call on this MF build even though
            // the reader is healthy (verified: GetNativeMediaType/GetCurrentMediaType/ReadSample
            // all fail with the sentinel and all work with index 0 on a camera that has exactly
            // one selected video stream).
            _reader.SetStreamSelection(0, 1);

            // NO SetCurrentMediaType: the UVC camera's only native type is NV12 1920x1080@30,
            // and requesting NV12 640x360 fails with MF_E_INVALIDMEDIATYPE (the source reader's
            // built-in converter cannot scale here). Read at the native size and scale down in
            // software below, so the rest of the pipeline still sees 640x360 NV12.
            _reader.GetCurrentMediaType(0, out var mt);
            uint fw = 0, fh = 0;
            if (mt != null) Mf.MFGetAttributeSize(mt, Mf.MtFrameSize, out fw, out fh);
            if (fw == 0 || fh == 0) { fw = (uint)_outW * 3; fh = (uint)_outH * 3; }   // 1920x1080 default
            _openOk = true;
            _openDone.Set();
            Log.Voice($"camera streaming natively {fw}x{fh}");

            // ── read loop ──────────────────────────────────────────────────────────────────────
            while (!_cts.IsCancellationRequested)
            {
                next += 1000L / _fps;
                long delay = next - sw.ElapsedMilliseconds;
                if (delay > 0)
                {
                    try { Thread.Sleep((int)delay); } catch { }
                }

                int hr = Mf.ReadSampleRaw(_reader, 0, 0, out _, out var flags, out _, out var samplePtr);
                if (hr != Mf.S_OK || samplePtr == IntPtr.Zero)
                {
                    // Stream change (the reader switched media type) is not an error; keep going.
                    if ((flags & Mf.MF_SOURCE_READERF_CURRENTMEDIATYPE_CHANGED) != 0)
                    {
                        _reader.GetCurrentMediaType(0, out var mt2);
                        if (mt2 != null) Mf.MFGetAttributeSize(mt2, Mf.MtFrameSize, out fw, out fh);
                    }
                    continue;
                }
                // GetObjectForIUnknown AddRefs the pointer, but ReadSample already transferred
                // ownership of its reference to us — release the extra one immediately or every
                // recycled sample accumulates a reference and the reader eventually can't recycle.
                var sample = (Mf.IMFSample)Marshal.GetObjectForIUnknown(samplePtr);
                Marshal.Release(samplePtr);
                try
                {
                    // The reader can return 30 fps while we want 15: throttle by wall clock and
                    // drop the surplus (the sample is released below, so the reader queue never
                    // grows — ReadSample is pull-based and blocks until the next sample).
                    long nowMs = sw.ElapsedMilliseconds;
                    if (lastFrameMs >= 0 && nowMs - lastFrameMs < 1000L / _fps) continue;
                    lastFrameMs = nowMs;

                    var nv12 = Mf.SampleBytes(sample);
                    if (nv12 == null) continue;
                    // The reader may have switched to a different (native) frame size than the
                    // first one; re-check the current media type so scaling never mis-strides.
                    _reader.GetCurrentMediaType(0, out var mt3);
                    uint cw = fw, ch = fh;
                    if (mt3 != null) Mf.MFGetAttributeSize(mt3, Mf.MtFrameSize, out cw, out ch);
                    if (cw == 0 || ch == 0) continue;
                    if (nv12.Length < cw * ch * 3 / 2) continue;
                    if (cw != (uint)_outW || ch != (uint)_outH)
                        nv12 = ScaleNv12(nv12, (int)cw, (int)ch, _outW, _outH);
                    // Raw NV12 to the caller (VoiceClient.OnNv12Frame): it owns the H.264 encode
                    // AND the preview JPEG, so passing JPEG here would double-encode and feed
                    // JPEG bytes into the H.264 encoder as if they were NV12.
                    Frame?.Invoke(nv12, _outW, _outH);
                }
                finally
                {
                    try { Marshal.ReleaseComObject(sample); } catch { }
                }
            }
        }
        catch (Exception e)
        {
            Log.Voice("camera capture ended: " + e.Message);
        }
        _openDone.Set();
        _running = false;
    }

    /// Bilinear NV12 downscale (the encoder + preview pipeline expect 640x360; UVC cameras
    /// usually offer only their native 1080p/720p size). Y is scaled alone, UV together.
    static byte[] ScaleNv12(byte[] src, int sw, int sh, int dw, int dh)
    {
        var dst = new byte[dw * dh * 3 / 2];
        // Y plane
        for (int y = 0; y < dh; y++)
        {
            float sy = (y + 0.5f) * sh / dh - 0.5f;
            int y0 = Math.Max(0, (int)MathF.Floor(sy));
            int y1 = Math.Min(sh - 1, y0 + 1);
            float fy = sy - y0;
            for (int x = 0; x < dw; x++)
            {
                float sx = (x + 0.5f) * sw / dw - 0.5f;
                int x0 = Math.Max(0, (int)MathF.Floor(sx));
                int x1 = Math.Min(sw - 1, x0 + 1);
                float fx = sx - x0;
                float top = src[y0 * sw + x0] + (src[y0 * sw + x1] - src[y0 * sw + x0]) * fx;
                float bot = src[y1 * sw + x0] + (src[y1 * sw + x1] - src[y1 * sw + x0]) * fx;
                dst[y * dw + x] = (byte)(top + (bot - top) * fy + 0.5f);
            }
        }
        // UV plane (interleaved U,V; half resolution in both axes)
        int suw = sw / 2, suh = sh / 2, duw = dw / 2, duh = dh / 2;
        int srcUv = sw * sh, dstUv = dw * dh;
        for (int y = 0; y < duh; y++)
        {
            float sy = (y + 0.5f) * suh / duh - 0.5f;
            int y0 = Math.Max(0, (int)MathF.Floor(sy));
            int y1 = Math.Min(suh - 1, y0 + 1);
            float fy = sy - y0;
            for (int x = 0; x < duw; x++)
            {
                float sx = (x + 0.5f) * suw / duw - 0.5f;
                int x0 = Math.Max(0, (int)MathF.Floor(sx));
                int x1 = Math.Min(suw - 1, x0 + 1);
                float fx = sx - x0;
                for (int c = 0; c < 2; c++)
                {
                    int o = srcUv + (y0 * suw + x0) * 2 + c;
                    float top = src[o] + (src[o + 2] - src[o]) * fx;
                    float bot = src[o + suw * 2] + (src[o + suw * 2 + 2] - src[o + suw * 2]) * fx;
                    dst[dstUv + (y * duw + x) * 2 + c] = (byte)(top + (bot - top) * fy + 0.5f);
                }
            }
        }
        return dst;
    }

    public void Dispose() => Stop();
}
