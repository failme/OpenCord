using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace OpenCord;

// The media plane: one UDP socket to the voice server carrying encrypted RTP packets. Owns the
// IP-discovery handshake (so select-protocol can advertise our external address), the 5-second
// keepalive, and the per-packet protect/unprotect via VoiceRtp. Audio encode/decode lives in
// VoiceAudio; this class only ships bytes.
//
// Created as soon as READY arrives (it knows the ssrc + server address + mode list); the transport
// key only lands with the session description, so SetSession() feeds it in before any audio flows.
sealed class UdpVoice : IDisposable
{
    // (senderSsrc, decrypted Opus frame). The ssrc is what attributes the audio to a user, which
    // is how the speaking ring is driven — the gateway's op-5 flag only fires on transitions and
    // never reliably reports "stopped".
    public event Action<uint, byte[]>? PacketReceived;
    public event Action<uint, byte[]>? VideoFrameReceived;  // (ssrc, decrypted full video frame)
    public event Action<int, string>? RawPacket;   // every raw UDP datagram (diagnostics)
    public event Action<int, string>? SentPacket;  // every sent UDP datagram (diagnostics)
    public event Action<string>? LogLine;          // voice-path diagnostics (mirrored into debug.log)
    // Fired on the first DAVE E2EE decrypt failures (with the reason) so the app log can
    // distinguish "no packets arriving" from "packets arriving but the ratchet is wrong".
    public event Action<string>? DaveFail;
    // E2EE frame hex in both directions (first few), so a live log shows the peer's real frame
    // layout next to ours — a byte-level interop check that the protocol is built on. Fired only
    // for the first few frames (counters inside this class) so the 20ms audio hot path never pays
    // for the hex conversion beyond the diagnostic window.
    public event Action<string>? E2eeFrameReceived;
    public event Action<string>? E2eeFrameSent;
    int _e2eeRxLog, _e2eeTxLog;

    // Optional DAVE layer: (senderSsrc, transport-decrypted frame) -> plaintext Opus,
    // or null to drop. Wired once the MLS session is armed; null keeps transport-only.
    public Func<uint, byte[], byte[]?>? DaveTransform;

    // The video stream's SSRC and RTP payload type. VideoSsrc is the one the voice gateway
    // ALLOCATED in READY (audio ssrc + 1) — the SFU relays only that; inventing our own SSRC
    // registered nothing. PayloadType follows the session's negotiated video codec (H264=107).
    public uint VideoSsrc;
    public byte VideoPayloadType = VideoRtp.PayloadType;

    // Our audio SSRC (from READY) — the RTCP sender identifier the SFU expects in PLI/RR packets.
    public uint AudioSsrc => _ssrc;

    // Diagnostic counters (read by VoiceProbe, harmless for the app).
    public long PacketsSent, PacketsReceived, TransportFailures, DecryptFailures;
    public string? LastSendError;      // first send failure (probe diagnostics)
    string? _daveLastFail;             // set by the DAVE transform on failure (see DaveFail)
    public void SetDaveLastFail(string why) => _daveLastFail = why;

    readonly VoiceGateway _gw;
    readonly uint _ssrc;
    readonly string _mode;
    readonly IPEndPoint _remote;
    readonly UdpClient _udp = new();
    byte[] _key = Array.Empty<byte>();
    CancellationTokenSource? _cts;
    Task? _recvTask, _keepAliveTask;
    ushort _sequence;
    uint _timestamp;
    uint _counter;               // transport nonce counter, appended to every packet

    // Video: own sequence/timestamp space on the same socket, one reassembler per sender ssrc.
    ushort _videoSequence;
    uint _videoTimestamp;
    readonly System.Collections.Concurrent.ConcurrentDictionary<uint, VideoRtp.Assembler> _byteAssemblers = new();
    readonly System.Collections.Concurrent.ConcurrentDictionary<uint, VideoRtp.H264Assembler> _h264Assemblers = new();
    H264Decoder? _h264Decoder;
    public string ExternalIp { get; private set; } = "";
    public ushort ExternalPort { get; private set; }
    public bool UseAes { get; private set; }

