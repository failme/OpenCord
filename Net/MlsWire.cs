using System.Text;

namespace ClaudeScord;

// ─────────────────────────────────────────────────────────────────────────────
// MLS wire format (RFC 9420 TLS presentation language + the MLS variable-size
// vector length headers, implemented to match mlspp byte-for-byte — Discord's
// DAVE layer uses mlspp, so every length prefix and tag must agree with it).
//
// Conventions (all verified against mlspp source):
//   * u8/u16/u32/u64: big-endian.
//   * opaque/bytes and vectors: varint length prefix.
//   * varint: 2-bit header selects 1/2/4-byte length. 1B: 0..0x3F.
//     2B: 0x4000|v (v ≤ 0x3FFF). 4B: 0x80000000|v (v ≤ 0x3FFFFFFF).
//   * optional<T>: 1 byte presence (0/1) + value.
//   * variants (tls::variant): the enum tag value (u8 for uint8 enums, u16 for
//     uint16 enums) followed by the selected member.
// ─────────────────────────────────────────────────────────────────────────────

static class Varint
{
    const long V1 = 0x00, V2 = 0x4000, V4 = 0x80000000L;
    const long V1Max = 0x3F, V2Max = 0x3FFF, V4Max = 0x3FFFFFFF;

    public static void Write(List<byte> dst, long value)
    {
        if (value <= V1Max) { dst.Add((byte)(V1 | value)); return; }
        if (value <= V2Max) { TlsWriter.WriteU16(dst, (ushort)(V2 | value)); return; }
        if (value <= V4Max) { TlsWriter.WriteU32(dst, (uint)(V4 | value)); return; }
        throw new InvalidDataException("varint too large");
    }

    public static long Read(ReadOnlySpan<byte> src, ref int pos)
    {
        if (pos >= src.Length) throw new InvalidDataException("varint underflow");
        byte b0 = src[pos++];
        int log = b0 >> 6;
        if (log > 2) throw new InvalidDataException("malformed varint");
        int len = 1 << log;                 // total encoded bytes: 1, 2, or 4
        long v = b0;                        // header byte carries the top 6 bits
        for (int i = 1; i < len; i++)       // read the remaining len-1 bytes
        {
            if (pos >= src.Length) throw new InvalidDataException("varint underflow");
            v = (v << 8) | src[pos++];
        }
        v ^= log switch { 0 => V1, 1 => V2, _ => V4 };
        return v;
    }
}

sealed class TlsWriter
{
    public readonly List<byte> Buf = new();

    public static implicit operator byte[](TlsWriter w) => w.Buf.ToArray();

    public TlsWriter U8(int v) { Buf.Add((byte)v); return this; }
    public TlsWriter U16(int v) { WriteU16(Buf, (ushort)v); return this; }
    public TlsWriter U32(long v) { WriteU32(Buf, (uint)v); return this; }
    public TlsWriter U64(ulong v) { WriteU64(Buf, v); return this; }

    // Append already-serialized struct bytes with NO length prefix. RFC 9420 embeds
    // struct members inline; only opaque and vector fields carry a varint length.
    public TlsWriter Raw(ReadOnlySpan<byte> b)
    {
        Buf.AddRange(b);
        return this;
    }

    public TlsWriter Bytes(ReadOnlySpan<byte> b)
    {
        Varint.Write(Buf, b.Length);
        Buf.AddRange(b);
        return this;
    }

    public TlsWriter OptBytes(byte[]? b)
    {
        if (b == null) { Buf.Add(0); return this; }
        Buf.Add(1);
        return Bytes(b);
    }

    public TlsWriter Vec(Action<TlsWriter> each)
    {
        var tmp = new TlsWriter();
        each(tmp);
        Varint.Write(Buf, tmp.Buf.Count);
        Buf.AddRange(tmp.Buf);
        return this;
    }

    public static void WriteU16(List<byte> dst, ushort v)
    {
        dst.Add((byte)(v >> 8));
        dst.Add((byte)v);
    }

