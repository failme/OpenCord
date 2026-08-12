using System.Net.Http;
using Concentus;
using Concentus.Structs;
using NAudio.Wave;

namespace ClaudeScord;

// Playback for audio attachments and voice messages. One clip at a time, like Discord.
//
// Two decode paths, because Windows only covers one of them:
//
//   - mp3 / m4a / aac / wav / wma go through Media Foundation, which *streams* from a file. A
//     40-minute mp3 therefore costs a buffer, not its decoded length — worth insisting on, since a
//     three-minute track fully decoded to PCM is 34MB and this client's whole point is not doing
//     that.
//   - ogg / opus has no Media Foundation decoder at all, and it is exactly the format Discord uses
//     for voice messages. Those are demuxed here and decoded with Concentus, which the voice stack
//     already depends on. Voice messages are short by construction, so decoding one to PCM up front
//     is a megabyte or two and it is released the moment playback stops.
static class Audio
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    static WaveOutEvent? _out;
    static WaveStream? _stream;
    static string? _temp;
    static string? _url;
    static bool _loading;

    /// The clip currently loaded, playing or paused.
    public static string? Current => _url;
    public static bool IsPlaying => _out?.PlaybackState == PlaybackState.Playing;
    public static bool IsLoading(string url) => _loading && _url == url;

    /// Raised when playback state changes, so rows can repaint their button and progress.
    public static event Action? Changed;

    public static TimeSpan Position => _stream?.CurrentTime ?? TimeSpan.Zero;
    public static TimeSpan Duration => _stream?.TotalTime ?? TimeSpan.Zero;

    /// Fraction played, 0..1 — what the seek bar draws.
    public static float Progress
    {
        get
        {
            var d = Duration.TotalSeconds;
            return d <= 0 ? 0f : (float)Math.Clamp(Position.TotalSeconds / d, 0, 1);
        }
    }

    /// Play `url`, pause it if it is already the playing clip, resume if paused.
    public static void Toggle(string url)
    {
        if (_url == url && _out != null)
        {
            if (_out.PlaybackState == PlaybackState.Playing) _out.Pause();
            else _out.Play();
            Changed?.Invoke();
            return;
        }
        Stop();
        _url = url;
        _loading = true;
        Changed?.Invoke();
        _ = Load(url);
    }

    public static void Seek(string url, float fraction)
    {
        if (_url != url || _stream == null) return;
        try { _stream.CurrentTime = TimeSpan.FromSeconds(Duration.TotalSeconds * Math.Clamp(fraction, 0, 1)); }
        catch { }
        Changed?.Invoke();
    }

    public static void Stop()
    {
        try { _out?.Stop(); } catch { }
        _out?.Dispose();
        _stream?.Dispose();
        _out = null;
        _stream = null;
        _url = null;
        _loading = false;
        // The temp file only exists for the Media Foundation path, and only for as long as the clip
        // is loaded — leaving these behind would quietly fill the temp folder with other people's
        // attachments.
        if (_temp != null) { try { File.Delete(_temp); } catch { } _temp = null; }
        Changed?.Invoke();
    }

    static async Task Load(string url)
    {
        try
        {
            var bytes = await Http.GetByteArrayAsync(url).ConfigureAwait(false);
            if (_url != url) return;                       // superseded while downloading

            WaveStream stream;
            string? temp = null;
            if (IsOgg(bytes))
            {
                var (pcm, rate, channels) = OggOpus.Decode(bytes);
                stream = new RawSourceWaveStream(new MemoryStream(pcm), new WaveFormat(rate, 16, channels));
            }
            else
            {
                temp = Path.Combine(Path.GetTempPath(), "cs-" + Guid.NewGuid().ToString("N") + Ext(url));
                await File.WriteAllBytesAsync(temp, bytes).ConfigureAwait(false);
                stream = new MediaFoundationReader(temp);
            }

            if (_url != url) { stream.Dispose(); if (temp != null) try { File.Delete(temp); } catch { } return; }

            var device = Prefs.Current.OutputDevice;
            var player = new WaveOutEvent
            {
                DeviceNumber = device >= 0 && device < WaveOut.DeviceCount ? device : -1,
            };
            player.Init(stream);
            player.PlaybackStopped += (_, _) => Changed?.Invoke();
            player.Play();

            _stream = stream;
            _out = player;
            _temp = temp;
            _loading = false;
            Changed?.Invoke();
        }
        catch (Exception e)
        {
            Log.Write("audio", "playback failed: " + e.Message);
            _loading = false;
            _url = null;
            Changed?.Invoke();
        }
    }

    static bool IsOgg(byte[] b) => b.Length > 4 && b[0] == 'O' && b[1] == 'g' && b[2] == 'g' && b[3] == 'S';

    static string Ext(string url)
    {
        int q = url.IndexOf('?');
        var path = q < 0 ? url : url[..q];
        var e = Path.GetExtension(path);
        return string.IsNullOrEmpty(e) || e.Length > 6 ? ".bin" : e;
    }

    /// Discord ships a voice message's waveform as base64 bytes, one 0-255 amplitude per bucket.
    /// Returns an empty array for anything unparseable, so a bad waveform draws a flat bar rather
    /// than taking the message row down.
    public static byte[] DecodeWaveform(string? base64)
    {
        if (string.IsNullOrEmpty(base64)) return Array.Empty<byte>();
        try { return Convert.FromBase64String(base64); }
        catch { return Array.Empty<byte>(); }
    }
}

