using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace ClaudeScord;

// ─────────────────────────────────────────────────────────────────────────────
// MLS / DAVE cryptography on P-256 (ciphersuite 2: P256_AES128GCM_SHA256_P256).
// Everything matches mlspp byte-for-byte: the "MLS 1.0 " HKDF-label prefix,
// the varint-wrapped SignContent/EncryptContext, RFC 9180 HPKE base mode, and
// the RFC 9420 key schedule + exporter.
// ─────────────────────────────────────────────────────────────────────────────

static class MlsCrypto
{
    // ── primitives ─────────────────────────────────────────────────────────────
    public static byte[] Sha256(ReadOnlySpan<byte> data) => SHA256.HashData(data);

    public static byte[] Hmac(ReadOnlySpan<byte> key, ReadOnlySpan<byte> data)
    {
        using var h = new HMACSHA256(key.ToArray());
        return h.ComputeHash(data.ToArray());
    }

    public static byte[] HkdfExtract(ReadOnlySpan<byte> salt, ReadOnlySpan<byte> ikm)
    {
        using var h = new HMACSHA256(salt.ToArray());
        return h.ComputeHash(ikm.ToArray());
    }

    public static byte[] HkdfExpand(ReadOnlySpan<byte> prk, ReadOnlySpan<byte> info, int len)
    {
        // RFC 5869: T(i) = HMAC(PRK, T(i-1) || info || i). A fresh instance per
        // round — reusing one HMAC via TransformBlock/TransformFinalBlock hangs
        // the Windows CNG provider (undefined post-finalize state) and drops the
        // T-chain, both of which surface as wrong keys or a dead process.
        var output = new byte[len];
        byte[] t = Array.Empty<byte>();
        int done = 0;
        for (byte counter = 1; done < len; counter++)
        {
            var buf = new byte[t.Length + info.Length + 1];
            Array.Copy(t, 0, buf, 0, t.Length);
            info.CopyTo(buf.AsSpan(t.Length));
            buf[^1] = counter;
            using var hmac = new HMACSHA256(prk.ToArray());
            t = hmac.ComputeHash(buf);
            int n = Math.Min(t.Length, len - done);
            Array.Copy(t, 0, output, done, n);
            done += n;
        }
        return output;
    }

    // RFC 9420 ExpandWithLabel: HKDFLabel{length u16, label<varint> = "MLS 1.0 "+label, context<varint>}.
    public static byte[] ExpandWithLabel(ReadOnlySpan<byte> secret, string label,
                                         ReadOnlySpan<byte> context, int length)
    {
        var labelBytes = Encoding.ASCII.GetBytes("MLS 1.0 " + label);
        var w = new TlsWriter();
        TlsWriter.WriteU16(w.Buf, (ushort)length);
        Varint.Write(w.Buf, labelBytes.Length);
        w.Buf.AddRange(labelBytes);
        Varint.Write(w.Buf, context.Length);
        w.Buf.AddRange(context);
        return HkdfExpand(secret, CollectionsMarshal.AsSpan(w.Buf), length);
    }

    // RFC 9420 DeriveSecret: expand with empty context, full hash length.
    public static byte[] DeriveSecret(ReadOnlySpan<byte> secret, string label) =>
        ExpandWithLabel(secret, label, Array.Empty<byte>(), 32);

    // mlspp derive_tree_secret: context = big-endian u32 generation.
    public static byte[] DeriveTreeSecret(ReadOnlySpan<byte> secret, string label,
                                          uint generation, int length)
    {
        var ctx = new byte[4];
        ctx[0] = (byte)(generation >> 24);
        ctx[1] = (byte)(generation >> 16);
        ctx[2] = (byte)(generation >> 8);
        ctx[3] = (byte)generation;
        return ExpandWithLabel(secret, label, ctx, length);
    }

