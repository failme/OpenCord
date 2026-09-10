using System.Text;
using Concentus.Enums;
using Concentus.Structs;
using NAudio.Wave;

namespace OpenCord;

// Voice messages: mic capture -> mono 48k PCM, then Concentus Opus frames muxed into a minimal Ogg
// stream — the exact shape Discord's own voice messages come back as (one "voice-message.ogg"
// attachment carrying waveform + duration_secs, on a message flagged IS_VOICE_MESSAGE), so the
// existing player renders them without changes.
static class VoiceNote
{
    public const string FileName = "voice-message.ogg";
    public const int Flag = 1 << 13;   // IS_VOICE_MESSAGE

    /// Peak amplitude per bucket, 0-255, base64 — the same encoding the gateway sends back.
    public static string Waveform(short[] pcm, int buckets = 255)
    {
        var wf = new byte[buckets];
        if (pcm.Length == 0) return Convert.ToBase64String(wf);
        double per = pcm.Length / (double)buckets;
        for (int b = 0; b < buckets; b++)
        {
            int from = (int)(b * per), to = Math.Min(pcm.Length, (int)((b + 1) * per));
            int peak = 0;
            for (int i = from; i < to; i++) peak = Math.Max(peak, Math.Abs(pcm[i]));
            wf[b] = (byte)Math.Min(255, peak * 255 / 32768);
        }
        return Convert.ToBase64String(wf);
    }

    /// Encode mono PCM into an Ogg/Opus stream: an OpusHead page, an OpusTags page, then one audio
    /// packet per page. A minute of speech lands around 300KB at this bitrate.
    public static byte[] EncodeOggOpus(short[] pcm, int sampleRate = 48000, int frameSamples = 960)
    {
        var enc = new OpusEncoder(sampleRate, 1, OpusApplication.OPUS_APPLICATION_VOIP)
        { Bitrate = 32000 };
        var body = new MemoryStream();

        // Granule position = total samples per channel encoded so far. Opus always plays at 48k,
        // so the header declares that regardless of the capture rate.
        WritePage(body, HeadPacket(), granule: 0, first: true);
        WritePage(body, TagsPacket(), granule: 0);

        var frame = new short[frameSamples];
        long granule = 0;
        for (int off = 0; off < pcm.Length; off += frameSamples)
        {
            int n = Math.Min(frameSamples, pcm.Length - off);
            Array.Clear(frame);
            Array.Copy(pcm, off, frame, 0, n);
            var buf = new byte[400];
            int len = enc.Encode(frame, 0, n, buf, 0, buf.Length);
            granule += n;
            WritePage(body, buf.AsSpan(0, len).ToArray(), granule);
        }

        return body.ToArray();
    }

    static byte[] HeadPacket()
    {
        // Magic, version, channels, pre-skip, input rate, gain, mapping family.
        var b = new List<byte>(19);
        b.AddRange("OpusHead"u8);
        b.Add(1); b.Add(1);
        b.Add(0x38); b.Add(0x01);                 // pre-skip 312, what Discord's own files carry
        b.AddRange(BitConverter.GetBytes(48000));
        b.Add(0); b.Add(0);
        b.Add(0);
        return b.ToArray();
    }

    static byte[] TagsPacket()
    {
        var vendor = "OpenCord"u8.ToArray();
        var b = new List<byte>(8 + 4 + vendor.Length + 4);
        b.AddRange("OpusTags"u8);
        b.AddRange(BitConverter.GetBytes(vendor.Length));
        b.AddRange(vendor);
        b.AddRange(BitConverter.GetBytes(0));     // no user comments
        return b.ToArray();
    }

    // ── Ogg muxing ──
    // A page is "OggS", version 0, header flags, granule (LE64), serial (LE32), sequence (LE32),
    // CRC (LE32), segment table, then the payload split into <=255-byte lacing segments. The CRC
    // covers the whole page with the field zeroed, using Ogg's non-reflected 0x04c11db7.

