#!/usr/bin/env python3
"""Deep-dive a peer audio frame: parse, print fields, compute standard GCM tag over
the parsed ct and compare with the frame's 8-byte tag."""
import re, sys, struct
import nacl.bindings
from cryptography.hazmat.primitives.ciphers import Cipher, algorithms, modes

def main(path):
    log = open(path, "rb").read().decode("utf-8", "replace")
    key = None
    m = re.search(r"transport key=([0-9A-F]{64})", log)
    if m: key = bytes.fromhex(m.group(1))
    gen0 = {}
    for m in re.finditer(r"recv ratchet user=(\d+) gen0=([0-9A-F]{32})", log):
        gen0[int(m.group(1))] = bytes.fromhex(m.group(2))

    def gcm_tag(key, nonce, aad, ct):
        enc = Cipher(algorithms.AES(key), modes.GCM(nonce)).encryptor()
        enc.authenticate_additional_data(aad)
        out = enc.update(ct) + enc.finalize()
        return out

    def try_dec(key, nonce, ct, want_tag):
        try:
            dec = Cipher(algorithms.AES(key), modes.GCM(nonce, want_tag)).decryptor()
            return dec.update(ct) + dec.finalize()
        except Exception as e:
            return None

    n = 0
    for line in log.splitlines():
        m = re.search(r"udp rx (\d+)B ([0-9A-F]+)", line)
        if not m: continue
        plen = int(m.group(1)); hx = m.group(2)
        if len(hx) // 2 < plen: continue
        pkt = bytes.fromhex(hx)
        if len(pkt) < 36: continue
        hdr = pkt[:16]
        if hdr[0] != 0x90 or hdr[12:14] != b"\xbe\xde": continue
        cttag = pkt[16:-4]; trailer = pkt[-4:]
        nonce = trailer + b"\x00" * 20
        try:
            pt = nacl.bindings.crypto_aead_xchacha20poly1305_ietf_decrypt(cttag, hdr, nonce, key)
        except Exception:
            continue
        if len(pt) <= 20: continue     # silence only
        n += 1
        body = pt[8:] if pt[0] == 0x32 and pt[6] == 0x90 else pt
        print(f"peer#{n} plen={len(pt)} hdr={pt[:8].hex() if pt[0]==0x32 else '?'}")
        print(f"  frame({len(body)}B): {body.hex()}")
        size = body[-3]
        supp = body[-size:]
        tag = supp[:8]
        nb = supp[8:-3]
        val = 0; shift = 0; i = 0
        while i < len(nb) and i < 5:
            b = nb[i]; i += 1
            val |= (b & 0x7F) << shift
            if not (b & 0x80): break
            shift += 7
        ct = body[:-size]
        print(f"  size={size} counter={val} ct={len(ct)}B tag={tag.hex()}")
        # try every gen0 key with LE counter nonce
        for u, k in gen0.items():
            n12 = b"\x00" * 8 + struct.pack("<I", val & 0xFFFFFFFF)
            out = try_dec(k, n12, ct, tag)
            if out is not None:
                print(f"  >>> DECRYPTED key=uid{u} pt={out[:20].hex()} (len {len(out)})")
            else:
                # compute what the standard GCM tag WOULD be, to see how far off
                t = gcm_tag(k, n12, b"", ct)
                print(f"  key=uid{u}: no. std tag(16)={t.hex()} first8={t[:8].hex()}")
        if n >= 1: break

if __name__ == "__main__":
    main(sys.argv[1] if len(sys.argv) > 1 else "bin/Debug/net8.0-windows/debug.log")
