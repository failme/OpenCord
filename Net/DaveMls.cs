using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace OpenCord;

// ─────────────────────────────────────────────────────────────────────────────
// DAVE — Discord Audio Video End-to-End Encryption (the MLS layer).
//
// The voice gateway runs one MLS group per media session (group id = the voice
// channel's snowflake). This class plays one member of that group:
//
//   * parses the gateway's DAVE opcodes 21-31 (binary 25/27/29/30, JSON 21/22/24),
//   * creates the local group, exchanges key packages, commits and welcomes,
//   * derives per-sender key ratchets — MLS-Exporter("Discord Secure Frames v0",
//     LE64(userId), 16) → RFC 9420 §9.1 hash ratchet — per epoch,
//   * applies the protocol frame transform (AES-128-GCM, 8-byte tag, ULEB128
//     little-endian nonce at GCM bytes 8-11, unencrypted ranges, 0xFAFA footer).
//
// Everything here was cross-checked against mlspp (Discord's MLS library),
// libdave, the dave-protocol spec, and go-dave — the only byte-level unknowns
// are Discord-private, and every public source agrees on the layout used below.
// ─────────────────────────────────────────────────────────────────────────────

sealed class DaveMls
{
    public event Action<string>? LogLine;

    // Outbound gateway sends, wired by the caller.
    public Action<byte[]>? SendBinary;          // DAVE binary packet: [op][opaque...]
    public Action<int, object>? SendJson;       // JSON DAVE opcodes (23 ready, 31 invalid)

    const int TagBytes = 8;
    const int SupplementBase = TagBytes + 1 + 2;    // tag + supplement-size byte + 0xFAFA

    readonly ulong _selfUserId;
    readonly ulong _channelId;
    byte[] _encD = null!, _encPub = null!;
    byte[] _sigD = null!, _sigPub = null!;
    byte[] _initD = null!, _initPub = null!;
    byte[]? _extSigKey;
    byte[]? _extIdentity;
    byte[]? _lastKeyPackage;

    MlsGroup? _group;               // current group (pending or established)
    uint _pendingTransitionId;

    // send side
    MlsCrypto.HashRatchet? _sendRatchet;
    uint _sendCounter;

    // receive side: one ratchet per sender user id; the previous epoch's ratchets
    // are retained for 10s to catch in-flight frames (spec §Commit Handling).
    // _recv/_prevRecv/_ssrcUser are published by the gateway thread (transitions,
    // speaking events) and read by the UDP receive thread, so swaps go through
    // Volatile and the ssrc map is a ConcurrentDictionary.
    Dictionary<ulong, (uint epoch, MlsCrypto.HashRatchet ratchet)> _recv = new();
    Dictionary<ulong, (uint epoch, MlsCrypto.HashRatchet ratchet)> _prevRecv = new();
    ConcurrentDictionary<uint, ulong> _ssrcUser = new();

    byte[]? _nextSendBase;
    Dictionary<ulong, (uint epoch, MlsCrypto.HashRatchet ratchet)>? _nextRecv;
    long _prevDeadlineField;
    volatile bool _ready;

    public bool Enabled { get; private set; }    // session description had DAVE v1
    public bool Ready => _ready;                 // transition executed: ratchets live
    public bool SuppressCommit;                  // probe hook: process proposals but never send op 28
                                                 // (isolates the double-commit race: only one member commits)

    public DaveMls(ulong selfUserId, ulong channelId)
    {
        _selfUserId = selfUserId;
        _channelId = channelId;
        (_encD, var ex, var ey) = MlsCrypto.GenP256();
        _encPub = MlsCrypto.PubPoint(ex, ey);
        (_sigD, var sx, var sy) = MlsCrypto.GenP256();
        _sigPub = MlsCrypto.PubPoint(sx, sy);
    }

    void Log(string line) => LogLine?.Invoke(line);

    // ── session description ───────────────────────────────────────────────────
    public void OnSessionDescription(int daveProtocolVersion)
    {
        Enabled = daveProtocolVersion != 0;
        if (!Enabled) return;
        // The key package must go out as soon as select_protocol_ack (op 4)
        // advertises a non-zero DAVE version — the gateway registers this
        // connection as a pending group member at that point and broadcasts
        // add proposals once it has the key package (spec §Key Packages,
        // §Member Add; mirrored by discord.py/davey). Waiting for the
        // external-sender package (op 25) leaves us outside the group
        // negotiation: the server never includes us in an epoch, so the
        // E2EE transition never begins. The external-sender handler no
        // longer resends, so this is the only key package of the join.
        SendKeyPackage();
    }

