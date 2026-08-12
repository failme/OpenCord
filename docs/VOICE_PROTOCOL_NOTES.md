# Voice + DAVE protocol notes (ground truth captured live)

Status: transport layer (voice gateway v8 + UDP + Opus + audio) is implemented and
**live-verified against Discord's real servers** via `--voiceprobe`. The DAVE MLS layer
(required for all E2EE-eligible calls since March 1, 2026) is the remaining build.

## Transport (working, live-verified)

- Voice gateway: `wss://{endpoint}?v=8`. Identify (op 0) includes
  `max_dave_protocol_version: 1`. Heartbeat (op 3) is v8 format: `d = { t, seq_ack }`.
  READY (op 2) carries `ssrc`, `ip`, `port`, `modes`. Session description (op 4):
  `mode`, `secret_key` (32 bytes), `dave_protocol_version`.
- Live READY modes observed: `[aead_aes256_gcm_rtpsize, aead_xchacha20_poly1305_rtpsize]`.
  **The client forces `aead_xchacha20_poly1305_rtpsize`**: its packet layout is
  pinned byte-for-byte by discord.py and @discordjs/voice, while no public library
  implements the AES-GCM variant, so that mode cannot be cross-validated.
- Transport nonce (both rtpsize modes): 24-byte nonce = 4-byte BE counter + 20 zeros; the
  4-byte counter is appended to the UDP payload as a big-endian trailer.
- **The 12-byte RTP header is the AEAD's Additional Authenticated Data.** discord.py
  ("we require AEAD with Additional Authenticated Data (the header)") and
  @discordjs/voice both authenticate it, and the SFU verifies the tag before
  relaying — an empty-AAD packet is silently dropped in BOTH directions. The
  packet layout `header | ciphertext | tag | counter-trailer` is pinned in SelfTest
  against a libsodium known-answer vector.
- RTP: header `0x80 0x78 | seq BE(2) | ts BE(4) | ssrc BE(4)`; timestamp += 960 per 20ms
  frame at 48 kHz. IP discovery: send `00 01 | 00 46 | ssrc(4)` (74 bytes); response
  `00 02 | 00 46 | ssrc | 64-byte NUL-terminated address | port BE(2)`.
- Opus: 48 kHz stereo, 20ms frames (960 samples). 5 silence frames `F8 FF FE` before stop.
- Crypto is pinned in SelfTest: ChaCha20 (RFC 8439), Poly1305 (RFC 8439), HChaCha20
  (draft-irtf-cfrg-xchacha-03), AES-GCM vs .NET AesGcm + OpenSSL (24-byte nonce),
  XChaCha20-Poly1305, full transport packet round-trip.

## DAVE / MLS (the remaining build)

Sources: https://github.com/discord/dave-protocol (protocol.md, whitepaper v1.1).

- E2EE enforced for all audio/video since March 1, 2026. No downgrade path.
- DAVE v1 MLS parameters: MLS 1.0, **ciphersuite DHKEMP256_AES128GCM_SHA256_P256
  (MLS ciphersuite 2)** — P-256 ECDH + P-256 signatures + AES-128-GCM + SHA-256.
  All of these exist in .NET: `ECDiffieHellman`(P-256), `ECDsa`(P-256), `AesGcm`,
  `HKDF`, `HMACSHA256`. No hand-rolled EC math needed.
- Group extension: external_senders (exactly one external sender = the voice gateway).
  Credentials are basic, identity = big-endian 64-bit user snowflake.
  Key package lifetime: not_before = 0, not_after = 2^64-1.
- Group creation flow (all pending members are committing members):
  1. op 25 (external sender package) arrives on connect — **captured live**, 71 bytes.
  2. Client creates a local 1-member group (needs external sender + DAVE version).
  3. Client sends key package (op 26) — required after session description with
     non-zero dave_protocol_version.
  4. Gateway broadcasts add proposals (op 27) for all pending members.
  5. Clients commit: op 28 (commit + welcome, welcome mandatory when adds included,
     welcome must carry an inline tree).
  6. Gateway broadcasts winning commit (op 29) + welcome (op 30).
  7. Clients process -> op 23 (transition ready). Gateway -> op 22 (execute
     transition); only then do senders use the new ratchet.
  - Sole-member reset: op 24 epoch=1 + op 21 transition_id=0.
  - Invalid commit/welcome: op 31, reset local state, resend key package.
- Handshake messages are MLS PublicMessage (plaintext MLS, transport-encrypted only).
- **Sender keys** (frame E2EE):
  - `sender_base_secret = MLS-Exporter("Discord Secure Frames v0", LE64(senderUserId), 16)`
  - Ratchet per RFC 9420 §9.1-style sender ratchet; generation = MSB of the 4-byte
    truncated nonce. New ratchet per epoch; nonce resets to 0 per epoch.
