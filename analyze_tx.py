#!/usr/bin/env python3
"""Offline self-consistency check for OUR OWN transmitted audio.

Decrypts our own 'udp tx' packets (transport XChaCha20), strips the 8-byte audio
header, parses the DAVE frame, and tries AES-128-GCM with the logged ratchet keys.
If our frames decrypt with our OWN uid's ratchet, our crypto is self-consistent
and the peer-side failure is elsewhere (MLS state / peer parsing). If they don't,
our ProtectFrame / ratchet is broken.
"""
import re, sys, struct
from cryptography.hazmat.primitives.ciphers.aead import AESGCM

def main(path):
    log = open(path, "rb").read().decode("utf-8", "replace")
    key = None
    m = re.search(r"transport key=([0-9A-F]{64})", log)
    if m: key = bytes.fromhex(m.group(1))
    gen0 = {}
    for m in re.finditer(r"recv ratchet user=(\d+) gen0=([0-9A-F]{32})", log):
        gen0[int(m.group(1))] = bytes.fromhex(m.group(2))
    print(f"transport key: {'yes' if key else 'NO'}")
    print(f"ratchet gen0: { {k: v.hex()[:8] for k, v in gen0.items()} }")
    if not key or not gen0:
        print("missing keys — abort")
        return

    # our own uid = the one whose token we're logged in as (selfBase matches a base secret)
    # try every gen0 key anyway.

    def dave_decrypt(frame, label):
        if len(frame) < 14:
            print(f"  {label}: short frame len={len(frame)}")
            return
        if frame[-1] != 0xFA or frame[-2] != 0xFA:
            print(f"  {label}: NO FAFA footer, tail={frame[-12:].hex()}")
            return
        size = frame[-3]
        if size == 0 or size > 16 or size > len(frame):
            print(f"  {label}: bad supplement size {size} tail={frame[-12:].hex()}")
            return
        supp = frame[-size:]
        tag = supp[:8]
        n = supp[8:-3]
        # uleb128 counter
        val = 0; shift = 0; i = 0
        while i < len(n) and i < 5:
            b = n[i]; i += 1
            val |= (b & 0x7F) << shift
            if not (b & 0x80): break
            shift += 7
        ct = frame[:-size]
        # also try: supplement = tag only + full nonce 12 bytes elsewhere?
        for u, k in gen0.items():
            nonce12 = b"\x00" * 8 + struct.pack("<I", val & 0xFFFFFFFF)
            try:
                out = AESGCM(k).decrypt(nonce12, ct + tag, b"")
                print(f"  {label}: DECRYPTED counter={val} key=uid{u} pt={len(out)}B {out[:20].hex()}")
                return True
            except Exception:
                pass
        print(f"  {label}: counter={val} NO KEY WORKED (ct={len(ct)} tag={tag.hex()} suppsize={size} nonceval={val})")
        return False

    print("\n=== our own udp tx packets (transport-decrypt) ===")
    n = 0
    ok = 0
    for line in log.splitlines():
        m = re.search(r"udp tx (\d+)B ([0-9A-F]+)", line)
        if not m: continue
        plen = int(m.group(1)); hx = m.group(2)
        if len(hx) // 2 < plen: continue      # truncated: skip
        pkt = bytes.fromhex(hx)
        if len(pkt) < 16 + 4 + 16: continue
        hdr = pkt[:16]
        if hdr[0] != 0x90 or hdr[12:14] != b"\xbe\xde": continue   # audio only
        cttag = pkt[16:-4]; trailer = pkt[-4:]
        nonce = trailer + b"\x00" * 20
        try:
            import nacl.bindings
            pt = nacl.bindings.crypto_aead_xchacha20poly1305_ietf_decrypt(cttag, hdr, nonce, key)
        except Exception:
            print(f"  {label(pkt)} TRANSPORT FAIL")
            continue
        n += 1
        label = f"seq={int.from_bytes(pkt[2:4],'big')} ts={int.from_bytes(pkt[4:8],'big')} counter={int.from_bytes(trailer,'big')}"
        if len(pt) >= 8 and pt[0] == 0x32 and pt[6] == 0x90:
            hdr8, frame = pt[:8], pt[8:]
            print(f"  {label} hdr={hdr8.hex()}")
            if dave_decrypt(frame, label): ok += 1
        else:
            print(f"  {label} NO-HDR-SIG pt={pt[:20].hex()}")
            if dave_decrypt(pt, label): ok += 1
        if n >= 6: break
    print(f"\nchecked {n} tx packets, {ok} decrypted")

if __name__ == "__main__":
    main(sys.argv[1] if len(sys.argv) > 1 else "bin/Debug/net8.0-windows/debug.log")
