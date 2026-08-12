using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using NAudio.Wave;

namespace ClaudeScord;

// Inline video playback for message attachments. One clip plays at a time, exactly like [[Audio]]
// — the two share that single-player shape so starting a video stops a voice message and vice
// versa, which is what the real client does.
//
// Video frames come from a Media Foundation source reader set to RGB32 (the reader inserts the
// video processor, so whatever the file stores arrives ready to blit). Audio is NAudio reading the
// same file. The audio device is the clock: frames are held until their timestamp is due, which is
// the cheapest sync that does not drift, because the sound card is the thing that cannot be paused
// by a slow repaint.
static class Video
{
    static readonly HttpClient Http = new();

    static string? _url;
    static string? _temp;
    static Mf.IMFSourceReader? _reader;
    static WaveOutEvent? _out;
    static MediaFoundationReader? _audio;
    static Thread? _pump;
    static CancellationTokenSource? _cts;
    static Bitmap? _frame;
    static readonly object _lock = new();
    static bool _loading;
    static TimeSpan _duration;

    public static string? Current => _url;
    public static bool IsPlaying => _out?.PlaybackState == PlaybackState.Playing;
    public static bool IsLoading(string url) => _loading && _url == url;
    public static TimeSpan Duration => _duration;
    public static TimeSpan Position => _audio?.CurrentTime ?? TimeSpan.Zero;

    /// Raised whenever the visible state changed — a new frame, play/pause, or teardown.
    public static event Action? Changed;

    /// Draw the current frame, letterboxed into `box` — cropping to fill would cut content.
    /// Returns false when nothing has decoded yet, so the caller can say "Loading…" instead.
    ///
    /// The drawing happens *here*, under the same lock the decode thread swaps frames with, rather
    /// than handing the Bitmap out: the pump disposes the outgoing frame the instant the next one
    /// lands, so a borrowed reference is a use-after-free. Seeking is what made it fire — a seek
    /// flushes the decoder and the backlog arrives as a burst — and the resulting throw lands
    /// inside OnPaint, where WinForms latches the failure and paints a white box with a red X in
    /// place of the control for the rest of the session.
    public static bool DrawFrame(Graphics g, Rectangle box)
    {
        lock (_lock)
        {
            if (_frame == null) return false;
            float scale = Math.Min(box.Width / (float)_frame.Width, box.Height / (float)_frame.Height);
            int w = Math.Max(1, (int)(_frame.Width * scale)), h = Math.Max(1, (int)(_frame.Height * scale));
            g.DrawImage(_frame, new Rectangle(box.X + (box.Width - w) / 2, box.Y + (box.Height - h) / 2, w, h));
            return true;
        }
    }

    public static float Progress
    {
        get
        {
            var d = _duration.TotalSeconds;
            return d <= 0 ? 0f : (float)Math.Clamp(Position.TotalSeconds / d, 0, 1);
        }
    }

    public static void Toggle(string url)
    {
        if (_url == url)
        {
            if (_out == null) return;
            if (IsPlaying) _out.Pause(); else _out.Play();
            Changed?.Invoke();
            return;
        }
        Stop();
        // Playing a video and a voice message at once would be two sounds over each other.
        Audio.Stop();
        _url = url;
        _loading = true;
        Changed?.Invoke();
        _ = Load(url);
    }

    // Seek state, shared with the pump thread. Bumping _seekSeq invalidates whatever frame the pump
    // is holding (read, wait loop, or swap): it snapshot the sequence before each read and drops the
    // frame if a seek landed in between — the old timeline must not be drawn, or paced, against the
    // clock that has already jumped. _seekHeld gates the reader during the reposition window so the
    // pump stays out of ReadSample, and _seekSkip drops the decoder's keyframe remainder, the frames
    // before the target that a source reader emits first after a seek.
    static long _seekSeq;
    static volatile int _seekHeld;
    static long _seekSkip = -1;          // ticks; -1 = none

    public static void Seek(string url, float fraction)
    {
        if (_url != url || _audio == null) return;
        var to = TimeSpan.FromSeconds(_duration.TotalSeconds * Math.Clamp(fraction, 0, 1));
        try
        {
            // The audio clock is the pump's pacemaker, so it jumps first.
            _audio.CurrentTime = to;
            Interlocked.Increment(ref _seekSeq);
            _seekHeld = 1;
            try
            {
                // SetCurrentPosition fails with MF_E_INVALIDREQUEST while a ReadSample is pending —
                // and the pump is parked inside one most of the time, which is exactly why seeks
                // silently did nothing (the return value was never checked): forward scrubs streamed
                // the old timeline against the jumped clock (fast-forward), backward scrubs held each
                // stale frame while waiting on it (frozen). Flush cancels the pending read and clears
                // the decoder's queue of old-position frames, synchronously; only then can the seek
                // land and the next read start at the target.
                _reader?.Flush(Mf.MF_SOURCE_READER_FIRST_VIDEO_STREAM);
                var pv = Marshal.AllocHGlobal(16);
                try
                {
                    Marshal.WriteInt16(pv, 0, 20);                       // VT_I8
                    Marshal.WriteInt64(pv, 8, to.Ticks);                 // 100ns units, same as MF
                    _reader?.SetCurrentPosition(Guid.Empty, pv);
                }
                finally { Marshal.FreeHGlobal(pv); }
            }
            finally
            {
                // Arm the skip before releasing the reader: after a seek the decoder starts at the
                // keyframe before the target, and emitting that remainder would flash earlier
                // content. The pump clears it once it has drawn a frame at/after the target.
                Volatile.Write(ref _seekSkip, to.Ticks);
                _seekHeld = 0;
            }
        }
        catch (Exception e) { Log.Write("video", "seek: " + e.Message); }
        Changed?.Invoke();
    }

