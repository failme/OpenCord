#!/usr/bin/env python3
"""Decrypt every captured audio RTP packet with the logged transport key and
dump the 8-byte audio-extension prefix + the DAVE frame tail, so we can decode
the prefix format (seq/ts/flags) across many frames."""
import re, sys
from cryptography.hazmat.primitives.ciphers.aead import AESGCM

def main(path):
    log = open(path, "rb").read().decode("utf-8", "replace")
    key = None
    m = re.search(r"transport key=([0-9A-F]{64})", log)
    if m: key = bytes.fromhex(m.group(1))
    print(f"transport key: {key.hex() if key else 'NOT FOUND'}")

    packets = []
    for line in log.splitlines():
        m = re.search(r"udp rx (\d+)B ([0-9A-F]+)", line)
        if m:
            n = int(m.group(1))
            hx = m.group(2)
            # hex may be truncated at 96 bytes; only use complete packets
            if len(hx) // 2 < n: continue
            packets.append(bytes.fromhex(hx))
    print(f"complete udp rx packets: {len(packets)}")

    rows = []
    for pkt in packets:
        if len(pkt) < 12 + 16 + 4 + 4: continue
        hdr = pkt[:16]                     # 12 RTP + 4 BEDE0002
        if hdr[12:14] != b"\xbe\xde": continue  # skip RTCP etc
        ct = pkt[16:-4]
        ctr = pkt[-4:]
        nonce = b"\x00" * 8 + ctr
        try:
            pt = AESGCM(key).decrypt(nonce, ct, hdr)
        except Exception:
            continue
        # RTP header fields
        seq = int.from_bytes(pkt[2:4], "big")
        ts = int.from_bytes(pkt[4:8], "big")
        ssrc = int.from_bytes(pkt[8:12], "big")
        pt_byte = pkt[1] & 0x7F
        marker = bool(pkt[1] & 0x80)
        prefix = pt[:8]
        rest = pt[8:]
        rows.append((seq, ts, ssrc, pt_byte, marker, prefix, rest))
    rows.sort(key=lambda r: r[1])
    print(f"decrypted audio payloads: {len(rows)}\n")
    for seq, ts, ssrc, ptb, marker, prefix, rest in rows[:40]:
        tail = rest[-min(len(rest), 12):].hex()
        print(f"seq={seq:5d} ts={ts:10d} pt={ptb:3d} m={int(marker)} "
              f"prefix={prefix.hex()} rest_len={len(rest):3d} tail={tail}")

    # Analyze prefix fields across consecutive frames
    print("\n--- prefix field analysis (first 12, sorted by ts) ---")
    for seq, ts, ssrc, ptb, marker, prefix, rest in rows[:12]:
        b = prefix
        print(f"  {b.hex()}  b2b3={int.from_bytes(b[2:4],'big'):6d} "
              f"b2b3LE={int.from_bytes(b[2:4],'little'):6d} "
              f"b1={b[1]:02x} b5={b[5]:02x} b7={b[7]:02x} seq={seq} ts={ts}")

if __name__ == "__main__":
    main(sys.argv[1] if len(sys.argv) > 1 else "bin/Debug/net8.0-windows/debug.log")