    public static void WriteU32(List<byte> dst, uint v)
    {
        dst.Add((byte)(v >> 24));
        dst.Add((byte)(v >> 16));
        dst.Add((byte)(v >> 8));
        dst.Add((byte)v);
    }

    public static void WriteU64(List<byte> dst, ulong v)
    {
        for (int i = 7; i >= 0; i--) dst.Add((byte)(v >> (8 * i)));
    }
}

sealed class TlsReader
{
    readonly byte[] _src;
    int _pos;

    public TlsReader(ReadOnlySpan<byte> src) => _src = src.ToArray();

    public int Position => _pos;
    public int Remaining => _src.Length - _pos;
    public bool Done => _pos >= _src.Length;

    public int U8()
    {
        if (_pos >= _src.Length) throw new InvalidDataException("u8 underflow");
        return _src[_pos++];
    }

    public int U16()
    {
        Need(2);
        int v = (_src[_pos] << 8) | _src[_pos + 1];
        _pos += 2;
        return v;
    }

    public uint U32()
    {
        Need(4);
        uint v = (uint)((_src[_pos] << 24) | (_src[_pos + 1] << 16) | (_src[_pos + 2] << 8) | _src[_pos + 3]);
        _pos += 4;
        return v;
    }

    public ulong U64()
    {
        Need(8);
        ulong v = 0;
        for (int i = 0; i < 8; i++) v = (v << 8) | _src[_pos + i];
        _pos += 8;
        return v;
    }

    public byte[] Bytes()
    {
        long n = Varint.Read(_src, ref _pos);
        Need((int)n);
        var b = _src.AsSpan(_pos, (int)n).ToArray();
        _pos += (int)n;
        return b;
    }

    public byte[]? OptBytes()
    {
        int present = U8();
        if (present == 0) return null;
        if (present != 1) throw new InvalidDataException("bad optional");
        return Bytes();
    }

    public bool OptFlag() => U8() != 0;

    public void Vec(Action<TlsReader> each)
    {
        long n = Varint.Read(_src, ref _pos);
        int end = _pos + (int)n;
        if (end > _src.Length) throw new InvalidDataException("vector overrun");
        while (_pos < end) each(this);
        if (_pos != end) throw new InvalidDataException("vector under-read");
    }

    public int VecLength()
    {
        long n = Varint.Read(_src, ref _pos);
        return (int)n;
    }

    public void Skip(int n)
    {
        Need(n);
        _pos += n;
    }

    void Need(int n)
    {
        if (_pos + n > _src.Length) throw new InvalidDataException("buffer overrun");
    }
}

// ── MLS structures ───────────────────────────────────────────────────────────

// Credential (basic only in DAVE): uint16 type = 1, identity.
static class MlsCredential
{
    public const int Basic = 1;

    public static byte[] Encode(byte[] identity) =>
        new TlsWriter().U16(Basic).Bytes(identity).Buf.ToArray();

    public static (int type, byte[] identity) Decode(ReadOnlySpan<byte> data)
    {
        var r = new TlsReader(data);
        int type = r.U16();
        var id = r.Bytes();
        return (type, id);
    }
}

// Capabilities: versions<u16> ciphersuites<u16> extensions<u16> proposals<u16> credentials<u16>.
static class MlsCapabilities
{
    public static byte[] EncodeDefault()
    {
        return new TlsWriter()
            .Vec(w => w.U16(1))            // versions: [mls10]
            .Vec(w => w.U16(2))            // ciphersuites: [P256_AES128GCM_SHA256_P256]
            .Vec(w => { })                 // extensions
            .Vec(w => { })                 // proposals
            .Vec(w => w.U16(1))            // credentials: [basic]
            .Buf.ToArray();
    }
}

// Lifetime: uint64 not_before, uint64 not_after (max span per DAVE validation).
static class MlsLifetime
{
    public static byte[] EncodeMax() =>
        new TlsWriter().U64(0).U64(ulong.MaxValue).Buf.ToArray();
}