    public static void Stop()
    {
        _cts?.Cancel();
        try { _out?.Stop(); } catch { }
        _out?.Dispose();
        _audio?.Dispose();
        if (_reader != null) { try { Marshal.ReleaseComObject(_reader); } catch { } }
        lock (_lock) { _frame?.Dispose(); _frame = null; }
        _out = null; _audio = null; _reader = null; _pump = null;
        _url = null; _loading = false; _duration = TimeSpan.Zero;
        _seekHeld = 0;
        Volatile.Write(ref _seekSkip, -1);   // must not leak into the next clip's timeline
        if (_temp != null) { try { File.Delete(_temp); } catch { } _temp = null; }
        Changed?.Invoke();
    }

    static async Task Load(string url)
    {
        string? temp = null;
        try
        {
            // Downloaded rather than streamed: Media Foundation's http handler is fussy about
            // Discord's CDN redirects, and an attachment is a few MB at most.
            var bytes = await Http.GetByteArrayAsync(url).ConfigureAwait(false);
            if (_url != url) return;

            temp = Path.Combine(Path.GetTempPath(), "csv-" + Guid.NewGuid().ToString("N") + Ext(url));
            await File.WriteAllBytesAsync(temp, bytes).ConfigureAwait(false);
            if (_url != url) { try { File.Delete(temp); } catch { } return; }

            Mf.EnsureStarted();

            Mf.MFCreateAttributes(out var attrs, 1);
            attrs.SetUINT32(Mf.SourceReaderEnableVideoProcessing, 1);
            int hr = Mf.MFCreateSourceReaderFromURL(temp, attrs, out var reader);
            if (hr != Mf.S_OK) throw new InvalidOperationException($"source reader 0x{hr:X8}");

            // Ask for RGB32 on the video stream; the processor handles the conversion.
            Mf.MFCreateMediaType(out var want);
            want.SetGUID(Mf.MtMajorType, Mf.MediaTypeVideo);
            want.SetGUID(Mf.MtSubtype, Mf.VideoFormatRgb32);
            hr = reader.SetCurrentMediaType(Mf.MF_SOURCE_READER_FIRST_VIDEO_STREAM, IntPtr.Zero, want);
            if (hr != Mf.S_OK) throw new InvalidOperationException($"rgb32 0x{hr:X8}");

            reader.GetCurrentMediaType(Mf.MF_SOURCE_READER_FIRST_VIDEO_STREAM, out var cur);
            cur.GetUINT64(Mf.MtFrameSize, out var packed);
            int vw = (int)(packed >> 32), vh = (int)(packed & 0xFFFFFFFF);
            if (vw <= 0 || vh <= 0) throw new InvalidOperationException("no frame size");

            var audio = new MediaFoundationReader(temp);
            var player = new WaveOutEvent
            {
                DeviceNumber = Prefs.Current.OutputDevice is var d && d >= 0 && d < WaveOut.DeviceCount ? d : -1,
            };
            player.Init(audio);

            if (_url != url)
            {
                player.Dispose(); audio.Dispose();
                Marshal.ReleaseComObject(reader);
                try { File.Delete(temp); } catch { }
                return;
            }

            _temp = temp;
            _reader = reader;
            _audio = audio;
            _out = player;
            _duration = audio.TotalTime;
            _loading = false;
            _cts = new CancellationTokenSource();

            player.Play();
            _seekHeld = 0;
            Volatile.Write(ref _seekSkip, -1);   // a fresh clip has no seek remainder to drop
            _pump = new Thread(() => Pump(url, vw, vh, _cts.Token)) { IsBackground = true, Name = "video-decode" };
            _pump.Start();
            Changed?.Invoke();
        }
        catch (Exception e)
        {
            Log.Write("video", url + ": " + e.Message);
            if (temp != null) { try { File.Delete(temp); } catch { } }
            if (_url == url) { _url = null; _loading = false; Changed?.Invoke(); }
        }
    }

