using Concentus;
using Concentus.Enums;
using Concentus.Structs;
using NAudio.Wave;
using System.Runtime.InteropServices;

namespace OpenCord;

// The audio plane: Opus encode/decode (Concentus, pure managed — no native code, matching this
// client's zero-dependency footprint) and the two NAudio device loops. Capture runs on NAudio's
// waveIn thread at 48 kHz stereo (Discord's voice format); every 20ms frame is encoded and handed
// to the caller. Playback keeps a tiny jitter buffer so network jitter doesn't glitch.
sealed class VoiceAudio : IDisposable
{
    public const int SampleRate = 48000;
    public const int Channels = 2;
    public const int FrameSamples = 960;         // 20 ms
    public const int FrameBytes = FrameSamples * Channels * 2;

    readonly IOpusEncoder _encoder = new OpusEncoder(SampleRate, Channels, OpusApplication.OPUS_APPLICATION_VOIP);
    readonly IOpusDecoder _decoder = new OpusDecoder(SampleRate, Channels);

    WaveInEvent? _capture;
    WaveOutEvent? _output;
    BufferedWaveProvider? _provider;
    readonly byte[] _pcmBuf = new byte[FrameBytes];
    int _pcmFill;
    readonly byte[] _decodeBuf = new byte[FrameSamples * Channels * 2];
    readonly object _playLock = new();

    public event Action<byte[]>? FrameReady;      // one encoded Opus frame, 20 ms of audio

    public bool Muted { get; private set; }

    // ── input gating ────────────────────────────────────────────────────────────────────────────
    // Discord gates the microphone two ways: voice activity (open above a threshold) or
    // push-to-talk. Before this the mic was always open the moment you were unmuted, which is the
    // one setting the real client never has.
    public enum Mode { VoiceActivity, PushToTalk }

    public Mode InputMode = Mode.VoiceActivity;
    /// Extra RMS demanded on top of the measured noise floor. 0 — the default — means "automatic":
    /// the gate follows the room.
    ///
    /// This was a fixed 0.02 absolute threshold, which is where a real regression came from: a
    /// normal microphone idles around 0.0005 and speaks well under 0.02, so the gate never opened
    /// and the client transmitted silence for the whole call. An absolute number cannot work
    /// across microphones — the floor has to be learned.
    public float Sensitivity;
    /// Held down by the global hotkey while in push-to-talk.
    public volatile bool PttDown;
    public float InputGain = 1f;
    public float OutputGain = 1f;
    /// Below this the frame is treated as room noise and squelched entirely. Deliberately a plain
    /// gate, not "noise suppression" — real suppression needs a spectral model this does not have.
    public float NoiseGate = 0f;

    /// True while we are actually putting audio on the wire, so the UI can light our own tile.
    public bool Transmitting { get; private set; }
    public event Action<bool>? TransmitChanged;

    // Voice activity keeps the channel open briefly after you stop, or every pause between words
    // clips the start of the next one.
    long _openUntil;
    static readonly long HangoverTicks = System.Diagnostics.Stopwatch.Frequency / 4;   // 250 ms

    /// RMS of one frame, 0..1. Used for the gate and for the settings page's live meter.
    public float LastLevel { get; private set; }
    /// Adapting noise floor, seeded low so the gate is open from the first frame rather than
    /// spending the start of the call learning.
    float _floor = 0.0005f;
    /// The level the gate will open at right now — shown as the notch on the settings meter.
    public float OpenAt { get; private set; }
    int _gateLog;

    // ── capture (mic -> Opus) ───────────────────────────────────────────────────────────────────
    public bool StartCapture()
    {
        try
        {
            _capture = new WaveInEvent
            {
                WaveFormat = new WaveFormat(SampleRate, 16, Channels),
                BufferMilliseconds = 20,
                // 0 is WaveIn's default device; a saved index is only honoured while it still
                // exists, since unplugging a headset renumbers everything after it.
                DeviceNumber = Prefs.Current.InputDevice is var i && i >= 0 && i < WaveInEvent.DeviceCount ? i : 0,
            };
            _capture.DataAvailable += OnDataAvailable;
            _capture.StartRecording();
            return true;
        }
        catch (Exception e)
        {
            Log.Voice("capture unavailable: " + e.Message);
            _capture = null;
            return false;
        }
    }