    // ── key package (op 26) ───────────────────────────────────────────────────
    public byte[] BuildKeyPackage()
    {
        var (d, x, y) = MlsCrypto.GenP256();
        _initD = d;
        _initPub = MlsCrypto.PubPoint(x, y);
        var identity = Be64(_selfUserId);
        var cred = MlsCredential.Encode(identity);
        var caps = MlsCapabilities.EncodeDefault();
        var lifetime = MlsLifetime.EncodeMax();
        var noExts = MlsExtensions.EncodeList();
        // LeafNodeTBS / KeyPackageTBS (RFC 9420 §12.2/§12.4.5): struct members are
        // INLINE — credential, capabilities, the key-package Lifetime, the LeafNode
        // and the ExtensionList carry no length prefix. Varint-wrapping them signed
        // a different byte string than mlspp recomputes on the server side, so the
        // gateway's signature check silently rejected every key package and the
        // E2EE transition never began. Mirrors MlsGroup.LeafNodeTbs.
        var leafTbs = new TlsWriter()
            .Bytes(_encPub).Bytes(_sigPub).Raw(cred).Raw(caps)
            .U8(MlsLeafNode.SourceKeyPackage).Raw(lifetime).Raw(noExts).Buf.ToArray();
        var leafSig = MlsCrypto.SignWithLabel(_sigD, "LeafNodeTBS", leafTbs);
        var leaf = MlsLeafNode.Encode(_encPub, _sigPub, cred, caps, MlsLeafNode.SourceKeyPackage,
                                      lifetime, noExts, leafSig);
        var kpTbs = new TlsWriter().U16(1).U16(2).Bytes(_initPub).Raw(leaf).Raw(noExts).Buf.ToArray();
        var kpSig = MlsCrypto.SignWithLabel(_sigD, "KeyPackageTBS", kpTbs);
        return MlsKeyPackage.Encode(_initPub, leaf, noExts, kpSig);
    }

    public void SendKeyPackage()
    {
        var kp = BuildKeyPackage();
        _lastKeyPackage = kp;
        var w = new TlsWriter();
        // [op][bare KeyPackage] — the MLSMessage/KeyPackage carries its own version
        // and cipher-suite fields, so there is NO varint length prefix here. (The old
        // opaque-wrapped framing made the gateway read our length bytes as the key
        // package's version field and silently drop it.) discord.py/davey send the
        // same shape: bytes([op]) + serialized key package.
        w.U8(26).Raw(kp);
        SendBinary?.Invoke(w.Buf.ToArray());
        Log($"sent key package ({kp.Length} bytes)");
    }

    // ── local group ───────────────────────────────────────────────────────────
    void EnsureGroup()
    {
        if (_group != null || _extSigKey == null) return;
        var extData = MlsExternalSender.EncodeSenders(_extSigKey, _extIdentity ?? new byte[] { 0 });
        // The group id is the channel snowflake BIG-endian (go-dave
        // channelIDBytes / davey channel_id.to_be_bytes()): the gateway's
        // proposals carry it BE, so a little-endian local id rejected every
        // add proposal and the group never formed.
        _group = MlsGroup.Create(Be64(_channelId), _encD, _encPub, _sigD, _sigPub,
                                 Be64(_selfUserId),
                                 MlsExtensions.EncodeList((MlsExtensions.ExternalSenders, extData)),
                                 extData);
        Log("created local MLS group (channel " + _channelId + ")");
    }

    // ── binary DAVE opcodes from the gateway ──────────────────────────────────
    public void HandleDave(int op, byte[] payload)
    {
        try
        {
            switch (op)
            {
                case 25: HandleExternalSender(payload); break;
                case 27: HandleProposals(payload); break;
                case 29: HandleAnnounceCommit(payload); break;
                case 30: HandleWelcome(payload); break;
                default: Log($"unhandled DAVE opcode {op}"); break;
            }
        }
        catch (Exception e) { Log($"dave op {op}: {e.Message}"); }
    }

    void HandleExternalSender(byte[] payload)
    {
        var r = new TlsReader(payload);
        var sigKey = r.Bytes();                  // opaque SignaturePublicKey (varint prefix)
        var credData = payload.AsSpan(r.Position).ToArray();
        var (type, identity) = MlsCredential.Decode(credData);
        if (type != MlsCredential.Basic) throw new InvalidDataException("external sender credential must be basic");
        _extSigKey = sigKey;
        _extIdentity = identity;
        Log($"external sender: sigkey {sigKey.Length}B identity {Convert.ToHexString(identity)}");
        EnsureGroup();
        // No key package here: it was already sent when the session description
        // (op 4) negotiated a non-zero DAVE version, and the server builds the
        // add proposal from that one. Resending would replace the cached package
        // the pending add proposal references.
    }

    void HandleProposals(byte[] payload)
    {
        EnsureGroup();
        if (_group == null) { Log("proposals before group — ignored"); return; }
        var r = new TlsReader(payload);
        int operation = r.U8();
        int pos = r.Position;
        long n = Varint.Read(payload, ref pos);
        int end = pos + (int)n;
        if (end > payload.Length) throw new InvalidDataException("proposals vector overrun");
        var body = payload.AsSpan(pos, (int)n);

        if (operation == 1)                      // revoke: ProposalRef list
        {
            var refs = new List<byte[]>();
            var rr = new TlsReader(body);
            while (!rr.Done) refs.Add(rr.Bytes());
            _group.RevokeProposals(refs);
            Log($"revoked {refs.Count} proposals");
            return;
        }
        if (operation != 0) throw new InvalidDataException("unknown proposals operation");

        // append: `MLSMessage proposal_messages<V>` — a varint-length vector of RAW
        // MLSMessages. Each message is [version u16][wire u16][FramedContent][auth]:
        // structurally self-delimiting (the inner opaque/vector fields carry their
        // own lengths), so each is parsed to find the next boundary — the same way
        // go-dave's consumeMLSMessage walks the vector. Treating them as individual
        // opaque blobs mis-parsed the group_id varint as a message length and every
        // proposal was rejected, which stalled the E2EE transition.
        int count = 0;
        int p = 0;
        while (p < body.Length)
        {
            int start = p;
            var (wire, _, consumed) = MlsMessage.Decode(body[start..]);
            // DAVE proposals are always public messages (external senders cannot
            // create private ones); a private/wrong-wire message here would have
            // consumed the whole remaining vector as one element and silently
            // swallowed every proposal after it.
            if (wire != MlsMessage.WirePublic) throw new InvalidDataException("non-public proposal wire format");
            if (consumed <= 0) throw new InvalidDataException("proposal parse underrun");
            p += consumed;
            if (p > body.Length) throw new InvalidDataException("proposal vector overrun");
            if (_extSigKey == null) throw new InvalidDataException("no external sender for proposal");
            if (_group.ProcessProposal(body[start..p].ToArray(), _extSigKey)) count++;
        }
        Log($"processed {count} proposals (pending {_group.Pending.Count})");

        if (_group.Pending.Count > 0 && !SuppressCommit)
        {
            var (commit, welcome) = _group.BuildCommitAndWelcome();
            var w = new TlsWriter();
            // [28][bare commit][bare welcome?] — both MLSMessage structs are inline
            // (no varint length), with the welcome present iff the commit added
            // members. Matches discord.py: bytes([28]) + commit + (welcome or "").
            w.U8(28).Raw(commit);
            if (welcome.Length > 0) w.Raw(welcome);
            SendBinary?.Invoke(w.Buf.ToArray());
            Log($"sent commit {commit.Length}B welcome {welcome.Length}B");
        }
        else if (_group.Pending.Count > 0)
        {
            Log($"commit suppressed ({_group.Pending.Count} pending) — waiting for the welcome");
        }
    }