    // ── P-256 helpers ──────────────────────────────────────────────────────────
    static readonly ECCurve Curve = ECCurve.NamedCurves.nistP256;
    static readonly byte[] P256Order =
    {
        0xFF, 0xFF, 0xFF, 0xFF, 0x00, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0xFF, 0xFF,
        0xFF, 0xFF, 0xFF, 0xFF, 0xBC, 0xE6, 0xFA, 0xAD, 0xA7, 0x17, 0x9E, 0x84,
        0xF3, 0xB9, 0xCA, 0xC2, 0xFC, 0x63, 0x25, 0x51,
    };

    public static readonly byte[] ZeroSecret = new byte[32];

    public static (byte[] d, byte[] x, byte[] y) GenP256()
    {
        using var ecdsa = ECDsa.Create(Curve);
        var p = ecdsa.ExportParameters(true);
        return (p.D!, p.Q.X!, p.Q.Y!);
    }

    // Public key as 65-byte uncompressed point (0x04 || X || Y) — MLS format.
    public static byte[] PubPoint(byte[] x, byte[] y)
    {
        var b = new byte[65];
        b[0] = 4;
        Array.Copy(x, 0, b, 1, 32);
        Array.Copy(y, 0, b, 33, 32);
        return b;
    }

    public static (byte[] x, byte[] y) SplitPoint(ReadOnlySpan<byte> point)
    {
        if (point.Length != 65 || point[0] != 4)
            throw new InvalidDataException("bad uncompressed point");
        return (point.Slice(1, 32).ToArray(), point.Slice(33, 32).ToArray());
    }

    static bool ScalarValid(ReadOnlySpan<byte> d)
    {
        if (d.Length != 32) return false;
        if (d.SequenceEqual(new byte[32])) return false;
        var v = new BigInteger(d, isUnsigned: true, isBigEndian: true);
        var n = new BigInteger(P256Order, isUnsigned: true, isBigEndian: true);
        return v < n && v > 0;
    }

    static ECParameters ParamsFromD(byte[] d, byte[]? x = null, byte[]? y = null)
    {
        return new ECParameters
        {
            Curve = Curve,
            D = d,
            Q = new ECPoint { X = x, Y = y },
        };
    }

    // ── signatures (ECDSA P-256, DER-encoded per RFC 9420) ─────────────────────
    // The signature format is EXPLICIT (DSASignatureFormat.Rfc3279DerSequence):
    // .NET 8's default SignData/VerifyData overloads use IEEE P1363 raw r||s
    // (64 bytes for P-256), but MLS/mlspp exchange DER. The default overloads
    // round-trip fine against themselves, which is why the self-test passed while
    // Discord's gateway (OpenSSL EVP_DigestSign) rejected every key package.
    public static byte[] SignDer(byte[] d, ReadOnlySpan<byte> data)
    {
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportParameters(ParamsFromD(d));
        return ecdsa.SignData(data.ToArray(), HashAlgorithmName.SHA256,
                              DSASignatureFormat.Rfc3279DerSequence);
    }

    public static bool VerifyDer(byte[] x, byte[] y, ReadOnlySpan<byte> data, ReadOnlySpan<byte> sig)
    {
        try
        {
            using var ecdsa = ECDsa.Create();
            // Verification needs only the public point; importing a zero D alongside
            // it makes the key invalid and the import throws on Windows (which the
            // caller would silently treat as a bad signature).
            ecdsa.ImportParameters(new ECParameters
            {
                Curve = Curve,
                Q = new ECPoint { X = x, Y = y },
            });
            return ecdsa.VerifyData(data.ToArray(), sig.ToArray(), HashAlgorithmName.SHA256,
                                    DSASignatureFormat.Rfc3279DerSequence);
        }
        catch
        {
            return false;
        }
    }