// Extension: uint16 type + opaque data. ExtensionList: vector<Extension>.
static class MlsExtensions
{
    public const int ExternalSenders = 5;
    public const int RatchetTree = 2;

    public static byte[] EncodeList(params (int type, byte[] data)[] exts)
    {
        return new TlsWriter().Vec(w =>
        {
            foreach (var (t, d) in exts) w.U16(t).Bytes(d);
        }).Buf.ToArray();
    }

    // Returns (type, data) pairs.
    public static List<(int type, byte[] data)> DecodeList(ReadOnlySpan<byte> data)
    {
        var list = new List<(int, byte[])>();
        var r = new TlsReader(data);
        r.Vec(inner =>
        {
            int t = inner.U16();
            var d = inner.Bytes();
            list.Add((t, d));
        });
        return list;
    }
}

// ExternalSendersExtension data: vector<ExternalSender>.
// ExternalSender = SignaturePublicKey (opaque) + Credential (struct, inline).
static class MlsExternalSender
{
    public static byte[] EncodeSenders(byte[] sigKey, byte[] identity)
    {
        var cred = MlsCredential.Encode(identity);
        return new TlsWriter().Vec(w => w.Bytes(sigKey).Raw(cred)).Buf.ToArray();
    }

    public static (byte[] sigKey, byte[] identity) DecodeSender(ReadOnlySpan<byte> data)
    {
        var r = new TlsReader(data);
        byte[]? sigKey = null, identity = null;
        r.Vec(inner =>
        {
            sigKey = inner.Bytes();
            var (type, id) = MlsCredential.Decode(inner.Bytes());
            if (type != MlsCredential.Basic) throw new InvalidDataException("non-basic credential");
            identity = id;
        });
        return (sigKey!, identity!);
    }
}

// LeafNode (RFC 9420 §12.4.4):
//   encryption_key<opaque> signature_key<opaque> Credential(inline) Capabilities(inline)
//   leaf_node_source u8, payload (key_package: Lifetime struct inline,
//                                 update: none, commit: ParentHash<opaque>)
//   extensions (ExtensionList inline) signature<opaque>
// sourcePayload is the RAW payload: the 16-byte Lifetime struct for key_package,
// the raw 32-byte parent hash for commit (wrapped as opaque here).
static class MlsLeafNode
{
    public const int SourceKeyPackage = 1, SourceUpdate = 2, SourceCommit = 3;

    public static byte[] Encode(byte[] encKey, byte[] sigKey, byte[] credential,
                                byte[] capabilities, int source, byte[]? sourcePayload,
                                byte[] extensions, byte[] signature)
    {
        var w = new TlsWriter();
        w.Bytes(encKey).Bytes(sigKey).Raw(credential).Raw(capabilities);
        switch (source)
        {
            case SourceKeyPackage:
                w.U8(SourceKeyPackage).Raw(sourcePayload!);   // Lifetime struct, inline
                break;
            case SourceUpdate:
                w.U8(SourceUpdate);
                break;
            default:
                w.U8(SourceCommit).Bytes(sourcePayload!);       // ParentHash opaque
                break;
        }
        w.Raw(extensions).Bytes(signature);
        return w.Buf.ToArray();
    }

