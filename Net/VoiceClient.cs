using System.Diagnostics;

namespace ClaudeScord;

// Owns one live voice connection end to end: gateway credentials -> UDP handshake -> audio loops.
// Discord expects a continuous 50-frame/s stream, so a 20ms metronome keeps the UDP socket fed
// with silence frames between real speech packets (the docs require five silence frames on the
// way out; keeping the stream hot also keeps the SFU happy).
sealed class VoiceClient
{
    public static VoiceClient? Current { get; private set; }

    public event Action? Connected;                    // audio flowing
    public event Action? Disconnected;
    public event Action<string>? LogLine;
    public event Action<ulong, byte[]>? VideoFrame;    // (userId, decrypted JPEG frame) from a peer
    public event Action<byte[]>? SelfVideoFrame;       // our own capture, for the self preview tile

    VoiceGateway? _gw;
    UdpVoice? _udp;
    VoiceAudio? _audio;
    DaveMls? _dave;
    CancellationTokenSource? _cts;
    Task? _metronome;
    long _lastRealFrameMs;
    volatile bool _muted;
    volatile bool _hangingUp;

    // Video plane: the server-allocated video ssrc (READY streams[0]) we broadcast on, the session's
    // negotiated codec, and the camera pipeline. Screen sharing is NOT here — Discord runs it as a
    // separate Go Live connection (see StreamClient); this connection only ever carries the camera.
    CameraCapture? _camera;
    volatile H264Encoder? _h264;   // probed on the gateway thread in OnReady, read by UI
    volatile bool _cameraOn;
    bool _h264Enabled;
    uint _videoSsrc, _videoRtx;
    byte _videoPt = VideoRtp.PayloadType;
    CancellationTokenSource? _videoRtcp;
    volatile uint _subscribedSsrc;
    int _videoDiagLeft = 6;   // first-N-frame diagnostics for the live video send path

    public ulong ChannelId { get; private set; }
    public bool IsConnected { get; private set; }
    public bool Muted => _muted;
    public bool VideoOn => _cameraOn;

    // The camera's encoded size. Broadcast in op 12's max_resolution and used to size the encoder.
    public const int CamW = 640, CamH = 360;

    ulong _userId;

    VoiceClient(ulong userId, ulong channelId)
    {
        _userId = userId;
        ChannelId = channelId;
    }

    // Mirror every protocol line into Log.Write (debug.log with --log) in addition to the UI
    // event: the DAVE/MLS exchange only reveals itself in logs, and without this the app's
    // voice trace was unwired (nothing subscribed to LogLine).
    void Log(string line)
    {
        LogLine?.Invoke(line);
        ClaudeScord.Log.Write("voice", line);
    }

    // Periodic transport health dump so a live failure leaves a trace: every ~5s, when anything
    // changed since the last dump (packets counted, or a failure appeared), log how many packets
    // went out, came in, failed transport decrypt, and failed E2EE decrypt (with the first DAVE
    // failure reason). Silent when the call is healthy AND steady; decisive in broken ones.
    System.Threading.Timer? _statsTimer;
    long _lastStatsTx = -1, _lastStatsRx = -1, _lastStatsTf = -1, _lastStatsEf = -1;
    void StartStatsTimer()
    {
        _statsTimer?.Dispose();
        _statsTimer = new System.Threading.Timer(_ =>
        {
            try
            {
                var u = _udp;
                if (u == null) return;
                var d = _dave;
                bool changed = u.PacketsSent != _lastStatsTx || u.PacketsReceived != _lastStatsRx
                            || u.TransportFailures != _lastStatsTf || u.DecryptFailures != _lastStatsEf;
                if (!changed && u.DecryptFailures == 0 && u.TransportFailures == 0) return;
                _lastStatsTx = u.PacketsSent; _lastStatsRx = u.PacketsReceived;
                _lastStatsTf = u.TransportFailures; _lastStatsEf = u.DecryptFailures;
                Log($"stats: tx={u.PacketsSent} rx={u.PacketsReceived} transportFail={u.TransportFailures} " +
                    $"e2eeFail={u.DecryptFailures} daveReady={d?.Ready ?? false} " +
                    $"lastE2eeFail={d?.LastFailReason ?? "-"} sendErr={u.LastSendError ?? "-"}");
            }
            catch { /* a logging hiccup must never kill the call */ }
        }, null, 5000, 5000);
    }
    // ── start ───────────────────────────────────────────────────────────────────────────────────
    public static async Task ConnectAsync(VoiceServerInfo info)
    {
        await HangUpAsync();                          // one call at a time
        var vc = new VoiceClient(info.UserId, info.ChannelId);
        Current = vc;
        await vc.RunAsync(info);
    }