    // SignWithLabel: ECDSA over SignContent{label<varint>, content<varint>}, label = "MLS 1.0 "+label.
    public static byte[] SignWithLabel(byte[] d, string label, ReadOnlySpan<byte> content)
    {
        var labelBytes = Encoding.ASCII.GetBytes("MLS 1.0 " + label);
        var w = new TlsWriter();
        Varint.Write(w.Buf, labelBytes.Length);
        w.Buf.AddRange(labelBytes);
        Varint.Write(w.Buf, content.Length);
        w.Buf.AddRange(content);
        return SignDer(d, CollectionsMarshal.AsSpan(w.Buf));
    }

    public static bool VerifyWithLabel(byte[] x, byte[] y, string label,
                                       ReadOnlySpan<byte> content, ReadOnlySpan<byte> sig)
    {
        var labelBytes = Encoding.ASCII.GetBytes("MLS 1.0 " + label);
        var w = new TlsWriter();
        Varint.Write(w.Buf, labelBytes.Length);
        w.Buf.AddRange(labelBytes);
        Varint.Write(w.Buf, content.Length);
        w.Buf.AddRange(content);
        return VerifyDer(x, y, CollectionsMarshal.AsSpan(w.Buf), sig);
    }

    // ── ECDH (for HPKE) ─────────────────────────────────────────────────────────
    // Raw X-coordinate DH: 32 bytes out.
    public static byte[] DhRaw(byte[] d, ReadOnlySpan<byte> peerPoint)
    {
        var (px, py) = SplitPoint(peerPoint);
        using var mine = ECDiffieHellman.Create();
        mine.ImportParameters(ParamsFromD(d));
        using var peer = ECDiffieHellman.Create();
        peer.ImportParameters(new ECParameters { Curve = Curve, Q = new ECPoint { X = px, Y = py } });
        return mine.DeriveRawSecretAgreement(peer.PublicKey);
    }

    // RFC 9180 DeriveKeyPair for P-256: derive (d, x, y) from a 32-byte secret.
    public static (byte[] d, byte[] x, byte[] y) DeriveP256(ReadOnlySpan<byte> ikm)
    {
        var suiteId = new byte[] { (byte)'K', (byte)'E', (byte)'M', 0x00, 0x10 };
        var eaePrk = LabeledExtract(Array.Empty<byte>(), "dkp_prk", ikm, suiteId);
        for (int counter = 0; counter < 255; counter++)
        {
            byte[] info;
            if (counter == 0)
                info = suiteId;
            else
            {
                info = new byte[suiteId.Length + 1];
                Array.Copy(suiteId, info, suiteId.Length);
                info[suiteId.Length] = (byte)counter;
            }
            var sk = LabeledExpand(eaePrk, "sk", info, 32, suiteId);
            if (!ScalarValid(sk)) continue;
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportParameters(ParamsFromD(sk));
            var p = ecdsa.ExportParameters(false);
            return (sk, p.Q.X!, p.Q.Y!);
        }
        throw new CryptographicException("DeriveKeyPair failed");
    }

    // ── HPKE (RFC 9180) base mode, DHKEM(P-256, HKDF-SHA256) + AES-128-GCM ────
    static readonly byte[] HpkeKemSuiteId = { (byte)'K', (byte)'E', (byte)'M', 0x00, 0x10 };
    // The HPKE suite id includes the KEM id too: "HPKE" || kem_id(2) || kdf_id(2)
    // || aead_id(2). Dropping kem_id breaks every key-schedule output (pinned by
    // the RFC 9180 A.3.1 self-test).
    static readonly byte[] HpkeSuiteId =
    {
        (byte)'H', (byte)'P', (byte)'K', (byte)'E', 0x00, 0x10, 0x00, 0x01, 0x00, 0x01,
    };

    static byte[] LabeledExtract(ReadOnlySpan<byte> salt, string label,
                                 ReadOnlySpan<byte> ikm, ReadOnlySpan<byte> suiteId)
    {
        var labeled = new byte[7 + suiteId.Length + label.Length + ikm.Length];
        int o = 0;
        foreach (var c in "HPKE-v1") labeled[o++] = (byte)c;
        suiteId.CopyTo(labeled.AsSpan(o)); o += suiteId.Length;
        foreach (var c in label) labeled[o++] = (byte)c;
        ikm.CopyTo(labeled.AsSpan(o));
        return HkdfExtract(salt, labeled);
    }

