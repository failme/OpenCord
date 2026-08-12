#!/usr/bin/env python3
"""Isolate why python GCM disagrees with the app's hand-rolled GCM on the peer frame."""
import re, sys, struct
from cryptography.hazmat.primitives.ciphers import Cipher, algorithms, modes

def gcm_encrypt8(key, nonce, pt):
    enc = Cipher(algorithms.AES(key), modes.GCM(nonce)).encryptor()
    ct = enc.update(pt) + enc.finalize()   # ct || tag(16)
    return ct[:len(pt)], ct[len(pt):]

def gcm_decrypt8(key, nonce, ct, tag):
    dec = Cipher(algorithms.AES(key), modes.GCM(nonce, tag)).decryptor()
    return dec.update(ct) + dec.finalize()

# Test 1: self round trip with 16-byte key, 12-byte nonce
key = bytes(range(16))
nonce = b"\x00" * 8 + struct.pack("<I", 34)
pt = b"hello dave frame test!"
ct, tag16 = gcm_encrypt8(key, nonce, pt)
out = gcm_decrypt8(key, nonce, ct, tag16)
print("roundtrip:", out == pt)

# Now the peer frame from the log (app says it decrypts with uid 8727 gen0)
gen0 = {}
log = open(sys.argv[1] if len(sys.argv) > 1 else "bin/Debug/net8.0-windows/debug.log", "rb").read().decode("utf-8", "replace")
for m in re.finditer(r"recv ratchet user=(\d+) gen0=([0-9A-F]{32})", log):
    gen0[int(m.group(1))] = bytes.fromhex(m.group(2))
import nacl.bindings
tk = re.search(r"transport key=([0-9A-F]{64})", log)
tkey = bytes.fromhex(tk.group(1))

# first full peer audio packet
for line in log.splitlines():
    m = re.search(r"udp rx (\d+)B ([0-9A-F]+)", line)
    if not m: continue
    plen = int(m.group(1)); hx = m.group(2)
    if len(hx) // 2 < plen: continue
    pkt = bytes.fromhex(hx)
    if len(pkt) < 36 or pkt[0] != 0x90 or pkt[12] != 0xBE: continue
    cttag = pkt[16:-4]; trailer = pkt[-4:]
    nonce24 = trailer + b"\x00" * 20
    try:
        pt = nacl.bindings.crypto_aead_xchacha20poly1305_ietf_decrypt(cttag, pkt[:16], nonce24, tkey)
    except Exception:
        continue
    if len(pt) <= 20: continue
    frame = pt[8:] if pt[0] == 0x32 and pt[6] == 0x90 else pt
    size = frame[-3]
    supp = frame[-size:]
    tag = supp[:8]
    val = 0; shift = 0; i = 0
    while i < len(supp[8:-3]) and i < 5:
        b = supp[8:-3][i]; i += 1
        val |= (b & 0x7F) << shift
        if not (b & 0x80): break
        shift += 7
    ct = frame[:-size]
    nonce12 = b"\x00" * 8 + struct.pack("<I", val & 0xFFFFFFFF)
    print(f"peer frame: counter={val} ct={len(ct)} tag={tag.hex()}")
    for u, k in gen0.items():
        try:
            out = gcm_decrypt8(k, nonce12, ct, tag)
            print(f"  DECRYPTED uid={u} pt={out[:20].hex()} len={len(out)}")
        except Exception as e:
            print(f"  uid={u} failed: {type(e).__name__}")
    # also show the tag the app computed: 78 80 3D 4B F5 3E 6A 60 ...
    print("  app-verified pt starts with: 78803d4bf53e6a60  (from C# dave-diag)")
    break
