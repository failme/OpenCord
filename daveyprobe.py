# daveyprobe.py — real DAVE handshake with the working davey client.
# usage: python daveyprobe.py <token> <channel_id> <guild_id> [label]
import asyncio, json, socket, struct, sys, time
import websockets
import davey

TOKEN = sys.argv[1]
CHANNEL = int(sys.argv[2])
GUILD = int(sys.argv[3]) if len(sys.argv) > 3 and sys.argv[3] != "0" else 0
LABEL = sys.argv[4] if len(sys.argv) > 4 else "probe"

dave_sess = None
uid = None

def log(*a):
    print(f"[{LABEL}]", *a, flush=True)

async def udp_discovery(host, port, ssrc):
    # Unconnected socket + sendto/recvfrom: the discovery reply may arrive from the same
    # host:port, but recv on a *connected* socket filters by peer, so use recvfrom.
    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    sock.bind(('0.0.0.0', 0))
    local = sock.getsockname()
    sock.settimeout(4)
    req = struct.pack('>HHI', 0x0001, 0x0046, ssrc) + bytes(68)
    sock.sendto(req, (host, port))
    try:
        resp, _ = sock.recvfrom(1024)
        ipb = resp[8:72]
        end = ipb.find(b'\x00')
        if end < 0: end = len(ipb)
        ip = ipb[:end].decode('ascii', 'replace')
        p = struct.unpack('>H', resp[72:74])[0]
        return ip, p
    except socket.timeout:
        # mirror C# UdpVoice: fall back to the local endpoint address
        return local[0], port
    finally:
        sock.close()

async def voice_connect(endpoint, vs_session, vs_token):
    global dave_sess
    log(f"voice ws -> wss://{endpoint}?v=8")
    async with websockets.connect(f'wss://{endpoint}?v=8', max_size=2**26) as ws:
        ident = {'op': 0, 'd': {
            'server_id': str(GUILD if GUILD else CHANNEL),
            'user_id': uid,
            'session_id': vs_session,
            'token': vs_token,
            'max_dave_protocol_version': 1,
        }}
        log(f"SEND identify {json.dumps(ident)}")
        await ws.send(json.dumps(ident))
        ssrc = None
        hb_task = None
        while True:
            raw = await asyncio.wait_for(ws.recv(), timeout=120)
            if isinstance(raw, bytes):
                op = raw[2]
                payload = raw[3:]
                log(f"dave op {op} len={len(payload)} hex={payload.hex()[:120]}")
                try:
                    if op == 25:
                        if dave_sess is not None:
                            dave_sess.set_external_sender(payload)
                            log("external sender set")
                        else:
                            log("WARN op25 before session")
                    elif op == 27:
                        op_type = davey.ProposalsOperationType.append if payload[0] == 0 else davey.ProposalsOperationType.revoke
                        cw = dave_sess.process_proposals(op_type, payload[1:])
                        if cw is not None:
                            log(f"commit {len(cw.commit)}B welcome {len(cw.welcome) if cw.welcome else 0}B")
                            out = bytes([28]) + cw.commit + (cw.welcome or b'')
                            log(f"->gw op 28 len={len(out)} hex={out.hex()}")
                            await ws.send(out)
                        else:
                            log("proposals processed, no commit")
                    elif op == 29:
                        tid = struct.unpack('>H', payload[:2])[0]
                        log(f"op 29 announce tid={tid} commit {len(payload)-2}B hex={payload[2:].hex()[:120]}")
                        dave_sess.process_commit(payload[2:])
                        log("commit processed! epoch=%s" % dave_sess.epoch)
                    elif op == 30:
                        tid = struct.unpack('>H', payload[:2])[0]
                        log(f"op 30 welcome tid={tid} {len(payload)-2}B")
                        dave_sess.process_welcome(payload[2:])
                        log("welcome processed! epoch=%s ready=%s" % (dave_sess.epoch, dave_sess.ready))
                except Exception as e:
                    log(f"dave op {op} ERROR: {e}")
                continue
            m = json.loads(raw)
            op = m.get('op')
            log(f"voice json op {op} seq={m.get('seq')} d={json.dumps(m.get('d'))[:160]}")
            if op == 8:
                hb = m['d']['heartbeat_interval']
                async def beat():
                    try:
                        n = 0
                        while True:
                            await ws.send(json.dumps({'op': 3, 'd': {'t': n, 'seq_ack': 0}}))
                            n += 1
                            await asyncio.sleep(hb / 1000)
                    except Exception:
                        pass
                hb_task = asyncio.create_task(beat())
            elif op == 2:
                ssrc = m['d']['ssrc']
                ip0, port0 = m['d']['ip'], m['d']['port']
                modes = m['d']['modes']
                log(f"READY ssrc={ssrc} ip={ip0} port={port0} modes={modes}")
                ip, port = await udp_discovery(ip0, port0, ssrc)
                log(f"IP DISCOVERY -> {ip}:{port}")
                mode = 'aead_xchacha20_poly1305_rtpsize' if 'aead_xchacha20_poly1305_rtpsize' in modes else modes[0]
                sel = {'op': 1, 'd': {'protocol': 'udp', 'data': {'address': ip, 'port': port, 'mode': mode}}}
                log(f"SEND select {json.dumps(sel)}")
                await ws.send(json.dumps(sel))
            elif op == 4:
                mode = m['d']['mode']
                dave_ver = m['d'].get('dave_protocol_version', 0)
                log(f"SESSION DESCRIPTION mode={mode} dave={dave_ver}")
                if dave_ver and dave_sess is None:
                    dave_sess = davey.DaveSession(1, int(uid), CHANNEL)
                    kp = dave_sess.get_serialized_key_package()
                    out = bytes([26]) + kp
                    log(f"->gw op 26 len={len(out)} hex={out.hex()}")
                    await ws.send(out)