    static uint Crc32(byte[] data, int len)
    {
        uint crc = 0;
        for (int i = 0; i < len; i++)
        {
            crc ^= (uint)(data[i] << 24);
            for (int k = 0; k < 8; k++)
                crc = (crc & 0x80000000) != 0 ? (crc << 1) ^ 0x04c11db7 : crc << 1;
        }
        return crc;
    }

    static void WritePage(Stream s, byte[] packet, long granule, bool first = false)
    {
        int segs = (packet.Length + 254) / 255;
        if (segs == 0) segs = 1;
        var page = new byte[27 + segs + packet.Length];
        page[0] = (byte)'O'; page[1] = (byte)'g'; page[2] = (byte)'g'; page[3] = (byte)'S';
        page[4] = 0;                                        // stream structure version
        page[5] = (byte)(first ? 2 : 0);                    // BOS on the first page only
        BitConverter.GetBytes(granule).CopyTo(page, 6);
        BitConverter.GetBytes(Serial).CopyTo(page, 14);
        BitConverter.GetBytes(Seq++).CopyTo(page, 18);
        // page[22..26] stays zero while computing the checksum
        page[26] = (byte)segs;                              // lacing: how many segments follow
        for (int i = 0; i < segs - 1; i++) page[27 + i] = 255;
        page[27 + segs - 1] = (byte)(packet.Length - 255 * (segs - 1));
        if (packet.Length == 0) page[27 + segs - 1] = 0;
        packet.CopyTo(page, 27 + segs);
        BitConverter.GetBytes(Crc32(page, page.Length)).CopyTo(page, 22);
        s.Write(page);
    }

    // Per-process stream identity is arbitrary; one id keeps every voice note its own logical stream.
    static readonly uint Serial = (uint)Random.Shared.Next() | 1;
    static uint Seq;
}

// The mic side. Captures straight into a growing short[] until stopped; Stop returns everything
// needed to send, Cancel throws it away.
sealed class VoiceNoteRecorder : IDisposable
{
    WaveInEvent? _capture;
    List<short[]> _chunks = new();
    int _total;
    DateTime _started;

    public bool Recording { get; private set; }
    public TimeSpan Elapsed => Recording ? DateTime.UtcNow - _started : TimeSpan.Zero;
    public double Seconds => _total / 48000.0;

    public bool Start()
    {
        try
        {
            _capture = new WaveInEvent
            {
                WaveFormat = new WaveFormat(48000, 16, 1),
                BufferMilliseconds = 50,
                DeviceNumber = Prefs.Current.InputDevice is var i && i >= 0 && i < WaveInEvent.DeviceCount ? i : 0,
            };
            _chunks = new List<short[]>(capacity: 4096);
            _total = 0;
            _started = DateTime.UtcNow;
            _capture.DataAvailable += (_, e) =>
            {
                var shorts = new short[e.BytesRecorded / 2];
                Buffer.BlockCopy(e.Buffer, 0, shorts, 0, shorts.Length * 2);
                lock (_chunks) { _chunks.Add(shorts); _total += shorts.Length; }
            };
            _capture.RecordingStopped += (_, _) => { };
            _capture.StartRecording();
            Recording = true;
            return true;
        }
        catch (Exception e)
        {
            Log.Write("voice", "recorder unavailable: " + e.Message);
            _capture?.Dispose();
            _capture = null;
            return false;
        }
    }

    /// Flatten and hand over. Null when nothing was captured (or under Discord's ~10min cap).
    public (short[] Pcm, double Secs)? Take()
    {
        Recording = false;
        StopCapture();
        lock (_chunks)
        {
            if (_total == 0 || Seconds > 600) return null;
            var pcm = new short[_total];
            int at = 0;
            foreach (var c in _chunks) { c.CopyTo(pcm, at); at += c.Length; }
            return (pcm, Seconds);
        }
    }

    public void Cancel()
    {
        Recording = false;
        StopCapture();
        lock (_chunks) { _chunks.Clear(); _total = 0; }
    }

    void StopCapture()
    {
        try { _capture?.StopRecording(); } catch { }
        _capture?.Dispose();
        _capture = null;
    }

    public void Dispose() => Cancel();
}
