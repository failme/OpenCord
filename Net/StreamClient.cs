namespace ClaudeScord;

// One live Go Live (screen share) connection.
//
// Screen sharing is a SECOND RTC session, entirely separate from the voice connection: the main
// gateway mints it with op 18 (see UserClient.GoLiveAsync) and answers with its own endpoint and
// token, and the screen video rides that connection on a "screen" stream. The previous build put a
// screen entry in the VOICE connection's op 12, which the SFU ignores in a guild channel — the
// bytes went out and no client had anything to watch.
//
// Everything below the gateway is reused verbatim from the voice path (VoiceGateway, UdpVoice,
// DaveMls, VideoRtp, H264Encoder); this class is the glue plus the capture source. There is no
// audio plane and no receive path: a broadcaster only sends.
sealed class StreamClient
{
    /// The share we are broadcasting, and the one we are watching. They are independent
    /// connections and can both be live at once.
    public static StreamClient? Current { get; private set; }
    public static StreamClient? Watcher { get; private set; }

    public event Action<byte[]>? SelfVideoFrame;        // our own capture, for the preview tile
    public event Action<ulong, byte[]>? VideoFrame;     // (broadcaster, decoded JPEG) when watching
    public event Action? Ended;

    // Go Live's own encode budget: fit inside 1280x720 (the real client's default stream quality)
    // while keeping the MONITOR's aspect ratio. Forcing a fixed 16:9 stretched every non-16:9
    // desktop — a 16:10 or ultrawide screen went out visibly squashed, and no amount of correct
    // rendering at the far end can undo that.
    public const int MaxW = 1280, MaxH = 720, Fps = 15, Bitrate = 2_500_000;

    static (int w, int h) CaptureSize()
    {
        var b = Screen.PrimaryScreen?.Bounds ?? new System.Drawing.Rectangle(0, 0, 1920, 1080);
        return FitBudget(b.Width, b.Height);
    }

    /// A source size fitted inside MaxW x MaxH at its own aspect ratio, width rounded to 16 and
    /// height to 2 (NV12 needs even rows; the encoder wants an aligned stride).
    public static (int w, int h) FitBudget(int srcW, int srcH)
    {
        double aspect = srcW > 0 && srcH > 0 ? srcW / (double)srcH : 16.0 / 9.0;
        int w = MaxW, h = (int)Math.Round(MaxW / aspect);
        if (h > MaxH) { h = MaxH; w = (int)Math.Round(MaxH * aspect); }
        return (Math.Clamp((w + 15) & ~15, 160, MaxW), Math.Clamp((h + 1) & ~1, 120, MaxH));
    }

    int _w = MaxW, _h = MaxH;

    VoiceGateway? _gw;
    UdpVoice? _udp;
    DaveMls? _dave;
    ScreenCapture? _screen;
    volatile H264Encoder? _h264;
    uint _videoSsrc, _videoRtx;
    bool _h264Enabled;
    volatile bool _stopping;
    int _diagLeft = 4;

    readonly ulong _userId, _channelId, _broadcaster;
    readonly bool _watching;
    volatile uint _subscribedSsrc;
    CancellationTokenSource? _rtcp;

    public bool IsLive { get; private set; }

    StreamClient(ulong userId, ulong channelId, ulong broadcaster)
    {
        _userId = userId;
        _channelId = channelId;
        _broadcaster = broadcaster;
        _watching = broadcaster != 0 && broadcaster != userId;
    }

    void Log(string line) => ClaudeScord.Log.Write("voice", (_watching ? "[watch] " : "[stream] ") + line);

    public static async Task StartAsync(VoiceServerInfo info, ulong altServerId = 0)
    {
        await StopAsync();
        var sc = new StreamClient(info.UserId, info.ChannelId, 0) { _altServerId = altServerId };
        Current = sc;
        await sc.RunAsync(info);
    }

    /// Join a peer's Go Live connection as a viewer: no capture, subscribe and render.
    public static async Task WatchAsync(ulong broadcaster, VoiceServerInfo info, ulong altServerId = 0)
    {
        await StopWatchAsync();
        var sc = new StreamClient(info.UserId, info.ChannelId, broadcaster) { _altServerId = altServerId };
        Watcher = sc;
        await sc.RunAsync(info);
    }

    public static async Task StopAsync()
    {
        var sc = Current;
        if (sc == null) return;
        Current = null;
        await sc.TearDownAsync();
    }

    public static async Task StopWatchAsync()
    {
        var sc = Watcher;
        if (sc == null) return;
        Watcher = null;
        await sc.TearDownAsync();
    }

    // ponytail: one-shot server_id retry. The stream gateway takes either the rtc_server_id from
    // STREAM_CREATE or the guild/channel from the stream key, and nothing on the wire says which;
    // a wrong guess is a 4006 before READY. If the live logs settle it, drop the retry and pin the
    // right one in VoiceServerInfo.
    ulong _altServerId;
    bool _gotReady, _retried;