    void HandleAnnounceCommit(byte[] payload)
    {
        // [transition_id u16][commit_message...] — the MLSMessage is inline (no
        // varint length), like the gateway's other DAVE binary payloads.
        var r = new TlsReader(payload);
        uint transitionId = (uint)r.U16();
        var commit = payload.AsSpan(r.Position).ToArray();
        if (_group == null) { Log("commit before group — ignored"); return; }

        // Simultaneous-join race: two pending members joining a fresh call both
        // commit; the gateway broadcasts the winning commit to everyone. The loser
        // has already joined via the welcome, so the announced commit is from an
        // OLDER epoch — flagging it invalid would reset a perfectly good join and
        // the remove/re-add recovery would churn forever. Only flag commits that
        // are for our current epoch and genuinely unprocessable.
        try
        {
            var (_, authContent, _) = MlsMessage.Decode(commit);
            var (_, epoch, _, _, _, _, _, _, _, _) = MlsAuthContent.Decode(authContent);
            if (epoch < _group.Epoch)
            {
                Log($"stale announce commit (epoch {epoch} < {_group.Epoch}) — ignoring");
                return;
            }
        }
        catch (Exception e) { Log("commit parse: " + e.Message); }

        bool ok = _group.ProcessOwnCommit(commit);
        if (!ok) ok = _group.ProcessCommit(commit, false);
        if (!ok)
        {
            Log("commit rejected — flagging invalid commit");
            SendInvalid(transitionId);
            return;
        }
        PrepareTransition(transitionId);
    }

    void HandleWelcome(byte[] payload)
    {
        // [transition_id u16][welcome_message...] — inline MLSMessage, no varint.
        var r = new TlsReader(payload);
        uint transitionId = (uint)r.U16();
        var welcome = payload.AsSpan(r.Position).ToArray();
        if (_lastKeyPackage == null) throw new InvalidDataException("welcome before key package");
        var group = MlsGroup.FromWelcome(welcome, _lastKeyPackage, _initD, _initPub,
                                         _encD, _encPub, _sigD, _sigPub, Be64(_selfUserId));
        if (group == null)
        {
            Log("welcome rejected — flagging invalid welcome");
            SendInvalid(transitionId);
            return;
        }
        _group = group;
        PrepareTransition(transitionId);
    }

    // ── JSON DAVE opcodes ─────────────────────────────────────────────────────
    public void HandleDaveJson(int op, JsonElement d)
    {
        try
        {
            switch (op)
            {
                case 24:                         // prepare epoch
                    if (d.TryGetProperty("epoch", out var ep) && ep.GetInt64() == 1)
                        ResetForNewGroup();
                    break;
                case 21:                         // prepare transition
                    if (d.TryGetProperty("transition_id", out var t0))
                    {
                        int tid = t0.GetInt32();
                        // Spec §Sole member reset: tid 0 is (re)initialization and the
                        // client executes it immediately — no ready report, no op 22.
                        if (tid == 0) ExecuteTransition(0);
                        else SendJson?.Invoke(23, new { transition_id = tid });
                    }
                    break;
                case 22:                         // execute transition
                    if (d.TryGetProperty("transition_id", out var t1))
                        ExecuteTransition(t1.GetInt32());
                    break;
            }
        }
        catch (Exception e) { Log($"dave json op {op}: {e.Message}"); }
    }

    void ResetForNewGroup()
    {
        Log("reset for new group (epoch=1)");
        _group = null;
        _sendRatchet = null;
        _sendCounter = 0;
        _ready = false;
        Volatile.Write(ref _recv, new());
        Volatile.Write(ref _prevRecv, new());
        _nextRecv = null;
        _nextSendBase = null;
        EnsureGroup();
        SendKeyPackage();
    }

