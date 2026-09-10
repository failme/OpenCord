# OpenCord

Native Windows Discord client, built in C#. No Electron garbage, making it extremely memory efficient.

You will need a Discord account token (or a bot token) to log in

## Features

- **Text**: channels, threads, DMs, group DMs, reactions, replies, polls,
  embeds, stickers, slash commands, etc.
- **Calling**: voice & video calls, screensharing
- **UI**: closely matches real Discord UI
- **low memory**: typical usage of **10-30mb** ram, compared to Discord's >800mb ram
- desktop notifications & pings

## Fetching your Discord token
1. Login to Discord (web version or Windows app) and open DM or server
2. Open DevTools (Ctrl+Shift+I) and navigate to the Network tab
3. Refresh (Ctrl+R)
4. In the Network tab, filter URLs for "messages" (you should see something like "messages?limit=10")
5. Click on it to show the Headers tab
6. Scroll down until you reach Request Headers
7. Copy the token next to "Authorization"

## Bot tokens

A bot token also works — paste it the same way, and OpenCord detects the kind
automatically (or set `OPENCORD_BOT_TOKEN` instead of `OPENCORD_TOKEN`).

Bots connect on the bot gateway, and the client asks for the privileged intents
(`GUILD_MEMBERS`, `GUILD_PRESENCES`, `MESSAGE_CONTENT`) so message content and the
member list work. If any of them isn't toggled on for your app in the
[Developer Portal](https://discord.com/developers/applications), OpenCord falls back
to the base intents automatically and still connects — live message content and the
member list just stay empty until they're enabled. Set `OPENCORD_INTENTS` to a
bitmask to request a specific set instead.

## Build Requirements

- Windows (WinForms)
- .NET 8 SDK

## Build & run

```
dotnet build
dotnet run
```

## License

MIT — see [LICENSE](LICENSE).