    async Task RunAsync(VoiceServerInfo info)
    {
        _cts = new CancellationTokenSource();
        try
        {
            _gw = new VoiceGateway(info);
            _gw.LogLine += Log;
            _gw.Ready += OnReady;
            _gw.SessionDescription += OnSession;
            _gw.DaveMessage += (op, payload) => _dave?.HandleDave(op, payload);
            _gw.DaveJson += (op, d) => _dave?.HandleDaveJson(op, d);
            _gw.Speaking += (uid, ssrc, _) => _dave?.OnSpeaking(uid, ssrc);
            _gw.ClientConnect += (uid, ssrc) => _dave?.OnClientConnect(uid, ssrc);
            // A member's camera came on (or off): subscribe to their video so the SFU forwards it.
            _gw.ClientVideo += OnPeerVideoState;
            _gw.Closed += () => { if (!_hangingUp) Log("voice gateway closed"); };

            Log("connecting to voice gateway");
            await _gw.ConnectAsync();
        }
        catch (Exception e)
        {
            Log("voice connect failed: " + e.Message);
            await HangUpAsync();
        }
    }

    async void OnReady(VoiceGateway.ReadyData ready)
    {
        try
        {
            if (_gw == null) return;
            _udp = new UdpVoice(_gw, ready);
            _udp.LogLine += Log;
            _udp.PacketReceived += OnPacket;
            _udp.VideoFrameReceived += OnVideoFrame;
            StartSpeakingTick();
            // First 20 raw datagrams in each direction, so a stuck call shows whether the SFU is
            // even relaying media to us (rx side) or rejecting ours (tx errors surface in stats).
            int rxN = 0, txN = 0, failN = 0, e2eeRx = 0, e2eeTx = 0;
            _udp.RawPacket += (len, hex) => { if (rxN++ < 20) Log($"udp rx {len}B {hex}"); };
            _udp.SentPacket += (len, hex) => { if (txN++ < 10) Log($"udp tx {len}B {hex}"); };
            // DAVE decrypt failures: log the reason the FIRST time it happens so a broken ratchet
            // or ssrc map is visible in the log without spamming every 20ms frame.
            _udp.DaveFail += why => { if (failN++ < 5) Log($"dave decrypt fail: {why}"); };
            // First E2EE frames in BOTH directions: a byte-level look at real Discord's frame vs
            // ours. If the layout matches the spec, the ratchet/keys are the only thing left to
            // differ; if it doesn't, the frame format itself is wrong. Compare hex side by side.
            _udp.E2eeFrameReceived += hex => { if (e2eeRx++ < 3) Log($"e2ee frame rx {hex}"); };
            _udp.E2eeFrameSent += hex => { if (e2eeTx++ < 3) Log($"e2ee frame tx {hex}"); };
            // READY allocates the video stream (streams[0]: ssrc = audio + 1). Remember them so
            // SetVideoEnabled can broadcast on the exact ssrcs the SFU allocated.
            _videoSsrc = ready.VideoSsrc != 0 ? ready.VideoSsrc : ready.Ssrc + 1;
            _videoRtx = ready.RtxSsrc != 0 ? ready.RtxSsrc : _videoSsrc + 1;
            if (_videoSsrc != 0) _udp.VideoSsrc = _videoSsrc;

            var (ip, port) = await _udp.HandshakeAsync();
            Log($"ip discovery -> {ip}:{port} mode={_udp.ChosenMode}");
            // Probe the H.264 encoder so the UI knows whether camera/screenshare can use real
            // H.264 or must degrade to the legacy JPEG transport. The select-protocol codec list
            // ALWAYS advertises H264: a real Discord peer broadcasts H264, and our H.264 decoder
            // renders it (the encoder is only needed to PRODUCE video). Advertising VP8 instead
            // made the session negotiate VP8 — which we have no decoder for — so the peer's
            // camera never rendered.
            try
            {
                _h264 = new H264Encoder(CamW, CamH, 15, 900_000);
                if (!_h264.Ready) Log("H264 encoder unavailable: " + _h264.Error);
            }
            catch (Exception e) { _h264 = null; Log("H264 encoder failed: " + e.Message); }
            await _gw.SendSelectProtocolAsync(ip, port, _udp.ChosenMode);
        }
        catch (Exception e) { Log("udp handshake: " + e.Message); }
    }