    // Returns (encKey, sigKey, credential, capabilities, source, sourcePayload,
    //          extensions, signature, consumed). Credential and capabilities are
    // structs: parsed structurally and captured as their exact inline byte spans.
    public static (byte[] encKey, byte[] sigKey, byte[] credential, byte[] capabilities,
                   int source, byte[] sourcePayload, byte[] extensions, byte[] signature, int consumed)
        Decode(ReadOnlySpan<byte> data)
    {
        var r = new TlsReader(data);
        var enc = r.Bytes();
        var sig = r.Bytes();
        int credStart = r.Position;
        r.U16();                        // credential type
        r.Bytes();                      // identity
        var cred = data.Slice(credStart, r.Position - credStart).ToArray();
        int capsStart = r.Position;
        for (int i = 0; i < 5; i++) r.Bytes();   // versions/ciphersuites/extensions/proposals/credentials
        var caps = data.Slice(capsStart, r.Position - capsStart).ToArray();
        int source = r.U8();
        byte[] payload = Array.Empty<byte>();
        if (source == SourceKeyPackage)
        {
            payload = data.Slice(r.Position, 16).ToArray();   // Lifetime: not_before/not_after u64s
            r.Skip(16);
        }
        else if (source == SourceCommit) payload = r.Bytes();
        // Extensions is an ExtensionList (V<V>): capture the FULL serialized
        // bytes including its vector varint, so a stored-then-re-encoded field
        // (ParseLeaf → LeafBytes) round-trips byte-exact. Capturing just the
        // Bytes() payload lost the leading 0x00 of an empty list — the re-encoded
        // leaf then dropped that byte and the signature varint misaligned the parse.
        int extsStart = r.Position;
        int extsLen = r.VecLength();
        r.Skip(extsLen);
        var exts = data.Slice(extsStart, r.Position - extsStart).ToArray();
        var sigBytes = r.Bytes();
        return (enc, sig, cred, caps, source, payload, exts, sigBytes, r.Position);
    }

    public static byte[] EncodeParentHash(byte[] ph) => new TlsWriter().Bytes(ph).Buf.ToArray();
}

// ParentNode: public_key<opaque> parent_hash<opaque> unmerged_leaves<vector<u32>>.
static class MlsParentNode
{
    public static byte[] Encode(byte[] pubKey, byte[] parentHash, List<uint> unmerged)
    {
        var w = new TlsWriter();
        w.Bytes(pubKey).Bytes(parentHash).Vec(v => { foreach (var u in unmerged) v.U32(u); });
        return w.Buf.ToArray();
    }

    public static (byte[] pubKey, byte[] parentHash, List<uint> unmerged) Decode(ReadOnlySpan<byte> data)
    {
        var r = new TlsReader(data);
        var pk = r.Bytes();
        var ph = r.Bytes();
        var unmerged = new List<uint>();
        r.Vec(v => unmerged.Add(v.U32()));
        return (pk, ph, unmerged);
    }
}

// KeyPackage (RFC 9420 §12.2): version u16 cipher_suite u16 init_key<opaque>
//             LeafNode(inline) extensions(ExtensionList inline) signature<opaque>
static class MlsKeyPackage
{
    public static byte[] Encode(byte[] initKey, byte[] leafNode, byte[] extensions, byte[] signature)
    {
        return new TlsWriter()
            .U16(1).U16(2)          // mls10, P256_AES128GCM_SHA256_P256
            .Bytes(initKey).Raw(leafNode).Raw(extensions).Bytes(signature)
            .Buf.ToArray();
    }

    // Returns (initKey, leafNode, extensions, signature, consumed). The extensions
    // are an inline ExtensionList (V<V>): capture the FULL serialized bytes including
    // their vector varint, so re-encoding with Raw() round-trips byte-exact for the
    // KeyPackageTBS reconstruction (reading them as an opaque dropped the leading
    // 0x00 of an empty list and the key package signature never verified).
    public static (byte[] initKey, byte[] leafNode, byte[] extensions, byte[] signature, int consumed)
        Decode(ReadOnlySpan<byte> data)
    {
        var r = new TlsReader(data);
        int version = r.U16();
        int suite = r.U16();
        if (version != 1 || suite != 2) throw new InvalidDataException("unexpected kp params");
        var init = r.Bytes();
        int leafStart = r.Position;
        var (_, _, _, _, _, _, _, _, leafLen) = MlsLeafNode.Decode(data.Slice(leafStart));
        r.Skip(leafLen);
        var leaf = data.Slice(leafStart, leafLen).ToArray();
        int extsStart = r.Position;
        int extsLen = r.VecLength();
        r.Skip(extsLen);
        var exts = data.Slice(extsStart, r.Position - extsStart).ToArray();
        var sig = r.Bytes();
        return (init, leaf, exts, sig, r.Position);
    }
}

