using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace OpenCord;

// The voice-gateway websocket (protocol v9). This is the control plane of a voice connection: it
// carries the credentials exchange (identify -> ready), the UDP handshake (select protocol ->
// session description), speaking flags, heartbeats with seq_ack, the video plane (op 12 VIDEO
// broadcast, op 15 MEDIA_SINK_WANTS subscribe, op 14 SESSION_UPDATE codecs), and — for DAVE
// calls — the binary opcodes 21-31 that drive the MLS group. The media itself flows over UDP
// (see UdpVoice).
//
// Opcode table (v9, per the real client / dolfies discord.py-self):
//   0 identify    1 select protocol    2 ready     3 heartbeat (d: {t, seq_ack})
//   4 session description   5 speaking   6 heartbeat ack   7 resume   8 hello   9 resumed
//   11 clients_connect   12 VIDEO   13 client_disconnect   14 SESSION_UPDATE
//   15 MEDIA_SINK_WANTS   16 voice_backend_version
//   21-31 DAVE binary (server -> client: 2-byte BE seq, 1-byte opcode, payload).
//
// (v8 vs v9: the video ops are numbered differently — v8 pushed video state as op 18 {user_id,
// flags} and sink wants as op 20; v9 consolidated them into op 12 / op 15 with full ssrcs. The
// DAVE opcodes 21-31 are identical in both, so the proven E2EE audio path is version-agnostic.)
sealed class VoiceGateway : IDisposable
{
    // VideoSsrc/RtxSsrc come from READY's `streams` array — the server ALLOCATES the video ssrc
    // (audio ssrc + 1, rtx + 2) at connect time; the broadcaster does not invent its own.
    public record ReadyData(uint Ssrc, string Ip, ushort Port, string[] Modes,
                            uint VideoSsrc = 0, uint RtxSsrc = 0);
    public record SessionData(string Mode, byte[] SecretKey, int DaveProtocolVersion,
                              string VideoCodec = "VP8");

    public event Action<ReadyData>? Ready;
    public event Action<SessionData>? SessionDescription;
    public event Action<int, byte[]>? DaveMessage;    // (opcode, payload) — MLS layer input
    public event Action<int, JsonElement>? DaveJson;  // JSON DAVE opcodes (21/22/24)
    public event Action<ulong, uint, bool>? Speaking; // (userId, ssrc, speaking) — ssrc mapping
    public event Action<ulong, uint>? ClientConnect;  // (userId, audioSsrc) from op 11
    // (userId, audioSsrc, videoSsrc, rtxSsrc, active) — op 12 VIDEO state. active=false means
    // the peer turned the camera off; subscribers should stop requesting and clear the tile.
    // rtxSsrc is the peer's retransmission stream (its packets are [OSN][ciphertext], never
    // media on their own — the receive path drops them rather than corrupting the assembler).
    public event Action<ulong, uint, uint, uint, bool>? ClientVideo;
    // Fired when the server announces a member's video stream is available/active: op 12 VIDEO
    // (v9) carries {user_id, video_ssrc, rtx_ssrc, streams:[...]}. The peer's video ssrc comes
    // straight from the payload; fall back to audio ssrc + 1 only if it is missing.
    public event Action<ulong>? PeerVideo;
    public event Action<string>? LogLine;
    public event Action? Closed;

    // Probe hook: dump every inbound JSON op with its full payload (VoiceProbe sets this).
    public bool DumpAll;

    // ssrc -> userId, kept by the gateway for EVERY speaking/clients_connect event
    // regardless of DAVE state. A speaker already in the channel announces itself
    // (op 5) BEFORE this connection's own session description, i.e. before the
    // DAVE layer exists — those early mappings were lost, so the E2EE receive
    // path had no ssrc->uid entry for the other member and dropped every frame.
    // The DAVE layer seeds its ratchet map from this on creation.
    public readonly System.Collections.Concurrent.ConcurrentDictionary<uint, ulong> SsrcUser = new();

    readonly VoiceServerInfo _info;
    readonly int _maxDaveVersion;
    readonly SemaphoreSlim _sendLock = new(1, 1);   // ClientWebSocket forbids concurrent SendAsync
    ClientWebSocket? _ws;
    CancellationTokenSource? _cts;
    System.Timers.Timer? _heartbeat;
    long _heartbeatNonce;
    int _lastSeq = -1;          // last numbered message seen; -1 = none (for seq_ack)
    uint _ssrc;
    volatile bool _disposed;
    bool _closedFired;