    void OnSession(VoiceGateway.SessionData sess)
    {
        try
        {
            if (_udp == null) { Log("session description before ready"); return; }
            _udp.SetSession(sess);

            // DAVE is mandatory for E2EE-eligible calls: if the gateway negotiated a
            // non-zero protocol version we run the MLS group exchange and encrypt the
            // media frames on top of the transport layer.
            if (sess.DaveProtocolVersion != 0 && _dave == null)
            {
                _dave = new DaveMls(info_UserId(), info_ChannelId());
                _dave.LogLine += Log;
                _dave.SendBinary = pkt => { _ = _gw.SendDaveBinaryAsync(pkt); };
                _dave.SendJson = (op, d) => { _ = _gw.SendDaveJsonAsync(op, d); };
                _dave.OnSessionDescription(sess.DaveProtocolVersion);
                // Seed the ssrc->uid map with everyone the gateway has seen speak
                // so far (a pre-existing speaker announces before our session
                // description, when the DAVE layer did not exist yet).
                foreach (var (ssrc, uid) in _gw.SsrcUser) _dave.OnSpeaking(uid, ssrc);
                _udp.DaveTransform = (ssrc, frame) =>
                {
                    var dec = _dave?.UnprotectFrame(ssrc, frame);
                    if (dec == null) _udp.SetDaveLastFail(_dave?.LastFailReason ?? "unknown");
                    return dec;
                };
                // A subscriber (the real client or another ClaudeScord) requested a keyframe:
                // rebuild the encoder so the next frame is a fresh IDR. Without this the peer's
                // decoder stays black until the encoder's own next keyframe. The encoder reset
                // takes a moment, so it must NOT run on the UDP receive thread (that would stall
                // audio); the encoder's own guard collapses repeated PLIs into one reset.
                _udp.KeyframeRequested += () =>
                {
                    if (_h264 != null)
                    {
                        // The encoder's own guard collapses PLI floods into at most one reset per
                        // 3s (a faster cadence guarantees the rebuilt encoder never emits its
                        // fresh IDR), so only log when a reset is actually scheduled.
                        _ = Task.Run(() =>
                        {
                            try
                            {
                                if (_h264.RequestKeyframe())
                                    Log("PLI received — H264 encoder reset (fresh keyframe on next frames)");
                            }
                            catch { }
                        });
                    }
                };
                Log("DAVE MLS session armed");
            }

            // AUDIO FIRST. The H.264 encoder MFT can be slow to initialise (its self-test encodes
            // probe frames; some builds never emit and the check takes a while), so it must NEVER
            // sit in front of audio capture — a slow encoder would otherwise delay the session
            // setup and the call would connect with dead air on both ends.
            _audio = new VoiceAudio();
            _audio.FrameReady += OnFrame;
            ApplyVoicePrefs();
            // Our own tile lights from the gate, not from the raw mic — it should agree with what
            // the other side actually hears.
            _audio.TransmitChanged += on =>
            {
                if (on) MarkSpeaking(_userId);
                else if (_speaking.Remove(_userId)) SpeakingChanged?.Invoke(_userId, false);
            };
            bool cap = _audio.StartCapture();
            bool play = _audio.StartPlayback();
            Log($"transport {sess.Mode} capture={(cap ? "on" : "off")} playback={(play ? "on" : "off")}");
            Log($"transport key={Convert.ToHexString(sess.SecretKey)}");

            // The negotiated video codec picks the RTP payload type. We always advertise H264
            // (the only codec we can decode — a real Discord peer broadcasts H264), so the
            // session lands on H264/PT 107. Our own legacy JPEG fallback rides the same PT with
            // a magic marker; the receiver sniffs it before routing, so both directions work.
            _videoPt = sess.VideoCodec switch { "H264" => 107, "VP9" => 98, _ => 101 };
            if (_udp != null) _udp.VideoPayloadType = _videoPt;
            if (_h264?.Ready == true) Log("H264 encoder armed");
            else Log("H264 encoder unavailable — JPEG fallback on PT " + _videoPt);

            // A non-zero speaking flag is mandatory before sending audio or the gateway drops us.
            _ = _gw.SendSpeakingAsync(1);
            IsConnected = true;
            Connected?.Invoke();
            StartStatsTimer();

            if (_cts != null)
                _metronome = Task.Run(() => MetronomeLoopAsync(_cts.Token));
        }
        catch (Exception e) { Log("session setup: " + e.Message); }
    }