    void PrepareTransition(uint transitionId)
    {
        _pendingTransitionId = transitionId;
        // Prepare the ratchets the new epoch will use. The init transition id (0)
        // is used for (re)initialization and is applied IMMEDIATELY — the gateway
        // never follows it with an execute (op 22), so a client that waits (or
        // sends ready and stalls) never activates E2EE. Discord.Net returns right
        // after preparing for tid 0 and only reports ready (op 23) for tid > 0.
        _nextSendBase = _group!.SenderBaseSecret(_selfUserId);
        _nextRecv = new Dictionary<ulong, (uint, MlsCrypto.HashRatchet)>();
        foreach (var (uid, _) in _group.Roster())
            _nextRecv[uid] = ((uint)_group.Epoch, new MlsCrypto.HashRatchet(_group.SenderBaseSecret(uid)));
        // Diagnostic: dump the exporter secret, group context and per-user base secrets so
        // offline analysis can reproduce (and correct) the ratchet derivation against captured
        // peer frames — the media keys derive from these, and a mismatch anywhere poisons them.
        try
        {
            var ks = _group.Keys;
            Log($"mls epoch={_group.Epoch} exporter={Convert.ToHexString(ks.ExporterSecret)}");
            Log($"mls confirmed={Convert.ToHexString(_group.ConfirmedTranscript)}");
            Log($"mls ctx={Convert.ToHexString(_group.GroupContextBytes())}");
            Log($"mls selfBase={Convert.ToHexString(_nextSendBase)}");
            foreach (var (uid, _) in _group.Roster())
                Log($"mls base user={uid} secret={Convert.ToHexString(_group.SenderBaseSecret(uid))}");
        }
        catch (Exception ex) { Log($"mls diag failed: {ex.Message}"); }
        Log($"prepared transition {transitionId}: {_nextRecv.Count} sender ratchets (epoch {_group.Epoch})");
        // The spec (Welcome Handling / Commit Handling) requires EVERY client that
        // processed a welcome or commit to report readiness with op 23 — including
        // the tid=0 init transition. The gateway waits for the welcomed member's
        // ready before it finalizes the group and sends op 22 (execute); without it
        // the join is never confirmed server-side, the SFU never relays media, and
        // after ~10-20s the gateway re-initializes the joiner (op 24 sole-member
        // reset). Discord.Net skips op 23 for tid 0, but live tests show the reset
        // and total silence without it, and the spec is explicit.
        SendJson?.Invoke(23, new { transition_id = (int)transitionId });
        if (transitionId == 0) ExecuteTransition(0);
    }

    void ExecuteTransition(int transitionId)
    {
        if (_nextSendBase != null)
        {
            _sendRatchet = new MlsCrypto.HashRatchet(_nextSendBase);
            _sendCounter = 0;
            var cur = Volatile.Read(ref _recv);
            if (cur.Count > 0)
            {
                Volatile.Write(ref _prevRecv, cur);
                Volatile.Write(ref _prevDeadlineField, Stopwatch.GetTimestamp() + Stopwatch.Frequency * 10);
            }
            var next = _nextRecv ?? new();
            Volatile.Write(ref _recv, next);
            _nextRecv = null;
            _nextSendBase = null;
            _ready = true;
            Log($"transition {transitionId} executed: send ratchet active, {next.Count} receive ratchets");
            // Diagnostic: dump each receive ratchet's generation-0 key so offline
            // analysis can decrypt captured peer frames and pin the media format.
            foreach (var kv in next)
            {
                try
                {
                    var k0 = kv.Value.ratchet.Get(0);
                    Log($"recv ratchet user={kv.Key} gen0={Convert.ToHexString(k0)}");
                }
                catch { }
            }
        }
        else
        {
            Log($"transition {transitionId} executed (no group change)");
        }
    }

    void SendInvalid(uint transitionId)
    {
        try { SendJson?.Invoke(31, new { transition_id = (int)transitionId }); }
        catch { }
        ResetForNewGroup();
    }

    // ── ssrc ↔ user mapping (from gateway speaking / clients_connect events) ──
    public void OnSpeaking(ulong userId, uint ssrc) => _ssrcUser[ssrc] = userId;
    public void OnClientConnect(ulong userId, uint ssrc)
    {
        if (ssrc != 0) _ssrcUser[ssrc] = userId;
    }

    // ── frame transform ───────────────────────────────────────────────────────
    // Returns the protocol frame, or null when the caller should send the
    // plaintext (not ready, or a silence packet which passes through untransformed).
    public byte[]? ProtectFrame(ReadOnlySpan<byte> opus)
        => ProtectFrame(opus, System.Array.Empty<(int, int)>());