// Just enough Ogg to get Opus packets out of a Discord voice message.
//
// An Ogg file is a chain of pages: "OggS", a header, a segment table of byte counts, then the
// segments themselves. A packet is the run of segments up to (and including) the first one shorter
// than 255. Opus streams begin with two header packets — "OpusHead", which carries the channel
// count and pre-skip, and "OpusTags" — and everything after those is audio.
//
// This is deliberately not a general Ogg reader: no Vorbis, no chained streams, no seeking. It reads
// one Opus stream front to back, which is exactly what a voice message is.
static class OggOpus
{
    public static (byte[] Pcm, int SampleRate, int Channels) Decode(byte[] ogg)
    {
        const int Rate = 48000;                 // Opus always decodes at 48k
        int channels = 1;
        int packetIndex = 0;
        IOpusDecoder? decoder = null;
        var pcm = new MemoryStream();
        var frame = new short[5760 * 2];        // 120ms at 48k stereo, the largest Opus frame

        int i = 0;
        while (i + 27 <= ogg.Length)
        {
            if (ogg[i] != 'O' || ogg[i + 1] != 'g' || ogg[i + 2] != 'g' || ogg[i + 3] != 'S') break;
            int segCount = ogg[i + 26];
            int tableAt = i + 27;
            if (tableAt + segCount > ogg.Length) break;

            int dataAt = tableAt + segCount;
            int packetLen = 0;
            for (int s = 0; s < segCount; s++)
            {
                int len = ogg[tableAt + s];
                packetLen += len;
                if (len == 255) continue;       // packet continues into the next segment

                if (dataAt + packetLen <= ogg.Length && packetLen > 0)
                {
                    var packet = new ReadOnlySpan<byte>(ogg, dataAt, packetLen);
                    if (packetIndex == 0)
                    {
                        // OpusHead: channel count is byte 9.
                        if (packetLen >= 10) channels = Math.Clamp((int)packet[9], 1, 2);
                        decoder = new OpusDecoder(Rate, channels);
                    }
                    else if (packetIndex > 1 && decoder != null)
                    {
                        try
                        {
                            int got = decoder.Decode(packet, frame.AsSpan(), frame.Length / channels, false);
                            for (int n = 0; n < got * channels; n++)
                            {
                                pcm.WriteByte((byte)(frame[n] & 0xFF));
                                pcm.WriteByte((byte)((frame[n] >> 8) & 0xFF));
                            }
                        }
                        catch { }               // one bad packet must not lose the whole message
                    }
                    packetIndex++;
                }
                dataAt += packetLen;
                packetLen = 0;
            }
            i = dataAt + packetLen;
            if (packetLen > 0) i = dataAt;      // trailing continued packet: resume at the next page
        }

        return (pcm.ToArray(), Rate, channels);
    }
}
