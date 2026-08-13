using NAudio.Wave;

namespace OpenCord;

// Discord's UI sounds: the message ping and the two ring tones.
//
// Deliberately not part of [[Audio]]. That class is the *attachment* player — one clip at a time,
// with Current/Seek/Stop semantics the message rows bind to. A notification has to be able to fire
// while a voice message is playing, and the ring tone has to loop underneath everything, so sharing
// one WaveOut would have the ping cancel whatever the user was listening to.
static class Sfx
{
    static readonly string Dir = Path.Combine(AppContext.BaseDirectory, "Sounds");

    // The looping one — an incoming or outgoing ring. There is only ever one call being set up, so
    // one slot is enough; starting a second stops the first.
    static IWavePlayer? _loopOut;
    static IDisposable? _loopSrc;
    static string? _loopName;

    // Fire-and-forget voices. Kept only so they can be disposed when they finish; a WaveOutEvent
    // that is never disposed leaks its callback thread, which is exactly the kind of drip that
    // shows up as a thread-count climb in soak.ps1 long before it shows up as memory.
    static readonly List<IWavePlayer> _live = new();

    static int Device
    {
        get
        {
            int d = Prefs.Current.OutputDevice;
            return d >= 0 && d < WaveOut.DeviceCount ? d : -1;
        }
    }

    static string? PathOf(string name)
    {
        var p = Path.Combine(Dir, name + ".mp3");
        return File.Exists(p) ? p : null;
    }

    /// One-shot. Unknown or missing files are silently ignored — a missing asset must never be the
    /// reason a message fails to arrive.
    public static void Play(string name)
    {
        if (!Prefs.Current.SoundsEnabled) return;
        if (PathOf(name) is not { } path) return;
        try
        {
            var reader = new AudioFileReader(path);
            var outp = new WaveOutEvent { DeviceNumber = Device };
            outp.Init(reader);
            outp.PlaybackStopped += (_, _) =>
            {
                lock (_live) _live.Remove(outp);
                try { outp.Dispose(); reader.Dispose(); } catch { }
            };
            lock (_live)
            {
                // A stuck voice would otherwise pile up forever if PlaybackStopped never fired.
                if (_live.Count > 8) return;
                _live.Add(outp);
            }
            outp.Play();
        }
        catch (Exception e) { Log.Write("sfx", $"{name}: {e.Message}"); }
    }

    /// The in-call sounds — join, leave, mute, deafen, disconnect. Separate from Play so they can
    /// be silenced without also silencing the message ping, which is how the real client splits
    /// them.
    public static void Voice(string name)
    {
        if (!Prefs.Current.VoiceSounds) return;
        Play(name);
    }

    /// Start a looping ring. Re-calling with the same name is a no-op, so it is safe to drive this
    /// straight from a state rebuild that runs on every gateway event.
    public static void Loop(string name)
    {
        if (!Prefs.Current.SoundsEnabled) { StopLoop(); return; }
        if (_loopName == name && _loopOut != null) return;
        StopLoop();
        if (PathOf(name) is not { } path) return;
        try
        {
            // LoopStream rewinds at EOF, which is what makes a 3s ring tone ring until answered.
            var reader = new AudioFileReader(path);
            var loop = new LoopStream(reader);
            var outp = new WaveOutEvent { DeviceNumber = Device };
            outp.Init(loop);
            outp.Play();
            _loopOut = outp;
            _loopSrc = reader;
            _loopName = name;
        }
        catch (Exception e) { Log.Write("sfx", $"loop {name}: {e.Message}"); }
    }

    public static void StopLoop()
    {
        var o = _loopOut; var s = _loopSrc;
        _loopOut = null; _loopSrc = null; _loopName = null;
        try { o?.Stop(); o?.Dispose(); s?.Dispose(); } catch { }
    }

    // NAudio ships no looping provider; this is the documented three-line one.
    sealed class LoopStream : WaveStream
    {
        readonly WaveStream _src;
        public LoopStream(WaveStream src) { _src = src; }
        public override WaveFormat WaveFormat => _src.WaveFormat;
        public override long Length => _src.Length;
        public override long Position { get => _src.Position; set => _src.Position = value; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int done = 0;
            while (done < count)
            {
                int n = _src.Read(buffer, offset + done, count - done);
                if (n == 0)
                {
                    if (_src.Position == 0) break;   // empty file: bail rather than spin
                    _src.Position = 0;
                }
                done += n;
            }
            return done;
        }
    }
}