    void OnFrame(byte[] opus)
    {
        Interlocked.Exchange(ref _lastRealFrameMs, Stopwatch.GetTimestamp());
        // Our own ring, on the same rule as everyone else's — except that a muted mic never lights
        // it, however loud the room is.
        if (!_muted && !IsSilence(opus)) MarkSpeaking(_userId);
        // E2EE frames are produced once the MLS transition executes; until then the
        // plaintext Opus goes out transport-encrypted (the initial transitory phase).
        var frame = _dave?.ProtectFrame(opus) ?? opus;
        _udp?.SendAudio(frame);
    }

    int _realRxLog;

    void OnPacket(uint ssrc, byte[] opus)
    {
        // Diagnostic: confirm the peer's REAL speech (not SFU silence) reaches the decoder — a
        // call that "can't hear each other" either never gets real frames here or never sends
        // them; this line tells which in one test.
        if (_realRxLog++ < 3 && !IsSilence(opus))
            Log($"peer audio rx {opus.Length}B {Convert.ToHexString(opus, 0, Math.Min(opus.Length, 24))}");
        ulong from = _gw?.SsrcUser.GetValueOrDefault(ssrc) ?? 0;
        if (!IsSilence(opus) && from != 0) MarkSpeaking(from);
        // Per-user volume, the way right-clicking someone in a call sets it.
        _audio?.PlayFrame(opus, from == 0 ? 1f : Prefs.UserVolume(from));
    }

    // ── speaking ────────────────────────────────────────────────────────────────────────────────
    // Discord's green ring follows *audio*, not the gateway's op-5 flag: op 5 fires when someone
    // starts and is not reliably sent when they stop, so a flag-driven ring sticks on forever. The
    // SFU relays a 3-byte Opus silence frame while a user is quiet, so "a real frame arrived
    // recently" is the same signal the real client uses.
    public event Action<ulong, bool>? SpeakingChanged;

    readonly Dictionary<ulong, long> _lastVoice = new();
    readonly HashSet<ulong> _speaking = new();
    System.Threading.Timer? _speakTick;

    /// Opus silence: the 3-byte F8 FF FE frame, plus anything too small to be speech.
    static bool IsSilence(byte[] f) =>
        f.Length <= 3 || (f.Length == 3 && f[0] == 0xF8 && f[1] == 0xFF && f[2] == 0xFE);

    void MarkSpeaking(ulong uid)
    {
        lock (_lastVoice) _lastVoice[uid] = Stopwatch.GetTimestamp();
        if (_speaking.Add(uid)) SpeakingChanged?.Invoke(uid, true);
    }

    // Discord holds the ring for a beat after the last packet so normal pauses between words don't
    // make it flicker.
    const int SpeakHangoverMs = 250;

