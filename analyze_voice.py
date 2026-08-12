# Offline decoder for captured Discord voice packets.
# Usage: python analyze_voice.py <debug.log>
# Extracts the transport key hex line + first ~20 udp rx packets, then tries every
# plausible combination of cipher (xchacha/aes), nonce layout, and AAD against them.
import sys, re
from pathlib import Path

def try_xchacha(key, nonce, aad, ct_tag):
    # pure-python fallback is slow; use cryptography lib if available
    from cryptography.hazmat.primitives.ciphers.aead import ChaCha20Poly1305
    # XChaCha20-Poly1305: subkey = HChaCha20(key, nonce[:16]), then ChaCha20-Poly1305 with subkey + nonce[16:]
    # cryptography's ChaCha20Poly1305 only does 12-byte nonce; implement XChaCha via internal API below.
    from cryptography.hazmat.primitives.ciphers import aead as _aead
    # Fallback: use PyNaCl Aead (exact same primitive Discord uses)
    from nacl.secret import Aead
    try:
        box = Aead(bytes(key))
        ct = box.decrypt(bytes(ct_tag), bytes(aad), bytes(nonce))
        return ct
    except Exception:
        return None

def try_aes_gcm(key, nonce, aad, ct_tag):
    from cryptography.hazmat.primitives.ciphers.aead import AESGCM
    aes = AESGCM(bytes(key))
    try:
        return aes.decrypt(bytes(nonce), bytes(ct_tag), bytes(aad))
    except Exception:
        return None

def parse(logpath):
    lines = Path(logpath).read_text(errors='ignore').splitlines()
    key = None
    rxs = []
    for ln in lines:
        if 'transport key=' in ln:
            m = re.search(r'key=([0-9A-Fa-f]+)', ln)
            if m: key = bytes.fromhex(m.group(1))
        m = re.search(r'udp rx (\d+)B ([0-9A-Fa-f]+)', ln)
        if m:
            rxs.append(bytes.fromhex(m.group(2)))
    return key, rxs

def main():
    logpath = sys.argv[1] if len(sys.argv) > 1 else 'bin/Debug/net8.0-windows/debug.log'
    key, rxs = parse(logpath)
    print(f'key len={len(key) if key else 0}: {(key.hex().upper() if key else "MISSING")}')
    if not key:
        print('No transport key line found - run once with the new build.')
        return
    print(f'{len(rxs)} rx packets captured\n')
    if not rxs:
        return
    # hypothesis matrix
    hyps = []
    for i, pkt in enumerate(rxs[:10]):
        if len(pkt) < 32: continue
        hdr = pkt[:12]
        body = pkt[12:-4]
        trailer = pkt[-4:]
        ctr_le = int.from_bytes(trailer, 'little')
        # nonce layouts: counter variants + header-in-nonce variants
        nonces = [
            ('ctr_raw',    trailer + bytes(20)),
            ('ctr_be_le',  ctr_le.to_bytes(4, 'big') + bytes(20)),
            ('ctr_le_le',  ctr_le.to_bytes(4, 'little') + bytes(20)),
            ('hdr+ctr',    hdr + trailer + bytes(8)),
            ('ctr+hdr',    trailer + hdr + bytes(8)),
            ('hdr+zero',   hdr + bytes(12)),
        ]
        aads = [('hdr12', hdr), ('empty', b''), ('hdr+trailer', hdr + trailer)]
        for nname, nonce in nonces:
            for aname, aad in aads:
                pt = try_xchacha(key, nonce, aad, body)
                if pt is not None:
                    hyps.append((i, f'xchacha nonce={nname} aad={aname}', pt))
                pt = try_aes_gcm(key, nonce, aad, body)
                if pt is not None:
                    hyps.append((i, f'aes256 nonce={nname} aad={aname}', pt))
    if hyps:
        seen = set()
        for i, desc, pt in hyps:
            tag = (i, desc)
            if tag in seen: continue
            seen.add(tag)
            print(f'*** SUCCESS pkt#{i}: {desc} -> {pt[:64].hex()} ({len(pt)}B plaintext)')
    else:
        print('No hypothesis decrypted any packet. Key is wrong OR layout differs fundamentally.')
        for i, pkt in enumerate(rxs[:10]):
            print(f'pkt#{i} len={len(pkt)} hdr={pkt[:12].hex().upper()} trailer={pkt[-4:].hex().upper()} ct={len(pkt)-16}B')

if __name__ == '__main__':
    main()