    // The peers' RTX ssrcs (from their op 12 video state). RTX packets are [OSN][original
    // transport ciphertext] retransmissions (plus all-zero SFU bandwidth probes on the same
    // ssrc) — never media in their own right. Feeding them to the H.264 assembler produced
    // garbage "frames" that failed DAVE decryption and logged as passthrough noise, so they
    // are dropped here the moment the ssrc is known.
    readonly System.Collections.Concurrent.ConcurrentDictionary<uint, byte> _rtxSsrcs = new();
    public void AddRtxSsrc(uint ssrc) => _rtxSsrcs.TryAdd(ssrc, 0);
    int _rtxLog;

    public UdpVoice(VoiceGateway gw, VoiceGateway.ReadyData ready)
    {
        _gw = gw;
        _ssrc = ready.Ssrc;
        // Start the RTP sequence/timestamp spaces at random offsets like the real client: the
        // SFU and peers treat 0/0 as an uninitialized stream, and two fresh connections with
        // identical first timestamps confuse jitter tracking. (Next() alone never sets the
        // high bit, so the timestamp is built from two draws for the full 32-bit range.)
        _sequence = (ushort)Random.Shared.Next(ushort.MaxValue + 1);
        _timestamp = RandU32();
        _videoSequence = (ushort)Random.Shared.Next(ushort.MaxValue + 1);
        _videoTimestamp = RandU32();

        // Always use aead_xchacha20_poly1305_rtpsize: its nonce/AAD layout is
        // confirmed byte-for-byte against captured real-client traffic (header
        // as AAD, 4-byte LITTLE-ENDIAN counter in a 24-byte nonce, LE counter
        // trailer), and the server is required to offer it. The aes256gcm
        // variant has no public reference implementation to pin against, so
        // preferring it risks a silently dropped call.
        _mode = "aead_xchacha20_poly1305_rtpsize";
        _remote = new IPEndPoint(IPAddress.Parse(ready.Ip), ready.Port);
    }

    // A full 32-bit random: Random.Shared.Next() alone never sets the high bit.
    static uint RandU32() => ((uint)Random.Shared.Next() << 1) | (uint)Random.Shared.Next(2);

    public string ChosenMode => _mode;

    // Called when the gateway confirms the mode with the transport key.
    public void SetSession(VoiceGateway.SessionData sess)
    {
        _key = sess.SecretKey;
        UseAes = string.Equals(_mode, "aead_aes256_gcm_rtpsize", StringComparison.Ordinal);
    }

    // Connect the socket, discover our external address, and start the receive + keepalive loops.
    // Returns the external (ip, port) pair the gateway needs for select-protocol.
    public async Task<(string ip, ushort port)> HandshakeAsync()
    {
        _cts = new CancellationTokenSource();
        // UdpClient.Connect (NOT socket.Client.Connect!): only the UdpClient-level
        // call sets the internal m_Active flag that the SYNC Send(byte[], int)
        // path requires. Client.Connect left m_Active false, so every SendAudio
        // threw "operation not allowed on non-connected sockets" — silently
        // swallowed by the catch in SendAudio — and the call was dead air.
        _udp.Connect(_remote);                  // connected socket: recv only accepts the server
        var (ip, port) = await DiscoverAsync();
        ExternalIp = ip;
        ExternalPort = port;
        _recvTask = ReceiveLoopAsync(_cts.Token);
        _keepAliveTask = KeepAliveLoopAsync(_cts.Token);
        return (ip, port);
    }

