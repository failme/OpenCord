# OpenCord

A Discord desktop client, rebuilt from scratch in C#. No Electron garbage, making it extremely memory efficient.

You will need a Discord account token to log in

## Features

- **Text**: channels, threads, DMs, group DMs, reactions, replies, polls,
  embeds, stickers, slash commands, buttons/components, server discovery
- **Calling**: voice & video calls, screensharing
- **UI**: closely matches real Discord UI
- **low memory**: typical usage of 10-30mb ram, compared to Discord's >800mb ram

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