    // Ranged variant (video): `ranges` lists (offset, size) spans of the payload that ride
    // AUTHENTICATED but not encrypted — video codec headers must stay plaintext for the
    // packetizer. The rest of the payload is joined and encrypted as one block whose GCM AAD is
    // the concatenated unencrypted bytes; the frame is interleaved so the unencrypted spans keep
    // their original positions (libdave's exact construction).
    internal byte[]? ProtectFrame(ReadOnlySpan<byte> opus, IReadOnlyList<(int offset, int size)> ranges)
    {
        if (IsSilence(opus)) return opus.ToArray();
        if (!_ready || _sendRatchet == null) return null;

        // Shared with the video path (audio + video threads both call ProtectFrame), so the
        // counter must increment atomically — a torn increment would reuse a nonce.
        uint counter = (uint)Interlocked.Increment(ref _sendCounter);
        uint generation = counter >> 24;
        var key = _sendRatchet.Get(generation);

        var nonce = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(nonce.AsSpan(8), counter);   // bytes 8-11, LE

        // Split the payload into ciphertext blocks and authenticated (unencrypted) bytes.
        using var ctStream = new MemoryStream();
        using var aadStream = new MemoryStream();
        int idx = 0;
        foreach (var (off, sz) in ranges)
        {
            ctStream.Write(opus.Slice(idx, off - idx));
            aadStream.Write(opus.Slice(off, sz));
            idx = off + sz;
        }
        ctStream.Write(opus.Slice(idx, opus.Length - idx));
        var plain = ctStream.ToArray();
        var aad = aadStream.ToArray();
        var sealedBytes = VoiceRtp.GcmEncryptTrunc(key, nonce, plain, aad, TagBytes);

        // Serialize the ranges as `[offset uleb][size uleb]` pairs (no count byte).
        var rangeBuf = new byte[8];
        var rangeWriter = new MemoryStream();
        foreach (var (off, sz) in ranges)
        {
            int o = VoiceRtp.Uleb128((uint)off, rangeBuf);
            rangeWriter.Write(rangeBuf, 0, o);
            o = VoiceRtp.Uleb128((uint)sz, rangeBuf);
            rangeWriter.Write(rangeBuf, 0, o);
        }
        var rangesBytes = rangeWriter.ToArray();

        var uleb = new byte[5];
        int ulebLen = VoiceRtp.Uleb128(counter, uleb);
        int suppSize = SupplementBase + ulebLen + rangesBytes.Length;  // tag + nonce + ranges + size + marker

        // frame = interleaved || tag(8) || nonce uleb || ranges || supplement_size || 0xFAFA.
        // The tag is the last 8 bytes of sealedBytes and is NOT duplicated — the size byte
        // counts tag + nonce + ranges + itself + marker.
        var frame = new byte[opus.Length + TagBytes + ulebLen + rangesBytes.Length + 3];
        BuildInterleaved(opus, ranges, sealedBytes.AsSpan(0, plain.Length), frame);
        sealedBytes.AsSpan(plain.Length).CopyTo(frame.AsSpan(opus.Length));           // tag(8)
        uleb.AsSpan(0, ulebLen).CopyTo(frame.AsSpan(opus.Length + TagBytes));         // nonce
        rangesBytes.CopyTo(frame.AsSpan(opus.Length + TagBytes + ulebLen));           // ranges
        frame[^3] = (byte)suppSize;
        frame[^2] = 0xFA;
        frame[^1] = 0xFA;
        return frame;
    }

    // Lay the ciphertext into the encrypted spans of the payload, keeping the unencrypted spans
    // as the original bytes.
    static void BuildInterleaved(ReadOnlySpan<byte> payload, IReadOnlyList<(int offset, int size)> ranges,
                                 ReadOnlySpan<byte> ciphertext, Span<byte> outp)
    {
        int ci = 0;
        int oi = 0;
        foreach (var (off, sz) in ranges)
        {
            ciphertext.Slice(ci, off - oi).CopyTo(outp.Slice(oi, off - oi));
            payload.Slice(off, sz).CopyTo(outp.Slice(off, sz));
            ci += off - oi;
            oi = off + sz;
        }
        ciphertext.Slice(ci, payload.Length - oi).CopyTo(outp.Slice(oi, payload.Length - oi));
    }

    // ── H.264 frame protection (spec §Codec Handling / §H264 & H265) ────────────────────────────
    // The real client encrypts the WHOLE encoded video frame — not individual RTP packets. The
    // encryptor is codec-aware: it iterates the AU's NAL units and leaves unencrypted everything
    // the H.264 packetizer/depacketizer must read — a 4-byte start code per NAL, the NAL header
    // plus enough slice header to reach the PPS ID for VCL (slice/IDR) NALs, and the ENTIRE
    // non-VCL NAL (SPS/PPS/SEI). 3-byte input start codes are rewritten as 4-byte ones (WebRTC
    // expands them on the receiver, which would break the authenticated bytes). The ciphertext +
    // supplement must not contain a start-code sequence or the packetizer mis-splits; on a
    // collision the nonce is re-rolled (up to 10 tries), exactly like davey/libdave.
    public byte[]? ProtectVideoFrame(byte[] au)
    {
        if (!_ready || _sendRatchet == null || au.Length == 0) return null;
        // A malformed AU must never take down the capture thread: fall back to plaintext.
        try { return ProtectVideoFrameInner(au); }
        catch { return null; }
    }

