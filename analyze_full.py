#!/usr/bin/env python3
"""Offline DAVE media-key forensics v2.

Uses the app's debug.log:
1. Extracts the MLS exporter secret, per-user base secrets, and ratchet gen0 keys.
2. For every 'e2ee frame rx' line (now logged PRE-strip at full length, with ssrc),
   strips the 8-byte Discord audio extension header, parses the DAVE frame
   ([ct][tag8][uleb nonce][ranges][size][FAFA]) and tries AES-128-GCM with the
   logged ratchet gen0 keys.
3. Also transport-decrypts every complete 'udp rx' audio packet (XChaCha20) and
   runs the same DAVE attempt, so we can cross-check without relying on the
   app's own strip.

Prints exactly which stage fails so we can pinpoint any remaining mismatch.
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
    bases = {}
    for m in re.finditer(r"mls base user=(\d+) secret=([0-9A-F]{32,64})", log):
        bases[int(m.group(1))] = bytes.fromhex(m.group(2))
    exporter = None
    m = re.search(r"mls epoch=\d+ exporter=([0-9A-F]{64})", log)
    if m: exporter = bytes.fromhex(m.group(1))

    print(f"transport key: {'yes' if key else 'NO'}")
    print(f"exporter: {'yes' if exporter else 'NO'}")
    print(f"base secrets: {list(bases.keys())}")
    print(f"ratchet gen0: { {k: v.hex()[:8] for k, v in gen0.items()} }")

    def dave_attempt(label, frame):
        """frame = DAVE frame (header already stripped). Tries both gen0 keys."""
        if len(frame) < 12:
            print(f"  {label}: short frame len={len(frame)}")
            return
        if frame[-1] != 0xFA or frame[-2] != 0xFA:
            print(f"  {label}: NO FAFA footer (tail {frame[-8:].hex()})")
            return
        size = frame[-3]
        if size > len(frame):
            print(f"  {label}: bad supplement size {size}")
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
        for u, k in gen0.items():
            nonce12 = b"\x00" * 8 + struct.pack("<I", val)
            try:
                out = AESGCM(k).decrypt(nonce12, ct + tag, b"")
                print(f"  {label}: counter={val} gen={val >> 24} KEY uid={u} "
                      f"-> DECRYPTED ({len(out)}B) pt={out[:24].hex()}")
                return
            except Exception:
                pass
        print(f"  {label}: counter={val} gen={val >> 24} no key worked "
              f"(ct_len={len(ct)} tag={tag.hex()} suppsize={size})")

    # --- path 1: e2ee frame rx lines (full plaintext, logged pre-strip) ---
    print("\n=== e2ee frame rx lines (app's own transport decrypt) ===")
    rxlines = [l for l in log.splitlines() if "e2ee frame rx" in l]
    for l in rxlines[:10]:
        m = re.search(r"e2ee frame rx (?:(\d+) )?([0-9A-F]+)", l)
        if not m: continue
        ssrc = m.group(1) or "?"
        body = bytes.fromhex(m.group(2))
        if len(body) >= 8 and body[0] == 0x32 and body[6] == 0x90:
            hdr, frame = body[:8], body[8:]
            dave_attempt(f"ssrc={ssrc} hdr={hdr.hex()}", frame)
        else:
            dave_attempt(f"ssrc={ssrc} no-hdr-sig", body)

    # --- path 1b: our own tx frames (self-consistency check) ---
    print("\n=== our own e2ee tx frames (should decrypt with OUR OWN uid gen0) ===")
    for l in log.splitlines():
        if "e2ee frame sent" not in l: continue
        m = re.search(r"e2ee frame sent ([0-9A-F]+)", l)
        if not m: continue
        frame = bytes.fromhex(m.group(1))
        if len(frame) >= 8 and frame[0] == 0x32 and frame[6] == 0x90:
            hdr, frame = frame[:8], frame[8:]  # in case header is present in the log
            print(f"  tx had 8-byte header {hdr.hex()}")
        dave_attempt("tx", frame)

    # --- path 2: full transport decrypt of complete udp rx packets ---
    print("\n=== transport-decrypted peer packets (XChaCha20) ===")
    def xdecrypt(ct_tag, aad, nonce):
        import nacl.bindings
        return nacl.bindings.crypto_aead_xchacha20poly1305_ietf_decrypt(ct_tag, aad, nonce, key)

    n = 0
    for line in log.splitlines():
        m = re.search(r"udp rx (\d+)B ([0-9A-F]+)", line)
        if not m: continue
        plen = int(m.group(1)); hx = m.group(2)
        if len(hx) // 2 < plen: continue          # truncated hex: skip
        pkt = bytes.fromhex(hx)
        if len(pkt) < 16 + 4 + 16: continue
        hdr = pkt[:16]
        if hdr[12:14] != b"\xbe\xde": continue    # RTCP / non-audio
        cttag = pkt[16:-4]; trailer = pkt[-4:]
        nonce = trailer + b"\x00" * 20
        try:
            pt = xdecrypt(cttag, hdr, nonce)
        except Exception:
            continue
        ssrc = int.from_bytes(pkt[8:12], "big")
        n += 1
        if n > 10: break
        if len(pt) >= 8 and pt[0] == 0x32 and pt[6] == 0x90:
            hdr, frame = pt[:8], pt[8:]
            dave_attempt(f"xchacha ssrc={ssrc} hdr={hdr.hex()}", frame)
        else:
            dave_attempt(f"xchacha ssrc={ssrc} no-hdr-sig", pt)
    if n == 0:
        print("  (no complete packets in log — need a run with full-length logging)")

if __name__ == "__main__":
    main(sys.argv[1] if len(sys.argv) > 1 else "bin/Debug/net8.0-windows/debug.log")