    // IP discovery: request = [type 0x0001][len 0x0046][ssrc]. Response: [type 0x0002][len 0x0046]
    // [ssrc][address 64-byte NUL-terminated][port 2-byte BE]. All numbers big-endian.
    async Task<(string ip, ushort port)> DiscoverAsync()
    {
        var req = new byte[74];
        BinaryPrimitives.WriteUInt16BigEndian(req.AsSpan(0, 2), 0x0001);
        BinaryPrimitives.WriteUInt16BigEndian(req.AsSpan(2, 2), 0x0046);
        BinaryPrimitives.WriteUInt32BigEndian(req.AsSpan(4, 4), _ssrc);
        await _udp.SendAsync(req, req.Length);

        using var timeout = new CancellationTokenSource(3000);
        try
        {
            var resp = await _udp.ReceiveAsync(timeout.Token);
            var buf = resp.Buffer;
            if (buf.Length < 74) return ("127.0.0.1", (ushort)_remote.Port);
            int addrEnd = Array.IndexOf<byte>(buf, 0, 8, 64);
            if (addrEnd < 0) addrEnd = 72;
            var ip = System.Text.Encoding.ASCII.GetString(buf, 8, addrEnd - 8);
            var port = BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(72, 2));
            return (ip, port);
        }
        catch (OperationCanceledException)
        {
            // Discovery blocked (strict NAT): Discord accepts the local address in this case.
            var local = _udp.Client.LocalEndPoint as IPEndPoint;
            return (local?.Address.ToString() ?? "127.0.0.1", (ushort)(local?.Port ?? _remote.Port));
        }
    }

    // ── send ────────────────────────────────────────────────────────────────────────────────────
    // One 20ms frame. The caller drives the cadence; this just packets it. The capture thread
    // (real frames) and the metronome (silence) can both land here, so the sequence/timestamp/
    // counter accounting is interlocked — a lost increment would duplicate an RTP sequence number
    // and the receiver would drop the packet.
    readonly object _sendLock = new();

    // Discord's 8-byte audio extension header, prepended to EVERY audio payload (silence,
    // plaintext, and DAVE-encrypted frames alike) inside the transport-encrypted RTP payload.
    // Layout decoded from captured real-client frames (bytes 1-3 = a 24-bit big-endian timestamp
    // in 1/256s ticks, advancing ~5120 per 20ms frame; byte 4 = 0x10 constant; byte 5 = audio
    // level 0xFF silence / ~0x90-0xD0 speech; byte 6 = 0x90 constant; byte 7 = 0x00 silence /
    // 0x02 audio):
    //
    //     silence = 32 38 C2 4A 10 FF 90 00     audio = 32 3C 5D 76 10 AD 90 02
    //
    // The receiver reads this to find where the DAVE frame starts, so a payload without it is
    // unparseable (that alone broke both directions: we never emitted it, and we never stripped
    // the peer's). The values are informational (timing/level for Discord's audio extension);
    // the structure is what must match.
    readonly Stopwatch _hdrClock = Stopwatch.StartNew();   // 24-bit media timestamp in 1/256s ticks
                                                            // (bytes 1-3 of the header), wall-clock based
                                                            // like the real client's

    internal static byte[] AudioExtensionHeader(bool silence, uint ticks)
    {
        var h = new byte[8];
        h[0] = 0x32;
        h[1] = (byte)(ticks >> 16);
        h[2] = (byte)(ticks >> 8);
        h[3] = (byte)ticks;
        h[4] = 0x10;
        h[5] = silence ? (byte)0xFF : (byte)0x98;   // RFC 6464-style level byte
        h[6] = 0x90;
        h[7] = silence ? (byte)0x00 : (byte)0x02;
        return h;
    }

    public void SendAudio(ReadOnlySpan<byte> opus)
    {
        // The frame handed in is already DAVE-transformed (or plaintext in the transitory phase);
        // snapshot its hex so a live log shows exactly what went on the wire. Full length so
        // offline analysis can decrypt our own frames with our logged keys (self-consistency).
        if (_e2eeTxLog++ < 3)
            E2eeFrameSent?.Invoke(Convert.ToHexString(opus[..Math.Min(opus.Length, 300)]));
        bool silence = opus.Length == 3 && opus[0] == 0xF8 && opus[1] == 0xFF && opus[2] == 0xFE;
        byte[] packet;
        lock (_sendLock)
        {
            byte[] payload = new byte[8 + opus.Length];
            AudioExtensionHeader(silence, (uint)(_hdrClock.ElapsedMilliseconds * 256)).CopyTo(payload, 0);
            opus.CopyTo(payload.AsSpan(8));
            packet = VoiceRtp.ProtectPacket(_key, UseAes, _sequence, _timestamp, _ssrc, payload, _counter);
            _sequence++;
            _timestamp += 960;                   // 48 kHz / 50 frames per second
            _counter++;
        }
        SentPacket?.Invoke(packet.Length, Convert.ToHexString(packet, 0, Math.Min(packet.Length, 300)));
        try { _udp.Send(packet, packet.Length); PacketsSent++; }
        catch (Exception e) { LastSendError ??= e.GetType().Name + ": " + e.Message; }
    }

    // Graceful stream end: five silence frames (the voice docs' interpolation rule).
    public void SendSilenceFrames()
    {
        for (int i = 0; i < 5; i++) SendAudio(VoiceRtp.SilenceFrame);
    }

    // One complete video frame (already DAVE-transformed, or plaintext JPEG in the transitory
    // phase), fragmented across RTP packets with the video payload type and the M bit on the last
    // fragment. Legacy length-prefix framing; the modern path uses SendVideoRtp per H.264 packet.
    public void SendVideoFrame(ReadOnlySpan<byte> frame)
    {
        var frags = VideoRtp.Fragment(frame);
        lock (_sendLock)
        {
            uint ts = _videoTimestamp;
            uint ssrc = VideoSsrc != 0 ? VideoSsrc : _ssrc;
            for (int i = 0; i < frags.Count; i++)
            {
                bool marker = i == frags.Count - 1;
                var packet = VoiceRtp.ProtectPacket(_key, UseAes, _videoSequence, ts, ssrc,
                                                    frags[i], _counter, VideoPayloadType, marker);
                _videoSequence++;
                _counter++;
                try { _udp.Send(packet, packet.Length); PacketsSent++; }
                catch (Exception e) { LastSendError ??= e.GetType().Name + ": " + e.Message; }
            }
            _videoTimestamp += 90000 / 15;       // 90 kHz video clock at 15 fps
        }
    }

    // One H.264 RTP packet (a single-NAL or FU-A payload, already DAVE-protected by the caller).
    // The M bit on the last packet of a frame ends the marker group AND advances the RTP timestamp
    // for the next frame, so fragments of one AU share a timestamp exactly as RFC 6184 requires.
    // Video packets carry the real client's RTP header extension (X bit + BE DE + words; the
    // extension DATA is inside the transport ciphertext, the 16-byte header is the AAD) — without
    // it the real client's video receive path has no extended header to authenticate against.
    public void SendVideoRtp(ReadOnlySpan<byte> payload, bool marker)
    {
        lock (_sendLock)
        {
            uint ts = _videoTimestamp;
            uint ssrc = VideoSsrc != 0 ? VideoSsrc : _ssrc;
            var packet = VoiceRtp.ProtectVideoPacket(_key, UseAes, _videoSequence, ts, ssrc,
                                                     VideoExtData(_videoSequence), payload, _counter,
                                                     VideoPayloadType, marker);
            _videoSequence++;
            _counter++;
            if (marker) _videoTimestamp += 90000 / 15;   // 90 kHz clock at 15 fps
            try { _udp.Send(packet, packet.Length); PacketsSent++; }
            catch (Exception e) { LastSendError ??= e.GetType().Name + ": " + e.Message; }
        }
    }

    // The video extension the real client sends: 3 words (12 bytes) of RFC 8285 one-byte
    // elements — transport sequence (id 5, 2 bytes), absolute send time (id 3, 3 bytes), and the
    // rid (id 11, "100") — padded to a word boundary. The element IDs are informational (the
    // receiver strips the whole block after decrypt); the 16-byte header + 12-byte data shape is
    // what must match the real client's wire format.
    static byte[] VideoExtData(ushort transportSeq)
    {
        var e = new byte[12];
        int o = 0;
        e[o++] = 0x51;                                   // id 5, 2-byte payload
        e[o++] = (byte)(transportSeq >> 8);
        e[o++] = (byte)transportSeq;
        long secs = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        uint v = (uint)(((secs % 64) * 262144.0) % 0x1000000) & 0xFFFFFF;   // RFC 5450 24-bit
        e[o++] = 0x32;                                   // id 3, 3-byte payload
        e[o++] = (byte)(v >> 16);
        e[o++] = (byte)(v >> 8);
        e[o++] = (byte)v;
        e[o++] = 0xB2;                                   // id 11, rid "100"
        e[o++] = (byte)'1'; e[o++] = (byte)'0'; e[o++] = (byte)'0';
        return e;                                        // 11 used, byte 11 stays 0 (pad)
    }

    // RTCP (video pull signaling). The SFU is pull-based for video: it only starts forwarding a
    // subscribed stream after the subscriber sends a PLI (keyframe request, PT 206) and receiver
    // reports (PT 201). Real Discord clients send both continuously. RTCP is transport-encrypted
    // exactly like RTP in the RTP-size AEAD modes (8-byte header as AAD, shared nonce counter,
    // 4-byte LE trailer) — raw RTCP is silently dropped by the SFU, which used to leave the
    // peer's camera black even after a correct op 15 subscribe.
    public void SendRtcp(ReadOnlySpan<byte> rtcp)
    {
        lock (_sendLock)
        {
            var packet = VoiceRtp.ProtectRtcp(_key, UseAes, rtcp, _counter);
            _counter++;
            try { _udp.Send(packet, packet.Length); PacketsSent++; }
            catch (Exception e) { LastSendError ??= e.GetType().Name + ": " + e.Message; }
        }
    }

    // Fired when a subscriber requests a keyframe for our video stream (RTCP PLI, PT 206 FMT 1
    // whose media SSRC is our video ssrc). The encoder must re-emit an IDR or the requester's
    // decoder stays black until the next periodic keyframe.
    public event Action? KeyframeRequested;

    // ── receive ─────────────────────────────────────────────────────────────────────────────────
    async Task ReceiveLoopAsync(CancellationToken tok)
    {
        try
        {
            while (!tok.IsCancellationRequested)
            {
                // One malformed datagram must not kill the loop (audio would die
                // silently for the whole call): isolate every packet. The cancellation
                // exception still unwinds so Dispose() can stop the loop.
                UdpReceiveResult resp;
                try
                {
                    resp = await _udp.ReceiveAsync(tok);
                }
                catch (OperationCanceledException) { throw; }
                catch { continue; }

                try
                {
                    RawPacket?.Invoke(resp.Buffer.Length, Convert.ToHexString(resp.Buffer, 0, Math.Min(resp.Buffer.Length, 300)));
                    if (resp.Buffer.Length < 12) continue;  // keepalive echo / discovery leftovers
                    if (_key.Length == 0) continue;         // no transport key yet
                    // RTCP (peer sender/receiver reports AND PLI/REMB feedback) rides the same
                    // socket and is AEAD-encrypted with the transport key exactly like RTP — the
                    // 8-byte header is cleartext AAD, the body encrypted. Decrypt it so PLI
                    // (keyframe requests for our video) is actionable; reports stay informational.
                    if (VoiceRtp.IsRtcp(resp.Buffer))
                    {
                        HandleRtcp(resp.Buffer);
                        continue;
                    }
                    // The RTP header is cleartext (it is the AEAD AAD), so the payload type is
                    // readable before decryption — that is how video packets are routed.
                    bool isVideo = VoiceRtp.DecodeAnyHeader(resp.Buffer, out _, out uint ts,
                                                            out uint pktSsrc, out byte pt, out bool marker)
                                    && VideoRtp.IsVideoPt(pt);
                    // Diagnostic: the first few video-classified datagrams, raw, so a live log
                    // shows the peer's ACTUAL video RTP layout (header length, X bit, extension
                    // bytes) before any decryption touches it.
                    if (isVideo && _videoRxLog++ < 6)
                        Log($"video rx pt={pt} ssrc={pktSsrc} len={resp.Buffer.Length} " +
                            $"head={Convert.ToHexString(resp.Buffer, 0, Math.Min(resp.Buffer.Length, 20))}");
                    PacketsReceived++;
                    // RTX retransmissions / SFU padding arrive on the peer's announced rtx ssrc
                    // (their payload is [OSN][original ciphertext] or zeros — not media). Drop
                    // them before decryption so the H.264 assembler never sees them; the primary
                    // stream (pt=107) carries the actual video.
                    if (_rtxSsrcs.ContainsKey(pktSsrc))
                    {
                        if (_rtxLog++ < 4)
                            Log($"rtx dropped ssrc={pktSsrc} pt={pt} len={resp.Buffer.Length}");
                        continue;
                    }
                    // Video packets may use a different header length than the audio ones; the
                    // video-aware decrypt tries every plausible AAD length (16 / 16+ext / 12) and
                    // reports how many extension bytes ride at the front of the decrypted payload.
                    int vStrip = 0;
                    byte[]? body = isVideo
                        ? VoiceRtp.UnprotectVideoPacket(_key, UseAes, resp.Buffer, out _, out vStrip)
                        : VoiceRtp.UnprotectPacket(_key, UseAes, resp.Buffer);
                    if (body == null)
                    {
                        TransportFailures++;
                        if (_tfLog++ < 6)
                            Log($"transport decrypt fail len={resp.Buffer.Length} " +
                                $"head={Convert.ToHexString(resp.Buffer, 0, Math.Min(resp.Buffer.Length, 16))}");
                        continue;   // wrong key or tampered: drop
                    }
                    // RTP padding (RFC 3550): when the P bit is set, the payload's LAST byte is the
                    // padding count (including itself) and that many trailing bytes are padding. The
                    // real client sets P on its video packets (the DAVE-protected fragments are
                    // padded inside the transport ciphertext), so the padding MUST be stripped
                    // before the DAVE layer — otherwise the frame's FA FA footer is hidden under
                    // padding bytes and the audio/video path can't find it.
                    if ((resp.Buffer[0] & 0x20) != 0 && body.Length >= 2)
                    {
                        int pad = body[^1];
                        if (pad > 0 && pad < body.Length) body = body[..^pad];
                    }
                    // RTP header-extension DATA (RFC 8285): the real client encrypts the extension
                    // bytes inside the payload (the 16-byte header is the AAD), so after transport
                    // decryption the payload starts with `vStrip` extension bytes. They MUST be
                    // stripped before the H.264 assembler: the extension's first byte decodes as a
                    // valid NAL type (transport-seq 0x50 -> 16, abs-send-time 0x32 -> 18), so an
                    // unstripped extension was swallowed as a fake NAL and every DAVE frame came
                    // out corrupted (black tile + per-frame GCM failures).
                    if (isVideo && vStrip > 0)
                    {
                        if (vStrip >= body.Length) continue;
                        body = body[vStrip..];
                        // Diagnostic: the first few stripped fragments, so a live log shows the
                        // REAL client's video payload shape (extension length + first NAL bytes).
                        if (_vBodyLog++ < 4)
                            Log($"video body pt={pt} ssrc={pktSsrc} ext={vStrip} " +
                                $"head={Convert.ToHexString(body, 0, Math.Min(body.Length, 20))}");
                    }
                    // The real Discord client (and now us) prepends an 8-byte audio extension
                    // header to every audio payload. Log the pre-strip body so a live log shows
                    // the exact header + frame layout, then strip it before the DAVE layer —
                    // leaving it attached makes the DAVE ciphertext boundary swallow the header
                    // and every frame fails to decrypt (the header is random-looking in the
                    // ciphertext sense, so the 0x32/0x90 signature is reliable; a false match
                    // on another client's frame is ~1/65536 and only loses one frame).
                    if (DaveTransform != null && _e2eeRxLog++ < 3)
                        E2eeFrameReceived?.Invoke(pktSsrc + " " + Convert.ToHexString(body[..Math.Min(body.Length, 300)]));
                    if (!isVideo && body.Length >= 8 && body[0] == 0x32 && body[6] == 0x90)
                        body = body[8..];
                    if (isVideo)
                    {
                        // The real client DAVE-protects the WHOLE encoded frame, then packetizes;
                        // its decryptor therefore runs AFTER RTP depacketization (reassembly). Our
                        // H.264 branch decrypts the reassembled frame in HandleVideoFragment. The
                        // legacy whole-JPEG fallback is the exception: it DAVE-protects each
                        // fragment individually, so those fragments are decrypted here, before the
                        // byte assembler.
                        bool legacy = VideoRtp.HasLegacyMagic(body)
                                      || (_byteAssemblers.TryGetValue(pktSsrc, out var bA) && bA.Pending);
                        byte[]? dec = body;
                        if (legacy && DaveTransform != null)
                        {
                            dec = DaveTransform(pktSsrc, body);
                            if (dec == null)
                            {
                                DecryptFailures++;
                                DaveFail?.Invoke(_daveLastFail ?? "unknown (video)");
                                continue;
                            }
                        }
                        HandleVideoFragment(pktSsrc, pt, ts, marker, dec);
                        continue;
                    }
                    if (DaveTransform != null)
                    {
                        var dec = DaveTransform(pktSsrc, body);
                        if (dec == null)
                        {
                            DecryptFailures++;
                            DaveFail?.Invoke(_daveLastFail ?? "unknown");
                            continue;
                        }
                        body = dec;
                    }
                    PacketReceived?.Invoke(pktSsrc, body);
                }
                catch { continue; }                         // bad frame: skip, keep listening
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { /* socket disposed */ }
    }

    // Decrypt an inbound RTCP datagram and react to what matters: a PLI for our video ssrc means
    // a subscriber needs a keyframe now. SR/RR/APP/REMB are informational.
    void HandleRtcp(ReadOnlySpan<byte> packet)
    {
        if (_key.Length == 0) return;
        var body = VoiceRtp.UnprotectRtcp(_key, UseAes, packet);
        if (body == null)
        {
            // The SFU's own RTCP is encrypted with the session key too; a failure here means
            // the key changed or the packet is from a stale session — never media, so it must
            // not inflate the media transport-failure counter.
            if (_rtcpLog++ < 3) Log($"rtcp decrypt fail len={packet.Length}");
            return;
        }
        // Payload-specific feedback (PT 206) with FMT=1 = PLI; body = media SSRC (the stream the
        // requester wants a keyframe for).
        if (packet[1] == 206 && (packet[0] & 0x1F) == 1 && body.Length >= 4
            && BinaryPrimitives.ReadUInt32BigEndian(body) == VideoSsrc)
            KeyframeRequested?.Invoke();
    }

    // Feeds DAVE-decrypted per-packet payloads into the per-sender reassembler; a complete video
    // frame (H.264 AU or legacy JPEG blob) is decoded and surfaced. The routing is content-aware:
    // legacy byte framing starts every frame with the "JPEG" magic (or is mid-frame with a Pending
    // byte assembler), so it goes to the byte assembler even when it rides the session's H.264
    // payload type — feeding JPEG bytes into the H.264 assembler used to keep the remote tile black.
    void Log(string line) => LogLine?.Invoke(line);
    int _videoRxLog, _tfLog, _decLog, _rtcpLog, _vBodyLog, _videoAuLog, _videoOkLog, _videoDecLog;   // first-few diagnostics (receive loop)

    void HandleVideoFragment(uint ssrc, byte pt, uint ts, bool marker, byte[] payload)
    {
        bool legacy = VideoRtp.HasLegacyMagic(payload)
                      || (_byteAssemblers.TryGetValue(ssrc, out var existing) && existing.Pending);
        if (!legacy && VideoRtp.IsH264Pt(pt))
        {
            var asm = _h264Assemblers.GetOrAdd(ssrc, _ => new VideoRtp.H264Assembler());
            var au = asm.Feed(payload, marker);
            if (au == null) return;
            _h264Assemblers.TryRemove(ssrc, out _);
            if (_videoAuLog++ < 4)
                Log($"video au ssrc={ssrc} len={au.Length} " +
                    $"head={Convert.ToHexString(au, 0, Math.Min(au.Length, 16))}");
            // DAVE decrypts the WHOLE reassembled frame (the real client protects the encoded
            // frame, not individual RTP packets — per-packet decrypt fails against it). The
            // decryptor is codec-unaware, so UnprotectFrame handles the H.264 supplement fine.
            if (DaveTransform != null)
            {
                au = DaveTransform(ssrc, au);
                if (au == null)
                {
                    DecryptFailures++;
                    DaveFail?.Invoke(_daveLastFail ?? "unknown (video frame)");
                    return;
                }
                if (_videoOkLog++ < 4)
                    Log($"video dave ok ssrc={ssrc} len={au.Length} " +
                        $"head={Convert.ToHexString(au, 0, Math.Min(au.Length, 16))}");
            }
            _h264Decoder ??= new H264Decoder();
            if (!_h264Decoder.Ready)
            {
                // A silent decoder failure reads as "camera on, nobody sees it" — name it.
                if (_decLog++ < 3)
                    Log("H264 decoder unavailable: " + (_h264Decoder.Error ?? "no error"));
                return;
            }
            int decoded = 0, shown = 0;
            foreach (var nv12 in _h264Decoder.Decode(au))
            {
                decoded++;
                int w = _h264Decoder.Width, h = _h264Decoder.Height;
                if (w <= 0 || h <= 0 || nv12.Length < w * h * 3 / 2) continue;
                var jpeg = Nv12.ToJpeg(nv12, w, h, 60);
                if (jpeg == null) continue;
                shown++;
                VideoFrameReceived?.Invoke(ssrc, jpeg);
            }
            // "Decrypted fine and then vanished" is the failure this path had, and it left no
            // trace at all: name what the decoder actually produced for the first few AUs.
            if (_videoDecLog++ < 6 || (decoded == 0 && _videoDecLog < 40 && _videoDecLog % 10 == 0))
                Log($"video decode ssrc={ssrc} au={au.Length}B -> {decoded} frame(s), {shown} rendered " +
                    $"({_h264Decoder.Width}x{_h264Decoder.Height}) err={_h264Decoder.LastDrainError ?? "-"}");
            return;
        }
        var byteAsm = _byteAssemblers.GetOrAdd(ssrc, _ => new VideoRtp.Assembler());
        var frame = byteAsm.Feed(payload, marker);
        if (frame == null) return;
        _byteAssemblers.TryRemove(ssrc, out _);
        VideoFrameReceived?.Invoke(ssrc, frame);
    }

    async Task KeepAliveLoopAsync(CancellationToken tok)
    {
        try
        {
            while (!tok.IsCancellationRequested)
            {
                await Task.Delay(5000, tok);
                try { await _udp.SendAsync(Array.Empty<byte>(), 0); } catch { }
            }
        }
        catch (OperationCanceledException) { }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _udp.Dispose();
    }
}