    static byte[] LabeledExpand(ReadOnlySpan<byte> prk, string label,
                                ReadOnlySpan<byte> info, int len, ReadOnlySpan<byte> suiteId)
    {
        var labeled = new byte[2 + 7 + suiteId.Length + label.Length + info.Length];
        int o = 0;
        labeled[o++] = (byte)(len >> 8);
        labeled[o++] = (byte)len;
        foreach (var c in "HPKE-v1") labeled[o++] = (byte)c;
        suiteId.CopyTo(labeled.AsSpan(o)); o += suiteId.Length;
        foreach (var c in label) labeled[o++] = (byte)c;
        info.CopyTo(labeled.AsSpan(o));
        return HkdfExpand(prk, labeled, len);
    }

    static byte[] ExtractAndExpand(ReadOnlySpan<byte> dh, ReadOnlySpan<byte> kemContext)
    {
        var eaePrk = LabeledExtract(Array.Empty<byte>(), "eae_prk", dh, HpkeKemSuiteId);
        return LabeledExpand(eaePrk, "shared_secret", kemContext, 32, HpkeKemSuiteId);
    }

    // Base-mode HPKE seal: returns (enc, ciphertext). pt/AAD raw.
    public static (byte[] enc, byte[] ct) HpkeSeal(ReadOnlySpan<byte> pkR, ReadOnlySpan<byte> info,
                                                   ReadOnlySpan<byte> aad, ReadOnlySpan<byte> pt)
    {
        var (d, x, y) = GenP256();
        var enc = PubPoint(x, y);
        var dh = DhRaw(d, pkR);
        var kemContext = new byte[enc.Length + pkR.Length];
        enc.CopyTo(kemContext, 0);
        pkR.CopyTo(kemContext.AsSpan(enc.Length));
        var ss = ExtractAndExpand(dh, kemContext);
        var (key, nonce) = HpkeKeySchedule(ss, info, 0);
        var ct = Aes128GcmSeal(key, nonce, aad, pt);
        return (enc, ct);
    }

    public static byte[] HpkeOpen(ReadOnlySpan<byte> enc, byte[] d, ReadOnlySpan<byte> info,
                                  ReadOnlySpan<byte> aad, ReadOnlySpan<byte> ct, byte[] pubPoint)
    {
        var dh = DhRaw(d, enc);
        var kemContext = new byte[enc.Length + pubPoint.Length];
        enc.CopyTo(kemContext.AsSpan(0));
        pubPoint.CopyTo(kemContext, enc.Length);
        var ss = ExtractAndExpand(dh, kemContext);
        var (key, nonce) = HpkeKeySchedule(ss, info, 0);
        return Aes128GcmOpen(key, nonce, aad, ct);
    }

    static (byte[] key, byte[] nonce) HpkeKeySchedule(ReadOnlySpan<byte> sharedSecret,
                                                      ReadOnlySpan<byte> info, byte mode)
    {
        // Final RFC 9180 context: mode || psk_id_hash || info_hash (an early draft
        // carried an extra psk_hash in the middle — including it breaks every
        // published vector). In base mode both psk inputs are empty.
        var pskIdHash = LabeledExtract(Array.Empty<byte>(), "psk_id_hash", Array.Empty<byte>(), HpkeSuiteId);
        var infoHash = LabeledExtract(Array.Empty<byte>(), "info_hash", info, HpkeSuiteId);
        var ctx = new byte[1 + pskIdHash.Length + infoHash.Length];
        ctx[0] = mode;
        pskIdHash.CopyTo(ctx, 1);
        infoHash.CopyTo(ctx, 1 + pskIdHash.Length);
        var secret = LabeledExtract(sharedSecret, "secret", Array.Empty<byte>(), HpkeSuiteId);
        var key = LabeledExpand(secret, "key", ctx, 16, HpkeSuiteId);
        var nonce = LabeledExpand(secret, "base_nonce", ctx, 12, HpkeSuiteId);
        return (key, nonce);
    }