    void StartSpeakingTick()
    {
        _speakTick ??= new System.Threading.Timer(_ =>
        {
            long now = Stopwatch.GetTimestamp();
            List<ulong>? stopped = null;
            lock (_lastVoice)
            {
                foreach (var uid in _speaking)
                {
                    long last = _lastVoice.GetValueOrDefault(uid);
                    if ((now - last) * 1000.0 / Stopwatch.Frequency > SpeakHangoverMs)
                        (stopped ??= new()).Add(uid);
                }
            }
            if (stopped == null) return;
            foreach (var uid in stopped) { _speaking.Remove(uid); SpeakingChanged?.Invoke(uid, false); }
        }, null, 100, 100);
    }

    void OnVideoFrame(uint ssrc, byte[] jpeg)
    {
        // Map the ssrc back to a user via the gateway's ssrc->uid map (populated by speaking /
        // clients_connect / video-state events) and surface the frame for the tile UI.
        ulong uid = _gw?.SsrcUser.GetValueOrDefault(ssrc) ?? 0;
        VideoFrame?.Invoke(uid, jpeg);
    }

    void OnPeerVideoState(ulong uid, uint audioSsrc, uint videoSsrc, uint rtxSsrc, bool active)
    {
        if (uid == _userId || videoSsrc == 0) return;
        // Register the peer's video ssrc (and audio) with the DAVE layer: its ssrc->uid map is
        // fed only by speaking / clients_connect events, which carry the AUDIO ssrc — without
        // this the peer's video packets fail with "no ssrc map for <video ssrc>" and their
        // camera never renders (the gateway's own map already knows video_ssrc; DAVE's does not).
        if (audioSsrc != 0) _dave?.OnSpeaking(uid, audioSsrc);
        _dave?.OnSpeaking(uid, videoSsrc);
        // The peer's RTX stream (pt=96, retransmissions + SFU padding) is NOT media: its payload
        // is [OSN][original ciphertext] (or zeros), and feeding it to the H.264 assembler only
        // corrupts frames. Tell the UDP layer to drop it so the primary stream assembles cleanly.
        if (rtxSsrc != 0) _udp?.AddRtxSsrc(rtxSsrc);
        if (!active)
        {
            // Camera off: stop requesting the stream and clear the tile.
            if (_subscribedSsrc == videoSsrc)
            {
                _subscribedSsrc = 0;
                _videoRtcp?.Cancel();
                _videoRtcp = null;
            }
            VideoFrame?.Invoke(uid, Array.Empty<byte>());   // null-equivalent: clear the tile
            return;
        }
        // Subscribe (op 15 MEDIA_SINK_WANTS) and start the RTCP keyframe/report cadence. The send
        // is guarded by _subscribedSsrc so a camera toggle re-announce doesn't stack subscriptions.
        var gw = _gw;
        if (gw == null) return;
        if (_subscribedSsrc == videoSsrc) return;
        _subscribedSsrc = videoSsrc;
        _ = Task.Run(async () =>
        {
            try
            {
                await gw.SendMediaSinkWantsAsync(videoSsrc);
                Log($"subscribed to video ssrc {videoSsrc}");
            }
            catch (Exception e) { Log("video subscribe: " + e.Message); }
        });
        StartVideoRtcp(videoSsrc);
    }