    public VoiceGateway(VoiceServerInfo info, int maxDaveVersion = 1)
    {
        _info = info;
        _maxDaveVersion = maxDaveVersion;
    }

    void Log(string line) => LogLine?.Invoke(line);

    // ── lifecycle ────────────────────────────────────────────────────────────────────────────────
    public async Task ConnectAsync()
    {
        if (_disposed) return;
        _cts = new CancellationTokenSource();
        var tok = _cts.Token;

        // The endpoint from VOICE_SERVER_UPDATE already carries the gateway port. v9 is the version
        // the real client speaks; its DAVE ops (21-31) are identical to v8 so audio is unchanged.
        var url = new Uri($"wss://{_info.Endpoint}?v=9");
        Log("connecting " + url.Host + ":" + url.Port);
        _ws = new ClientWebSocket();
        try { await _ws.ConnectAsync(url, tok); }
        catch (Exception e) { Log("websocket connect failed: " + e.Message); throw; }

        await IdentifyAsync();
        _ = ReceiveLoopAsync(tok);
    }

    /// "video" on a normal voice connection (the camera plane), "screen" on a Go Live stream
    /// connection. It selects both the identify stream type and the op 12 broadcast shape — the
    /// SFU allocates the READY ssrc against this type, so a screen connection that identifies as
    /// "video" gets a camera stream nobody is watching.
    public string StreamType = "video";

    /// The websocket close code, once the server has hung up. 4004/4006 mean the identify was
    /// rejected (bad token / server_id / session), as opposed to an ordinary disconnect.
    public int LastCloseCode { get; private set; }

    async Task IdentifyAsync()
    {
        // Advertise video capability so the server allocates our video ssrc in READY and accepts
        // op 12 broadcasts. streams lists the media streams this client can produce (camera
        // "video"/"100" here; screen shares are "screen"); the server echoes them in READY with
        // the ssrcs it allocated. channel_id is required by v9.
        await SendJsonAsync(new
        {
            op = 0,
            d = new
            {
                server_id = _info.ServerId.ToString(),
                channel_id = _info.ChannelId.ToString(),
                user_id = _info.UserId.ToString(),
                session_id = _info.SessionId,
                token = _info.Token,
                video = true,
                streams = new[]
                {
                    new { type = StreamType, rid = "100", quality = 100, active = false },
                },
                max_dave_protocol_version = _maxDaveVersion,
            }
        });
    }

