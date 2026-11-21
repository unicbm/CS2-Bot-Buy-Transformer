# CS2-Bot-Buy-Transformer

A CounterStrikeSharp team-buy plugin for CS2. At freeze time it predicts the equipment each player should leave spawn with, then finds purchases the five actual wallets and loadouts can support. The team can full buy, save, or fund a teammate. Bot sponsorship charges the donor and delivers the item directly to the recipient. Human players receive private shopping suggestions and can choose what to buy.

## Install on a Windows server

1. Install Metamod:Source and CounterStrikeSharp. The preview was built with CounterStrikeSharp API 1.0.380 for Windows x64. Its native purchase profile was validated on CS2 1.42.0.0.
2. Download the Windows x64 ZIP from [Releases](https://github.com/unicbm/CS2-Bot-Buy-Transformer/releases). With the server stopped, move the entire existing BotBuy directory outside the plugins directory as a backup. Extract the ZIP into the server鈥檚 `game/csgo` directory. The resulting entry point is `addons/counterstrikesharp/plugins/BotBuy/BotBuy.dll`. Run a single purchase plugin.
3. Start a standard MR12 competitive match with five players on a side. Both team models start automatically inside `BotBuy.dll` using the bundled weights and the .NET runtime provided by CounterStrikeSharp.

Run `css_bot_team_buy` in the console. `Team=True` and `planner=managed` indicate the planner is ready. After a purchase phase, `plans` counts completed team plans across both sides. `direct_gifts` counts delivered gifts, `advice` counts private human recommendations, and `last` includes actual bot purchases. `css_bot_team_buy 0` disables the team planner for the session and `css_bot_team_buy 1` enables it. Clients in the server can use these controls.

## Policy and evidence

In a comparison on the same 1,338 team rounds across 28 held-out series, individual primary-weapon presence accuracy was 93.8%. In three samples per round the team left an eligible player with at least $4,000 and an empty primary slot once in 4,014 decisions. The policy occasionally force-buys in rounds where the professionals saved.

## Build

Build and test with a .NET 10 SDK.

```powershell
dotnet test tests/BotBuy.Tests.csproj -c Release
dotnet build plugin/BotBuy.csproj -c Release -o artifacts/plugin
```

Create the Windows x64 server ZIP from the Release build.

```powershell
./tools/package-windows.ps1
```

## License

GNU Affero General Public License v3.0. [License](LICENSE) 路 [Attributions](NOTICE.md)
