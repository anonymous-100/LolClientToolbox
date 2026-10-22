# LOL 客户端工具箱（插件版）

英雄联盟客户端的本地小工具集。**插件架构**：宿主只负责界面和客户端连接，功能全部是
`plugins/` 目录下的插件 —— 加功能不用改宿主，单插件失效不影响其他。

当前功能：

- **查询战绩** —— 查自己或「最近一起玩过的人」的近 20 局：模式/英雄/胜负/KDA/伤害/时长，双击看完整计分板

> 窗口修复、跳过卡结算、热重载、自动接收对局在旧单文件版（`E:\games\LOL客户端`，止步 v22），
> 不再维护；要迁进来按「插件开发手册」包一层即可，判据和实现都在旧源码里。

## 运行

需要 **.NET 9 桌面运行时（x64）**，客户端开着。不需要管理员。

```bash
dotnet build -c Release
dotnet publish src/LolClientToolbox/LolClientToolbox.csproj -c Release -o publish
# 插件已在仓库里，发布后自动带上；直接运行：
publish/LOLToolbox.exe
```

底部「插件：N 个已加载」显示加载了几个；明细看同目录 `采样日志.txt`。

## 插件开发

见 [插件开发手册.md](插件开发手册.md)：契约、生命周期、plugin.json  schema、
打包放置、故障隔离约定、红线。参考实现：`src/Plugins/MatchHistoryPlugin/`。

## 目录

```
src/
├─ LolClientToolbox/            宿主（纯外壳）
├─ LolClientToolbox.Contracts/  插件契约
└─ Plugins/MatchHistoryPlugin/  查询战绩
publish/                        可运行产物（gitignore）
```

## 技术备注

- 凭据通过 `NtQueryInformationProcess`（class 60）从 `LeagueClientUx.exe` 命令行读取，
  普通权限即可（国服客户端不写 lockfile）；LCU 是 Riot 非官方接口，个人自用。
- 宿主仍是 framework-dependent 单文件 exe；插件在 `plugins/<id>/` 下动态加载
  （自定义 `AssemblyLoadContext`，Contracts 复用宿主持有的那一份保证类型同一性）。
- 窗口修复算法曾参考开源项目 `LeagueTavern/fix-lcu-window`（MIT），该功能未随插件版发布。

## 许可

[MIT](LICENSE)
