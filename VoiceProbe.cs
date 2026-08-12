namespace ClaudeScord;

// --voiceprobe [token] [channelId]: headless live test of the voice transport against Discord's
// real servers. Joins the first voice channel it can find (or the given channel id / a "failme"
// DM), runs the full handshake, and prints everything the transport layer learns — READY modes,
// IP discovery result, session description, every DAVE binary opcode as raw hex, and every UDP
// frame received with its E2EE outcome. That dump is the ground truth the MLS layer is built
// against, and it runs the real two-party E2EE exchange with any other client in the channel.
static class VoiceProbe
{
    public static async Task Run(string token, string? channelArg)
    {
        // --nodave: advertise max_dave_protocol_version=0 and skip the MLS layer entirely, so the
        // call downgrades to plain transport-encrypted Opus. Isolates the transport relay from the
        // E2EE group state — if two --nodave probes can hear each other, the transport is proven
        // and the DAVE session is the remaining suspect, and vice versa.
        bool noDave = Environment.GetCommandLineArgs().Contains("--nodave");
        bool noCommit = Environment.GetCommandLineArgs().Contains("--no-commit");
        // --video: broadcast a synthetic video stream (op 16 + RTP video frames). --vsub: subscribe
        // to whoever else is in the channel (op 17). Isolates the SFU's video relay from the codec.
        bool video = Environment.GetCommandLineArgs().Contains("--video");
        bool vsub = Environment.GetCommandLineArgs().Contains("--vsub");
        bool noBroadcast = Environment.GetCommandLineArgs().Contains("--no-broadcast");   // skip op 14
        bool vsubPeer = Environment.GetCommandLineArgs().Contains("--vsub-peer");        // subscribe with the peer's ssrcs
        // --wait N: sleep N seconds before joining the voice channel. Lets a two-probe test stagger
        // the joins so only ONE client commits (the other is welcomed) — isolating the
        // simultaneous-commit race from the rest of the DAVE flow.
        int waitSec = 0;
        int postSec = 30;   // --post N: seconds to keep observing after the E2EE transition
        var argv = Environment.GetCommandLineArgs();
        for (int i = 0; i < argv.Length - 1; i++)
        {
            if (argv[i] == "--wait" && int.TryParse(argv[i + 1], out var w)) waitSec = w;
            if (argv[i] == "--post" && int.TryParse(argv[i + 1], out var p)) postSec = p;
        }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        void Ts(string l) => Console.WriteLine($"[t={sw.ElapsedMilliseconds,5}] {l}");
        if (waitSec > 0)
        {
            Ts($"waiting {waitSec}s before joining (staggered test)...");
            await Task.Delay(waitSec * 1000);
        }
        var client = new UserClient(token);
        client.OnLog += line => Console.WriteLine("[gw] " + line);
        Log.Sink = (cat, line) => Console.WriteLine($"[{cat}] " + line);

        var readyTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Ready += () => { readyTcs.TrySetResult(); return Task.CompletedTask; };

        Console.WriteLine("connecting gateway...");
        await client.ConnectAsync();
        await readyTcs.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Console.WriteLine($"ready as {client.CurrentUser?.Username} (id {client.CurrentUser?.Id})");

        // Pick a voice target: explicit id, else a guild voice channel, else the failme DM.
        ulong channel = 0, guild = 0;
        string label = "";
        if (ulong.TryParse(channelArg, out var cid))
        {
            channel = cid;
            label = "explicit " + cid;
            // A guild voice channel MUST be joined with its guild_id (a null guild is treated as
            // a DM and never yields VOICE_SERVER_UPDATE). Resolve the owning guild when given an id.
            foreach (var g in client.GuildById.Values)
            {
                if (g.Channels.Any(c => c.Id == cid)) { guild = g.Id; label = $"{g.Name}/{cid}"; break; }
            }
        }
        else
        {
            foreach (var g in client.GuildById.Values)
            {
                var vc = g.Channels.FirstOrDefault(c => c.IsVoice);
                if (vc != null) { channel = vc.Id; guild = g.Id; label = $"{g.Name}/{vc.Name}"; break; }
            }
            if (channel == 0)
            {
                var dm = client.DmById.Values.FirstOrDefault(d => d.Recipient != null
                    && d.Recipient.DisplayName.Contains("failme", StringComparison.OrdinalIgnoreCase));
                if (dm != null) { channel = dm.Id; label = "DM " + dm.Recipient!.DisplayName; }
            }
        }
        if (channel == 0) { Console.WriteLine("NO VOICE TARGET FOUND"); await client.DisconnectAsync(); return; }
        Ts($"joining {label} (channel={channel} guild={guild})");
        await client.SetVoiceStateAsync(guild == 0 ? null : guild, channel);

        var infoTcs = new TaskCompletionSource<VoiceServerInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.VoiceServerReady += info => infoTcs.TrySetResult(info);
        var info = await infoTcs.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Ts($"== voice server endpoint={info.Endpoint} server={info.ServerId} session={info.SessionId}");

        var gw = new VoiceGateway(info, noDave ? 0 : 1);
        gw.DumpAll = Environment.GetCommandLineArgs().Contains("--dump");
        gw.LogLine += l => Console.WriteLine("  [voice] " + l);
        DaveMls? dave = null;
        gw.DaveMessage += (op, payload) =>
        {
            Ts($"  [dave op {op}] len={payload.Length} hex={Convert.ToHexString(payload, 0, Math.Min(payload.Length, 1200))}");
            dave?.HandleDave(op, payload);
        };
        gw.DaveJson += (op, d) =>
        {
            Console.WriteLine($"  [dave json op {op}] {d}");
            dave?.HandleDaveJson(op, d);
        };
        ulong selfUid = info.UserId;
        ulong otherUid = 0; uint otherAudioSsrc = 0, otherVideoSsrc = 0;
        uint readySsrc = 0, readyVideoSsrc = 0, readyVideoRtx = 0;
        gw.Speaking += (uid, ssrc, speaking) =>
        {
            Console.WriteLine($"  [speaking] user={uid} ssrc={ssrc} state={speaking}");
            if (uid != selfUid) { otherUid = uid; otherAudioSsrc = ssrc; }
            dave?.OnSpeaking(uid, ssrc);
        };
        gw.ClientConnect += (uid, ssrc) =>
        {
            Console.WriteLine($"  [clients_connect] user={uid} ssrc={ssrc}");
            dave?.OnClientConnect(uid, ssrc);
        };
        gw.ClientVideo += (uid, audioSsrc, videoSsrc, rtxSsrc, active) =>
        {
            Console.WriteLine($"  [client video] user={uid} audio_ssrc={audioSsrc} video_ssrc={videoSsrc} rtx={rtxSsrc} active={active}");
            if (uid != selfUid) { otherUid = uid; otherAudioSsrc = audioSsrc; otherVideoSsrc = videoSsrc; }
        };

        UdpVoice? udp = null;
        long sent = 0;
        var sendCts = new CancellationTokenSource();
        gw.PeerVideo += uid =>
        {
            Console.WriteLine($"  [peer video] user={uid} — subscribing");
            if (uid == selfUid || !vsub) return;
            otherUid = uid;
            _ = Task.Run(async () =>
            {
                // v9 subscribe: op 15 MEDIA_SINK_WANTS keyed on the PEER's video ssrc (from the
                // op 12 VIDEO state, or audio ssrc + 1 as the server's deterministic allocation).
                uint pv = otherVideoSsrc != 0 ? otherVideoSsrc : otherAudioSsrc + 1;
                await gw.SendMediaSinkWantsAsync(pv);
                Console.WriteLine($"== media sink wants sent (peer video ssrc {pv}) peerAudio={otherAudioSsrc}");
                StartRtcp(udp, readySsrc, pv, sendCts);
            });
        };

        gw.Ready += async ready =>
        {
            Console.WriteLine($"== READY ssrc={ready.Ssrc} ip={ready.Ip} port={ready.Port} " +
                              $"modes=[{string.Join(", ", ready.Modes)}]");
            readySsrc = ready.Ssrc;
            readyVideoSsrc = ready.VideoSsrc;
            readyVideoRtx = ready.RtxSsrc;
            udp = new UdpVoice(gw, ready);
            if (ready.VideoSsrc != 0) udp.VideoSsrc = ready.VideoSsrc;
            udp.VideoFrameReceived += (ssrc, jpeg) =>
                Console.WriteLine($"  [video rx] ssrc={ssrc} full frame {jpeg.Length}B");
            int rxDump = 0, txDump = 0;
            udp.PacketReceived += (ssrc, p) => Console.WriteLine($"  [udp rx] ssrc={ssrc} {p.Length}-byte frame");
            udp.RawPacket += (len, hex) =>
            {
                // classify: 0x80 = RTP, 0x81 = RTP with CC, 0xC9-0xCF-ish RTCP (PT 201 = keepalive)
                string kind = len >= 2 ? (hex[..2] == "81" ? "keepalive" : (hex[..2] == "80" ? "RTP" : "other")) : "?";
                if (rxDump++ < 40) Ts($"  [udp raw rx {kind}] {len}B {hex}");
            };
            udp.SentPacket += (len, hex) => { if (txDump++ < 30) Ts($"  [udp raw tx] {len}B {hex}"); };
            var (ip, port) = await udp.HandshakeAsync();
            Console.WriteLine($"== IP DISCOVERY -> {ip}:{port} mode={udp.ChosenMode}");
            await gw.SendSelectProtocolAsync(ip, port, udp.ChosenMode);
        };
        gw.SessionDescription += async sess =>
        {
            Console.WriteLine($"== SESSION DESCRIPTION mode={sess.Mode} dave_protocol_version={sess.DaveProtocolVersion} " +
                              $"key={Convert.ToHexString(sess.SecretKey)}");
            if (udp != null) udp.SetSession(sess);     // the transport key must land before any send
            if (sess.DaveProtocolVersion != 0 && !noDave)
            {
                MlsGroup.Debug = l => Console.WriteLine("    [wdbg] " + l);
                dave = new DaveMls(info.UserId, info.ChannelId);
                dave.SuppressCommit = noCommit;
                if (noCommit) Console.WriteLine("    [mls] COMMIT SUPPRESSED (test mode: only the other client commits)");
                dave.LogLine += l => Console.WriteLine("    [mls] " + l);
                dave.SendBinary = pkt =>
                {
                    Console.WriteLine($"    [mls ->gw op {pkt[0]}] len={pkt.Length} hex={Convert.ToHexString(pkt)}");
                    _ = gw.SendDaveBinaryAsync(pkt);
                };
                dave.SendJson = (op, d2) =>
                {
                    Console.WriteLine($"    [mls ->gw json op {op}] {System.Text.Json.JsonSerializer.Serialize(d2)}");
                    _ = gw.SendDaveJsonAsync(op, d2);
                };
                dave.OnSessionDescription(sess.DaveProtocolVersion);
                foreach (var (ssrc, uid) in gw.SsrcUser) dave.OnSpeaking(uid, ssrc);
                if (udp != null) udp.DaveTransform = (ssrc, frame) =>
                {
                    var dec = dave?.UnprotectFrame(ssrc, frame);
                    Console.WriteLine($"  [udp rx e2ee] ssrc={ssrc} len={frame.Length} -> " +
                                      (dec == null ? $"FAIL ({dave?.LastFailReason})" : $"{dec.Length}-byte opus"));
                    return dec;
                };
                Console.WriteLine("    [mls] DAVE session armed");
            }
            await gw.SendSpeakingAsync(1);
            Console.WriteLine("== speaking=1 sent; transport alive");

            if (video)
            {
                string streamKey = $"video:{selfUid}:0";
                // The server allocated the video ssrc in READY (audio+1); broadcast THAT, with the
                // session's negotiated codec, and pick the matching RTP payload type.
                uint vssrc = readyVideoSsrc != 0 ? readyVideoSsrc : readySsrc + 1;
                uint vrtx = readyVideoRtx != 0 ? readyVideoRtx : vssrc + 1;
                string codec = sess.VideoCodec;
                if (udp != null)
                {
                    udp.VideoSsrc = vssrc;
                    udp.VideoPayloadType = codec switch
                    {
                        "H264" => 107, "VP9" => 98, _ => 101,   // VP8 and anything else
                    };
                }
                if (!noBroadcast)
                {
                    // v9 broadcast: op 12 VIDEO with the server-allocated ssrc + active:true.
                    await gw.SendVideoStateAsync(vssrc, vrtx, true);
                    await gw.SendSessionUpdateAsync();
                    Console.WriteLine($"== video state sent (ssrc={vssrc} rtx={vrtx} codec={codec} pt={udp?.VideoPayloadType})");
                }
                else Console.WriteLine("== video broadcast SKIPPED (--no-broadcast)");
                _ = Task.Run(async () =>
                {
                    // Real H.264 pipeline: NV12 test frames -> encoder -> single-NAL/FU-A packets,
                    // each DAVE-encrypted individually exactly like the app's camera path.
                    using var enc = new H264Encoder(640, 360, 15, 900_000);
                    if (!enc.Ready) Console.WriteLine($"  [video send] H264 encoder unavailable: {enc.Error}");
                    int frame = 0;
                    try
                    {
                        while (!sendCts.IsCancellationRequested)
                        {
                            frame++;
                            var nv12 = MakeTestNv12(frame);
                            foreach (var au in enc.Encode(nv12))
                            {
                                var packets = VideoRtp.PacketizeH264(au);
                                for (int i = 0; i < packets.Count; i++)
                                {
                                    bool marker = i == packets.Count - 1;
                                    var payload = dave?.ProtectFrame(packets[i]) ?? packets[i];
                                    udp?.SendVideoRtp(payload, marker);
                                }
                                if (frame % 15 == 0)
                                    Console.WriteLine($"  [video send] frame={frame} au={au.Length}B packets={packets.Count}");
                            }
                            await Task.Delay(66);
                        }
                    }
                    catch (Exception e) { Console.WriteLine($"  [video send] died: {e.GetType().Name}: {e.Message}"); }
                });
            }
            if (vsub)
            {
                Console.WriteLine("== vsub: waiting for another member's video stream...");
                _ = Task.Run(async () =>
                {
                    for (int i = 0; i < 150 && otherUid == 0; i++) await Task.Delay(100);
                    if (otherUid != 0)
                    {
                        uint vs = otherVideoSsrc != 0 ? otherVideoSsrc : otherAudioSsrc + 1;
                        await gw.SendMediaSinkWantsAsync(vs);
                        Console.WriteLine($"== media sink wants sent (peer video ssrc {vs})");
                        StartRtcp(udp, readySsrc, vs, sendCts);
                    }
                    else Console.WriteLine("== vsub: no other member seen; nothing to subscribe to");
                });
            }

            // Steady transitory-phase send (plaintext until E2EE, protected after): if the other
            // client is in the channel this tests both directions through the real SFU.
            _ = Task.Run(async () =>
            {
                byte[] opus = { 0xF8, 0xFF, 0xFE, 0x01 };   // near-silence, not the DTX frame
                var sw = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    while (!sendCts.IsCancellationRequested)
                    {
                        var u = udp;
                        if (u != null)
                        {
                            var frame = dave?.ProtectFrame(opus) ?? opus;
                            if (frame != null) { u.SendAudio(frame); sent++; }
                        }
                        await Task.Delay(20);
                        if (sent % 100 == 0 && sent > 0)
                            Console.WriteLine($"  [send loop] t={sw.ElapsedMilliseconds}ms sent={sent} udpSent={u?.PacketsSent ?? 0} sendErr={u?.LastSendError}");
                    }
                    Console.WriteLine($"  [send loop] finished t={sw.ElapsedMilliseconds}ms sent={sent}");
                }
                catch (Exception e) { Console.WriteLine($"  [send loop] died at t={sw.ElapsedMilliseconds}ms sent={sent}: {e.GetType().Name}: {e.Message}"); }
            });
        };