    // Decode loop. Reads one sample at a time and waits until the audio clock reaches its
    // timestamp; a frame that is already late is shown immediately rather than dropped, because
    // dropping is only worth it when decode cannot keep up, and at that point the clip is
    // unwatchable anyway.
    static void Pump(string url, int w, int h, CancellationToken ct)
    {
        try
        {
            // The decode thread must call MFStartup itself — see the note on EnsureThreadStarted.
            Mf.EnsureThreadStarted();
            int stride = w * 4;
            while (!ct.IsCancellationRequested && _url == url)
            {
                // A seek owns the reader between its Flush and SetCurrentPosition; re-entering
                // ReadSample there would make the seek fail with MF_E_INVALIDREQUEST again.
                if (_seekHeld != 0) { Thread.Sleep(3); continue; }
                long seq = Volatile.Read(ref _seekSeq);

                if (_out?.PlaybackState == PlaybackState.Paused) { Thread.Sleep(40); continue; }

                int hr = Mf.ReadSampleRaw(_reader!, Mf.MF_SOURCE_READER_FIRST_VIDEO_STREAM, 0,
                                          out _, out uint flags, out long ts, out var sample);
                if (hr != Mf.S_OK)
                {
                    // Flush cancels a pending read, so the failure right after a seek is expected;
                    // the next read starts at the new position. Any other failure is real.
                    if (_seekHeld != 0 || Volatile.Read(ref _seekSeq) != seq) { Thread.Sleep(10); continue; }
                    break;
                }
                if ((flags & 0x2) != 0)   // MF_SOURCE_READERF_ENDOFSTREAM is 0x2 on this path
                {
                    if (sample == IntPtr.Zero)
                    {
                        if (Volatile.Read(ref _seekSeq) != seq) { Thread.Sleep(10); continue; }
                        break;
                    }
                }
                if (sample == IntPtr.Zero) { Thread.Sleep(10); continue; }

                var obj = Marshal.GetObjectForIUnknown(sample) as Mf.IMFSample;
                Marshal.Release(sample);
                if (obj == null) continue;
                var bytes = Mf.SampleBytes(obj);
                Marshal.ReleaseComObject(obj);
                if (bytes == null || bytes.Length < stride * h) continue;

                // A seek landed while this sample was in flight — it belongs to the old timeline.
                if (Volatile.Read(ref _seekSeq) != seq) continue;

                // After a seek the decoder re-emits the keyframe remainder before the target; drop
                // those frames rather than flashing them.
                long skip = Volatile.Read(ref _seekSkip);
                if (skip >= 0 && ts < skip) continue;

                // Hold the frame until the sound has caught up to it. A seek can land mid-wait; the
                // epoch check drops the stale frame instead of pacing it against the clock that has
                // already jumped (which is what froze the video on a backward scrub).
                var due = TimeSpan.FromTicks(ts);
                for (int guard = 0; guard < 400 && !ct.IsCancellationRequested; guard++)
                {
                    if (Volatile.Read(ref _seekSeq) != seq) break;
                    var behind = due - Position;
                    if (behind <= TimeSpan.Zero) break;
                    Thread.Sleep((int)Math.Clamp(behind.TotalMilliseconds, 1, 50));
                }
                if (Volatile.Read(ref _seekSeq) != seq) continue;

                // First frame at/after the seek target: the remainder is over, stop dropping.
                if (skip >= 0) Volatile.Write(ref _seekSkip, -1);

                var bmp = new Bitmap(w, h, PixelFormat.Format32bppRgb);
                var bits = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
                // MF's video processor hands back top-down RGB32 rows (positive stride); copying
                // them in reverse was rendering every clip upside down.
                for (int y = 0; y < h; y++)
                    Marshal.Copy(bytes, y * stride, bits.Scan0 + y * bits.Stride, stride);
                bmp.UnlockBits(bits);

                // Stop() may have run while this frame was decoding (it runs on the UI thread and
                // this loop only checks _url at the top). Guard the swap too, or the orphan bitmap
                // would be installed after Stop already disposed the old frame and sit there until
                // the next playback — a one-frame leak plus a repaint of nothing.
                bool swapped;
                lock (_lock)
                {
                    swapped = _url == url;
                    if (swapped) { _frame?.Dispose(); _frame = bmp; }
                    else bmp.Dispose();
                }
                if (swapped) Changed?.Invoke();
            }
        }
        catch (Exception e) { Log.Write("video", "pump: " + e.Message); }

        // Reaching the end leaves the last frame up and the transport stopped, like a paused player.
        if (_url == url && !ct.IsCancellationRequested)
        {
            try { _out?.Stop(); } catch { }
            Changed?.Invoke();
        }
    }

    static string Ext(string url)
    {
        var q = url.IndexOf('?');
        var clean = q >= 0 ? url[..q] : url;
        var e = Path.GetExtension(clean);
        return string.IsNullOrEmpty(e) || e.Length > 6 ? ".mp4" : e;
    }
}
