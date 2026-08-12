#!/usr/bin/env python3
"""Offline DAVE media-key forensics.

Reads the app's debug.log, extracts the MLS exporter secret / base secrets / captured
peer E2EE frames, and tries every plausible derivation variant (ratchet labels,
contexts, key lengths, AAD choices, nonce layouts) to find the one that decrypts
the peer's real frames. Prints the winning variant, or a full matrix when none wins.
"""
import re, sys, hashlib, hmac, itertools

try:
    from cryptography.hazmat.primitives.ciphers.aead import AESGCM
except ImportError:
    AESGCM = None

def hkdf_expand(prk, info, length):
    out = b""; t = b""
    counter = 1
    while len(out) < length:
        t = hmac.new(prk, t + info + bytes([counter]), hashlib.sha256).digest()
        out += t; counter += 1
    return out[:length]

def expand_with_label(secret, label, context, length):
    full = b"MLS 1.0 " + label.encode()
    info = length.to_bytes(2, "big") + bytes([len(full)]) + full + bytes([len(context)]) + context
    return hkdf_expand(secret, info, length)

def derive_secret(secret, label):
    return expand_with_label(secret, label, b"", 32)

def mls_exporter(exporter_secret, label, context, size):
    return expand_with_label(derive_secret(exporter_secret, label), "exported",
                             hashlib.sha256(context).digest(), size)

def ratchet_key(base, gen, label="key", length=16, ctx_mode="be32"):
    ctx = { "be32": gen.to_bytes(4, "big"), "le32": gen.to_bytes(4, "little"),
            "empty": b"" }[ctx_mode]
    return expand_with_label(base, label, ctx, length)

def parse_frame(hexstr):
    f = bytes.fromhex(hexstr)
    if len(f) < 12: return None
    if f[-1] != 0xFA or f[-2] != 0xFA: return None
    size = f[-3]
    supp = f[-size:]
    tag = supp[:8]
    n = supp[8:-3]
    val = 0; shift = 0; i = 0
    while i < len(n):
        b = n[i]; i += 1
        val |= (b & 0x7F) << shift
        if not (b & 0x80): break
        shift += 7
    nonce_len = i
    ct = f[:-size]
    return { "ct": ct, "tag": tag, "counter": val, "nonce_len": nonce_len,
             "ranges": n[nonce_len:], "full": f }

def decrypt_try(key, nonce, ct, tag, aad):
    if AESGCM is None: return None
    try:
        return AESGCM(key).decrypt(nonce, ct + tag, aad)
    except Exception:
        return None

def main(path):
    log = open(path, "rb").read().decode("utf-8", "replace")
    exporter = None
    bases = {}
    gen0 = {}
    frames = []
    for line in log.splitlines():
        if "mls epoch=" in line and "exporter=" in line:
            m = re.search(r"exporter=([0-9A-F]{64})", line)
            if m: exporter = bytes.fromhex(m.group(1))
        m = re.search(r"mls base user=(\d+) secret=([0-9A-F]{32,64})", line)
        if m: bases[int(m.group(1))] = bytes.fromhex(m.group(2))
        m = re.search(r"recv ratchet user=(\d+) gen0=([0-9A-F]{32})", line)
        if m: gen0[int(m.group(1))] = bytes.fromhex(m.group(2))
        m = re.search(r"e2ee frame rx ([0-9A-F]+)", line)
        if m:
            p = parse_frame(m.group(1))
            if p and p["counter"] == 0: pass  # silence-ish, skip later
            if p: frames.append(p)
    print(f"exporter: {exporter.hex() if exporter else 'NOT LOGGED'}")
    print(f"bases: { {k: v.hex()[:16] for k, v in bases.items()} }")
    print(f"gen0: { {k: v.hex() for k, v in gen0.items()} }")
    print(f"frames: {len(frames)}  samples: "
          + ", ".join(f"ct{len(f['ct'])} c{f['counter']} ranges{len(f['ranges'])}" for f in frames[:5]))
    if not frames: print("no encrypted frames captured"); return

    target = frames[0]
    ct, tag, counter = target["ct"], target["tag"], target["counter"]
    peer = max(gen0, key=lambda k: 1 if k != 1033169317493223496 else 0) if gen0 else None
    print(f"using frame counter={counter} ct={len(ct)}B peer={peer}")

    variants = []
    # candidate base secrets
    base_cands = []
    if exporter: base_cands.append(("exporter/LE16", mls_exporter(exporter, "Discord Secure Frames v0", (peer or 0).to_bytes(8, "little"), 16)))
    if bases: base_cands.append(("logged-base", bases[peer]))
    if gen0 and peer in gen0: base_cands.append(("gen0-key", gen0[peer]))

    # ratchet variants
    keys = {}
    for bname, base in base_cands:
        for label in ("key", "media-key", "media_key", "mackey"):
            for ctxm in ("be32", "le32", "empty"):
                for ln in (16, 32):
                    k = ratchet_key(base, 0, label, ln, ctxm)
                    keys[f"{bname}|{label}|{ctxm}|{ln}"] = k[:16]

    # AAD candidates
    aads = { "empty": b"" }
    for n in (7, 8, 16):
        if len(target["full"]) >= n: aads[f"prefix{n}"] = target["full"][:n]

    # nonce layouts
    nonces = {}
    for layout in ("zeros+LE", "zeros+BE", "LE+zeros", "BE+zeros", "zeros+LEp1", "zeros+LEm1"):
        c = counter
        if layout.endswith("p1"): c = counter + 1
        if layout.endswith("m1"): c = counter - 1
        le = c.to_bytes(4, "little"); be = c.to_bytes(4, "big"); z = b"\x00" * 8
        nonces[layout] = { "zeros+LE": z + le, "zeros+BE": z + be, "LE+zeros": le + z,
                           "BE+zeros": be + z, "zeros+LEp1": z + le, "zeros+LEm1": z + le }[layout]

    wins = []
    for kname, key in keys.items():
        for aname, aad in aads.items():
            for nname, nonce in nonces.items():
                pt = decrypt_try(key, nonce, ct, tag, aad)
                if pt is not None:
                    wins.append((kname, aname, nname, pt))

    if wins:
        for kname, aname, nname, pt in wins:
            print(f"\n*** WIN: key={kname} aad={aname} nonce={nname}")
            print(f"    plaintext: {pt.hex()}")
            # check for opus silence in the tail
            if pt.endswith(b"\xf8\xff\xfe"): print("    (ends in opus silence F8FFFE)")
            if b"\xfa\xfa" in pt: print("    (contains FAFA)")
    else:
        print("\nNO VARIANT DECRYPTED the frame.")
        print("=> The peer is NOT using the derived keys: likely a DIFFERENT MLS group/epoch,")
        print("   or the export/ratchet derivation diverges in a way not covered above.")
        # also try the OTHER frames (maybe the counter mapping differs per frame)
        if len(frames) > 1:
            print("\ntrying frame 1 too:")
            for f in frames[1:3]:
                for kname, key in keys.items():
                    for aname, aad in aads.items():
                        for nname, nonce in nonces.items():
                            pt = decrypt_try(key, nonce, f["ct"], f["tag"], aad)
                            if pt is not None:
                                print(f"  WIN frame c={f['counter']}: {kname} {aname} {nname} -> {pt.hex()}")

if __name__ == "__main__":
    main(sys.argv[1] if len(sys.argv) > 1 else "bin/Debug/net8.0-windows/debug.log")