- **Frame format** (Opus: full frame encrypted, no unencrypted ranges, no AAD):
  `ciphertext | tag(8 truncated AES-128-GCM) | ULEB128 nonce | ULEB128 ranges (empty) |
  supplementSize(1) | 0xFA FA`
  - 12-byte GCM nonce = 8 zero bytes + 4-byte truncated nonce (bytes 8-11).
  - Supplement size = tag(8) + nonce + ranges + 1 + 2.
  - Silence `F8 FF FE` may pass through untransformed even in E2EE mode.
- Voice gateway DAVE binary envelopes (server->client): `seq BE(2) | opcode(1) |
  payload`. **Client->server binary messages carry NO sequence prefix.**

## DAVE MLS implementation status

Complete and green. The client now speaks the full DAVE MLS protocol:

- **Net/MlsWire.cs** — TLS-style wire primitives (varint/opaque/vector) + all MLS
  structs (leaf node, key package, proposals, commit, welcome, MLSMessage)
  verified against RFC 9420 and the mlspp implementation.
- **Net/MlsCrypto.cs** — P-256 ECDH/ECDSA, RFC 5869 HKDF, HPKE (RFC 9180 base
  mode, pinned to the official A.3.1 vector), MLS key schedule, sender ratchet.
- **Net/MlsTree.cs** — RFC 9420 tree math (parent/left/right/dir-path), TreeKEM
  public/private state, parent hashes, tree-hash serialization.
- **Net/MlsGroup.cs** — group state machine: create, external-sender Add, commit
  + welcome, joiner FromWelcome, epoch transitions, MLS-Exporter.
- **Net/DaveMls.cs** — DAVE session: op 25 external-sender parse, key-package
  response, op 27/28/29/30 handling, frame protect/unprotect with the 8-byte
  truncated-tag AES-128-GCM frame format.

### Wire-format conformance fixes (against RFC 9420 / mlspp ground truth)

The original implementation serialized several MLS structs with extra opaque
length prefixes that the real server (mlspp) does not use. Every one of these was
an interop bug the self-test could not catch (it synthesized its own payloads):

- **MLSMessage**: `[version u16][wire u16][body inline]` — no outer varint. The old
  encoder wrapped the body in a varint that mlspp never writes.
- **FramedContent is inline**, not opaque: `MlsAuthContent`/commit/proposal content
  is a self-delimiting struct; the TBS (`FramedContentTBS` = `wire || content ||
  extensions`) and the confirmation transcript sign/verify it inline. Proposal refs
  hash `[wire u16] || AuthenticatedContent`.
- **Add proposal**: `proposal_type u16 (1) || KeyPackage inline`. The old version
  varint-wrapped the key package.
- **LeafNode fields inline**: credential, capabilities, lifetime struct, and the
  ExtensionList — and a stored-then-re-encoded leaf must keep the extension list's
  leading vector varint (capturing only the payload dropped the `0x00` of an empty
  list, misaligning the signature and breaking the welcome tree parse).
- **GroupInfo**: `group_context || extensions || confirmation_tag || signer || sig`
  all inline, with the extensions' vector varint preserved.
