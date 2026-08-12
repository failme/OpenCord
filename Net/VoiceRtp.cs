using System.Buffers.Binary;
using System.Security.Cryptography;

namespace ClaudeScord;

// Voice-transport primitives: the RTP header, ULEB128, and the two AEAD ciphers the DAVE transport
// encryption uses (aead_aes256_gcm_rtpsize / aead_xchacha20_poly1305_rtpsize). Everything here is
// pure and static so SelfTest can pin it against published vectors.
//
// Both "rtpsize" modes use the same nonce rule: a 24-byte nonce whose first four bytes are a
// LITTLE-ENDIAN incrementing counter, the rest zeroed. The counter is appended to the end of the
// UDP payload in the same little-endian form. Captured real-client traffic proves the convention:
// the peer's trailers run 00 00 00 00, 01 00 00 00, 03 00 00 00, ... (LE), and its packets only
// decrypt with the LE nonce. We originally wrote big-endian; every packet after counter 0 failed
// on the peer's side (its receive counter is LE), which read as total silence. (The AES-GCM
// rtpsize variant shares the layout; DSharpPlus's receive path reading the nonce off the RTP
// header instead of the trailer is a bug for these modes.)
//

//
// The deprecated xsalsa20 modes are gone as of Nov 2024; the server always offers at least
// aead_xchacha20_poly1305_rtpsize and prefers aead_aes256_gcm_rtpsize when the hardware has AES.
static class VoiceRtp
{
    public const int HeaderSize = 12;
    const byte RtpNoExtension = 0x80;   // V=2, no X bit
    const byte RtpExtension = 0x90;     // V=2, X bit set (the modern client's audio header)
    const byte RtpPayloadType = 0x78;   // 120 = Opus audio

    // The 4 bytes the real Discord client appends after the 12-byte RTP header on every audio
    // packet: an RFC 8285-style extension header (profile 0xBEDE, length 0x0002) that claims two
    // words of extension data but actually carries NONE — the ciphertext starts immediately after.
    // The full 16 bytes are the AEAD AAD; verified against captured real-client traffic with
    // pynacl (a 12-byte AAD fails every packet, 16-byte decrypts 100%).
    public const int ExtHeaderSize = 4;
    static readonly byte[] ExtHeaderBytes = { 0xBE, 0xDE, 0x00, 0x02 };

    // ── RTP header ──────────────────────────────────────────────────────────────────────────────
    public static byte[] EncodeHeader(ushort sequence, uint timestamp, uint ssrc)
        => EncodeHeader(sequence, timestamp, ssrc, RtpPayloadType, marker: false);