    // The caller (OnPeerVideoState) owns the _subscribedSsrc dedup guard and has already set it;
    // this only (re)starts the RTCP cadence for that ssrc.
    void StartVideoRtcp(uint videoSsrc)
    {
        _videoRtcp?.Cancel();
        _videoRtcp = new CancellationTokenSource();
        var tok = _videoRtcp.Token;
        _ = Task.Run(async () =>
        {
            uint extSeq = 0;
            try
            {
                while (!tok.IsCancellationRequested)
                {
                    extSeq += 1000;   // pretend steady reception so the SFU sees a live subscriber
                    var u = _udp;
                    if (u != null)
                    {
                        // Both RTCP packets identify us by our AUDIO ssrc — the same sender id the
                        // probe used when video was verified end-to-end.
                        uint mine = u.AudioSsrc;
                        u.SendRtcp(VideoRtp.BuildPli(mine, videoSsrc));
                        u.SendRtcp(VideoRtp.BuildReceiverReport(mine, videoSsrc, extSeq));
                    }
                    await Task.Delay(1500, tok);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { Log("video rtcp: " + e.Message); }
        });
    }

    async Task MetronomeLoopAsync(CancellationToken tok)
    {
        var sw = Stopwatch.StartNew();
        long next = 0;
        try
        {
            while (!tok.IsCancellationRequested)
            {
                next += 20;
                long delay = next - sw.ElapsedMilliseconds;
                if (delay > 0) await Task.Delay((int)delay, tok);

                long now = Stopwatch.GetTimestamp();
                long last = Interlocked.Read(ref _lastRealFrameMs);
                bool sentReal = last != 0 && now - last < Stopwatch.Frequency / 16;   // within 62ms
                if (!sentReal) _udp?.SendAudio(VoiceRtp.SilenceFrame);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }      // token torn down mid-delay by a concurrent hangup
    }

    // ── video controls ──────────────────────────────────────────────────────────────────────────
    // The camera toggle: capture -> encode -> op 12 broadcast. Screen sharing is a separate Go Live
    // connection (StreamClient) and does not touch this plane at all.
    public void SetVideoEnabled(bool on)
    {
        if (on == _cameraOn || _udp == null || _gw == null) return;
        _cameraOn = on;
        if (on) StartCamera();
        else StopCamera();
    }

    void StartCamera()
    {
        StopCameraCapture();
        // H.264 encoder is optional: without an MF encoder (or to keep the tiny fallback path) the
        // pipeline degrades to the legacy whole-frame JPEG transport, which still works between two
        // ClaudeScord clients.
        _h264Enabled = _h264?.Ready ?? false;
        _camera = new CameraCapture(CamW, CamH, 15);
        _camera.Frame += OnNv12Frame;
        if (!_camera.Start())
        {
            _camera.Dispose(); _camera = null;
            _cameraOn = false;
            Log("camera unavailable");
            BroadcastVideoState();
            return;
        }
        BroadcastVideoState();
        Log($"camera on (h264={_h264Enabled})");
    }

    void StopCamera()
    {
        StopCameraCapture();
        BroadcastVideoState();
        Log("camera off");
    }

    void StopCameraCapture()
    {
        var cam = _camera; _camera = null;
        cam?.Stop(); cam?.Dispose();
    }

    void BroadcastVideoState()
    {
        var gw = _gw;
        if (gw == null) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await gw.SendVideoStateAsync(_videoSsrc, _videoRtx, _cameraOn, CamW, CamH);
                Log($"video broadcast (ssrc {_videoSsrc} cam={_cameraOn})");
            }
            catch (Exception e) { Log("video broadcast: " + e.Message); }
        });
    }

    // Webcam frame (NV12) -> preview + H.264 AUs (or the JPEG fallback).
    void OnNv12Frame(byte[] nv12, int w, int h)
    {
        SelfVideoFrame?.Invoke(Nv12.ToJpeg(nv12, w, h, 45));
        if (_h264Enabled && _h264 != null)
        {
            int aus = 0;
            foreach (var au in _h264.Encode(nv12)) { SendAu(au); aus++; }
            if (_videoDiagLeft-- > 0)
                Log($"video diag: camera frame -> {aus} AU(s) (encReady={_h264.Ready} err={_h264.Error})");
        }
        else SendJpegFallback(Nv12.ToJpeg(nv12, w, h, 45));
    }

    // One H.264 access unit -> DAVE-protected WHOLE frame, then single-NAL / FU-A packets.
    // This matches the real client's pipeline (encode -> DAVE -> packetize -> RTP): DAVE leaves
    // the start codes + NAL headers unencrypted (the packetizer reads them), so the protected
    // frame packetizes exactly like the plaintext AU. The old per-packet DAVE only ever worked
    // between two ClaudeScord clients — the real client protects whole frames and its decryptor
    // rejects per-packet ciphertext.
    void SendAu(byte[] au)
    {
        if (_dave != null)
        {
            var protectedAu = _dave.ProtectVideoFrame(au);
            if (protectedAu != null) au = protectedAu;
        }
        var packets = VideoRtp.PacketizeH264(au);
        for (int i = 0; i < packets.Count; i++)
        {
            bool marker = i == packets.Count - 1;
            _udp?.SendVideoRtp(packets[i], marker);
        }
    }