    void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        int off = 0;
        while (off < e.BytesRecorded)
        {
            int take = Math.Min(FrameBytes - _pcmFill, e.BytesRecorded - off);
            Array.Copy(e.Buffer, off, _pcmBuf, _pcmFill, take);
            _pcmFill += take;
            off += take;
            if (_pcmFill == FrameBytes)
            {
                _pcmFill = 0;
                var shorts = MemoryMarshal.Cast<byte, short>(_pcmBuf);

                // Input volume first, so the meter and the gate both see what will be sent.
                if (Math.Abs(InputGain - 1f) > 0.001f)
                    for (int i = 0; i < shorts.Length; i++)
                        shorts[i] = (short)Math.Clamp(shorts[i] * InputGain, short.MinValue, short.MaxValue);

                double sum = 0;
                for (int i = 0; i < shorts.Length; i++) { double s = shorts[i] / 32768.0; sum += s * s; }
                float rms = (float)Math.Sqrt(sum / shorts.Length);
                LastLevel = rms;

                // Learn the room. Falls quickly toward quiet and rises slowly, so a moment of
                // speech does not drag the floor up behind it and choke the next word.
                _floor += (rms - _floor) * (rms < _floor ? 0.08f : 0.001f);
                // Open at 3x the floor plus a small absolute margin, so a silent room still needs
                // real sound; Sensitivity only ever *raises* the bar above automatic.
                float openAt = Math.Max(_floor * 3f + 0.0008f, Sensitivity);
                OpenAt = openAt;

                bool open;
                if (Muted) open = false;
                else if (InputMode == Mode.PushToTalk) open = PttDown;
                else
                {
                    // Voice activity, with the hangover so pauses between words do not clip.
                    if (rms >= openAt) _openUntil = System.Diagnostics.Stopwatch.GetTimestamp() + HangoverTicks;
                    open = System.Diagnostics.Stopwatch.GetTimestamp() < _openUntil;
                }
                if (open && NoiseGate > 0 && rms < NoiseGate) open = false;

                if (open != Transmitting)
                {
                    // Logged on the edge only. A gate stuck shut is invisible from the outside —
                    // the call connects, the meters move, and the other end simply hears nothing —
                    // so the one line that would have caught it earlier is worth keeping.
                    if (_gateLog++ < 8)
                        Log.Voice($"gate {(open ? "open" : "shut")} rms={rms:0.0000} at={openAt:0.0000} floor={_floor:0.0000}");
                    Transmitting = open;
                    TransmitChanged?.Invoke(open);
                }
                if (!open) continue;              // nothing on the wire while the gate is shut

                var opus = new byte[4000];
                int len = _encoder.Encode(shorts, FrameSamples, opus, opus.Length);
                if (len > 0) FrameReady?.Invoke(opus.AsSpan(0, len).ToArray());
            }
        }
    }

    // ── playback (Opus -> speaker) ──────────────────────────────────────────────────────────────
    public bool StartPlayback()
    {
        try
        {
            _provider = new BufferedWaveProvider(new WaveFormat(SampleRate, 16, Channels))
            {
                DiscardOnBufferOverflow = true,
                BufferDuration = TimeSpan.FromMilliseconds(200),
            };
            // -1 is WaveOut's "system default"; same re-validation as the capture device.
            _output = new WaveOutEvent
            {
                DesiredLatency = 100,
                DeviceNumber = Prefs.Current.OutputDevice is var o && o >= 0 && o < WaveOut.DeviceCount ? o : -1,
            };
            _output.Init(_provider);
            _output.Play();
            return true;
        }
        catch (Exception e)
        {
            Log.Voice("playback unavailable: " + e.Message);
            _output = null;
            return false;
        }
    }

    // Called with an unencrypted Opus frame from the UDP receiver.
    public void PlayFrame(ReadOnlySpan<byte> opus, float userGain = 1f)
    {
        if (_output == null || _provider == null) return;
        var shorts = MemoryMarshal.Cast<byte, short>(_decodeBuf);
        int samples = _decoder.Decode(opus, shorts, FrameSamples, false);
        if (samples <= 0) return;
        int bytes = samples * Channels * 2;

        // Output volume and this speaker's own level, applied together so one multiply covers both.
        float gain = OutputGain * userGain;
        if (Math.Abs(gain - 1f) > 0.001f)
        {
            int n = samples * Channels;
            for (int i = 0; i < n; i++)
                shorts[i] = (short)Math.Clamp(shorts[i] * gain, short.MinValue, short.MaxValue);
        }
        lock (_playLock)
        {
            if (_provider.BufferedBytes >= _provider.BufferLength - FrameBytes)
                _provider.ClearBuffer();          // too far behind: resync instead of crackling
            _provider.AddSamples(_decodeBuf, 0, bytes);
        }
    }

    public void SetMuted(bool muted) => Muted = muted;

    public void Dispose()
    {
        try { _capture?.StopRecording(); } catch { }
        _capture?.Dispose();
        try { _output?.Stop(); } catch { }
        _output?.Dispose();
    }
}
