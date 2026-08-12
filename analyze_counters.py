import re, sys

log = open('bin/Debug/net8.0-windows/debug.log', 'rb').read()
log = log.replace(b'\x00', b'')
text = log.decode('utf-8', errors='replace')

def split_packet(hexstr):
    b = bytes.fromhex(hexstr)
    if len(b) < 12: return None
    # RTP header
    seq = (b[2] << 8) | b[3]
    ts = int.from_bytes(b[4:8], 'big')
    ssrc = int.from_bytes(b[8:12], 'big')
    hlen = 12
    if (b[0] & 0x10) and len(b) >= 16:
        hlen = 16
    counter_be = int.from_bytes(b[-4:], 'big')
    counter_le = int.from_bytes(b[-4:], 'little')
    return dict(seq=seq, ts=ts, ssrc=ssrc, hlen=hlen, total=len(b),
                trailer=b[-4:].hex(), cbe=counter_be, cle=counter_le)

print("=== RX (peer -> us) ===")
rx_seqs = []
for m in re.finditer(r'udp rx (\d+)B ([0-9A-F]+)', text):
    n, h = int(m.group(1)), m.group(2)
    # log may be truncated at 300 chars; only parse complete packets
    if len(h) < n * 2: continue
    p = split_packet(h)
    if p is None: continue
    if p['hlen'] != 16: continue  # media only
    rx_seqs.append(p)
    if len(rx_seqs) <= 10:
        print(f"len={n} seq={p['seq']:04X} ts={p['ts']:08X} ssrc={p['ssrc']:08X} trailer={p['trailer']} cBE={p['cbe']:08X} cLE={p['cle']:08X}")

print("\n=== TX (us -> peer) ===")
tx_seqs = []
for m in re.finditer(r'udp tx (\d+)B ([0-9A-F]+)', text):
    n, h = int(m.group(1)), m.group(2)
    if len(h) < n * 2: continue
    p = split_packet(h)
    if p is None: continue
    if p['hlen'] != 16: continue
    tx_seqs.append(p)
    if len(tx_seqs) <= 10:
        print(f"len={n} seq={p['seq']:04X} ts={p['ts']:08X} ssrc={p['ssrc']:08X} trailer={p['trailer']} cBE={p['cbe']:08X} cLE={p['cle']:08X}")

print(f"\nrx media packets parsed: {len(rx_seqs)}, tx: {len(tx_seqs)}")

# counter progression analysis: does the peer's counter increment by 1 (LE or BE)?
print("\n=== peer trailer sequence (first 15) ===")
for i, p in enumerate(rx_seqs[:15]):
    print(f"  #{i}: trailer={p['trailer']}  asLE={p['cle']}  asBE={p['cbe']}")

print("\n=== our trailer sequence (first 15) ===")
for i, p in enumerate(tx_seqs[:15]):
    print(f"  #{i}: trailer={p['trailer']}  asLE={p['cle']}  asBE={p['cbe']}")