- **op 27 proposals vector**: `[op u8][vec varint]` of RAW MLSMessages — each is
  parsed structurally (go-dave's `consumeMLSMessage`) to find its boundary; the old
  opaque-per-message parse mis-read the group_id varint as a message length.
- **Simultaneous-join race**: two pending members committing a fresh call — the
  loser joins via the winner's welcome, then receives the winner's announce commit
  from an OLDER epoch; commits with `epoch < current` are ignored instead of being
  flagged invalid (which would churn remove/re-add forever).

Verified end-to-end in `--selftest` (**129 checks**): the full two-party handshake
(key package → external-sender Add → commit/welcome → transition) round-trips, the
joiner's welcome tree parses and its confirmation tag verifies against the
committer's key schedule, E2EE-protected media frames exchange in both directions,
and tampered/unknown-ssrc frames are rejected. Plaintext Opus passes through
untransformed until the transition executes.

Live probe (`--voiceprobe TOKEN`, single user): transport handshake, DAVE v1
session description, external sender package parse, MLS group creation and key
package acceptance all succeed. The E2EE transition only completes when a second
member joins the channel (the gateway emits the Add proposal then).

## Video + screen share

Three things gate video that the transport layer cannot show you, each of which reads
as "camera on, nothing happens":

- **`self_video` on the MAIN gateway (op 4).** The voice gateway's op 12 only tells the
  SFU which ssrcs to relay. Every *receiving* client decides whether to draw a video tile
  from `self_video` in your VOICE_STATE_UPDATE. Without it a real Discord client shows a
  plain avatar and never subscribes, no matter how much RTP you send.
- **Keyframes.** The MS H.264 encoder MFT emits exactly ONE IDR — its first frame — for
  the life of the MFT. A subscriber only appears *after* it sees op 12, i.e. always after
  that IDR, so it has nothing to start its decoder from. `MF_MT_MAX_KEYFRAME_SPACING` on
  the output type (2s) fixes it at the source; PLI handling is the backstop, not the plan.
- **One thread per MFT.** Both codecs must be created *and* driven on a single thread that
  called `MFStartup` itself (`Mf.EnsureThreadStarted` is `[ThreadStatic]`). The decoder used
  to live on `UdpVoice`'s receive loop — an `async` method, so its continuations hop
  threadpool threads: `ProcessInput` returned S_OK and `ProcessOutput` never yielded a frame,
  silently. Both `H264Encoder` and `H264Decoder` now own a dedicated codec thread.

### MFT drive rules (all four cost a live test round to find)

- **Size the ProcessOutput buffer from `MFT_OUTPUT_STREAM_INFO.cbSize`**, never a constant.
  NV12 is 0.34MB at 640x360 and 1.32MB at 720p; a fixed 1MB buffer decoded our own camera
  perfectly and produced *nothing* for a 720p peer. Re-read it after every type change.
  (`GetOutputStreamInfo` fills `MFT_OUTPUT_STREAM_INFO` — three DWORDs — **not** the input
  struct, which leads with an 8-byte `hnsMaxLatency` and shifts every field.)
- **`MF_E_NOTACCEPTING` (0xC00D36B5) is not an error.** Drain the output and re-submit the same
  sample. Returning early leaves the finished frame stuck inside, so the MFT never accepts
  another input and never produces the output that would unblock it — permanently wedged.
- **`MF_E_TRANSFORM_TYPE_NOT_SET` (0xC00D6D61) is a renegotiation request**, same as
  `MF_E_TRANSFORM_STREAM_CHANGE`. The decoder drops the output type the moment it parses a real
  SPS whose resolution differs from the one you guessed. Handle both or 720p decodes to zero.
- **`MF_LOW_LATENCY`** on the decoder: without it the MS H.264 decoder fills a ~30 frame reorder
  buffer before its first output — two seconds of black on every camera toggle.

**Screen sharing is not part of the voice connection.** It is a second RTC session ("Go
Live"), even in a DM:

1. Main gateway **op 18** `{type: "guild"|"call", guild_id?, channel_id, preferred_region}`.
2. Dispatch **STREAM_CREATE** `{stream_key, rtc_server_id, ...}` — the key is
   `guild:<guild>:<channel>:<user>` or `call:<channel>:<user>`. This reaches **every member of
   the channel**, so it is also how you learn a *peer* went live, and it is where their
   `rtc_server_id` comes from.
3. Dispatch **STREAM_SERVER_UPDATE** `{token, endpoint, stream_key}` — its own one-shot token.
4. A whole second voice websocket to that endpoint: identify with
   `streams: [{type: "screen", rid: "100", quality: 100}]` and its own DAVE MLS group.
   **`server_id` is the `rtc_server_id` from STREAM_CREATE, not the guild** — the guild id is
   rejected 4006, which on the other end looks like a Go Live tile appearing and vanishing
   half a second later. (`StreamClient` retries with the other candidate once; if the logs
   settle it, pin the winner and drop the retry.)
5. op 12 on that connection with the screen stream `active: true`, then video RTP on the
   READY-allocated ssrc. **op 19** `{stream_key}` retires it.

To *watch* someone's share: **op 20** `{stream_key}` (their key), which answers with a
STREAM_SERVER_UPDATE for it; join that connection as a viewer, subscribe with op 15 + the
PLI/RR cadence, and keep **op 21** `{stream_key}` pinging or the gateway retires you.

A peer going live is signalled ONLY by `self_stream` on their VOICE_STATE_UPDATE — STREAM_CREATE
goes to the broadcaster, not to the channel. (And a share already running when you join arrives
with the guild's initial voice states, with no update to catch: scan once on your own join.)

**Colour**: `PixelFormat.Format24bppRgb` is **B,G,R** in memory. Both `Nv12.ToRgb` and
`Nv12.FromRgb` sit against GDI+ bitmaps, so both use that order; writing R,G,B swapped red and
blue in every frame we *rendered* while the video we *sent* — raw NV12 straight into the encoder,
never converted — stayed correct. Both directions are BT.709 TV-range and are pinned separately
in SelfTest, because a round-trip test passes happily with both ends swapped.

Adding a `type: "screen"` entry to the *voice* connection's op 12 is silently ignored:
the bytes go out, no client has anything to watch. See `Net/StreamClient.cs`.

## Testing commands

```
dotnet run -- --selftest          # 129 checks, all green
dotnet run -- --voiceprobe TOKEN  # live handshake + DAVE dump
dotnet run -- --apitest TOKEN     # REST smoke
dotnet run -- --demo              # offline UI demo
```