    byte[]? ProtectVideoFrameInner(byte[] au)
    {
        var unenc = new List<byte>();
        var cipher = new List<byte>();
        var ranges = new List<(int off, int sz)>();
        int index = 0;

        void AddUnencrypted(ReadOnlySpan<byte> data)
        {
            if (data.Length == 0) return;
            if (ranges.Count > 0)
            {
                var last = ranges[^1];
                if (last.off + last.sz == index) ranges[^1] = (last.off, last.sz + data.Length);
                else ranges.Add((index, data.Length));
            }
            else ranges.Add((index, data.Length));
            unenc.AddRange(data.ToArray());
            index += data.Length;
        }

        void AddEncrypted(ReadOnlySpan<byte> data)
        {
            if (data.Length == 0) return;
            cipher.AddRange(data.ToArray());
            index += data.Length;
        }

        // Iterate NAL units (3- or 4-byte Annex-B start codes) exactly like davey's
        // process_frame_h264: always write the long start code unencrypted; VCL slice/IDR NALs
        // leave header + PPS-ID bytes unencrypted and encrypt the payload; non-VCL NALs ride
        // fully unencrypted.
        var next = NextH26xNal(au, 0);
        while (next is var (nalStart, _))
        {
            if (nalStart >= au.Length - 1) break;
            byte nalType = (byte)(au[nalStart] & 0x1F);
            AddUnencrypted(new byte[] { 0, 0, 0, 1 });   // 4-byte start code
            var nextPair = NextH26xNal(au, nalStart);
            int nextNalStart = nextPair is var (ns, cs) ? ns - cs : au.Length;
            if (nalType == 1 || nalType == 5)            // slice / IDR
            {
                int header = 1;
                int pps = H264PpsBytes(au.AsSpan(nalStart + header), au.Length - nalStart - header);
                int unencLen = Math.Min(header + pps, nextNalStart - nalStart);
                AddUnencrypted(au.AsSpan(nalStart, unencLen));
                AddEncrypted(au.AsSpan(nalStart + unencLen, nextNalStart - nalStart - unencLen));
            }
            else
            {
                AddUnencrypted(au.AsSpan(nalStart, nextNalStart - nalStart));
            }
            next = nextPair;
        }
        if (cipher.Count == 0) return null;               // nothing to encrypt (metadata-only AU)

        var aad = unenc.ToArray();
        var plain = cipher.ToArray();
        var rangesBytes = SerializeRanges(ranges);
        var uleb = new byte[5];

        // Encrypt + validate; re-roll the nonce when a start-code sequence lands in ciphertext.
        for (int attempt = 0; attempt < 10; attempt++)
        {
            uint counter = (uint)Interlocked.Increment(ref _sendCounter);
            uint generation = counter >> 24;
            byte[] key;
            try { key = _sendRatchet.Get(generation); }
            catch { return null; }
            var nonce = new byte[12];
            BinaryPrimitives.WriteUInt32LittleEndian(nonce.AsSpan(8), counter);
            var sealedBytes = VoiceRtp.GcmEncryptTrunc(key, nonce, plain, aad, TagBytes);
            // Rebuild the frame from the JOINED unencrypted bytes + THIS attempt's ciphertext.
            // The output start codes are 4-byte regardless of the input's 3-byte ones, so the
            // plaintext spans sit at different offsets than in the original — BuildInterleaved's
            // payload-at-range-offset model does not apply. (Built after encryption: the tag must
            // cover the real ciphertext, and the interleave must place that ciphertext, not the
            // original plaintext.)
            var frame = new byte[unenc.Count + plain.Length];
            {
                int ui = 0, ci = 0, oi = 0;
                foreach (var (off, sz) in ranges)
                {
                    sealedBytes.AsSpan(ci, off - oi).CopyTo(frame.AsSpan(oi, off - oi));
                    aad.AsSpan(ui, sz).CopyTo(frame.AsSpan(off, sz));
                    ci += off - oi;
                    ui += sz;
                    oi = off + sz;
                }
                sealedBytes.AsSpan(ci, frame.Length - oi).CopyTo(frame.AsSpan(oi, frame.Length - oi));
            }
            int ulebLen = VoiceRtp.Uleb128(counter, uleb);
            int suppSize = TagBytes + ulebLen + rangesBytes.Length + 1 + 2;
            var outp = new byte[frame.Length + suppSize];
            frame.CopyTo(outp, 0);                                                    // interleaved frame
            sealedBytes.AsSpan(plain.Length).CopyTo(outp.AsSpan(frame.Length));       // tag(8)
            uleb.AsSpan(0, ulebLen).CopyTo(outp.AsSpan(frame.Length + TagBytes));     // nonce
            rangesBytes.CopyTo(outp.AsSpan(frame.Length + TagBytes + ulebLen));       // ranges
            outp[^3] = (byte)suppSize;
            outp[^2] = 0xFA;
            outp[^1] = 0xFA;
            if (!HasStartCodeInCiphertext(outp, ranges)) return outp;
        }
        return null;   // 10 nonce re-rolls all collided: drop the frame (spec says the same)
    }

    static byte[] SerializeRanges(List<(int off, int sz)> ranges)
    {
        var rangeBuf = new byte[8];
        using var stream = new MemoryStream();
        foreach (var (off, sz) in ranges)
        {
            int o = VoiceRtp.Uleb128((uint)off, rangeBuf);
            stream.Write(rangeBuf, 0, o);
            o = VoiceRtp.Uleb128((uint)sz, rangeBuf);
            stream.Write(rangeBuf, 0, o);
        }
        return stream.ToArray();
    }

    // Scan the ciphertext sections (gaps between the unencrypted ranges) plus the supplement for
    // a 3/4-byte start-code sequence, with a 2-byte margin at range boundaries. Mirrors davey's
    // validate_encrypted_frame (PADDING = 2).
    static bool HasStartCodeInCiphertext(byte[] frame, List<(int off, int sz)> ranges)
    {
        const int Padding = 2;
        int sectionStart = 0;
        foreach (var (off, sz) in ranges)
        {
            if (sectionStart == off) { sectionStart += sz; continue; }
            int start = sectionStart - Math.Min(sectionStart, Padding);
            int end = Math.Min(off + Padding, frame.Length);
            if (NextH26xNal(frame.AsSpan(start, end - start), 0) != null) return true;
            sectionStart = off + sz;
        }
        if (sectionStart == frame.Length) return false;
        int s2 = sectionStart - Math.Min(sectionStart, Padding);
        if (NextH26xNal(frame.AsSpan(s2, frame.Length - s2), 0) != null) return true;
        return false;
    }