// Proposal (RFC 9420 §12.1): variant u16 tag (1=add, 3=remove) + payload struct (inline).
//   add:    KeyPackage (inline struct)
//   remove: LeafIndex u32
static class MlsProposal
{
    public const int Add = 1, Remove = 3;

    public static byte[] EncodeAdd(byte[] keyPackage) =>
        new TlsWriter().U16(Add).Raw(keyPackage).Buf.ToArray();

    public static byte[] EncodeRemove(uint leafIndex) =>
        new TlsWriter().U16(Remove).U32(leafIndex).Buf.ToArray();

    // Returns (type, payload, consumed). For add the payload is the inline KeyPackage struct.
    public static (int type, byte[] payload, int consumed) Decode(ReadOnlySpan<byte> data)
    {
        var r = new TlsReader(data);
        int t = r.U16();
        if (t == Add)
        {
            int start = r.Position;
            var (_, _, _, _, kpLen) = MlsKeyPackage.Decode(data.Slice(start));
            return (t, data.Slice(start, kpLen).ToArray(), start + kpLen);
        }
        if (t == Remove)
        {
            if (data.Length < 6) throw new InvalidDataException("remove proposal too short");
            return (t, data.Slice(2, 4).ToArray(), 6);
        }
        throw new InvalidDataException("unsupported proposal type " + t);
    }
}

// HPKECiphertext: kem_output<opaque> ciphertext<opaque>.
static class MlsHpkeCiphertext
{
    public static byte[] Encode(byte[] kem, byte[] ct) =>
        new TlsWriter().Bytes(kem).Bytes(ct).Buf.ToArray();

    public static (byte[] kem, byte[] ct) Decode(ReadOnlySpan<byte> data)
    {
        var r = new TlsReader(data);
        var kem = r.Bytes();
        var ct = r.Bytes();
        return (kem, ct);
    }
}

// UpdatePathNode: public_key<opaque> encrypted_path_secret<vector<HPKECiphertext struct>>.
// UpdatePath: LeafNode(inline) nodes<vector<UpdatePathNode struct>>.
// (RFC 9420 §12.4.2 — struct members inline, only opaque/vector fields carry lengths.)
static class MlsUpdatePath
{
    public static byte[] EncodeNode(byte[] pubKey, List<byte[]> pathCts)
    {
        return new TlsWriter().Bytes(pubKey).Vec(w => { foreach (var c in pathCts) w.Raw(c); }).Buf.ToArray();
    }

    public static byte[] Encode(byte[] leafNode, List<byte[]> nodes) =>
        new TlsWriter().Raw(leafNode).Vec(w => { foreach (var n in nodes) w.Raw(n); }).Buf.ToArray();

    // Returns (leafNode, nodes, consumed).
    public static (byte[] leafNode, List<byte[]> nodes, int consumed) Decode(ReadOnlySpan<byte> data)
    {
        var r = new TlsReader(data);
        int leafStart = r.Position;
        var (_, _, _, _, _, _, _, _, leafLen) = MlsLeafNode.Decode(data.Slice(leafStart));
        r.Skip(leafLen);
        var leaf = data.Slice(leafStart, leafLen).ToArray();
        var nodes = new List<byte[]>();
        var buf = data.ToArray();               // Vec's callback cannot capture the span
        r.Vec(v =>
        {
            int start = v.Position;
            v.Bytes();                                  // public key
            v.Vec(x => { x.Bytes(); x.Bytes(); });      // cts: inline HPKECiphertext [kem V][ct V]
            nodes.Add(buf.AsSpan(start, v.Position - start).ToArray());
        });
        return (leaf, nodes, r.Position);
    }