    // Legacy transport: whole JPEG frame fragmented with the length prefix, DAVE per fragment.
    void SendJpegFallback(byte[] jpeg)
    {
        if (jpeg == null) return;
        var frags = VideoRtp.Fragment(jpeg);
        for (int i = 0; i < frags.Count; i++)
        {
            bool marker = i == frags.Count - 1;
            var payload = _dave?.ProtectFrame(frags[i]) ?? frags[i];
            _udp?.SendVideoRtp(payload, marker);
        }
    }

    // ── controls ────────────────────────────────────────────────────────────────────────────────
    /// Push every voice preference into the live engine. Called at connect and whenever the
    /// settings page changes one, so a slider moves the call you are already in.
    public void ApplyVoicePrefs()
    {
        if (_audio is not { } a) return;
        var p = Prefs.Current;
        a.InputMode = p.InputMode == 1 ? VoiceAudio.Mode.PushToTalk : VoiceAudio.Mode.VoiceActivity;
        a.Sensitivity = p.Sensitivity;
        a.InputGain = p.InputVolume;
        a.OutputGain = p.OutputVolume;
        a.NoiseGate = p.NoiseGate;
        PushToTalk.Enabled = a.InputMode == VoiceAudio.Mode.PushToTalk;
        PushToTalk.Key = p.PttKey;
    }

    /// The live input level, for the settings page's meter. Zero when not in a call.
    public float InputLevel => _audio?.LastLevel ?? 0f;

    /// Where the gate will open right now — the meter draws its notch here.
    public float OpenThreshold => _audio?.OpenAt ?? 0f;

    /// Driven by the global hotkey hook.
    public void SetPttDown(bool down) { if (_audio != null) _audio.PttDown = down; }

    public void SetMuted(bool muted)
    {
        _muted = muted;
        _audio?.SetMuted(muted);
    }

    // The user/channel snowflakes the MLS group identity needs. VoiceServerInfo
    // carries them (see Net/VoiceServerInfo.cs); these helpers keep OnSession tidy.
    ulong info_UserId() => _userId;
    ulong info_ChannelId() => ChannelId;

    public void SetDeafened(bool deaf)
    {
        // Deafen implies mute in Discord; both stop outgoing audio here.
        _muted = deaf;
        _audio?.SetMuted(deaf);
    }

    // ── stop ────────────────────────────────────────────────────────────────────────────────────
    public static async Task HangUpAsync()
    {
        var vc = Current;
        if (vc == null) return;
        Current = null;
        await vc.StopAsync();
    }

    async Task StopAsync()
    {
        _hangingUp = true;
        _cameraOn = false;
        _statsTimer?.Dispose();
        _statsTimer = null;
        _speakTick?.Dispose();
        _speakTick = null;
        // Drop every ring on the way out, or the last frame anyone sent leaves their tile lit.
        foreach (var uid in _speaking.ToList()) SpeakingChanged?.Invoke(uid, false);
        _speaking.Clear();
        _videoRtcp?.Cancel();
        _videoRtcp = null;
        _subscribedSsrc = 0;                 // a fresh join must re-subscribe to any peer video
        _camera?.Stop(); _camera?.Dispose(); _camera = null;
        _h264?.Dispose();
        _cts?.Cancel();
        try
        {
            if (_udp != null && IsConnected) _udp.SendSilenceFrames();   // graceful stream end
            if (_gw != null) await _gw.SendSpeakingAsync(0);
            if (_gw != null) await _gw.DisconnectAsync();
        }
        catch { }
        _audio?.Dispose();
        _udp?.Dispose();
        _gw?.Dispose();
        _audio = null; _udp = null; _gw = null;
        IsConnected = false;
        Disconnected?.Invoke();
    }
}
