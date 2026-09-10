# Changelog

## Unreleased — since v1.0.0 (everything not yet pushed to GitHub)

Baseline: `b9f25a5` (2026-08-12), the last commit on GitHub. Everything below is
in the working tree on top of it — about 1,800 added lines across 22 files plus
four new source files. Grouped by theme rather than by file.

### Bot accounts

OpenCord can now log in with a **bot token** as well as a user token.

- Login detects the token kind automatically: a token that 401s bare is retried
  with the `Bot` prefix, and the kind is remembered with the saved credential
  (`OPENCORD_BOT_TOKEN` forces bot mode; `OPENCORD_TOKEN` stays user-only).
- Bots connect on the bot gateway with a bot-specific identify (intents, no
  user client_state). The client asks for the privileged intents
  (`GUILD_MEMBERS`, `GUILD_PRESENCES`, `MESSAGE_CONTENT`); if Discord closes
  the socket with 4014 (intents not toggled in the Developer Portal) they are
  dropped and the connection retried instead of looping.
- Bots cannot use the user gateway's lazy member sync (op 14), so the roster is
  requested with op 8 and assembled from `GUILD_MEMBERS_CHUNK`s, grouped
  hoisted-roles-first the way the sidebar expects.
- A bot READY has no DM list or friend list; those panes degrade gracefully,
  and the first `GUILD_CREATE` auto-selects so channels appear immediately.

### Polls & voice messages

- **Create polls** from the composer: the new `PollDialog` builds a question,
  2–10 answers, multiple-choice and expiry (1h–7d), and posts through the same
  optimistic-row path as a normal message.
- **Voice messages**: hold the mic button to record; audio is captured to mono
  48k PCM, encoded with Concentus into the exact Ogg/Opus shape Discord's own
  voice messages use (`IS_VOICE_MESSAGE` flag, waveform preview, duration), and
  uploaded. Playback uses the existing inline audio card.

### Member & role management

- **Role management** (`RoleDialog`): create roles, edit name/colour (Discord's
  own 20-colour palette), hoist and mentionable flags, from the server menu's
  "Manage Roles".
- **Ban dialog** with the optional audit-log reason; kick/ban/timeout all
  surface through the member context menu as before.
- The composer's `@`-autocomplete now caches the roster per channel and
  invalidates it when members change, and matches anywhere in a display name
  or @username (typing "smith" finds "John Smith").

### Settings

Three new sections that talk to the real user-account REST routes — each is
hidden for bot sessions:

- **Privacy & Safety** — who can add you as a friend (the three friend sources
  behave as one radio group, exactly like the web client) and DM filtering.
- **Devices** — every logged-in session with OS/platform/location, and Log Out
  for any of them.
- **Connections** — linked accounts with verified/revoked state.

### Chat view

- **Copy button on fenced code blocks**, like the live client: hovering a
  block shows a copy chip in its top-right corner; clicking copies the raw
  code and the chip reads "Copied!" for a moment.
- Edited messages (`MESSAGE_UPDATE`) now merge the payload onto the cached row
  instead of replacing it — an edit no longer wipes reactions, poll data or
  attachments until the channel is reopened.
- Right-clicking an attachment offers **Save**; failures surface as a toast
  rather than vanishing into the log.
- Unplayable videos name the failure instead of an eternal blank.

### Search & threads

- Search results page ("Load more" when a full page comes back), with
  `from:`/`mentions:` member autocomplete.
- Thread browser shows **archived threads** too, fetched from REST under an
  "Archived" divider (active ones already rode `THREAD_LIST_SYNC`); clicking
  one resolves like a synced thread.

### Friends page

- **Search box** above the list, filtering by display name like the live
  client.
- Rows show the friend's **custom status / activity** as the subtext (the bare
  "Online" is now only the fallback) and their **server tag chip** with badge.
- The **member/profile column now hides** on the Friends page (and comes back
  when a channel is opened) — it used to stay floating beside the list.
- Blocking from a profile popout keeps the Friends list in sync through the
  relationship gateway events.

### Group DMs

- A group renamed or re-iconed **from another client** now updates in place
  (sidebar + open header) instead of showing the old name until restart.

### Keyboard

- **Ctrl+/ opens the keyboard-shortcut list** (new `ShortcutHelp` overlay),
  listing every binding the app actually implements.
- **Ctrl+U is freed for underline formatting** — it was previously hijacked
  by the member-list toggle, which made the composer's underline unreachable.
- The member list is still toggleable from its header button.

### Media

- **WebP and AVIF images decode** everywhere (avatars, attachments, embed
  thumbnails): GDI+ handles neither, so those URLs are transparently re-fetched
  through the media proxy in PNG form.
- Video clips with no audio track (muted memes) play correctly — the clock no
  longer depends on a WaveOut that doesn't exist.
- A just-recorded voice note plays from its local temp file while the upload
  swaps in the CDN URL.

### Composer fixes

- Recording owns Enter — a stray keystroke mid-recording can no longer send.
- Typing state and the mention cache reset per channel; a floating menu that
  ever steals focus is healed on the next edit.
- Popups raise themselves without activating (`SetWindowPos` with
  `SWP_NOACTIVATE`), so opening the emoji/mention/slash menu no longer kills
  the caret in the composer.

### Demo & README

- `--demo` takes a scenario: `guild` (default), `dm`, or `voice`, with richer
  placeholder content (polls, buttons, a fenced code block to exercise the
  copy chip).
- README documents the bot-token flow.

### Internals

- `UserMessage.PartialUpdate` plus `Has*` flags let ChatView distinguish a
  MESSAGE_UPDATE diff from a full message.
- Autocomplete matching is substring, not prefix.
- `Native.RaiseNoActivate` shared by all floating menus.

---

Notes for release: local `main` is also 16 commits behind `origin/main`, but
those remote commits are README wording tweaks and screenshot renames only —
no code changes — so the list above is complete on the code side.