    public static byte[] EncodeHeader(ushort sequence, uint timestamp, uint ssrc, byte payloadType, bool marker)
    {
        var h = new byte[HeaderSize];
        h[0] = RtpNoExtension;
        h[1] = (byte)(payloadType | (marker ? 0x80 : 0));   // RTP M bit is bit 7 of the PT byte
        BinaryPrimitives.WriteUInt16BigEndian(h.AsSpan(2), sequence);
        BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(4), timestamp);
        BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(8), ssrc);
        return h;
    }

    // The real client's audio header: the 12-byte RTP header with the X bit set, plus the 4-byte
    // BE DE 00 02 extension header. Both are included in the AEAD AAD.
    public static byte[] EncodeExtHeader(ushort sequence, uint timestamp, uint ssrc, byte payloadType, bool marker)
    {
        var h = new byte[HeaderSize + ExtHeaderSize];
        h[0] = RtpExtension;
        h[1] = (byte)(payloadType | (marker ? 0x80 : 0));
        BinaryPrimitives.WriteUInt16BigEndian(h.AsSpan(2), sequence);
        BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(4), timestamp);
        BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(8), ssrc);
        ExtHeaderBytes.CopyTo(h.AsSpan(HeaderSize));
        return h;
    }

    // The authenticated header length of a received packet. The modern client's audio packets
    // carry the 4-byte BE DE 00 02 extension header (X bit set) with no extension data; RTCP
    // packets use a plain 8-byte header. Anything else is a plain 12-byte RTP header.
    //
    // RTCP payload types are 8-bit values (SR=0xC8, RR=0xC9) — masking with 0x7F would turn
    // them into 72/73, so they are matched RAW. No Discord RTP payload type is 0xC8/0xC9, so
    // the check is unambiguous even against the RTP marker bit (audio marker 0xF8 is a plain
    // audio packet, never 0xC8/0xC9).
    public static int HeaderLengthFor(ReadOnlySpan<byte> p)
    {
        if (p.Length < HeaderSize) return 0;
        if (IsRtcp(p)) return 8;                                     // RTCP SR/RR
        if ((p[0] & 0x10) != 0) return HeaderSize + ExtHeaderSize;   // X bit: + BE DE 00 02
        return HeaderSize;
    }

    // RTCP: sender/receiver reports (200/201 = 0xC8/0xC9), SDES (202 = 0xCA), BYE (203 = 0xCB),
    // APP (204 = 0xCC — the real client emits APP packets for media sink feedback), and feedback
    // (205 NACK, 206 PLI/REMB, 207 = 0xCD/0xCE/0xCF). All ride the same UDP socket, none carry
    // media, and none are AEAD-encrypted — a client that feeds them to the transport decrypt
    // would count every one as a failure. (A real Discord peer sends PLI + receiver reports
    // continuously for the streams it subscribes to, so this check is what keeps transportFailures
    // from growing on a healthy call.)
    public static bool IsRtcp(ReadOnlySpan<byte> p) =>
        p.Length >= 2 && (p[1] >= 0xC8 && p[1] <= 0xCF);

    /// (sequence, timestamp, ssrc). Returns false when the bytes are not a Discord audio RTP header.
    public static bool DecodeHeader(ReadOnlySpan<byte> p, out ushort sequence, out uint timestamp, out uint ssrc)
    {
        if (!DecodeAnyHeader(p, out sequence, out timestamp, out ssrc, out var pt, out _)) return false;
        return pt == RtpPayloadType;
    }

    /// (sequence, timestamp, ssrc, payloadType, marker) for ANY Discord RTP packet — audio or video.
    /// Video packets carry their own payload type (see VideoRtp), so the receive path must not
    /// reject them just because byte 1 isn't 0x78.
    ///
    /// Accepts ANY V=2 header: 0x80 (plain), 0x90 (X bit), 0xA0 (P bit — the real client's video
    /// packets carry RTP padding, which this used to REJECT: the packet was then misrouted to the
    /// audio path, fed to the DAVE layer as an "Opus" frame, and played as garbage — the peer's
    /// camera never rendered AND the audio path was poisoned), and 0xB0 (P + X).
    public static bool DecodeAnyHeader(ReadOnlySpan<byte> p, out ushort sequence, out uint timestamp,
                                       out uint ssrc, out byte payloadType, out bool marker)
    {
        sequence = 0; timestamp = 0; ssrc = 0; payloadType = 0; marker = false;
        if (p.Length < HeaderSize || (p[0] & 0xC0) != 0x80) return false;
        sequence = BinaryPrimitives.ReadUInt16BigEndian(p[2..]);
        timestamp = BinaryPrimitives.ReadUInt32BigEndian(p[4..]);
        ssrc = BinaryPrimitives.ReadUInt32BigEndian(p[8..]);
        payloadType = (byte)(p[1] & 0x7F);
        marker = (p[1] & 0x80) != 0;
        return true;
    }

    // ── ULEB128 (the DAVE frame nonce / unencrypted-range encoding) ─────────────────────────────
    public static int Uleb128(uint value, Span<byte> target)
    {
        int n = 0;
        while (value >= 0x80) { target[n++] = (byte)(0x80 | (value & 0x7F)); value >>= 7; }
        target[n++] = (byte)value;
        return n;
    }

    public static bool TryUleb128(ReadOnlySpan<byte> p, out uint value, out int consumed)
    {
        value = 0; consumed = 0;
        int shift = 0;
        for (int i = 0; i < p.Length && i < 5; i++)
        {
            byte b = p[i];
            value |= (uint)(b & 0x7F) << shift;
            consumed++;
            if ((b & 0x80) == 0) return true;
            shift += 7;
        }
        return false;   // overlong (>32 bits) or truncated
    }

    // ── GHASH: GF(2^128) with the GCM reduction polynomial x^128 + x^7 + x^2 + x + 1 ─────────────
    // The GCM multiply as specified in NIST SP 800-38D Algorithm 1 (the right-shift variant): bit i
    // of a block counts down from the first byte's MSB, and V is right-shifted with R = 0xE1 in the
    // first byte when the dropped bit is set. Verified against System.Security.Cryptography.AesGcm
    // by SelfTest; a hand-rolled left-shift mirror with a low-byte 0x87 constant did NOT agree with
    // the platform, so this is the form that ships.
    static void XorInto(byte[] z, byte[] v)
    {
        for (int j = 0; j < 16; j++) z[j] ^= v[j];
    }

    static byte[] GfMul(byte[] a, byte[] b)
    {
        var z = new byte[16];
        var v = (byte[])b.Clone();
        for (int i = 0; i < 128; i++)
        {
            if (((a[i >> 3] >> (7 - (i & 7))) & 1) != 0) XorInto(z, v);
            bool drop = (v[15] & 1) != 0;
            for (int j = 15; j >= 1; j--) v[j] = (byte)((v[j] >> 1) | (v[j - 1] << 7));
            v[0] >>= 1;
            if (drop) v[0] ^= 0xE1;
        }
        return z;
    }

    // GHASH: Y_0 = 0; Y_i = (Y_{i-1} XOR X_i) * H. Blocks are the big-endian byte strings the
    // protocol appends (AAD padded, ciphertext padded, then the two 64-bit bit-lengths).
    static byte[] Ghash(byte[] h, ReadOnlySpan<byte> data)
    {
        var y = new byte[16];
        for (int off = 0; off + 16 <= data.Length; off += 16)
        {
            for (int j = 0; j < 16; j++) y[j] ^= data[off + j];
            y = GfMul(y, h);
        }
        return y;
    }

    // ── AES-GCM by hand ─────────────────────────────────────────────────────────────────────────
    // .NET's AesGcm only takes 12-byte nonces; DAVE's transport mode needs a 24-byte nonce (the
    // 4-byte counter + 20 zeros), which per NIST SP 800-38D uses the GHASH-derived J0 path. Building
    // GCM on AES-ECB keeps the nonce length arbitrary and lets SelfTest cross-check the 12-byte case
    // against System.Security.Cryptography.AesGcm.
    static byte[] AesBlock(byte[] key, ReadOnlySpan<byte> block)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        using var enc = aes.CreateEncryptor();
        var outp = new byte[16];
        enc.TransformBlock(block.ToArray(), 0, 16, outp, 0);
        return outp;
    }

    static byte[] GcmCtr(byte[] key, byte[] j0, ReadOnlySpan<byte> input)
    {
        var outp = new byte[input.Length];
        var counter = (byte[])j0.Clone();
        Increment(counter);                 // CTR starts at inc32(J0)
        int off = 0;
        while (off < input.Length)
        {
            var ks = AesBlock(key, counter);
            int n = Math.Min(16, input.Length - off);
            for (int i = 0; i < n; i++) outp[off + i] = (byte)(input[off + i] ^ ks[i]);
            Increment(counter);
            off += n;
        }
        return outp;

        static void Increment(byte[] cb)   // big-endian low 32 bits
        {
            for (int i = cb.Length - 1; i >= cb.Length - 4; i--)
                if (++cb[i] != 0) break;
        }
    }

    static byte[] GcmCore(byte[] key, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> input, ReadOnlySpan<byte> aad,
                          bool decrypt, out byte[] tag)
    {
        var hBlock = AesBlock(key, new byte[16]);   // H = E(K, 0^128)

        byte[] j0;
        if (nonce.Length == 12)
        {
            j0 = new byte[16];
            nonce.CopyTo(j0.AsSpan());
            j0[15] = 1;                     // IV || 0^31 || 1
        }
        else
        {
            // J0 = GHASH(IV || 0^(s+64) || [len(IV)]_64), len(IV) in bits, s = 128*ceil(len/128) - len.
            // The (s+64) zero bits are the padding to the block boundary plus 64 more; for a 24-byte
            // nonce that is 16 zero bytes, so the GHASH string is 24 + 16 + 8 = 48 bytes = 3 blocks.
            int padLen = ((nonce.Length + 15) / 16) * 16;
            var buf = new byte[padLen + 16];
            nonce.CopyTo(buf.AsSpan());
            BinaryPrimitives.WriteUInt64BigEndian(buf.AsSpan(padLen + 8), (ulong)nonce.Length * 8);
            j0 = Ghash(hBlock, buf);
        }

        var output = GcmCtr(key, j0, input);   // pt -> ct on encrypt, ct -> pt on decrypt

        // S = GHASH(AAD || 0^s || CT || 0^s || [len(AAD)]_64 || [len(CT)]_64) (lengths in bits).
        // The MAC always runs over the CIPHERTEXT: on encrypt that is `output`, on decrypt the
        // ciphertext is the `input` (the freshly decrypted bytes must never be MAC'd).
        var macText = decrypt ? input : output;
        int aadPad = ((aad.Length + 15) / 16) * 16, ctPad = ((macText.Length + 15) / 16) * 16;
        var macBuf = new byte[aadPad + ctPad + 16];
        aad.CopyTo(macBuf.AsSpan());
        macText.CopyTo(macBuf.AsSpan(aadPad));
        BinaryPrimitives.WriteUInt64BigEndian(macBuf.AsSpan(aadPad + ctPad), (ulong)aad.Length * 8);
        BinaryPrimitives.WriteUInt64BigEndian(macBuf.AsSpan(aadPad + ctPad + 8), (ulong)macText.Length * 8);
        var s = Ghash(hBlock, macBuf);

        var ek = AesBlock(key, j0);
        tag = new byte[16];                 // T = S XOR E(K, J0), full 16 bytes
        for (int i = 0; i < 16; i++) tag[i] = (byte)(s[i] ^ ek[i]);
        if (DebugGcm && nonce.Length == 12)
            System.Console.WriteLine("  debug s_mine=" + Convert.ToHexString(s) + " aad=" + aad.Length + " ct=" + macText.Length);
        return output;
    }

    /// Debug seams for SelfTest while pinning the crypto against known answers.
    public static bool DebugGcm;
    public static bool DebugPoly;

    /// AEAD-encrypt with a full 16-byte tag appended: ct || tag(16). Arbitrary nonce length.
    public static byte[] GcmEncryptWithTag(byte[] key, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> aad)
    {
        var ct = GcmCore(key, nonce, plaintext, aad, decrypt: false, out var tag);
        var outp = new byte[ct.Length + 16];
        ct.CopyTo(outp.AsSpan());
        tag.CopyTo(outp.AsSpan(ct.Length));
        return outp;
    }

    /// Returns null when the tag fails. ciphertextAndTag = ct || tag(16).
    public static byte[]? GcmDecrypt(byte[] key, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> ciphertextAndTag, ReadOnlySpan<byte> aad)
    {
        if (ciphertextAndTag.Length < 16) return null;
        var ct = ciphertextAndTag[..^16];
        var want = ciphertextAndTag[^16..];
        var pt = GcmCore(key, nonce, ct, aad, decrypt: true, out var got);
        if (!CryptographicOperations.FixedTimeEquals(got, want)) return null;
        return pt;
    }

    // ── DAVE protocol-frame AEAD: AES-128-GCM with a truncated tag (8 of 16 bytes) ────────────────
    // .NET's AesGcm requires >= 12-byte tags, so DAVE's 64-bit tags go through the hand-rolled GCM
    // (the same one the transport uses; only the tag length differs).
    public static byte[] GcmEncryptTrunc(byte[] key, ReadOnlySpan<byte> nonce,
                                         ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> aad, int tagLen)
    {
        var ct = GcmCore(key, nonce, plaintext, aad, decrypt: false, out var tag);
        var outp = new byte[ct.Length + tagLen];
        ct.CopyTo(outp.AsSpan());
        tag.AsSpan(0, tagLen).CopyTo(outp.AsSpan(ct.Length));
        return outp;
    }

    /// Decrypt verifying only the first wantTag.Length bytes of the GCM tag (DAVE truncates to 8).
    public static byte[]? GcmDecryptTrunc(byte[] key, ReadOnlySpan<byte> nonce,
                                          ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> aad,
                                          ReadOnlySpan<byte> wantTag)
    {
        if (wantTag.Length > 16) return null;
        var pt = GcmCore(key, nonce, ciphertext, aad, decrypt: true, out var got);
        if (!CryptographicOperations.FixedTimeEquals(got.AsSpan(0, wantTag.Length), wantTag)) return null;
        return pt;
    }

    // ── ChaCha20 family (for aead_xchacha20_poly1305_rtpsize) ──────────────────────────────────
    static void ChaChaBlock(uint[] state, Span<byte> outp)
    {
        var x = (uint[])state.Clone();
        for (int i = 0; i < 10; i++)
        {
            Quarter(0, 4, 8, 12); Quarter(1, 5, 9, 13); Quarter(2, 6, 10, 14); Quarter(3, 7, 11, 15);
            Quarter(0, 5, 10, 15); Quarter(1, 6, 11, 12); Quarter(2, 7, 8, 13); Quarter(3, 4, 9, 14);
        }
        for (int i = 0; i < 16; i++) BinaryPrimitives.WriteUInt32LittleEndian(outp[(i * 4)..], x[i] + state[i]);

        void Quarter(int a, int b, int c, int d)
        {
            x[a] += x[b]; x[d] = Rotl(x[d] ^ x[a], 16);
            x[c] += x[d]; x[b] = Rotl(x[b] ^ x[c], 12);
            x[a] += x[b]; x[d] = Rotl(x[d] ^ x[a], 8);
            x[c] += x[d]; x[b] = Rotl(x[b] ^ x[c], 7);
        }
        static uint Rotl(uint v, int n) => (v << n) | (v >> (32 - n));
    }

    // IETF ChaCha20 state: 4 constants, 8 key words, counter at word 12, nonce8 at words 13-14,
    // word 15 zero — the layout XChaCha20 uses with the 8-byte trailing nonce.
    static uint[] ChaChaState(byte[] key, uint counter, ReadOnlySpan<byte> nonce8)
    {
        var st = new uint[16];
        st[0] = 0x61707865; st[1] = 0x3320646e; st[2] = 0x79622d32; st[3] = 0x6b206574;
        for (int i = 0; i < 8; i++) st[4 + i] = BinaryPrimitives.ReadUInt32LittleEndian(key[(i * 4)..]);
        st[12] = counter;
        st[13] = BinaryPrimitives.ReadUInt32LittleEndian(nonce8[..4]);
        st[14] = BinaryPrimitives.ReadUInt32LittleEndian(nonce8[4..]);
        st[15] = 0;
        return st;
    }

    /// HChaCha20: 20 rounds with counter 0 and NO final addition; the output is words 0-3 and 12-15
    /// of the final state. The full 16 nonce bytes occupy state words 12-15. Expands the XChaCha
    /// subkey from the first 16 bytes of the 24-byte nonce.
    public static byte[] HChaCha20(byte[] key, ReadOnlySpan<byte> nonce16)
    {
        var x = new uint[16];
        x[0] = 0x61707865; x[1] = 0x3320646e; x[2] = 0x79622d32; x[3] = 0x6b206574;
        for (int i = 0; i < 8; i++) x[4 + i] = BinaryPrimitives.ReadUInt32LittleEndian(key[(i * 4)..]);
        for (int i = 0; i < 4; i++) x[12 + i] = BinaryPrimitives.ReadUInt32LittleEndian(nonce16[(i * 4)..]);
        for (int i = 0; i < 10; i++)
        {
            Qr(0, 4, 8, 12); Qr(1, 5, 9, 13); Qr(2, 6, 10, 14); Qr(3, 7, 11, 15);
            Qr(0, 5, 10, 15); Qr(1, 6, 11, 12); Qr(2, 7, 8, 13); Qr(3, 4, 9, 14);
        }
        var outp = new byte[32];
        for (int i = 0; i < 4; i++)
        {
            uint v = x[i], w = x[12 + i];
            int o1 = i * 4, o2 = 16 + i * 4;
            outp[o1] = (byte)v; outp[o1 + 1] = (byte)(v >> 8); outp[o1 + 2] = (byte)(v >> 16); outp[o1 + 3] = (byte)(v >> 24);
            outp[o2] = (byte)w; outp[o2 + 1] = (byte)(w >> 8); outp[o2 + 2] = (byte)(w >> 16); outp[o2 + 3] = (byte)(w >> 24);
        }
        return outp;

        void Qr(int a, int b, int c, int d)
        {
            x[a] += x[b]; x[d] = Rotl(x[d] ^ x[a], 16);
            x[c] += x[d]; x[b] = Rotl(x[b] ^ x[c], 12);
            x[a] += x[b]; x[d] = Rotl(x[d] ^ x[a], 8);
            x[c] += x[d]; x[b] = Rotl(x[b] ^ x[c], 7);
        }
        static uint Rotl(uint v, int n) => (v << n) | (v >> (32 - n));
    }

    /// Test seam: the raw ChaCha20 64-byte keystream block (RFC 8439 layout: counter at word 12,
    /// words 13-14 = nonce, word 15 = 0). SelfTest pins the RFC 8439 §2.3.2 known answer here.
    public static byte[] ChaChaBlockForTest(byte[] key, uint counter, ReadOnlySpan<byte> nonce8)
    {
        var outp = new byte[64];
        ChaChaBlock(ChaChaState(key, counter, nonce8), outp);
        return outp;
    }

    /// XChaCha20 keystream over input at the given counter (0 for the Poly1305 key, 1 for data).
    static byte[] XChaChaStream(byte[] key, ReadOnlySpan<byte> nonce24, uint counter, ReadOnlySpan<byte> input)
    {
        var subkey = HChaCha20(key, nonce24[..16]);
        var state = ChaChaState(subkey, counter, nonce24[16..]);
        var outp = new byte[input.Length];
        var block = new byte[64];
        int off = 0;
        while (off < input.Length)
        {
            ChaChaBlock(state, block);
            for (int i = 0; i < 64 && off + i < input.Length; i++) outp[off + i] = (byte)(input[off + i] ^ block[i]);
            state[12]++;
            off += 64;
        }
        return outp;
    }

    // ── Poly1305 (RFC 8439) ─────────────────────────────────────────────────────────────────────
    // The classic 5x26-bit-limb accumulation ("poly1305-donna" structure).
    public static byte[] Poly1305(ReadOnlySpan<byte> key32, ReadOnlySpan<byte> message)
    {
        // The clamped 128-bit key is read through five overlapping 32-bit little-endian windows
        // starting at bytes 0, 3, 6, 9, 12; each is shifted so a 26-bit limb lands at the bottom.
        // The odd masks clear the extra bits that survive the shifts (donna's exact constants).
        uint r0 = ReadU32(key32, 0) & 0x3ffffff;
        uint r1 = (ReadU32(key32, 3) >> 2) & 0x3ffff03;
        uint r2 = (ReadU32(key32, 6) >> 4) & 0x3ffc0ff;
        uint r3 = (ReadU32(key32, 9) >> 6) & 0x3f03fff;
        uint r4 = (ReadU32(key32, 12) >> 8) & 0x00fffff;
        uint s1 = r1 * 5, s2 = r2 * 5, s3 = r3 * 5, s4 = r4 * 5;
        ulong h0 = 0, h1 = 0, h2 = 0, h3 = 0, h4 = 0;

        // The message is processed in 16-byte blocks; each block contributes its 128 little-endian
        // bits plus the implicit 2^128 bit, split across the five 26-bit limbs:
        //   h0 = bits  0-25   h1 = bits 26-51   h2 = bits 52-77   h3 = bits 78-103   h4 = bits 104-129
        // Reading the 32-bit window 3 bytes early and shifting right slides each limb into place.
        if (DebugPoly) System.Console.WriteLine($"  poly r0={r0:x} r1={r1:x} r2={r2:x} r3={r3:x} r4={r4:x}");
        int off = 0;
        while (off < message.Length)
        {
            // The message is a little-endian 130-bit number built from 16-byte blocks, each with an
            // implicit high bit. For a FULL block that bit is 2^128 (byte 16); for the final PARTIAL
            // block of n bytes it is 2^(8n), which must be placed at byte n — otherwise the tag is
            // wrong for any message whose length isn't a multiple of 16.
            var block = new byte[17];
            int n = Math.Min(16, message.Length - off);
            message.Slice(off, n).CopyTo(block);
            off += n;
            if (n == 16) block[16] = 1;
            else block[n] = 1;

            h0 += ReadU32(block, 0) & 0x3ffffff;
            h1 += (ReadU32(block, 3) >> 2) & 0x3ffffff;
            h2 += (ReadU32(block, 6) >> 4) & 0x3ffffff;
            h3 += (ReadU32(block, 9) >> 6) & 0x3ffffff;
            h4 += (uint)(block[13] | (block[14] << 8) | (block[15] << 16)) + (uint)(n == 16 ? (1u << 24) : 0);

            ulong d0 = h0 * r0 + h1 * s4 + h2 * s3 + h3 * s2 + h4 * s1;
            ulong d1 = h0 * r1 + h1 * r0 + h2 * s4 + h3 * s3 + h4 * s2;
            ulong d2 = h0 * r2 + h1 * r1 + h2 * r0 + h3 * s4 + h4 * s3;
            ulong d3 = h0 * r3 + h1 * r2 + h2 * r1 + h3 * r0 + h4 * s4;
            ulong d4 = h0 * r4 + h1 * r3 + h2 * r2 + h3 * r1 + h4 * r0;

            uint c;
            c = (uint)(d0 >> 26); h0 = (uint)(d0 & 0x3ffffff); d1 += c;
            c = (uint)(d1 >> 26); h1 = (uint)(d1 & 0x3ffffff); d2 += c;
            c = (uint)(d2 >> 26); h2 = (uint)(d2 & 0x3ffffff); d3 += c;
            c = (uint)(d3 >> 26); h3 = (uint)(d3 & 0x3ffffff); d4 += c;
            c = (uint)(d4 >> 26); h4 = (uint)(d4 & 0x3ffffff); h0 += (ulong)c * 5;
            c = (uint)(h0 >> 26); h0 &= 0x3ffffff; h1 += c;
            if (DebugPoly) System.Console.WriteLine($"  poly block n={n} h0={h0:x} h1={h1:x} h2={h2:x} h3={h3:x} h4={h4:x}");
        }

        // Fully carry h: the block loop ends with h1..h4 at most one carry hot. Normalize every
        // limb below 2^26 before the final reduction (donna's finish step).
        ulong cc;
        cc = h1 >> 26; h1 &= 0x3ffffff; h2 += cc;
        cc = h2 >> 26; h2 &= 0x3ffffff; h3 += cc;
        cc = h3 >> 26; h3 &= 0x3ffffff; h4 += cc;
        cc = h4 >> 26; h4 &= 0x3ffffff; h0 += cc * 5;
        cc = h0 >> 26; h0 &= 0x3ffffff; h1 += cc;

        // Candidate sum g = h + (2^130 - 5), limb by limb. If the top limb overflows 2^26 the sum
        // wrapped past 2^130, meaning h was already < p and must be kept as-is; otherwise the sum
        // is the reduced value. mask is all-ones in the take-the-sum case, zero in the keep-h case.
        ulong g0 = h0 + 5, c0 = g0 >> 26; g0 &= 0x3ffffff;
        ulong g1 = h1 + c0, c1 = g1 >> 26; g1 &= 0x3ffffff;
        ulong g2 = h2 + c1, c2 = g2 >> 26; g2 &= 0x3ffffff;
        ulong g3 = h3 + c2, c3 = g3 >> 26; g3 &= 0x3ffffff;
        ulong g4 = h4 + c3 - (1UL << 26);
        uint mask = (uint)(g4 >> 63) - 1;
        g4 &= 0x3ffffff;
        g0 = (g0 & mask) | (h0 & ~mask); g1 = (g1 & mask) | (h1 & ~mask);
        g2 = (g2 & mask) | (h2 & ~mask); g3 = (g3 & mask) | (h3 & ~mask);
        g4 = (g4 & mask) | (h4 & ~mask);

        if (DebugPoly) System.Console.WriteLine($"  poly final g0={g0:x} g1={g1:x} g2={g2:x} g3={g3:x} g4={g4:x} mask={mask:x}");

        // Pack the five limbs into four little-endian 32-bit words, truncating each to 32 bits
        // BEFORE the s addition. The overlapping limb layout puts a limb's spill bits in both
        // neighbouring words; an untruncated carry chain would add those spill bits twice.
        // (donna casts each h-word to uint32_t before adding the s words.)
        ulong f0 = (uint)(g0 | (g1 << 26));
        ulong f1 = (uint)((g1 >> 6) | (g2 << 20));
        ulong f2 = (uint)((g2 >> 12) | (g3 << 14));
        ulong f3 = (uint)((g3 >> 18) | (g4 << 8));
        // The s part of the key is bytes 16-31: four little-endian words added to the final tag.
        uint k0 = ReadU32(key32, 16), k1 = ReadU32(key32, 20), k2 = ReadU32(key32, 24), k3 = ReadU32(key32, 28);

        ulong t0 = f0 + k0, cc0 = t0 >> 32;
        ulong t1 = f1 + k1 + cc0, cc1 = t1 >> 32;
        ulong t2 = f2 + k2 + cc1, cc2 = t2 >> 32;
        ulong t3 = f3 + k3 + cc2;

        var tag = new byte[16];
        WriteU32(tag, 0, (uint)t0);
        WriteU32(tag, 4, (uint)t1);
        WriteU32(tag, 8, (uint)t2);
        WriteU32(tag, 12, (uint)t3);
        return tag;

        static uint ReadU32(ReadOnlySpan<byte> p, int o) => (uint)(p[o] | (p[o + 1] << 8) | (p[o + 2] << 16) | (p[o + 3] << 24));
        static void WriteU32(Span<byte> p, int o, uint v) { p[o] = (byte)v; p[o + 1] = (byte)(v >> 8); p[o + 2] = (byte)(v >> 16); p[o + 3] = (byte)(v >> 24); }
    }

    /// XChaCha20-Poly1305 AEAD (draft-irtf-cfrg-xchacha), 24-byte nonce, 16-byte tag. The DAVE
    /// transport counter nonce (4 bytes + 20 zeros) is a valid 24-byte nonce for this construction.
    public static byte[] XChaCha20Poly1305Encrypt(byte[] key32, ReadOnlySpan<byte> nonce24, ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> aad)
    {
        var polyKey = XChaChaStream(key32, nonce24, 0, new byte[64])[..32];
        var ciphertext = XChaChaStream(key32, nonce24, 1, plaintext);
        var tag = Poly1305(polyKey, BuildMacData(aad, ciphertext));
        var outp = new byte[ciphertext.Length + 16];
        ciphertext.CopyTo(outp.AsSpan());
        tag.CopyTo(outp.AsSpan(ciphertext.Length));
        return outp;
    }

    public static byte[]? XChaCha20Poly1305Decrypt(byte[] key32, ReadOnlySpan<byte> nonce24, ReadOnlySpan<byte> ciphertextAndTag, ReadOnlySpan<byte> aad)
    {
        if (ciphertextAndTag.Length < 16) return null;
        var ct = ciphertextAndTag[..^16];
        var want = ciphertextAndTag[^16..];
        var polyKey = XChaChaStream(key32, nonce24, 0, new byte[64])[..32];
        var got = Poly1305(polyKey, BuildMacData(aad, ct));
        if (!CryptographicOperations.FixedTimeEquals(got, want)) return null;
        return XChaChaStream(key32, nonce24, 1, ct);
    }

    static byte[] BuildMacData(ReadOnlySpan<byte> aad, ReadOnlySpan<byte> ct)
    {
        int aadPad = ((aad.Length + 15) / 16) * 16, ctPad = ((ct.Length + 15) / 16) * 16;
        var buf = new byte[aadPad + ctPad + 16];
        aad.CopyTo(buf);
        ct.CopyTo(buf.AsSpan(aadPad));
        BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(aadPad + ctPad), (ulong)aad.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(aadPad + ctPad + 8), (ulong)ct.Length);
        return buf;
    }

    // ── Discord's transport-encrypted packet ────────────────────────────────────────────────────
    // Send:  [RTP header (12 or 16)] | AEAD(opus || tag16) | counter(4 BE)
    // Audio uses the 16-byte header (X bit + BE DE 00 02) that the real client authenticates;
    // video stays on the plain 12-byte header until its on-wire format is verified.
    // Receive: strip the 4-byte counter trailer, rebuild the 24-byte nonce [counter, 20 zeros].
    public static byte[] ProtectPacket(byte[] key, bool useAes, ushort sequence, uint timestamp, uint ssrc,
                                       ReadOnlySpan<byte> opus, uint counter)
        => ProtectPacket(key, useAes, sequence, timestamp, ssrc, opus, counter, RtpPayloadType, marker: false, extHeader: true);

    // The video variant: same transport AEAD (header as AAD, 4-byte counter trailer), but a custom
    // payload type and the RTP marker bit that flags the last fragment of a multi-packet video frame.
    public static byte[] ProtectPacket(byte[] key, bool useAes, ushort sequence, uint timestamp, uint ssrc,
                                       ReadOnlySpan<byte> opus, uint counter, byte payloadType, bool marker)
        => ProtectPacket(key, useAes, sequence, timestamp, ssrc, opus, counter, payloadType, marker, extHeader: false);

    public static byte[] ProtectPacket(byte[] key, bool useAes, ushort sequence, uint timestamp, uint ssrc,
                                       ReadOnlySpan<byte> opus, uint counter, byte payloadType, bool marker,
                                       bool extHeader)
    {
        // The AAD is the RTP header — the real client authenticates the full 16-byte header
        // (12-byte RTP with the X bit + the BE DE 00 02 extension header) on audio packets.
        // Verified against captured real-client traffic: with a 12-byte AAD every received
        // packet fails the tag; with 16 bytes every packet decrypts.
        var nonce = new byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(nonce, counter);   // LE, matching the real client
        var header = extHeader ? EncodeExtHeader(sequence, timestamp, ssrc, payloadType, marker)
                               : EncodeHeader(sequence, timestamp, ssrc, payloadType, marker);
        var body = useAes ? GcmEncryptWithTag(key, nonce, opus, header)
                          : XChaCha20Poly1305Encrypt(key, nonce, opus, header);
        int hlen = header.Length;
        var packet = new byte[hlen + body.Length + 4];
        header.CopyTo(packet.AsSpan());
        body.CopyTo(packet.AsSpan(hlen));
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(hlen + body.Length), counter);
        return packet;
    }

    // Video packet with the real client's RTP header extension: the 12-byte RTP header carries
    // the X bit, then a 4-byte BE DE + words extension header — the full 16 bytes are the AEAD
    // AAD, and the extension DATA rides INSIDE the ciphertext (verified against captured
    // real-client traffic and the dolfies reference: AAD=16 authenticates, and the receiver
    // strips `words*4` bytes off the decrypted payload front). The real client's video receive
    // is built around this extended shape, so a bare 12-byte-header video packet is at best
    // decryptable-by-luck there; this matches the on-wire format exactly.
    public static byte[] ProtectVideoPacket(byte[] key, bool useAes, ushort sequence, uint timestamp,
                                            uint ssrc, ReadOnlySpan<byte> extData, ReadOnlySpan<byte> media,
                                            uint counter, byte payloadType, bool marker)
    {
        var header = new byte[HeaderSize + ExtHeaderSize];
        header[0] = RtpExtension;                                  // V=2, X bit
        header[1] = (byte)(payloadType | (marker ? 0x80 : 0));
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(2), sequence);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), timestamp);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8), ssrc);
        header[12] = 0xBE; header[13] = 0xDE;
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(14), (ushort)(extData.Length / 4));

        var payload = new byte[extData.Length + media.Length];
        extData.CopyTo(payload.AsSpan());
        media.CopyTo(payload.AsSpan(extData.Length));

        var nonce = new byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(nonce, counter);   // LE, matching the real client
        var body = useAes ? GcmEncryptWithTag(key, nonce, payload, header)
                          : XChaCha20Poly1305Encrypt(key, nonce, payload, header);
        var packet = new byte[header.Length + body.Length + 4];
        header.CopyTo(packet.AsSpan());
        body.CopyTo(packet.AsSpan(header.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(header.Length + body.Length), counter);
        return packet;
    }

    /// Returns the decrypted Opus frame, or null on failure. Handles both aead modes and the
    /// 8/12/16-byte header lengths the real client uses (RTCP / plain RTP / X-bit extension).
    // Receive-side note: LE-read + LE-write reproduces the trailer bytes exactly, so this path is
    // convention-agnostic — it decrypts whatever the sender wrote (the peer's LE counters, and any
    // BE sender too). Do not "fix" it back to BE; it already matches the pynacl-verified captures.
    public static byte[]? UnprotectPacket(byte[] key, bool useAes, ReadOnlySpan<byte> packet)
    {
        int hlen = HeaderLengthFor(packet);
        if (hlen == 0 || packet.Length < hlen + 4 + 16) return null;
        return UnprotectAt(key, useAes, packet, hlen);
    }

    /// Length of the RTP header-extension DATA (bytes) a packet's extension header declares:
    /// 4 * words, where words is the 16-bit field at bytes 14-15 (RFC 8285). Zero when the X bit
    /// is clear or the field is absent. The reference client (dolfies/discord-native-voice)
    /// authenticates only the 12-byte RTP header + the 4-byte BE DE + words header as the AEAD
    /// AAD and carries the extension DATA inside the ciphertext — so the decrypted payload starts
    /// with this many bytes of extension that must be stripped before the codec sees it. (Audio
    /// does the same with its 8-byte 0x32 header; the receive loop strips that by signature.)
    public static int ExtensionLenFor(ReadOnlySpan<byte> p)
    {
        if (p.Length < 16 || (p[0] & 0x10) == 0) return 0;
        int words = BinaryPrimitives.ReadUInt16BigEndian(p[14..]);
        return words * 4;
    }

    /// Decrypt a VIDEO RTP packet, reporting how many leading bytes of the decrypted payload are
    /// RTP extension data (to strip before the codec/depacketizer). The AEAD AAD is the header,
    /// so the correct length is self-verifying via the tag; the plausible layouts are tried in
    /// order and the first that authenticates wins:
    ///
    ///   AAD = 16 (12-byte RTP + BE DE + words)  -> payload = [ext data][media]  (the real
    ///         client and dnv: extension data rides INSIDE the ciphertext; strip `extLen`)
    ///   AAD = 16 + extLen (full header incl. data) -> payload = [media] (no strip)
    ///   AAD = 12 (no extension at all)          -> payload = [media] (no strip)
    ///
    /// Previously this returned the payload with the extension bytes still attached. The video
    /// extension's first byte (e.g. 0x50/0x32 for transport-seq / abs-send-time) decodes as a
    /// VALID H.264 NAL type (16/18), so the assembler swallowed the extension as a fake NAL and
    /// every reassembled DAVE frame was corrupted — the peer's tile stayed black AND the DAVE
    /// GCM failed on every frame. stripPrefix says how many bytes to drop. Returns null when no
    /// layout authenticates.
    public static byte[]? UnprotectVideoPacket(byte[] key, bool useAes, ReadOnlySpan<byte> packet,
                                               out int headerLength, out int stripPrefix)
    {
        headerLength = 0;
        stripPrefix = 0;
        int extLen = ExtensionLenFor(packet);
        if (packet.Length < 12 + 4 + 16) return null;
        if ((packet[0] & 0x10) == 0)
        {
            // Plain 12-byte header (no X bit): the real client's unextended packets.
            var plain = UnprotectAt(key, useAes, packet, 12);
            if (plain != null) { headerLength = 12; stripPrefix = 0; return plain; }
            return null;
        }
        // Extended packet: try the dnv layout first (AAD = 12 + 4, ext data in ciphertext),
        // then the full header (AAD = 12 + 4 + extLen), then a bare 12-byte AAD as a last resort.
        if (packet.Length >= 16 + 4 + 16)
        {
            var dnv = UnprotectAt(key, useAes, packet, 16);
            if (dnv != null) { headerLength = 16; stripPrefix = extLen; return dnv; }
        }
        if (packet.Length >= 16 + extLen + 4 + 16)
        {
            var full = UnprotectAt(key, useAes, packet, 16 + extLen);
            if (full != null) { headerLength = 16 + extLen; stripPrefix = 0; return full; }
        }
        if (packet.Length >= 12 + 4 + 16)
        {
            var bare = UnprotectAt(key, useAes, packet, 12);
            if (bare != null) { headerLength = 12; stripPrefix = 4 + extLen; return bare; }
        }
        return null;
    }

    static byte[]? UnprotectAt(byte[] key, bool useAes, ReadOnlySpan<byte> packet, int hlen)
    {
        uint counter = BinaryPrimitives.ReadUInt32LittleEndian(packet[^4..]);
        var nonce = new byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(nonce, counter);
        var header = packet[..hlen];
        var body = packet[hlen..^4];
        return useAes ? GcmDecrypt(key, nonce, body, header)
                      : XChaCha20Poly1305Decrypt(key, nonce, body, header);
    }

    // ── RTCP transport encryption ────────────────────────────────────────────────────────────────
    // RTCP rides the same UDP socket and is encrypted EXACTLY like RTP in the RTP-size AEAD modes:
    // the 8-byte RTCP header (V/P/RC/FMT + PT + length + sender SSRC) is the cleartext AAD, the
    // report/feedback body is encrypted, and a 4-byte LE counter trailer is appended. Discord's
    // SFU is pull-based for video: it only starts relaying a subscribed stream after it sees valid
    // (encrypted) PLI + receiver reports from the subscriber. Sending raw RTCP — as this client
    // used to — is silently dropped by the SFU, which is why the peer's camera never arrived even
    // after a correct op 15 subscribe. The reference client (dolfies/discord-native-voice,
    // TransportCrypto.encrypt_rtcp) uses the identical construction, sharing the RTP nonce counter.
    public static byte[] ProtectRtcp(byte[] key, bool useAes, ReadOnlySpan<byte> rtcp, uint counter)
    {
        var nonce = new byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(nonce, counter);
        var header = rtcp[..8].ToArray();
        var body = rtcp[8..];
        var cipher = useAes ? GcmEncryptWithTag(key, nonce, body, header)
                            : XChaCha20Poly1305Encrypt(key, nonce, body, header);
        var packet = new byte[8 + cipher.Length + 4];
        header.CopyTo(packet, 0);
        cipher.CopyTo(packet, 8);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8 + cipher.Length), counter);
        return packet;
    }

    /// Decrypt an inbound RTCP packet; returns the body (everything after the 8-byte header).
    /// The header bytes stay cleartext, so packet[1] (PT) and packet[0]&0x1F (RC/FMT) are readable
    /// before decryption. Null when the tag fails (wrong key or not AEAD-framed).
    public static byte[]? UnprotectRtcp(byte[] key, bool useAes, ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 8 + 4 + 16) return null;
        uint counter = BinaryPrimitives.ReadUInt32LittleEndian(packet[^4..]);
        var nonce = new byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(nonce, counter);
        var header = packet[..8];
        var body = packet[8..^4];
        return useAes ? GcmDecrypt(key, nonce, body, header)
                      : XChaCha20Poly1305Decrypt(key, nonce, body, header);
    }

    // The 3-byte silence frame the SFU synthesizes for muted sources, and what a sender sends to
    // keep the connection alive between speech bursts.
    public static readonly byte[] SilenceFrame = { 0xF8, 0xFF, 0xFE };
}