    public async Task DisconnectAsync()
    {
        if (_closedFired) return;               // already torn down
        _closedFired = true;
        try
        {
            if (_ws != null && _ws.State == WebSocketState.Open)
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "client left", CancellationToken.None);
        }
        catch { /* socket already gone */ }
        _cts?.Cancel();
        _heartbeat?.Stop();
        _heartbeat?.Dispose();
        _heartbeat = null;
        Closed?.Invoke();
    }

    public void Dispose()
    {
        _disposed = true;
        _cts?.Cancel();
        _heartbeat?.Stop();
        _heartbeat?.Dispose();
        _ws?.Dispose();
    }

    // ── outbound ─────────────────────────────────────────────────────────────────────────────────
    public async Task SendSelectProtocolAsync(string address, ushort port, string mode)
    {
        // The codecs array tells the server what media codecs we can produce/consume. Advertise
        // H264 (PT 107): the receiving H264 decoder MFT can decode a real Discord peer's camera
        // stream, and our own JPEG fallback rides the same PT with a magic marker that the other
        // OpenCord's receiver sniffs before routing (see VideoRtp) — so the H264 PT is correct
        // in BOTH directions. Advertising VP8 instead made the session negotiate VP8, which this
        // client has no decoder for: the peer's camera never rendered.
        await SendJsonAsync(new
        {
            op = 1,
            d = new
            {
                protocol = "udp",
                data = new { address, port, mode },
                codecs = new object[]
                {
                    new { name = "opus", type = "audio", priority = 1000, payload_type = 120, encode = true, decode = true },
                    new { name = "H264", type = "video", priority = 2000, payload_type = 107, rtx_payload_type = 108, encode = true, decode = true },
                },
            }
        });
    }

    // speaking is a bitmask: 1 = microphone, 2 = soundshare, 4 = priority. 0 stops transmitting.
    public async Task SendSpeakingAsync(int speaking)
    {
        await SendJsonAsync(new
        {
            op = 5,
            d = new { speaking, delay = 0, ssrc = _ssrc }
        });
    }

    // op 12 VIDEO: tell the SFU which streams we are broadcasting (active:true) or have stopped
    // (active:false) and on which allocated ssrcs. The camera stream is type "video" (rid 100); a
    // screen share adds a type "screen" stream. The server echoes this to the other members as
    // {user_id, video_ssrc, streams:[...]} and starts relaying our video RTP to subscribers.
    // `width`/`height` are the encoder's real output size. The real client broadcasts
    // max_resolution {"type":"fixed","width":W,"height":H} (observed verbatim in a live peer's
    // echoed state); the old {"type":"source","width":0,"height":0} advertised a zero-sized
    // stream, which the SFU has no reason to allocate bandwidth for.
    public async Task SendVideoStateAsync(uint videoSsrc, uint rtxSsrc, bool active,
                                          int width = 640, int height = 360)
    {
        var streams = new List<object>
        {
            new
            {
                type = StreamType,
                rid = "100",
                quality = 100,
                active,
                max_bitrate = 2_500_000,
                max_framerate = 30,
                max_resolution = new { type = "fixed", width, height },
                ssrc = videoSsrc,
                rtx_ssrc = rtxSsrc,
            },
        };
        await SendJsonAsync(new
        {
            op = 12,
            d = new
            {
                audio_ssrc = (uint)_ssrc,
                video_ssrc = videoSsrc,
                rtx_ssrc = rtxSsrc,
                streams,
            }
        });
    }

    // op 15 MEDIA_SINK_WANTS: the subscribe. The real client tells the SFU which video ssrcs it
    // wants to receive and at what quality; the SFU then forwards that member's video RTP to us.
    // (v8 numbered this op 20; v9 consolidated it here. Our earlier op 17 "subscribe" was an
    // unknown opcode that the server silently ignored — that is why no video ever relayed.)
    // The "any" key is the real client's initial want (its own sink-wants relayed to us read
    // {"any":100} / {"any":100,"<ssrc>":0}); the per-ssrc key refines it. Sending both mirrors
    // that exactly, so the SFU has no excuse to hold back the subscribed stream.
    public async Task SendMediaSinkWantsAsync(uint videoSsrc, int quality = 100)
    {
        var d = new Dictionary<string, int>
        {
            ["any"] = quality,
            [videoSsrc.ToString()] = quality,
        };
        Log($"sending media_sink_wants for ssrc {videoSsrc}: {System.Text.Json.JsonSerializer.Serialize(d)}");
        await SendJsonAsync(new { op = 15, d });
    }

    // op 14 SESSION_UPDATE: re-announce codecs mid-session (used when codecs change). Not required
    // for a basic call but harmless to send once after select-protocol.
    public async Task SendSessionUpdateAsync()
    {
        await SendJsonAsync(new
        {
            op = 14,
            d = new
            {
                codecs = new object[]
                {
                    new { name = "opus", type = "audio", priority = 1000, payload_type = 120 },
                    new { name = "H264", type = "video", priority = 2000, payload_type = 107, rtx_payload_type = 108 },
                },
            }
        });
    }

    public async Task SendResumeAsync()
    {
        await SendJsonAsync(new
        {
            op = 7,
            d = new
            {
                server_id = _info.ServerId.ToString(),
                session_id = _info.SessionId,
                token = _info.Token,
                seq_ack = _lastSeq,
            }
        });
    }

    // Client -> server DAVE binary: [1-byte opcode][payload] with NO sequence prefix.
    public async Task SendDaveBinaryAsync(byte[] packet)
    {
        var ws = _ws;
        if (ws == null || ws.State != WebSocketState.Open) return;
        await _sendLock.WaitAsync();
        try
        {
            if (ws.State == WebSocketState.Open)
                await ws.SendAsync(packet, WebSocketMessageType.Binary, true, _cts?.Token ?? CancellationToken.None);
        }
        finally { _sendLock.Release(); }
    }

    public async Task SendDaveJsonAsync(int op, object d)
        => await SendJsonAsync(new { op, d });

    async Task SendJsonAsync(object payload)
    {
        var ws = _ws;
        if (ws == null || ws.State != WebSocketState.Open) return;
        var json = JsonSerializer.Serialize(payload);
        var bytes = Encoding.UTF8.GetBytes(json);
        // Sends come from the heartbeat timer, the receive-loop handlers, and the UI thread; the
        // websocket rejects a second send while one is in flight, so serialize them all here.
        await _sendLock.WaitAsync();
        try
        {
            if (ws.State == WebSocketState.Open)
                await ws.SendAsync(bytes, WebSocketMessageType.Text, true, _cts?.Token ?? CancellationToken.None);
        }
        finally { _sendLock.Release(); }
    }

    // ── receive ──────────────────────────────────────────────────────────────────────────────────
    async Task ReceiveLoopAsync(CancellationToken tok)
    {
        var buffer = new byte[4096];
        try
        {
            while (!tok.IsCancellationRequested && _ws?.State == WebSocketState.Open)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult r;
                do
                {
                    r = await _ws.ReceiveAsync(buffer, tok);
                    if (r.MessageType == WebSocketMessageType.Close)
                    {
                        Log("voice gateway closed: " + _ws.CloseStatus);
                        LastCloseCode = (int?)_ws.CloseStatus ?? 0;
                        _ws.Dispose();
                        if (!_closedFired) { _closedFired = true; Closed?.Invoke(); }
                        return;
                    }
                    ms.Write(buffer, 0, r.Count);
                }
                while (!r.EndOfMessage);

                var data = ms.ToArray();
                if (r.MessageType == WebSocketMessageType.Binary) HandleBinary(data);
                else HandleJson(JsonDocument.Parse(data).RootElement);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { Log("voice receive: " + e.Message); }
    }

    void HandleJson(JsonElement root)
    {
        if (!root.TryGetProperty("op", out var opEl)) return;
        int op = opEl.GetInt32();
        // Numbered messages: remember the seq for heartbeat/resume seq_ack.
        if (root.TryGetProperty("seq", out var seqEl) && seqEl.ValueKind == JsonValueKind.Number)
            _lastSeq = seqEl.GetInt32();

        if (DumpAll) Log($"<-gw op {op} {root}");

        switch (op)
        {
            case 2: HandleReady(root.GetProperty("d")); break;
            case 4: HandleSessionDescription(root.GetProperty("d")); break;
            case 8: HandleHello(root.GetProperty("d")); break;
            case 9: Log("voice session resumed"); break;
            case 5: HandleSpeaking(root.GetProperty("d")); break;
            case 6: break;   // heartbeat ack
            case 11: HandleClientConnect(root.GetProperty("d")); break;
            case 12: HandleVideoState(root.GetProperty("d")); break;
            case 13: Log("client_disconnect: " + (root.TryGetProperty("d", out var cd) ? cd.ToString() : "")); break;
            case 14: Log("session_update: " + (root.TryGetProperty("d", out var su) ? su.ToString() : "")); break;
            case 15: Log("media_sink_wants: " + (root.TryGetProperty("d", out var mw) ? mw.ToString() : "")); break;
            case 16: Log("voice_backend_version: " + (root.TryGetProperty("d", out var vb) ? vb.ToString() : "")); break;
            case 21:
            case 22:
            case 24:
                DaveJson?.Invoke(op, root.GetProperty("d"));
                break;
            default:
                Log($"voice gateway op {op} ({(root.TryGetProperty("d", out var d) ? d.ToString() : "")})");
                break;
        }
    }

    void HandleReady(JsonElement d)
    {
        _ssrc = d.GetProperty("ssrc").GetUInt32();
        var ip = d.GetProperty("ip").GetString() ?? "";
        ushort port = 0;
        if (d.TryGetProperty("port", out var p)) port = p.GetUInt16();
        var modes = d.TryGetProperty("modes", out var m)
            ? m.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x != "").ToArray()
            : Array.Empty<string>();
        // READY allocates the video stream: streams:[{type:"video", ssrc, rtx_ssrc, rid, active}] —
        // the only SSRCs the broadcaster may use for video (the SFU relays by these).
        uint vssrc = 0, vrtx = 0;
        if (d.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in streams.EnumerateArray())
            {
                if (!s.TryGetProperty("ssrc", out var sEl)) continue;
                vssrc = sEl.GetUInt32();
                if (s.TryGetProperty("rtx_ssrc", out var rEl)) vrtx = rEl.GetUInt32();
                break;
            }
        }
        Log($"ready ssrc={_ssrc} video_ssrc={vssrc} rtx={vrtx} ip={ip} port={port} modes=[{string.Join(", ", modes)}]");
        Ready?.Invoke(new ReadyData(_ssrc, ip, port, modes, vssrc, vrtx));
    }

    void HandleSessionDescription(JsonElement d)
    {
        var mode = d.GetProperty("mode").GetString() ?? "aead_xchacha20_poly1305_rtpsize";
        var key = d.TryGetProperty("secret_key", out var k) && k.ValueKind == JsonValueKind.Array
            ? k.EnumerateArray().Select(x => (byte)x.GetInt32()).ToArray() : Array.Empty<byte>();
        int daveVer = d.TryGetProperty("dave_protocol_version", out var v) ? v.GetInt32() : 0;
        // The server picks the session's video codec ("H264" for calls, "VP8"/"VP9" elsewhere).
        string vcodec = d.TryGetProperty("video_codec", out var vc) && vc.ValueKind == JsonValueKind.String
            ? vc.GetString() ?? "VP8" : "VP8";
        Log($"session description mode={mode} dave_protocol_version={daveVer} video_codec={vcodec} keylen={key.Length}");
        SessionDescription?.Invoke(new SessionData(mode, key, daveVer, vcodec));
    }

    void HandleVideoState(JsonElement d)
    {
        // op 12 VIDEO, server -> client: {user_id, video_ssrc, rtx_ssrc, streams:[{type,rid,
        // quality,active,ssrc,rtx_ssrc}]}. Fires when a member starts/stops broadcasting video.
        try
        {
            if (!d.TryGetProperty("user_id", out var u) || u.ValueKind != JsonValueKind.String) return;
            ulong userId = ulong.Parse(u.GetString() ?? "0");
            uint vssrc = 0, vrtx = 0;
            if (d.TryGetProperty("video_ssrc", out var v) && v.ValueKind == JsonValueKind.Number)
                vssrc = v.GetUInt32();
            if (d.TryGetProperty("rtx_ssrc", out var r) && r.ValueKind == JsonValueKind.Number)
                vrtx = r.GetUInt32();
            // streams[] may also carry per-stream ssrcs; prefer the explicit one, else the first.
            bool active = false;
            if (vssrc == 0 && d.TryGetProperty("streams", out var st) && st.ValueKind == JsonValueKind.Array)
            {
                foreach (var s in st.EnumerateArray())
                {
                    if (s.TryGetProperty("ssrc", out var sEl) && sEl.ValueKind == JsonValueKind.Number)
                    { vssrc = sEl.GetUInt32(); break; }
                }
            }
            // streams[] entries carry their own rtx_ssrc (e.g. {"ssrc":4280,"rtx_ssrc":4281});
            // prefer that when the top-level field is absent.
            if (vrtx == 0 && d.TryGetProperty("streams", out var stRtx) && stRtx.ValueKind == JsonValueKind.Array)
            {
                foreach (var s in stRtx.EnumerateArray())
                {
                    if (s.TryGetProperty("rtx_ssrc", out var rEl) && rEl.ValueKind == JsonValueKind.Number)
                    { vrtx = rEl.GetUInt32(); break; }
                }
            }
            if (d.TryGetProperty("streams", out var st2) && st2.ValueKind == JsonValueKind.Array)
            {
                foreach (var s in st2.EnumerateArray())
                {
                    if (s.TryGetProperty("active", out var aEl) && aEl.ValueKind == JsonValueKind.True) { active = true; break; }
                }
            }
            uint asrc = 0;
            if (d.TryGetProperty("audio_ssrc", out var a) && a.ValueKind == JsonValueKind.Number) asrc = a.GetUInt32();
            Log($"video state user={userId} audio_ssrc={asrc} video_ssrc={vssrc} rtx={vrtx} active={active} " +
                $"({(d.TryGetProperty("streams", out var st3) ? st3.ToString() : "none")})");
            if (vssrc != 0) SsrcUser[vssrc] = userId;
            if (asrc != 0) SsrcUser[asrc] = userId;
            if (vssrc != 0) ClientVideo?.Invoke(userId, asrc, vssrc, vrtx, active);
            // A video ssrc (or audio) means the peer is a camera-capable member to subscribe to.
            if (vssrc != 0 || asrc != 0) PeerVideo?.Invoke(userId);
        }
        catch { }
    }

    void HandleSpeaking(JsonElement d)
    {
        // {"speaking":1,"ssrc":N,"user_id":"..."} — the ssrc->user mapping the MLS
        // receive path needs to pick the right per-sender key ratchet.
        try
        {
            if (!d.TryGetProperty("ssrc", out var s) || !d.TryGetProperty("user_id", out var u)) return;
            uint ssrc = s.GetUInt32();
            ulong userId = ulong.Parse(u.GetString() ?? "0");
            int speaking = d.TryGetProperty("speaking", out var sp) ? sp.GetInt32() : 1;
            SsrcUser[ssrc] = userId;
            Log($"speaking user={userId} ssrc={ssrc} state={speaking}");
            Speaking?.Invoke(userId, ssrc, speaking != 0);
        }
        catch { }
    }

    void HandleClientConnect(JsonElement d)
    {
        // clients_connect: array of {user_id, audio_ssrc?, ...} — another ssrc source.
        try
        {
            if (d.ValueKind != JsonValueKind.Array) return;
            foreach (var u in d.EnumerateArray())
            {
                if (!u.TryGetProperty("user_id", out var uidEl)) continue;
                ulong userId = ulong.Parse(uidEl.GetString() ?? "0");
                uint ssrc = 0;
                if (u.TryGetProperty("audio_ssrc", out var a) && a.ValueKind == JsonValueKind.Number)
                    ssrc = a.GetUInt32();
                if (ssrc != 0) SsrcUser[ssrc] = userId;
                // A user with video active carries video_ssrc here — remember it so the DAVE
                // receive path can map their video packets to their user id.
                if (u.TryGetProperty("video_ssrc", out var v) && v.ValueKind == JsonValueKind.Number)
                {
                    uint vssrc = v.GetUInt32();
                    if (vssrc != 0) SsrcUser[vssrc] = userId;
                    Log($"clients_connect user={userId} audio_ssrc={ssrc} video_ssrc={vssrc}");
                    ClientVideo?.Invoke(userId, ssrc, vssrc, 0, true);
                }
                ClientConnect?.Invoke(userId, ssrc);
            }
        }
        catch { }
    }

    void HandleHello(JsonElement d)
    {
        int interval = d.TryGetProperty("heartbeat_interval", out var h) ? h.GetInt32() : 41250;
        // v8 heartbeat: d = { t: nonce, seq_ack: lastSeq }.
        _heartbeat?.Stop();
        _heartbeat = new System.Timers.Timer(interval) { AutoReset = true };
        _heartbeat.Elapsed += async (_, _) =>
        {
            _heartbeatNonce++;
            try { await SendJsonAsync(new { op = 3, d = new { t = _heartbeatNonce, seq_ack = _lastSeq } }); }
            catch { }
        };
        _heartbeat.Start();
        Log($"hello heartbeat_interval={interval}");
    }

    // DAVE binary messages: [2-byte BE seq] [1-byte opcode] [payload].
    void HandleBinary(byte[] data)
    {
        try
        {
            if (data.Length < 3) { Log("short binary voice message: " + Convert.ToHexString(data)); return; }
            int seq = (data[0] << 8) | data[1];
            _lastSeq = seq;
            int opcode = data[2];
            var payload = data.AsSpan(3).ToArray();
            Log($"dave opcode {opcode} seq={seq} payload({payload.Length}) {Convert.ToHexString(payload, 0, Math.Min(payload.Length, 96))}");
            DaveMessage?.Invoke(opcode, payload);
        }
        catch (Exception e) { Log("binary parse: " + e.Message); }
    }
}
