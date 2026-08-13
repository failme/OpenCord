# OpenCord

Native Windows Discord client, built in C#. No Electron garbage, making it extremely memory efficient.

You will need a Discord account token to log in

<div align="center">

<img
  src="screenshots/server.png"
  width="900"
/>

</div>

## Features

- **Text**: channels, threads, DMs, group DMs, reactions, replies, polls,
  embeds, stickers, slash commands, etc.
- **Calling**: voice & video calls, screensharing
- **UI**: closely matches real Discord UI
- **low memory**: typical usage of **30-70mb** ram, compared to Discord's >800mb ram
- desktop notifications & pings

<div align="left">

<img
  src="screenshots/memory.png"
  width="500"
/>

</div>

## Fetching your Discord token
1. Login to Discord (web version or Windows app) and open DM or server
2. Open DevTools (Ctrl+Shift+I) and navigate to the Network tab
3. Refresh (Ctrl+R)
4. In the Network tab, filter URLs for "messages" (you should see something like "messages?limit=10")
5. Click on it to show the Headers tab
6. Scroll down until you reach Request Headers
7. Copy the token next to "Authorization"

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