    // UpdatePathNode: public_key + vector<HPKECiphertext> (inline structs).
    public static (byte[] pub, List<(byte[] kem, byte[] ct)> cts) DecodeNode(ReadOnlySpan<byte> data)
    {
        var r = new TlsReader(data);
        var pub = r.Bytes();
        var cts = new List<(byte[], byte[])>();
        r.Vec(v =>
        {
            var kem = v.Bytes();
            var ct = v.Bytes();
            cts.Add((kem, ct));
        });
        return (pub, cts);
    }
}

// Commit: proposals<vector<ProposalOrRef>> path<optional<UpdatePath>>.
// ProposalOrRef: u8 tag (1=value, 2=reference) + content.
static class MlsCommit
{
    public static byte[] Encode(List<(int tag, byte[] content)> proposals, byte[]? path)
    {
        var w = new TlsWriter();
        w.Vec(v => { foreach (var (tag, c) in proposals) v.U8(tag).Bytes(c); });
        if (path == null) w.U8(0);
        else w.U8(1).Bytes(path);
        return w.Buf.ToArray();
    }

    // Returns (proposals, path, consumed). The optional path carries the UpdatePath
    // struct inline (presence byte then the struct, no opaque wrapper).
    public static (List<(int tag, byte[] content)> proposals, byte[]? path, int consumed) Decode(ReadOnlySpan<byte> data)
    {
        var r = new TlsReader(data);
        var proposals = new List<(int, byte[])>();
        r.Vec(v =>
        {
            int tag = v.U8();
            var c = v.Bytes();
            proposals.Add((tag, c));
        });
        byte[]? path = null;
        if (r.OptFlag())
        {
            int start = r.Position;
            var (_, _, pLen) = MlsUpdatePath.Decode(data.Slice(start));
            r.Skip(pLen);
            path = data.Slice(start, pLen).ToArray();
        }
        return (proposals, path, r.Position);
    }
}

// GroupContext: version u16 suite u16 group_id<opaque> epoch u64 tree_hash<opaque>
//               confirmed_transcript_hash<opaque> extensions(ExtensionList)
static class MlsGroupContext
{
    public static byte[] Encode(byte[] groupId, ulong epoch, byte[] treeHash,
                                byte[] confirmedTranscript, byte[] extensions)
    {
        return new TlsWriter()
            .U16(1).U16(2)
            .Bytes(groupId).U64(epoch)
            .Bytes(treeHash).Bytes(confirmedTranscript)
            .Bytes(extensions)
            .Buf.ToArray();
    }
}

// GroupInfo (RFC 9420 §12.4.3): GroupContext(inline) extensions(ExtensionList inline)
//            confirmation_tag<opaque> signer u32 signature<opaque>
static class MlsGroupInfo
{
    public static byte[] Encode(byte[] groupContext, byte[] extensions, byte[] confirmationTag,
                                uint signer, byte[] signature)
    {
        return new TlsWriter()
            .Raw(groupContext).Raw(extensions).Bytes(confirmationTag)
            .U32(signer).Bytes(signature)
            .Buf.ToArray();
    }

    // Returns (groupContext, extensions, confirmationTag, signer, signature, consumed).
    public static (byte[] groupContext, byte[] extensions, byte[] confirmationTag, uint signer,
                   byte[] signature, int consumed)
        Decode(ReadOnlySpan<byte> data)
    {
        var r = new TlsReader(data);
        int gcStart = r.Position;
        // GroupContext is self-delimiting: version u16, suite u16, five opaque V fields.
        r.U16(); r.U16(); r.Bytes(); r.U64(); r.Bytes(); r.Bytes(); r.Bytes();
        var gc = data.Slice(gcStart, r.Position - gcStart).ToArray();
        // ExtensionList is inline (V<V>): keep its leading vector varint so the
        // bytes are exactly what Encode's Raw() wrote — reading it as an opaque
        // would consume that varint and leave DecodeList mis-parsing the type u16.
        int extStart = r.Position;
        int extLen = r.VecLength();
        r.Skip(extLen);
        var exts = data.Slice(extStart, r.Position - extStart).ToArray();
        var tag = r.Bytes();
        var signer = r.U32();
        var sig = r.Bytes();
        return (gc, exts, tag, signer, sig, r.Position);
    }
}