    async Task RunAsync(VoiceServerInfo info)
    {
        try
        {
            _gw = new VoiceGateway(info) { StreamType = "screen" };
            _gw.LogLine += Log;
            _gw.Ready += OnReady;
            _gw.SessionDescription += OnSession;
            _gw.DaveMessage += (op, payload) => _dave?.HandleDave(op, payload);
            _gw.DaveJson += (op, d) => _dave?.HandleDaveJson(op, d);
            _gw.Speaking += (uid, ssrc, _) => _dave?.OnSpeaking(uid, ssrc);
            _gw.ClientConnect += (uid, ssrc) => _dave?.OnClientConnect(uid, ssrc);
            _gw.ClientVideo += OnPeerVideoState;
            _gw.Closed += () =>
            {
                if (_stopping) return;
                int code = _gw?.LastCloseCode ?? 0;
                // Rejected identify (4004 bad token / 4006 stale session) before READY: the only
                // field we are guessing is server_id, so try the other candidate once.
                if (!_gotReady && !_retried && _altServerId != 0 && (code == 4006 || code == 4004))
                {
                    _retried = true;
                    Log($"identify rejected {code} — retrying with server_id {_altServerId}");
                    _ = RunAsync(info with { ServerId = _altServerId });
                    return;
                }
                Log("stream gateway closed");
                Ended?.Invoke();
            };
            Log($"connecting to stream gateway (server_id {info.ServerId})");
            await _gw.ConnectAsync();
        }
        catch (Exception e)
        {
            Log("stream connect failed: " + e.Message);
            await TearDownAsync();
        }
    }

    async void OnReady(VoiceGateway.ReadyData ready)
    {
        try
        {
            if (_gw == null) return;
            _gotReady = true;
            _udp = new UdpVoice(_gw, ready);
            _udp.LogLine += Log;
            _udp.VideoFrameReceived += (ssrc, jpeg) =>
                VideoFrame?.Invoke(_gw?.SsrcUser.GetValueOrDefault(ssrc) is { } u && u != 0 ? u : _broadcaster, jpeg);
            _videoSsrc = ready.VideoSsrc != 0 ? ready.VideoSsrc : ready.Ssrc + 1;
            _videoRtx = ready.RtxSsrc != 0 ? ready.RtxSsrc : _videoSsrc + 1;
            _udp.VideoSsrc = _videoSsrc;
            // A viewer asking for a keyframe must be answered or their decoder never starts. The
            // reset blocks, so it must not run on the UDP receive thread.
            _udp.KeyframeRequested += () =>
            {
                var enc = _h264;
                if (enc == null) return;
                _ = Task.Run(() =>
                {
                    try { if (enc.RequestKeyframe()) Log("PLI received — encoder reset"); } catch { }
                });
            };

            var (ip, port) = await _udp.HandshakeAsync();
            Log($"ip discovery -> {ip}:{port} mode={_udp.ChosenMode}");
            if (!_watching)
            {
                (_w, _h) = CaptureSize();
                try
                {
                    _h264 = new H264Encoder(_w, _h, Fps, Bitrate);
                    if (!_h264.Ready) Log("H264 encoder unavailable: " + _h264.Error);
                }
                catch (Exception e) { _h264 = null; Log("H264 encoder failed: " + e.Message); }
            }
            await _gw.SendSelectProtocolAsync(ip, port, _udp.ChosenMode);
        }
        catch (Exception e) { Log("stream udp handshake: " + e.Message); }
    }

    void OnSession(VoiceGateway.SessionData sess)
    {
        try
        {
            if (_udp == null || _gw == null) { Log("session description before ready"); return; }
            _udp.SetSession(sess);
            if (sess.DaveProtocolVersion != 0 && _dave == null)
            {
                // The stream connection negotiates its OWN MLS group; it shares nothing with the
                // voice connection's DAVE session beyond the channel the group id is derived from.
                _dave = new DaveMls(_userId, _channelId);
                _dave.LogLine += Log;
                _dave.SendBinary = pkt => { _ = _gw.SendDaveBinaryAsync(pkt); };
                _dave.SendJson = (op, d) => { _ = _gw.SendDaveJsonAsync(op, d); };
                _dave.OnSessionDescription(sess.DaveProtocolVersion);
                foreach (var (ssrc, uid) in _gw.SsrcUser) _dave.OnSpeaking(uid, ssrc);
                _udp.DaveTransform = (ssrc, frame) =>
                {
                    var dec = _dave?.UnprotectFrame(ssrc, frame);
                    if (dec == null) _udp.SetDaveLastFail(_dave?.LastFailReason ?? "unknown");
                    return dec;
                };
                Log("DAVE MLS session armed");
            }
            _udp.VideoPayloadType = sess.VideoCodec switch { "H264" => 107, "VP9" => 98, _ => 101 };

            if (_watching)
            {
                // A viewer sends nothing: it waits for the broadcaster's op 12 and subscribes.
                IsLive = true;
                Log("watching stream (waiting for the broadcaster's video state)");
                return;
            }

            _h264Enabled = _h264?.Ready ?? false;
            _screen = new ScreenCapture(_w, _h, Fps, 45);
            _screen.Frame += OnRgbFrame;
            if (!_screen.Start())
            {
                _screen.Dispose(); _screen = null;
                Log("screen capture unavailable");
                Ended?.Invoke();
                return;
            }
            IsLive = true;
            Broadcast(true);
            Log($"screenshare live {_w}x{_h} (h264={_h264Enabled})");
        }
        catch (Exception e) { Log("stream session setup: " + e.Message); }
    }