    // Find the next Annex-B start code (3- or 4-byte) at/after searchStart; returns the NAL
    // header index and the start-code length. Mirrors davey's next_h26x_nalu_index.
    static (int nalStart, int codeSize)? NextH26xNal(ReadOnlySpan<byte> buf, int searchStart)
    {
        if (buf.Length < 3) return null;
        int i = searchStart;
        while (i < buf.Length - 3)
        {
            if (buf[i + 2] > 1) i += 3;
            else if (buf[i + 1] != 0) i += 2;
            else if (buf[i] != 0 || buf[i + 2] != 1) i += 1;
            else
            {
                int nalStart = i + 3;
                if (i >= 1 && buf[i - 1] == 0) return (nalStart, 4);
                return (nalStart, 3);
            }
        }
        return null;
    }

    // Byte count into an H.264 slice header needed to reach the PPS ID: the first three
    // Exp-Golomb values (first_mb_in_slice, slice_type, pps_id), skipping emulation-prevention
    // bytes. Mirrors davey's bytes_covering_h264_pps.
    static int H264PpsBytes(ReadOnlySpan<byte> payload, int sizeRemaining)
    {
        const byte EmulationPrevention = 0x03;
        int bit = 0, zeroes = 0, parsed = 0;
        while (bit < (long)sizeRemaining * 8 && parsed < 3)
        {
            int bitIndex = bit % 8;
            int byteIndex = bit / 8;
            byte b = payload[byteIndex];
            if (bitIndex == 0 && byteIndex >= 2 && b == EmulationPrevention
                && payload[byteIndex - 1] == 0 && payload[byteIndex - 2] == 0)
            {
                bit += 8;
                continue;
            }
            if ((b & (1 << (7 - bitIndex))) == 0) { zeroes++; bit++; if (zeroes >= 32) return 0; }
            else { parsed++; bit += 1 + zeroes; zeroes = 0; }
        }
        long result = (bit / 8) + 1;
        return result <= ushort.MaxValue ? (int)result : 0;
    }

    // Test hook for the offline davey/libdave reference test (SelfTest dumps the frame + the
    // generation-0 send key; the Rust test decrypts the frame with the REAL library to prove our
    // H.264 whole-frame protection is byte-compatible with the reference — audio already is).
    public (byte[] frame, byte[] key0, byte[] baseSecret)? ProtectVideoFrameForDump(byte[] au)
    {
        if (!_ready || _sendRatchet == null || _group == null) return null;
        var frame = ProtectVideoFrame(au);
        if (frame == null) return null;
        try { return (frame, _sendRatchet.Get(0), _group.SenderBaseSecret(_selfUserId)); }
        catch { return null; }
    }

    public string? LastFailReason;    // first E2EE decrypt failure reason (probe diagnostics)

    void Fail(string why) => LastFailReason ??= why;

    int _passthroughLog;   // first few passthroughs, so a live log pins the peer's frame shape

    void LogPassthrough(uint ssrc, string why, ReadOnlySpan<byte> frame)
    {
        if (_passthroughLog++ >= 6) return;
        Log($"passthrough ({why}) ssrc={ssrc} len={frame.Length} " +
            $"head={Convert.ToHexString(frame[..Math.Min(frame.Length, 16)])} " +
            $"tail={Convert.ToHexString(frame[^Math.Min(frame.Length, 8)..])}");
    }