    // MLS HPKE info: EncryptContext{label<varint> = "MLS 1.0 "+label, context<varint>}.
    public static byte[] MlsEncryptInfo(string label, ReadOnlySpan<byte> context)
    {
        var labelBytes = Encoding.ASCII.GetBytes("MLS 1.0 " + label);
        var w = new TlsWriter();
        Varint.Write(w.Buf, labelBytes.Length);
        w.Buf.AddRange(labelBytes);
        Varint.Write(w.Buf, context.Length);
        w.Buf.AddRange(context);
        return w.Buf.ToArray();
    }

    // ── AES-128-GCM ────────────────────────────────────────────────────────────
    public static byte[] Aes128GcmSeal(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce,
                                       ReadOnlySpan<byte> aad, ReadOnlySpan<byte> pt)
    {
        var tag = new byte[16];
        var ct = new byte[pt.Length];
        using var gcm = new AesGcm(key.ToArray(), 16);
        gcm.Encrypt(nonce.ToArray(), pt.ToArray(), ct, tag, aad.ToArray());
        var outBytes = new byte[ct.Length + tag.Length];
        ct.CopyTo(outBytes, 0);
        tag.CopyTo(outBytes, ct.Length);
        return outBytes;
    }

    public static byte[]? Aes128GcmOpen(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce,
                                        ReadOnlySpan<byte> aad, ReadOnlySpan<byte> ctWithTag)
    {
        if (ctWithTag.Length < 16) return null;
        try
        {
            var ct = ctWithTag[..^16].ToArray();
            var tag = ctWithTag[^16..].ToArray();
            var pt = new byte[ct.Length];
            using var gcm = new AesGcm(key.ToArray(), 16);
            gcm.Decrypt(nonce.ToArray(), ct, tag, pt, aad.ToArray());
            return pt;
        }
        catch
        {
            return null;
        }
    }

    // ── refs (RFC 9420: SHA-256 over varint(label) || varint(value)) ───────────
    public static byte[] KeyPackageRef(ReadOnlySpan<byte> keyPackage)
    {
        var label = Encoding.ASCII.GetBytes("MLS 1.0 KeyPackage Reference");
        return RawRef(label, keyPackage);
    }

    public static byte[] ProposalRef(ReadOnlySpan<byte> authenticatedContent)
    {
        var label = Encoding.ASCII.GetBytes("MLS 1.0 Proposal Reference");
        return RawRef(label, authenticatedContent);
    }

    static byte[] RawRef(byte[] label, ReadOnlySpan<byte> value)
    {
        var w = new TlsWriter();
        Varint.Write(w.Buf, label.Length);
        w.Buf.AddRange(label);
        Varint.Write(w.Buf, value.Length);
        w.Buf.AddRange(value);
        return Sha256(CollectionsMarshal.AsSpan(w.Buf));
    }

    // ── RFC 9420 key schedule ───────────────────────────────────────────────────
    public sealed class KeySchedule
    {
        public byte[] JoinerSecret = null!;
        public byte[] EpochSecret = null!;
        public byte[] ExporterSecret = null!;
        public byte[] InitSecret = null!;
        public byte[] ConfirmationKey = null!;
        public byte[] ConfirmationTag = null!;
        // RFC 9420 §8: membership_key = DeriveSecret(epoch_secret, "membership").
        // Member-sent PublicMessages append a membership tag HMAC'd with this key
        // (mlspp PublicMessage::membership_mac / openmls set_membership_tag).
        public byte[] MembershipKey = null!;

        // Group creation: epoch 0 with a fresh random init secret.
        public static KeySchedule Create(byte[] initSecret, ReadOnlySpan<byte> context) =>
            FromInitSecret(initSecret, ZeroSecret, Array.Empty<byte>(), context);

