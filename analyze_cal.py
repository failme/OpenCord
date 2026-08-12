#!/usr/bin/env python3
"""Calibrate against the PEER's full frames via transport-decrypted udp rx packets."""
import re, sys, struct
import nacl.bindings
from cryptography.hazmat.primitives.ciphers import Cipher, algorithms, modes

def gcm_decrypt8(key, nonce, ct, tag):
    # DAVE frames carry an 8-byte GCM tag; cryptography's AEAD class only
    # supports 16-byte tags, so use the low-level modes.GCM with tag[:8].
    dec = Cipher(algorithms.AES(key), modes.GCM(nonce, tag)).decryptor()
    return dec.update(ct) + dec.finalize()

def main(path):
    log = open(path, "rb").read().decode("utf-8", "replace")
    key = None
    m = re.search(r"transport key=([0-9A-F]{64})", log)
    if m: key = bytes.fromhex(m.group(1))
    gen0 = {}
    for m in re.finditer(r"recv ratchet user=(\d+) gen0=([0-9A-F]{32})", log):
        gen0[int(m.group(1))] = bytes.fromhex(m.group(2))
    print(f"transport: {'yes' if key else 'NO'}; gen0: { {k: v.hex()[:8] for k, v in gen0.items()} }")
    if not key or not gen0: return

    def parse_try(frame, keys, label):
        if len(frame) < 14:
            print(f"  {label}: short len={len(frame)}")
            return
        if frame[-1] != 0xFA or frame[-2] != 0xFA:
            print(f"  {label}: NO FAFA tail={frame[-10:].hex()}")
            return
        size = frame[-3]
        if size < 11 or size > len(frame) - 3:
            print(f"  {label}: bad size={size} len={len(frame)}")
            return
        supp = frame[-size:]
        tag = supp[:8]
        n = supp[8:-3]
        val = 0; shift = 0; i = 0
        while i < len(n) and i < 5:
            b = n[i]; i += 1
            val |= (b & 0x7F) << shift
            if not (b & 0x80): break
            shift += 7
        ct = frame[:-size]
        for u, k in keys.items():
            nonce12 = b"\x00" * 8 + struct.pack("<I", val & 0xFFFFFFFF)
            try:
                out = gcm_decrypt8(k, nonce12, ct, tag)
                print(f"  {label}: OK counter={val} key=uid{u} pt={len(out)}B {out[:16].hex()}")
                return
            except Exception:
                pass
        print(f"  {label}: FAIL counter={val} ct={len(ct)} size={size} tag={tag.hex()}")

    print("\n=== peer udp rx audio packets ===")
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
        n += 1
        label = f"peer seq={int.from_bytes(pkt[2:4],'big')}"
        if len(pt) >= 8 and pt[0] == 0x32 and pt[6] == 0x90:
            parse_try(pt[8:], gen0, label)
        else:
            parse_try(pt, gen0, label)
        if n >= 4: break

    print("\n=== our own udp tx audio packets ===")
    n = 0
    for line in log.splitlines():
        m = re.search(r"udp tx (\d+)B ([0-9A-F]+)", line)
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
        n += 1
        label = f"our seq={int.from_bytes(pkt[2:4],'big')}"
        if len(pt) >= 8 and pt[0] == 0x32 and pt[6] == 0x90:
            parse_try(pt[8:], gen0, label)
        else:
            parse_try(pt, gen0, label)
        if n >= 4: break

if __name__ == "__main__":
    main(sys.argv[1] if len(sys.argv) > 1 else "bin/Debug/net8.0-windows/debug.log")
