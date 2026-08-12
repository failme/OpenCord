# OpenCord

A Discord desktop client, rebuilt from scratch in C# / WinForms — no Electron, no
Discord.Net, no scraped screenshots. Every piece of UI is laid out and painted by hand
against measurements taken from the real client's DOM (geometry, fonts, colours,
gradients), not eyeballed from a picture of it.

This is a hobby reverse-engineering project, not an official or endorsed Discord
client.

## Before you run this

OpenCord talks to Discord using a **user account token**, the same credential the
official web/desktop client uses once you're logged in — not a bot token. Discord's
Terms of Service prohibit third-party clients and automation on user accounts, and
using one can get an account actioned. Use this at your own risk, on an account you're
willing to lose, and don't automate anything with it. This project exists for learning
how the client protocol and UI actually work, not to evade Discord's rules.

## What's here

- **Text & voice**: channels, threads, DMs, group DMs, reactions, replies, polls,
  embeds, stickers, slash commands, buttons/components, server discovery — and a real
  voice/video pipeline (Opus, H.264, the DAVE E2EE handshake) talking to Discord's
  actual voice gateway, not a stub.
- **Fidelity over approximation**: colours are read out of the live client's resolved
  CSS (including the oklab/color-mix tokens the redesign uses), not picked from a
  screenshot; icon geometry is lifted from the real inline SVGs; layout constants are
  measured pixel offsets, not guesses. `docs/` and the code comments carry the
  reasoning and the measured values inline, including the cases where the live client
  turned out to be inconsistent with itself.
- **A voice protocol written against the real server**: `docs/VOICE_PROTOCOL_NOTES.md`
  documents the voice gateway v8 + UDP + Opus/H.264 + DAVE (MLS) handshake as verified
  live, opcode by opcode.

## Requirements

- Windows (WinForms)
- .NET 8 SDK

## Build & run

```
dotnet build
dotnet run
```

On first launch you'll get a login screen. You can also skip it by setting your token
as an environment variable before launching:

```
setx CLAUDESCORD_TOKEN "your token here"
```

The environment variable always wins over anything saved on disk. Once you do log in
through the app, the token is DPAPI-encrypted to your Windows user account before it's
written to `prefs.json` next to the executable — never stored in plain text.

## Useful flags

- `--selftest` — runs the pure-logic test suite (markdown parsing, DPI scaling,
  crypto, message-model edge cases) and exits. No token needed.
- `--demo` — launches with synthetic data, no network connection at all.
- `--log` — writes verbose diagnostics to `debug.log` next to the executable.
- `--apitest [token]` / `--voiceprobe [token] [channelId]` — headless smoke tests
  against the real REST API / voice gateway, useful when bringing this up on a new
  machine. Print to stdout / `apitest.log`.

## Project layout

- `Core/` — theme, icons, markdown/rich-text rendering, media cache, crypto, DSP.
- `Net/` — the user gateway client, REST client, voice gateway/UDP/DAVE, and every
  wire model.
- `Ui/` — every screen and control (WinForms `Control` subclasses, hand-painted).
- `docs/` — protocol notes captured against the live servers.

## License

MIT — see [LICENSE](LICENSE).
