import re
from cryptography.hazmat.primitives.ciphers.aead import ChaCha20Poly1305

log = open('bin/Debug/net8.0-windows/debug.log', 'rb').read().replace(b'\x00', b'')
text = log.decode('utf-8', errors='replace')

key = bytes.fromhex(re.search(r'transport key=?\s*([0-9A-F]{64})', text, re.I).group(1))

def try_decrypt(b, nonce, aad, body):
    try:
        return ChaCha20Poly1305(key).decrypt(nonce, body, aad)
    except Exception:
        return None

# Use the peer's first packet (47B silence, trailer 00000000) and a later one (trailer 01000000)
packets = []
for m in re.finditer(r'udp rx (\d+)B ([0-9A-F]+)', text):
    n, h = int(m.group(1)), m.group(2)
    if len(h) < n * 2: continue
    b = bytes.fromhex(h)
    if len(b) >= 12 and b[1] not in (0xC8, 0xC9):
        packets.append(b)
    if len(packets) >= 4: break

for b in packets:
    print("packet len", len(b), "seq", int.from_bytes(b[2:4], 'big'), "trailer", b[-4:].hex())
    for hlen in (12, 16):
        header = b[:hlen]
        body = b[hlen:-4]
        trailer = b[-4:]
        nonces = {
            'copy-trailer': trailer + b'\x00'*20,
            'reversed': trailer[::-1] + b'\x00'*20,
            'le-value-be-write': int.from_bytes(trailer,'little').to_bytes(4,'big') + b'\x00'*20,
            'be-value-le-write': int.from_bytes(trailer,'big').to_bytes(4,'little') + b'\x00'*20,
        }
        for nname, nonce in nonces.items():
            for aname, aad in [('hdr16', header if hlen==16 else b[:16]), ('hdr12', header if hlen==12 else b[:12]), ('hdr+trailer', header + trailer)]:
                pt = try_decrypt(b, nonce, aad, body)
                if pt:
                    print(f"  *** SUCCESS hlen={hlen} nonce={nname} aad={aname} pt={pt[:20].hex()}")
