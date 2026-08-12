#!/usr/bin/env python3
"""Print the full transport-decrypted plaintext of the first peer audio packet and our own."""
import re, sys, struct
import nacl.bindings

def main(path):
    log = open(path, "rb").read().decode("utf-8", "replace")
    key = None
    m = re.search(r"transport key=([0-9A-F]{64})", log)
    if m: key = bytes.fromhex(m.group(1))

    def show(prefix, plen, hx, label):
        if len(hx) // 2 < plen: return
        pkt = bytes.fromhex(hx)
        if len(pkt) < 36: return
        hdr = pkt[:16]
        if hdr[0] != 0x90 or hdr[12:14] != b"\xbe\xde": return
        cttag = pkt[16:-4]; trailer = pkt[-4:]
        nonce = trailer + b"\x00" * 20
        try:
            pt = nacl.bindings.crypto_aead_xchacha20poly1305_ietf_decrypt(cttag, hdr, nonce, key)
        except Exception as e:
            print(f"{label}: transport FAIL {e}")
            return
        print(f"{label}: plaintext {len(pt)}B: {pt.hex()}")
        print(f"           last16: {pt[-16:].hex()}")

    n = 0
    for line in log.splitlines():
        m = re.search(r"udp rx (\d+)B ([0-9A-F]+)", line)
        if not m: continue
        n += 1
        show("rx", int(m.group(1)), m.group(2), f"peer #{n}")
        if n >= 2: break
    n = 0
    for line in log.splitlines():
        m = re.search(r"udp tx (\d+)B ([0-9A-F]+)", line)
        if not m: continue
        n += 1
        show("tx", int(m.group(1)), m.group(2), f"our #{n}")
        if n >= 3: break

if __name__ == "__main__":
    main(sys.argv[1] if len(sys.argv) > 1 else "bin/Debug/net8.0-windows/debug.log")