        try
        {
            await gw.ConnectAsync();
        }
        catch (Exception e)
        {
            Console.WriteLine("voice connect failed: " + e.Message);
        }

        Console.WriteLine("probing: waiting for the E2EE transition (up to 75s)...");
        bool e2ee = false;
        for (int i = 0; i < 375; i++)
        {
            if (dave != null && dave.Ready) { e2ee = true; break; }
            await Task.Delay(200);
        }
        Console.WriteLine(e2ee ? "== E2EE TRANSITION EXECUTED - ratchets live" : "== E2EE never became active");
        await Task.Delay(postSec * 1000);   // long post-transition window: give the SFU time to relay media
        sendCts.Cancel();
        var u2 = udp;
        Console.WriteLine($"== stats: sent={sent} rx={(u2?.PacketsReceived ?? 0)} " +
                          $"transportFail={(u2?.TransportFailures ?? 0)} e2eeFail={(u2?.DecryptFailures ?? 0)} " +
                          $"external={(u2 != null ? u2.ExternalIp + ":" + u2.ExternalPort : "no udp")} " +
                          $"e2eeReady={(dave?.Ready ?? false)}");

        await gw.DisconnectAsync();
        await client.DisconnectAsync();
        Console.WriteLine("probe complete");
    }

    // ── RTCP ────────────────────────────────────────────────────────────────────────────────
    // Discord's SFU is pull-based for video: a subscriber must send PLI (keyframe requests) and
    // receiver reports or the SFU never releases the subscribed stream. The probe mirrors the real
    // client's cadence so a live camera test actually gets video. (Builders live in VideoRtp, shared
    // with the app's VoiceClient.)
    static readonly HashSet<uint> _rtcpStarted = new();
    static void StartRtcp(UdpVoice? udp, uint ownAudioSsrc, uint peerVideoSsrc, CancellationTokenSource cts)
    {
        if (udp == null || peerVideoSsrc == 0) return;
        if (!_rtcpStarted.Add(peerVideoSsrc)) return;
        _ = Task.Run(async () =>
        {
            uint extSeq = 0;
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    extSeq += 1000;   // pretend steady reception so the SFU sees a live subscriber
                    udp.SendRtcp(VideoRtp.BuildPli(ownAudioSsrc, peerVideoSsrc));
                    udp.SendRtcp(VideoRtp.BuildReceiverReport(ownAudioSsrc, peerVideoSsrc, extSeq));
                    Console.WriteLine($"  [rtcp] PLI+RR -> video ssrc {peerVideoSsrc}");
                    await Task.Delay(1500, cts.Token);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { Console.WriteLine($"  [rtcp] loop died: {e.GetType().Name}: {e.Message}"); }
        });
    }

    // A synthetic camera for the H.264 pipeline: a horizontal gradient whose hue shifts per frame
    // (so consecutive frames differ and the encoder actually emits deltas), NV12 640x360.
    static byte[] MakeTestNv12(int frame)
    {
        int w = 640, h = 360, yStride = (w + 15) & ~15;
        var nv = new byte[yStride * h + yStride * h / 2];
        int shift = (frame * 5) & 0xFF;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                nv[y * yStride + x] = (byte)((x * 255 / w + shift) & 0xFF);
        for (int y = 0; y < h / 2; y++)
            for (int x = 0; x < w / 2; x++)
            {
                nv[yStride * h + y * yStride + x * 2] = (byte)(128 + (frame % 8) * 8);
                nv[yStride * h + y * yStride + x * 2 + 1] = (byte)128;
            }
        return nv;
    }

    static System.Drawing.Color ColorFromHsv(int h, float s, float v)
    {
        float c = v * s, x = c * (1 - Math.Abs(h / 60f % 2 - 1)), m = v - c;
        (float r, float g, float b) = h switch
        {
            < 60 => (c, x, 0f), < 120 => (x, c, 0f), < 180 => (0f, c, x),
            < 240 => (0f, x, c), < 300 => (x, 0f, c), _ => (c, 0f, x),
        };
        return System.Drawing.Color.FromArgb((int)((r + m) * 255), (int)((g + m) * 255), (int)((b + m) * 255));
    }
}
