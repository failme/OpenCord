# Exhaustive nonce brute-force for Discord aead_xchacha20_poly1305_rtpsize.
# Usage: python brute_nonce.py <debug.log>
import sys, re, itertools
from pathlib import Path
from nacl.secret import Aead

def parse(logpath):
    lines = Path(logpath).read_text(errors='ignore').splitlines()
    key = None
    rxs = []
    for ln in lines:
        m = re.search(r'key=([0-9A-Fa-f]{64})', ln)
        if m: key = bytes.fromhex(m.group(1))
        m = re.search(r'udp rx (\d+)B ([0-9A-Fa-f]+)', ln)
        if m: rxs.append(bytes.fromhex(m.group(2)))
    return key, rxs

def try_dec(key, nonce, aad, ct_tag):
    box = Aead(bytes(key))
    try:
        return box.decrypt(bytes(ct_tag), bytes(aad), bytes(nonce))
    except Exception:
        return None

def main():
    logpath = sys.argv[1] if len(sys.argv) > 1 else 'bin/Debug/net8.0-windows/debug.log'
    key, rxs = parse(logpath)
    if not key:
        print('no key'); return
    print(f'key={key.hex().upper()}')
    # use the first 47B audio packet (12B RTP header) and the first 52B packet (8B RTCP header)
    targets = []
    for pkt in rxs:
        if len(pkt) == 47 and pkt[1] == 0x78:
            targets.append(('audio', pkt)); break
    for pkt in rxs:
        if len(pkt) == 52:
            targets.append(('rtcp', pkt)); break
    if not targets:
        print('no target packets'); return

    for name, pkt in targets:
        print(f'\n=== {name} packet len={len(pkt)} ===')
        hdr_len = 8 if pkt[1] == 0xC9 else 12
        hdr = pkt[:hdr_len]
        body = pkt[hdr_len:-4]
        trailer = pkt[-4:]
        ctr_raw = trailer
        ctr_le_int = int.from_bytes(trailer, 'little')
        ctr_be_int = int.from_bytes(trailer, 'big')
        print(f'hdr={hdr.hex().upper()} ctr={ctr_raw.hex().upper()} (le={ctr_le_int} be={ctr_be_int})')
        print(f'body={len(body)}B')

        counter_forms = {
            'raw': ctr_raw,
            'beofle': ctr_le_int.to_bytes(4, 'big'),
            'beofbe': ctr_be_int.to_bytes(4, 'big'),
            'leofbe': ctr_be_int.to_bytes(4, 'little'),
            'zero': b'\x00\x00\x00\x00',
        }
        hdr12 = pkt[:12]
        nonce_templates = []
        for cn, c4 in counter_forms.items():
            for pos in (0, 4, 8, 12, 16, 20):
                n = bytearray(24)
                n[pos:pos+4] = c4
                nonce_templates.append((f'c@{pos}({cn})', bytes(n)))
            nonce_templates.append((f'hdr12+c({cn})', hdr12 + c4 + bytes(8)))
            nonce_templates.append((f'hdr12+8z+c({cn})', hdr12 + bytes(8) + c4))
            nonce_templates.append((f'c({cn})+hdr12+8z', c4 + hdr12 + bytes(8)))
            nonce_templates.append((f'8z+hdr12+c({cn})', bytes(8) + hdr12 + c4))
            nonce_templates.append((f'hdr8+c({cn})+12z', pkt[:8] + c4 + bytes(12)))
        # de-dup
        seen = {}
        for t in nonce_templates:
            seen.setdefault(t[1], t[0])
        nonce_templates = [(desc, n) for n, desc in seen.items()]

        aads = {
            'hdr': hdr,
            'hdr12': hdr12,
            'empty': b'',
            'hdr+ctr': hdr + trailer,
        }
        found = []
        for ndesc, nonce in nonce_templates:
            for adesc, aad in aads.items():
                pt = try_dec(key, nonce, aad, body)
                if pt is not None:
                    found.append((ndesc, adesc, pt))
        if found:
            for ndesc, adesc, pt in found[:5]:
                print(f'  *** DECRYPTED nonce={ndesc} aad={adesc}: {pt[:64].hex()} ({len(pt)}B)')
        else:
            print('  no match')

if __name__ == '__main__':
    main()
