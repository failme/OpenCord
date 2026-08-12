import re
from nacl.bindings import crypto_aead_xchacha20poly1305_ietf_decrypt

log = open('bin/Debug/net8.0-windows/debug.log', 'rb').read().replace(b'\x00', b'')
text = log.decode('utf-8', errors='replace')
key = bytes.fromhex(re.search(r'transport key=\s*([0-9A-F]{64})', text, re.I).group(1))

def decrypt(b, nonce_mode='copy'):
    if len(b) < 12 or b[1] in (0xC8, 0xC9): return None
    hlen = 16 if (b[0] & 0x10) else 12
    header = b[:hlen]
    body = b[hlen:-4]
    trailer = b[-4:]
    if nonce_mode == 'copy':
        nonce = trailer + b'\x00'*20
    elif nonce_mode == 'le':
        nonce = int.from_bytes(trailer, 'little').to_bytes(4, 'big') + b'\x00'*20
    elif nonce_mode == 'be':
        nonce = int.from_bytes(trailer, 'big').to_bytes(4, 'big') + b'\x00'*20
    else:
        nonce = b'\x00'*24
    try:
        return crypto_aead_xchacha20poly1305_ietf_decrypt(body, header, nonce, key)
    except Exception:
        return None

for mode in ['copy', 'le', 'be']:
    ok = 0; total = 0
    rx = []
    for m in re.finditer(r'udp rx (\d+)B ([0-9A-F]+)', text):
        n, h = int(m.group(1)), m.group(2)
        if len(h) < n*2: continue
        b = bytes.fromhex(h)
        pt = decrypt(b, mode)
        total += 1
        if pt: ok += 1
    print(f"rx nonce={mode}: {ok}/{total} decrypted")
    for m in re.finditer(r'udp tx (\d+)B ([0-9A-F]+)', text):
        n, h = int(m.group(1)), m.group(2)
        if len(h) < n*2: continue
        b = bytes.fromhex(h)
        pt = decrypt(b, mode)
        total += 1
        if pt: ok += 1
    print(f"rx+tx nonce={mode}: {ok}/{total}")

print("\n=== detailed (nonce=copy) ===")
for tag, pat in [('PEER','udp rx'), ('OURS','udp tx')]:
    print(f"--- {tag} ---")
    for m in re.finditer(pat + r' (\d+)B ([0-9A-F]+)', text):
        n, h = int(m.group(1)), m.group(2)
        if len(h) < n*2:
            print(f"  len={n} (truncated)")
            continue
        b = bytes.fromhex(h)
        pt = decrypt(b, 'copy')
        if pt is None:
            print(f"  len={n} seq={int.from_bytes(b[2:4],'big'):04X} FAIL")
        else:
            hdr = pt[:8] if len(pt) >= 8 else pt
            print(f"  len={n} seq={int.from_bytes(b[2:4],'big'):04X} ts={int.from_bytes(b[4:8],'big'):08X} pt={hdr.hex()} rest={pt[8:16].hex() if len(pt)>=16 else ''} (total {len(pt)})")
