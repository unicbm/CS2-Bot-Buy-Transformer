# CS2-Bot-Buy-Transformer

CS2 的 5v5 团队购买插件。模型根据全队的现金和已有装备安排购买。Bot 可以替真人支付武器、投掷物、防弹衣、头盔和拆弹器，装备直接到账。需要自己买的物品会单独提示。模型直接在 C# 插件内运行。

使用 DemoTracer 时，插件会等回放开始，再用队友的实际余额代购。回放 Bot 自己的装备由 DemoTracer 分配。

## 安装

1. 在 Windows x64 服务器上安装 Metamod:Source 和 CounterStrikeSharp。已验证环境为 CS2 1.42.0.0、CounterStrikeSharp API 1.0.380。
2. 关闭服务器。升级时先将旧的 `BotBuy` 目录移出插件目录，再把 [Windows 正式版](https://github.com/unicbm/CS2-Bot-Buy-Transformer/releases/latest) 的 ZIP 解压到 `game/csgo`。
3. 启动标准 MR12 竞技 5v5，T 和 CT 默认启用团队购买。

## 查看状态

在控制台输入 `css_bot_team_buy`。看到 `Team=True` 和 `planner=managed` 表示策略已启用。购买后，`plans` 会增加，`last` 显示最近一次实际购买结果。用 `css_bot_team_buy 0` 关闭，`css_bot_team_buy 1` 开启。

[AGPL-3.0 许可证](LICENSE) · [署名说明](NOTICE.md)
