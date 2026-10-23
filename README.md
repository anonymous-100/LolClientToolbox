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
  普通权限即可（国服客户端不写 lockfile）；LCU 是 Riot 非官方接口，个人自用（合规分析见上节）。
- 宿主仍是 framework-dependent 单文件 exe；插件在 `plugins/<id>/` 下动态加载
  （自定义 `AssemblyLoadContext`，Contracts 复用宿主持有的那一份保证类型同一性）。
- 窗口修复算法曾参考开源项目 `LeagueTavern/fix-lcu-window`（MIT），该功能未随插件版发布。

## 合规与风险（2026-10-23 查证）

**结论：个人自用不违法，合规上落在 Riot 明确容忍的区间。**

- **法律上**：连自己机器上的客户端、用客户端自己的凭据、读自己账号的数据——
  没有未授权访问，是用户协议问题，不是刑事问题。
- **Riot 政策上**：官方支持页《Third Party Applications》把「追踪你自己的数据」
  列为第三方应用的正当用途；被禁止的是提供可度量优势、**替你执行操作**、
  暴露被故意隐藏的信息。本插件只读、赛后、无自动操作，三样都不碰。
- **实际执行上**：同类工具（Blitz / op.gg / Porofessor）运行多年；Riot 的打击对象是
  脚本与外挂。2025-2026 年 Riot 关闭第三方读游戏内存、把大家赶向官方 API，
  LCU 是客户端自带本地接口，属于「官方机制」那一侧。

两条义务（不是风险）：

1. **分发必须报备**：Riot 开发者门户要求使用 League Client API 的产品注册。
   个人自用不注册是业界普遍做法；要发给别人用，先去门户建应用报备。
2. **政策会变**：Riot 明言 policies will inevitably change over time，
   以开发者门户 General Policies 为准，别以旧教程为准。

## 许可

[MIT](LICENSE)