async def main():
    global uid
    async with websockets.connect('wss://gateway.discord.gg/?v=10&encoding=json', max_size=2**24) as ws:
        m = json.loads(await ws.recv())
        hb_iv = m['d']['heartbeat_interval']
        async def beat():
            try:
                n = 0
                while True:
                    await ws.send(json.dumps({'op': 1, 'd': n}))
                    n += 1
                    await asyncio.sleep(hb_iv / 1000)
            except Exception:
                pass
        asyncio.create_task(beat())
        await ws.send(json.dumps({'op': 2, 'd': {
            'token': TOKEN,
            'properties': {'os': 'windows', 'browser': 'daveyprobe', 'device': 'daveyprobe'},
            'intents': 1 | 128,  # GUILDS + GUILD_VOICE_STATES
        }}))
        vs_session = None
        vs_token = None
        vs_endpoint = None
        while True:
            m = json.loads(await ws.recv())
            t = m.get('t')
            if t not in ('VOICE_STATE_UPDATE', 'VOICE_SERVER_UPDATE', 'READY', 'GUILD_CREATE'):
                log(f"gw event {t}")
            if t == 'READY':
                uid = m['d']['user']['id']
                log(f"READY user={uid}")
                await ws.send(json.dumps({'op': 4, 'd': {
                    'guild_id': str(GUILD) if GUILD else None,
                    'channel_id': str(CHANNEL),
                    'self_mute': False, 'self_deaf': False,
                }}))
            elif t == 'VOICE_STATE_UPDATE':
                log(f"VOICE_STATE_UPDATE user={m['d'].get('user_id')} ch={m['d'].get('channel_id')} sess={'Y' if m['d'].get('session_id') else 'N'}")
                if m['d'].get('session_id'):
                    vs_session = m['d']['session_id']
                    log(f"VOICE_STATE_UPDATE session={vs_session}")
            elif t == 'VOICE_SERVER_UPDATE':
                vs_token = m['d']['token']
                vs_endpoint = m['d']['endpoint']
                log(f"VOICE_SERVER_UPDATE endpoint={vs_endpoint}")
            if vs_session and vs_endpoint:
                await voice_connect(vs_endpoint, vs_session, vs_token)
                return

asyncio.run(main())