    // The broadcaster's op 12 on this connection carries the ssrcs their screen rides. Same pull
    // model as the camera plane: op 15 to subscribe, then a PLI/receiver-report cadence or the SFU
    // never releases the stream.
    void OnPeerVideoState(ulong uid, uint audioSsrc, uint videoSsrc, uint rtxSsrc, bool active)
    {
        if (!_watching || videoSsrc == 0 || uid == _userId) return;
        if (audioSsrc != 0) _dave?.OnSpeaking(uid, audioSsrc);
        _dave?.OnSpeaking(uid, videoSsrc);
        if (rtxSsrc != 0) _udp?.AddRtxSsrc(rtxSsrc);
        if (!active)
        {
            _subscribedSsrc = 0;
            _rtcp?.Cancel(); _rtcp = null;
            VideoFrame?.Invoke(uid, Array.Empty<byte>());
            return;
        }
        var gw = _gw;
        if (gw == null || _subscribedSsrc == videoSsrc) return;
        _subscribedSsrc = videoSsrc;
        _ = Task.Run(async () =>
        {
            try
            {
                await gw.SendMediaSinkWantsAsync(videoSsrc);
                Log($"subscribed to stream ssrc {videoSsrc} (user {uid})");
            }
            catch (Exception e) { Log("stream subscribe: " + e.Message); }
        });
        _rtcp?.Cancel();
        _rtcp = new CancellationTokenSource();
        var tok = _rtcp.Token;
        _ = Task.Run(async () =>
        {
            uint extSeq = 0;
            try
            {
                while (!tok.IsCancellationRequested)
                {
                    extSeq += 1000;
                    if (_udp is { } u)
                    {
                        u.SendRtcp(VideoRtp.BuildPli(u.AudioSsrc, videoSsrc));
                        u.SendRtcp(VideoRtp.BuildReceiverReport(u.AudioSsrc, videoSsrc, extSeq));
                    }
                    await Task.Delay(1500, tok);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { Log("stream rtcp: " + e.Message); }
        });
    }

    void Broadcast(bool active)
    {
        var gw = _gw;
        if (gw == null || _watching) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await gw.SendVideoStateAsync(_videoSsrc, _videoRtx, active, _w, _h);
                Log($"stream broadcast (ssrc {_videoSsrc} active={active})");
            }
            catch (Exception e) { Log("stream broadcast: " + e.Message); }
        });
    }

    void OnRgbFrame(byte[] rgb, int w, int h)
    {
        SelfVideoFrame?.Invoke(Nv12.RgbToJpeg(rgb, w, h, 40)!);
        var enc = _h264;
        if (!_h264Enabled || enc == null) return;
        var nv12 = Nv12.FromRgb(rgb, w, h, w * 3);
        int aus = 0;
        foreach (var au in enc.Encode(nv12)) { SendAu(au); aus++; }
        if (_diagLeft-- > 0)
            Log($"diag: screen frame -> {aus} AU(s) (encReady={enc.Ready} err={enc.Error})");
    }

    // Same pipeline as the camera plane: DAVE protects the WHOLE access unit, then it packetizes
    // into single-NAL / FU-A RTP packets with the marker on the last one.
    void SendAu(byte[] au)
    {
        if (_dave != null)
        {
            var prot = _dave.ProtectVideoFrame(au);
            if (prot != null) au = prot;
        }
        var packets = VideoRtp.PacketizeH264(au);
        for (int i = 0; i < packets.Count; i++)
            _udp?.SendVideoRtp(packets[i], i == packets.Count - 1);
    }

    async Task TearDownAsync()
    {
        _stopping = true;
        IsLive = false;
        _rtcp?.Cancel(); _rtcp = null;
        var scr = _screen; _screen = null;
        scr?.Stop(); scr?.Dispose();
        if (_gw != null) Broadcast(false);
        _h264?.Dispose();
        try { if (_gw != null) await _gw.DisconnectAsync(); } catch { }
        _udp?.Dispose();
        _gw?.Dispose();
        _udp = null; _gw = null; _dave = null; _h264 = null;
        Log("screenshare stopped");
    }
}
