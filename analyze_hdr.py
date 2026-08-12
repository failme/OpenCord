import re

log = open('bin/Debug/net8.0-windows/debug.log', 'rb').read().replace(b'\x00', b'')
text = log.decode('utf-8', errors='replace')

# Peer E2EE rx frames: payload after transport decrypt = [8-byte header][DAVE frame]
print("=== peer rx payload headers (from e2ee frame rx) ===")
for m in re.finditer(r'e2ee frame rx \d+ ([0-9A-F]+)', text):
    h = m.group(1)
    print(h[:16], " frame_len_bytes=", len(h)//2)
    break  # show all actually

print("\n=== ALL peer rx headers ===")
for m in re.finditer(r'e2ee frame rx \d+ ([0-9A-F]+)', text):
    h = m.group(1)
    b = bytes.fromhex(h)
    if len(b) >= 8 and b[0] == 0x32:
        print(f"hdr={b[:8].hex()}  byte1={b[1]:02X}  seq16={b[2]:02X}{b[3]:02X}  level={b[5]:02X}  type={b[7]:02X}")

print("\n=== our tx E2EE frames (DAVE layer output, no header) ===")
for m in re.finditer(r'e2ee frame tx ([0-9A-F]+)', text):
    h = m.group(1)
    print(f"len={len(h)//2} head={h[:16]}")

# Check for transport key in any log
print("\n=== transport key / secret key in logs ===")
for f in ['bin/Debug/net8.0-windows/debug.log']:
    t = open(f, 'rb').read().replace(b'\x00', b'').decode('utf-8', errors='replace')
    for m in re.finditer(r'(secret[_ ]?key|transport[_ ]?key)[^0-9A-F]*([0-9A-F]{32,64})', t, re.I):
        print(f, m.group(1), m.group(2)[:64])