    // Decrypts a protocol frame into an Opus frame, or null if not decryptable.
    public byte[]? UnprotectFrame(uint ssrc, ReadOnlySpan<byte> frame)
    {
        LastFailReason = null;
        if (IsSilence(frame)) return frame.ToArray();
        // The SFU synthesizes DTX silence for muted/quiet sources. On the wire this arrives as a
        // junk-prefixed F8FFFE (observed against the real client: [8 bytes][F8 FF FE]) — not the
        // bare 3-byte passthrough the spec describes. Whatever the DAVE state, strip the prefix
        // and hand the decoder the exact 3-byte silence (a real protocol frame always ends in
        // [size][FA][FA], so it can never match this tail).
        if (frame.Length > 3 && frame[^3] == 0xF8 && frame[^2] == 0xFF && frame[^1] == 0xFE)
        {
            LogPassthrough(ssrc, "sfu-silence-suffix", frame);
            return frame[^3..].ToArray();
        }
        // Transitory period (armed but transition not executed): media flows as
        // plaintext transport-encrypted Opus until the gateway executes the
        // transition, so hand those frames to the decoder untouched.
        if (!_ready) return frame.ToArray();

        // Protocol frame check. The supplement (which includes the size byte and
        // the 0xFAFA marker) starts at len - supplement_size: tag(8) || nonce uleb
        // || unencrypted ranges || size || marker (go-dave/libdave agree).
        //
        // A frame WITHOUT the 0xFAFA footer is plaintext Opus — either the peer's
        // transition never executed (asymmetric MLS state: we are ready, they are
        // not) or the peer has no DAVE layer at all. Hand it through rather than
        // dropping it: fail-open here is what keeps a call audible when the MLS
        // exchange is one-sided. (A ready peer always writes the footer, so a
        // footerless frame can only be plaintext.)
        if (frame.Length < SupplementBase + 1 || frame[^1] != 0xFA || frame[^2] != 0xFA)
        {
            LogPassthrough(ssrc, "no-footer", frame);
            return frame.ToArray();
        }
        int suppSize = frame[^3];
        if (suppSize < SupplementBase || suppSize > frame.Length - 3) { Fail($"bad suppSize {suppSize}"); return null; }
        int suppStart = frame.Length - suppSize;

        var tag = frame.Slice(suppStart, TagBytes);
        var nonceAndRanges = frame.Slice(suppStart + TagBytes, frame.Length - 3 - suppStart - TagBytes);
        if (!VoiceRtp.TryUleb128(nonceAndRanges, out uint counter, out int nonceLen)) { Fail($"bad uleb {Convert.ToHexString(nonceAndRanges)}"); return null; }
        var rangeBytes = nonceAndRanges.Slice(nonceLen);

        // Unencrypted ranges (spec §Unencrypted ranges): `[offset uleb][size uleb]` pairs with no
        // count byte, covering bytes of the media frame that ride AUTHENTICATED but not encrypted
        // (video codec headers must stay plaintext for the packetizer/depacketizer — the real
        // client leaves the first 2 bytes of H26X fragments unencrypted). We previously rejected
        // any ranges, which silently dropped every peer video frame; now they are parsed, the
        // encrypted region is split around them, the unencrypted bytes become the GCM AAD, and the
        // frame is reassembled exactly as libdave's InboundFrameProcessor does.
        var ranges = new List<(int offset, int size)>();
        var rp = rangeBytes;
        while (!rp.IsEmpty)
        {
            if (!VoiceRtp.TryUleb128(rp, out uint off, out int offLen)) { Fail("bad range offset"); return null; }
            rp = rp.Slice(offLen);
            if (!VoiceRtp.TryUleb128(rp, out uint sz, out int szLen)) { Fail("bad range size"); return null; }
            rp = rp.Slice(szLen);
            // Sorted, non-overlapping, in-bounds — the same checks go-dave's validateRanges runs.
            if ((int)off < 0 || (int)sz < 0 || (int)off + (int)sz > suppStart) { Fail($"range {off}+{sz} out of frame"); return null; }
            if (ranges.Count > 0 && off < ranges[^1].offset + ranges[^1].size) { Fail("overlapping ranges"); return null; }
            ranges.Add(((int)off, (int)sz));
        }

        if (!_ssrcUser.TryGetValue(ssrc, out var uid)) { Fail($"no ssrc map for {ssrc}"); return null; }
        if (!TryGetRatchet(uid, out var ratchet)) { Fail($"no ratchet for uid {uid}"); return null; }
        uint generation = counter >> 24;
        byte[] key;
        try { key = ratchet.Get(generation); }
        catch { Fail($"ratchet gen {generation} expired"); return null; }

        var nonce = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(nonce.AsSpan(8), counter);

        // Split the frame into ciphertext and authenticated (unencrypted) bytes.
        if (ranges.Count == 0)
        {
            // Fast path: no ranges — the whole frame is ciphertext, empty AAD.
            var pt0 = VoiceRtp.GcmDecryptTrunc(key, nonce, frame[..suppStart], ReadOnlySpan<byte>.Empty, tag);
            if (pt0 == null) Fail($"gcm fail uid={uid} ssrc={ssrc} counter={counter} gen={generation}");
            return pt0;
        }
        using var ctStream = new MemoryStream();
        using var aadStream = new MemoryStream();
        int idx = 0;
        foreach (var (off, sz) in ranges)
        {
            ctStream.Write(frame.Slice(idx, off - idx));
            aadStream.Write(frame.Slice(off, sz));
            idx = off + sz;
        }
        ctStream.Write(frame.Slice(idx, suppStart - idx));
        var ciphertext = ctStream.ToArray();
        var aad = aadStream.ToArray();
        var pt = VoiceRtp.GcmDecryptTrunc(key, nonce, ciphertext, aad, tag);
        if (pt == null) { Fail($"gcm fail uid={uid} ssrc={ssrc} counter={counter} gen={generation} ranges={ranges.Count}"); return null; }

        // Reassemble: ciphertext regions come from the decrypted plaintext, unencrypted regions
        // stay as the authenticated original bytes.
        var outp = new byte[suppStart];
        int ci = 0;
        int oi = 0;
        foreach (var (off, sz) in ranges)
        {
            pt.AsSpan(ci, off - oi).CopyTo(outp.AsSpan(oi, off - oi));
            frame.Slice(off, sz).CopyTo(outp.AsSpan(off, sz));
            ci += off - oi;
            oi = off + sz;
        }
        pt.AsSpan(ci, suppStart - oi).CopyTo(outp.AsSpan(oi, suppStart - oi));
        return outp;
    }

    bool TryGetRatchet(ulong uid, out MlsCrypto.HashRatchet ratchet)
    {
        ratchet = null!;
        var cur = Volatile.Read(ref _recv);
        if (cur.TryGetValue(uid, out var r)) { ratchet = r.ratchet; return true; }
        var prev = Volatile.Read(ref _prevRecv);
        if (prev.TryGetValue(uid, out var p) &&
            Stopwatch.GetTimestamp() < Volatile.Read(ref _prevDeadlineField))
        {
            ratchet = p.ratchet;
            return true;
        }
        return false;
    }

    static bool IsSilence(ReadOnlySpan<byte> b) =>
        b.Length == 3 && b[0] == 0xF8 && b[1] == 0xFF && b[2] == 0xFE;

    static byte[] Le64(ulong v)
    {
        var b = new byte[8];
        for (int i = 0; i < 8; i++) b[i] = (byte)(v >> (8 * i));
        return b;
    }

    // The credential identity is the user's snowflake BIG-endian (spec: "identity
    // bytes are the big endian representation of the user's 64-bit Discord
    // snowflake"), while the sender-base-secret exporter context is little-endian.
    static byte[] Be64(ulong v)
    {
        var b = new byte[8];
        for (int i = 0; i < 8; i++) b[i] = (byte)(v >> (8 * (7 - i)));
        return b;
    }
}
