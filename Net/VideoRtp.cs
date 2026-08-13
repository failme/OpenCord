using System.Buffers.Binary;

namespace OpenCord;

// The video plane: RTP payload type, fragmentation and reassembly for multi-packet video frames.
//
// Discord's video (like audio) rides the SAME UDP socket as the audio stream, but with its own
// SSRC and payload type. A DAVE-encrypted video frame is one contiguous blob (the protocol frame,
// 0xFAFA magic only on its final byte), which WebRTC fragments across several RTP packets — only
// the last one carries the RTP marker bit. We do the same: JPEG-encode a captured frame, run it
// through the DAVE frame transform, fragment the E2EE frame, and mark the final fragment.
//
// Fragment payload framing (ours, both ends are this client): the FIRST fragment is prefixed with
// the little-endian total payload length so the receiver knows how many bytes to accumulate.
// The SFU relays by ssrc without inspecting the payload (it only checks the negotiated payload
// type matches), so JPEG bytes ride under the session's H264 PT and decrypt fine between two
// OpenCord clients — which is exactly how this client is used (both alt accounts in a call).
static class VideoRtp
{
    // H264's RTP payload type on Discord's wire. The session description negotiates "H264" for
    // calls (observed live: video_codec="H264"), and the SFU relays by SSRC without looking inside
    // the payload — so we carry JPEG bytes under this PT and both ends of the call are this client.
    public const byte PayloadType = 107;

    // Accept any real video payload type on receive (H264 107/100, VP8 101/96, VP9 98) so a peer
    // client — ours or Discord's own — routes into the video path rather than the audio decoder.
    public static bool IsVideoPt(byte pt) =>
        pt == 107 || pt == 100 || pt == 101 || pt == 96 || pt == 98 || pt == 106 || pt == 99;

    // Payload types that carry RFC 6184 H.264 (single-NAL / FU-A). Discord calls negotiate H264 at
    // 107; 100/106 are the classic WebRTC H264 PTs. Anything else is legacy byte framing or VP8/9
    // and must NOT be fed to the H.264 assembler.
    public static bool IsH264Pt(byte pt) => pt == 107 || pt == 100 || pt == 106 || pt == 125 || pt == 99;

    // Max bytes of (fragment header + E2EE data) per RTP payload. 1200 keeps the UDP datagram
    // (payload + 12-byte header + 4-byte nonce trailer + AEAD tag) comfortably under MTU.
    public const int MaxFragmentData = 1200;

    // How many fragments a video frame may span before a receiver gives up on it.
    public const int MaxFragments = 256;