        public static KeySchedule Joiner(byte[] joinerSecret, ReadOnlySpan<byte> context,
                                         ReadOnlySpan<byte> confirmedTranscriptHash)
        {
            var psk = ZeroSecret;
            var member = HkdfExtract(joinerSecret, psk);
            var epoch = ExpandWithLabel(member, "epoch", context, 32);
            return Finish(joinerSecret, epoch, confirmedTranscriptHash);
        }

        static KeySchedule FromInitSecret(byte[] initSecret, byte[] commitSecret,
                                          ReadOnlySpan<byte> confirmedHash, ReadOnlySpan<byte> context)
        {
            var preJoiner = HkdfExtract(initSecret, commitSecret);
            var joiner = ExpandWithLabel(preJoiner, "joiner", context, 32);
            var psk = ZeroSecret;
            var member = HkdfExtract(joiner, psk);
            var epoch = ExpandWithLabel(member, "epoch", context, 32);
            return Finish(joiner, epoch, confirmedHash);
        }

        public KeySchedule Next(byte[] commitSecret, ReadOnlySpan<byte> context,
                                ReadOnlySpan<byte> confirmedTranscriptHash) =>
            FromInitSecret(InitSecret, commitSecret, confirmedTranscriptHash, context);

        static KeySchedule Finish(byte[] joinerSecret, byte[] epochSecret,
                                  ReadOnlySpan<byte> confirmedHash)
        {
            var ks = new KeySchedule
            {
                JoinerSecret = joinerSecret,
                EpochSecret = epochSecret,
                ExporterSecret = DeriveSecret(epochSecret, "exporter"),
                InitSecret = DeriveSecret(epochSecret, "init"),
                ConfirmationKey = DeriveSecret(epochSecret, "confirm"),
                MembershipKey = DeriveSecret(epochSecret, "membership"),
            };
            ks.ConfirmationTag = Hmac(ks.ConfirmationKey, confirmedHash);
            return ks;
        }

        // MLS-Exporter: derive_secret(exporter_secret, label) expanded with
        // "exported" over Hash(context).
        public byte[] Export(string label, ReadOnlySpan<byte> context, int size)
        {
            var secret = DeriveSecret(ExporterSecret, label);
            var ctxHash = Sha256(context);
            return ExpandWithLabel(secret, "exported", ctxHash, size);
        }

        // Welcome key/nonce from the joiner secret (RFC 9420 §8.5).
        public static (byte[] key, byte[] nonce) WelcomeKeyNonce(byte[] joinerSecret)
        {
            var extract = HkdfExtract(joinerSecret, ZeroSecret);
            var ws = DeriveSecret(extract, "welcome");
            var key = ExpandWithLabel(ws, "key", Array.Empty<byte>(), 16);
            var nonce = ExpandWithLabel(ws, "nonce", Array.Empty<byte>(), 12);
            return (key, nonce);
        }
    }

    // ── sender hash ratchet (mlspp HashRatchet, RFC 9420 §9.1 via DAVE) ────────
    public sealed class HashRatchet
    {
        readonly Dictionary<uint, byte[]> _cache = new();
        byte[] _nextSecret;
        uint _nextGeneration;

        public HashRatchet(byte[] baseSecret)
        {
            _nextSecret = baseSecret;
        }

        public byte[] Get(uint generation)
        {
            if (_cache.TryGetValue(generation, out var cached)) return cached;
            if (generation < _nextGeneration) throw new InvalidDataException("expired ratchet key");
            while (_nextGeneration <= generation)
            {
                var key = DeriveTreeSecret(_nextSecret, "key", _nextGeneration, 16);
                DeriveTreeSecret(_nextSecret, "nonce", _nextGeneration, 12);
                _nextSecret = DeriveTreeSecret(_nextSecret, "secret", _nextGeneration, 32);
                _cache[_nextGeneration] = key;
                _nextGeneration++;
            }
            return _cache[generation];
        }
    }
}