// GroupSecrets: joiner_secret<opaque> path_secret<optional<opaque>> psks<vector>.
static class MlsGroupSecrets
{
    public static byte[] Encode(byte[] joinerSecret, byte[]? pathSecret)
    {
        var w = new TlsWriter();
        w.Bytes(joinerSecret);
        if (pathSecret == null) w.U8(0);
        else w.U8(1).Bytes(pathSecret);
        w.Vec(v => { });            // no PSKs
        return w.Buf.ToArray();
    }

    public static (byte[] joinerSecret, byte[]? pathSecret) Decode(ReadOnlySpan<byte> data)
    {
        var r = new TlsReader(data);
        var js = r.Bytes();
        byte[]? ps = null;
        if (r.OptFlag()) ps = r.Bytes();
        r.Vec(v => { });            // skip psks
        return (js, ps);
    }
}

// EncryptedGroupSecrets: new_member<opaque ref> encrypted_group_secrets<HPKECiphertext>.
// The HPKECiphertext is a struct whose members are inline (RFC 9420 §12.4.2): the
// wire form after new_member is kem_output<opaque> then ciphertext<opaque> — the
// encrypted_group_secrets field carries NO length of its own. Wrapping the pair in
// one opaque added 2 bytes per secret and made other clients' welcomes unparseable.
// Welcome: cipher_suite u16 secrets<vector<EncryptedGroupSecrets>> encrypted_group_info<opaque>.
static class MlsWelcome
{
    public static byte[] Encode(List<(byte[] kpRef, byte[] encGroupSecrets)> secrets, byte[] encGroupInfo)
    {
        return new TlsWriter()
            .U16(2)
            .Vec(w => { foreach (var (r, g) in secrets) { w.Bytes(r); w.Raw(g); } })
            .Bytes(encGroupInfo)
            .Buf.ToArray();
    }

    public static (int suite, List<(byte[] kpRef, byte[] encGroupSecrets)> secrets, byte[] encGroupInfo)
        Decode(ReadOnlySpan<byte> data)
    {
        var r = new TlsReader(data);
        int suite = r.U16();
        var secrets = new List<(byte[], byte[])>();
        r.Vec(v =>
        {
            var kpRef = v.Bytes();
            var kem = v.Bytes();
            var ct = v.Bytes();
            secrets.Add((kpRef, MlsHpkeCiphertext.Encode(kem, ct)));
        });
        var info = r.Bytes();
        return (suite, secrets, info);
    }
}// AuthenticatedContent (RFC 9420 §6.1, serialized as the MLSMessage public-message
// payload): group_id<opaque> epoch u64 sender(variant u8 + payload)
// authenticated_data<opaque> content_type u8 + content(struct, inline)
// auth: signature<opaque> (+ confirmation_tag<opaque> for commit)
// member-sent messages append a membership_tag<opaque> after auth (mlspp
// PublicMessage: content || auth || membership_tag; the tag covers the TBS+auth
// and is verified by every member, so omitting it makes the message unparseable
// to mlspp — Discord's server rejects such commits).
// The wire_format is NOT serialized here: it is the MLSMessage's variant tag, and
// the signing/confirmed-transcript inputs prepend it separately.
static class MlsAuthContent
{
    public const int SenderMember = 1, SenderExternal = 2;
    public const int ContentProposal = 2, ContentCommit = 3;
    public const int ProposalOrRefValue = 1, ProposalOrRefRef = 2;