    // ── RTCP ────────────────────────────────────────────────────────────────────────────────────
    // Discord's SFU is pull-based for video: a subscriber must send PLI (keyframe requests) and
    // receiver reports or the SFU never releases the subscribed stream. The real client sends these
    // continuously; VoiceClient mirrors that after every subscribe.
    public static byte[] BuildPli(uint senderSsrc, uint mediaSsrc)
    {
        // RFC 4585: V=2, P=0, FMT=1 (PLI), PT=206, length=2, sender SSRC, media SSRC. FMT=4 is
        // FIR (Full Intra Request) — Discord's SFU and the real client only honor FMT=1 PLI, so
        // the old 0x84 byte was silently ignored and the subscribed stream never got a keyframe
        // request (matches dolfies/discord-native-voice: 0x80 | RTCP_PICTURE_LOSS_INDICATION(1)).
        var p = new byte[12];
        p[0] = 0x80 | 1; p[1] = 206; p[2] = 0; p[3] = 2;
        BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(4), senderSsrc);
        BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(8), mediaSsrc);
        return p;
    }

    public static byte[] BuildReceiverReport(uint senderSsrc, uint mediaSsrc, uint extSeq)
    {
        // RFC 3550: V=2, RC=1, PT=201, length=7, sender SSRC, one report block. The block's
        // fraction/jitter/lsr can be zero; the highest-extended-sequence count is what the SFU
        // reads. RFC 3550's sequence arithmetic: extSeq = (highest seq) + (cycles << 16).
        var p = new byte[32];
        p[0] = 0x80 | 1; p[1] = 201; p[2] = 0; p[3] = 7;
        BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(4), senderSsrc);
        BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(8), mediaSsrc);
        BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(20), extSeq);
        return p;
    }

    // ── send side ──────────────────────────────────────────────────────────────────────────────
    // Splits one complete (already E2EE-transformed) video frame into fragment payloads. The first
    // fragment carries the legacy-frame magic (0x4A 0x50 0x45 0x47 = "JPEG") followed by a 4-byte
    // LE total-length prefix. Returns the payload for each fragment in order; the caller supplies
    // the RTP header (the last fragment must set the M bit).
    //
    // The magic is what lets a receiver tell legacy byte-framed JPEG apart from real H.264 riding
    // the same payload type — a JPEG fallback must never be fed to the H.264 assembler, or the
    // remote tile stays black.
    public static readonly byte[] LegacyMagic = { 0x4A, 0x50, 0x45, 0x47 };  // "JPEG"

    public static bool HasLegacyMagic(ReadOnlySpan<byte> p) =>
        p.Length >= LegacyMagic.Length && p[0] == LegacyMagic[0] && p[1] == LegacyMagic[1]
        && p[2] == LegacyMagic[2] && p[3] == LegacyMagic[3];

    public static List<byte[]> Fragment(ReadOnlySpan<byte> frame)
    {
        var frags = new List<byte[]>((frame.Length + MaxFragmentData - 1) / MaxFragmentData + 1);
        int off = 0;
        bool first = true;
        while (off < frame.Length || first)      // emit at least one fragment for an empty frame
        {
            int budget = MaxFragmentData;
            var frag = new byte[first ? 4 + LegacyMagic.Length : 0];
            if (first)
            {
                LegacyMagic.CopyTo(frag, 0);
                BinaryPrimitives.WriteUInt32LittleEndian(frag.AsSpan(LegacyMagic.Length), (uint)frame.Length);
                budget -= 4 + LegacyMagic.Length;
                first = false;
            }
            int take = Math.Min(budget, frame.Length - off);
            int start = frag.Length;
            Array.Resize(ref frag, frag.Length + take);
            frame.Slice(off, take).CopyTo(frag.AsSpan(start));
            off += take;
            frags.Add(frag);
            if (off >= frame.Length) break;
        }
        return frags;
    }

    // ── receive side ───────────────────────────────────────────────────────────────────────────
    // Accumulates the fragments of one video frame (keyed by RTP ssrc + timestamp) until complete.
    // Complete when the marker bit is set on a fragment. Returns the full payload, or null while
    // still accumulating / on a malformed frame.
    public sealed class Assembler
    {
        readonly List<byte> _buf = new(16384);
        int _expected = -1;         // -1 = waiting for the first (magic + length-prefixed) fragment
        int _fragments;

        /// True while a frame is being accumulated (mid-frame fragments carry no magic; the
        /// receiver uses this to keep routing to the byte assembler once a frame has started).
        public bool Pending => _expected >= 0 || _buf.Count > 0;

        /// feed one transport-decrypted fragment payload. Returns the complete frame payload, or null.
        public byte[]? Feed(ReadOnlySpan<byte> fragment, bool marker)
        {
            if (_expected < 0)
            {
                // First fragment: optional "JPEG" magic, then the 4-byte LE total length. The
                // magic is only ever written by our own fragmenter, so it doubles as a marker
                // that this stream is the legacy byte transport rather than H.264.
                if (HasLegacyMagic(fragment)) fragment = fragment[LegacyMagic.Length..];
                if (fragment.Length < 4) return Reset(null);
                _expected = (int)BinaryPrimitives.ReadUInt32LittleEndian(fragment);
                if (_expected < 0 || _expected > 4 * 1024 * 1024) return Reset(null);  // sanity cap
                _buf.Clear();
                _fragments = 0;
                fragment = fragment[4..];
            }
            else if (_buf.Count + fragment.Length > _expected + 256)
            {
                return Reset(null);                 // overran the declared frame: start over
            }
            _buf.AddRange(fragment);
            _fragments++;
            if (marker || _buf.Count >= _expected)
            {
                if (_expected < 0 || _buf.Count < _expected) return Reset(null);
                var frame = _buf.ToArray();
                Reset(null);
                return frame;
            }
            if (_fragments > MaxFragments) return Reset(null);
            return null;
        }

        byte[]? Reset(byte[]? result)
        {
            _expected = -1;
            _buf.Clear();
            _fragments = 0;
            return result;
        }
    }

    // ── H.264 RTP (RFC 6184) ───────────────────────────────────────────────────────────────────
    // One H.264 access unit (Annex-B, from the encoder) becomes one RTP "frame" delimited by the
    // marker bit. NAL units fit the packet budget -> single-NAL packets; larger ones use FU-A
    // fragmentation. The REAL client's decoder requires this exact wire format, so app-to-app and
    // app<->Discord video share one packetizer.
    public static List<byte[]> PacketizeH264(ReadOnlySpan<byte> au)
    {
        var outp = new List<byte[]>((au.Length + MaxFragmentData - 1) / MaxFragmentData + 4);
        var nals = SplitNals(au);
        for (int n = 0; n < nals.Count; n++)
        {
            var nal = nals[n];
            bool lastNal = n == nals.Count - 1;
            if (nal.Length <= MaxFragmentData)
            {
                outp.Add(nal.ToArray());          // single NAL unit packet
            }
            else
            {
                // FU-A: [FU indicator: F|NRI|28][FU header: S|E|R|type][payload chunks]
                byte fuIndicator = (byte)((nal[0] & 0xE0) | 28);
                byte nalType = (byte)(nal[0] & 0x1F);
                int chunk = MaxFragmentData - 2;
                int off = 1;
                while (off < nal.Length)
                {
                    int take = Math.Min(chunk, nal.Length - off);
                    int start = off == 1 ? 1 : 0;
                    int end = off + take >= nal.Length ? 1 : 0;
                    var p = new byte[2 + take];
                    p[0] = fuIndicator;
                    p[1] = (byte)(nalType | (start << 7) | (end << 6));
                    nal.AsSpan(off, take).CopyTo(p.AsSpan(2));
                    outp.Add(p);
                    off += take;
                }
            }
            // Marker belongs on the last packet of the last NAL. The caller sets it on its final
            // send; nothing to do here (the list is in order).
        }
        return outp;
    }

    /// Split an Annex-B AU (00 00 01 / 00 00 00 01 start codes) into NAL units WITHOUT their start
    /// codes, each starting with its NAL header byte.
    public static List<byte[]> SplitNals(ReadOnlySpan<byte> au)
    {
        var nals = new List<byte[]>();
        int i = 0;
        int start = -1;
        while (i + 3 <= au.Length)
        {
            // Check the 4-byte form FIRST: a 4-byte code also contains the 3-byte pattern at its
            // last three bytes, so a plain 3-byte match at position i+1 would mis-split.
            if (i + 4 <= au.Length && au[i] == 0 && au[i + 1] == 0 && au[i + 2] == 0 && au[i + 3] == 1)
            {
                if (start >= 0 && i > start) nals.Add(au.Slice(start, i - start).ToArray());
                i += 4;
                start = i;
            }
            else if (au[i] == 0 && au[i + 1] == 0 && au[i + 2] == 1)
            {
                if (start >= 0 && i > start) nals.Add(au.Slice(start, i - start).ToArray());
                i += 3;
                start = i;
            }
            else i++;
        }
        if (start >= 0 && au.Length - start > 0) nals.Add(au.Slice(start, au.Length - start).ToArray());
        return nals;
    }

    // Receiver-side H.264 reassembly: reconstructs complete access units from single-NAL and FU-A
    // RTP packets, delimited by the RTP marker bit (one AU per marker group). The DAVE layer has
    // already decrypted each RTP payload before it lands here.
    public sealed class H264Assembler
    {
        // Reconstructed AU with 00 00 00 01 separators — WebRTC's depacketizer uses 4-byte start
        // codes, and DAVE authenticates those exact bytes: the sender rewrites its 3-byte codes to
        // 4-byte before encrypting, so a receiver that re-adds 3-byte codes fails the AEAD tag.
        readonly List<byte> _au = new(16384);
        byte[]? _frag;                             // in-progress FU-A fragment (no header)
        byte _fragNalHeader;
        bool _fragActive, _fragEnd;

        /// Feed one decrypted RTP video payload. Returns a complete AU when the marker group ends.
        public byte[]? Feed(ReadOnlySpan<byte> payload, bool marker)
        {
            if (payload.Length == 0) return null;
            int type = payload[0] & 0x1F;
            if (type == 28)                      // FU-A
            {
                bool s = (payload[1] & 0x80) != 0, e = (payload[1] & 0x40) != 0;
                byte nalHeader = (byte)((payload[0] & 0xE0) | (payload[1] & 0x1F));
                if (s)
                {
                    _fragNalHeader = nalHeader;
                    _frag = payload.Slice(2).ToArray();
                    _fragActive = true;
                    _fragEnd = e;
                }
                else if (_fragActive)
                {
                    var old = _frag!;
                    var joined = new byte[old.Length + payload.Length - 2];
                    old.CopyTo(joined, 0);
                    payload.Slice(2).CopyTo(joined.AsSpan(old.Length));
                    _frag = joined;
                    _fragEnd = e;
                }
                if (_fragActive && _fragEnd && _frag != null)
                {
                    AppendNal(_fragNalHeader, _frag);
                    _frag = null;
                    _fragActive = false;
                }
            }
            else if (type is >= 1 and <= 23)     // single NAL unit packet
            {
                AppendNal(payload[0], payload[1..]);
            }
            else if (type == 24)                 // STAP-A aggregation: [size 2][NAL]...
            {
                // The real client sends SPS/PPS in a STAP-A at the head of every keyframe. This
                // used to be dropped (type 24 fell through both branches), so the reassembled
                // frame was missing the SPS/PPS bytes that the DAVE ranges reference — every
                // keyframe failed the GCM tag (black tile) even though the supplement parsed.
                int off = 1;
                while (off + 2 <= payload.Length)
                {
                    int naluLen = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(off, 2));
                    off += 2;
                    if (naluLen == 0 || off + naluLen > payload.Length) break;   // malformed: drop rest
                    AppendNal(payload[off], payload.Slice(off + 1, naluLen - 1));
                    off += naluLen;
                }
            }
            if (marker)
            {
                if (_au.Count == 0) return null;
                var au = _au.ToArray();
                _au.Clear();
                return au;
            }
            return null;
        }

        void AppendNal(byte header, ReadOnlySpan<byte> rest)
        {
            _au.Add(0); _au.Add(0); _au.Add(0); _au.Add(1);
            _au.Add(header);
            foreach (var b in rest) _au.Add(b);
        }
    }
}