    public static byte[] EncodePublicMessage(byte[] groupId, ulong epoch,
                                             int senderType, uint senderIndex,
                                             int contentType, byte[] content,
                                             byte[] signature, byte[]? confirmationTag,
                                             byte[]? membershipTag = null)
    {
        var w = new TlsWriter();
        w.Bytes(groupId).U64(epoch);
        w.U8(senderType).U32(senderIndex);          // member: leaf idx / external: sender idx
        w.Vec(v => { });                            // authenticated_data
        w.U8(contentType).Raw(content);             // content struct, inline
        w.Bytes(signature);
        if (confirmationTag != null) w.Bytes(confirmationTag);
        if (membershipTag != null) w.Bytes(membershipTag);
        return w.Buf.ToArray();
    }

    // Returns (groupId, epoch, senderType, senderIndex, contentType, content,
    //          signature, confirmationTag, membershipTag, consumed). Content
    //          length is derived by parsing the proposal/commit struct (they are
    //          self-delimiting).
    public static (byte[] groupId, ulong epoch, int senderType, uint senderIndex,
                   int contentType, byte[] content, byte[] signature, byte[]? confirmationTag,
                   byte[]? membershipTag, int consumed)
        Decode(ReadOnlySpan<byte> data)
    {
        var r = new TlsReader(data);
        var gid = r.Bytes();
        ulong epoch = r.U64();
        int senderType = r.U8();
        uint senderIndex = 0;
        if (senderType == SenderMember || senderType == SenderExternal) senderIndex = r.U32();
        r.Bytes();                              // authenticated_data (always empty in DAVE)
        int contentType = r.U8();
        int contentStart = r.Position;
        int contentLen;
        if (contentType == ContentProposal)
        {
            var (_, _, pc) = MlsProposal.Decode(data.Slice(contentStart));
            contentLen = pc;
        }
        else if (contentType == ContentCommit)
        {
            var (_, _, cc) = MlsCommit.Decode(data.Slice(contentStart));
            contentLen = cc;
        }
        else throw new InvalidDataException("unsupported content type " + contentType);
        r.Skip(contentLen);
        var content = data.Slice(contentStart, contentLen).ToArray();
        var sig = r.Bytes();
        byte[]? conf = null;
        if (contentType == ContentCommit) conf = r.Bytes();
        byte[]? memTag = null;
        if (senderType == SenderMember && r.Remaining > 0) memTag = r.Bytes();
        return (gid, epoch, senderType, senderIndex, contentType, content, sig, conf, memTag, r.Position);
    }

    // The full serialized AuthenticatedContent (what proposal refs hash over).
    public static byte[] RefHashInput(byte[] authContent)
    {
        var label = Encoding.ASCII.GetBytes("MLS 1.0 Proposal Reference");
        return new TlsWriter().Bytes(label).Bytes(authContent).Buf.ToArray();
    }
}

// MLSMessage (RFC 9420 §6): version u16 wire_format u16 + payload struct (inline).
// The payload is the variant selected by wire_format — for public messages that is
// the serialized AuthenticatedContent above.
static class MlsMessage
{
    public const int WirePublic = 1, WireKeyPackage = 5;

    public static byte[] Encode(byte[] authContent) =>
        new TlsWriter().U16(1).U16(1).Raw(authContent).Buf.ToArray();

    public static byte[] EncodeKeyPackage(byte[] keyPackage) =>
        new TlsWriter().U16(1).U16(WireKeyPackage).Raw(keyPackage).Buf.ToArray();

    // Returns (wireFormat, body, consumed) where body is the variant payload.
    public static (int wireFormat, byte[] body, int consumed) Decode(ReadOnlySpan<byte> data)
    {
        var r = new TlsReader(data);
        int version = r.U16();
        int wire = r.U16();
        if (version != 1) throw new InvalidDataException("unexpected message version");
        if (wire == WirePublic)
        {
            var (_, _, _, _, _, _, _, _, _, authLen) = MlsAuthContent.Decode(data.Slice(r.Position));
            return (wire, data.Slice(r.Position, authLen).ToArray(), r.Position + authLen);
        }
        return (wire, data.Slice(r.Position).ToArray(), data.Length);
    }
}
