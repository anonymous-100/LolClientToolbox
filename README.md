# LOL 客户端工具箱（插件版）

英雄联盟客户端的本地小工具集。**插件架构**：宿主只负责界面和客户端连接，功能全部是
`plugins/` 目录下的插件 —— 加功能不用改宿主，单插件失效不影响其他。

当前功能（7 个插件）：

- **恢复窗口大小** —— 窗口尺寸异常时自动修，也可点左侧立即修
- **查询战绩** —— 查自己或「最近一起玩过的人」的近 20 局；点选一局，下方显示当局所有人 10 个人的完整计分板
- **跳过卡结算** —— 结算卡住时自动跳过（三判据：5 秒事件驱动快判 / 单阶段 15 秒 / 集合级 45 秒）
- **热重载客户端** —— 界面卡死点它
- **接收对局** —— 匹配成功弹出确认框时自动点接受
- **看视频（手动）** —— 热键（默认 Ctrl+F8）切到视频网站，再按一次把视频窗口最小化回到游戏。
  **v0 手动版**：零检测风险，纯窗口控制
  - **不需要管理员权限**：「关闭视频」不是去抢游戏的前台（游戏是管理员权限运行，
    普通权限进程抢不到——UIPI 限制），而是把视频窗口**最小化**，Windows 会自动把焦点
    交给 Z 序下一个窗口（全屏游戏）。视频进度、页面都保留，下次一键就回来
  - 兜底：若最小化后焦点没落到游戏（例如工具箱窗口正压在游戏上面），会自动再尝试聚焦一次；
    发布目录里的 `以管理员身份启动.bat` 仅在极端情况（独占全屏等）下备用
- **看视频（自动）** —— **v1 自动版**：对局中死亡自动切到视频、复活自动切回，不用按任何键。
  数据源是官方 Live Client Data API（`respawnTimer`，在 `allPlayers[]` 里 ——
  网上教程写的 activePlayer.respawnTimer 是错的，实测不存在）；接口取不到时退
  「死亡灰屏 + OCR 倒计时」。只负责判断，切窗口/暂停/静音都由手动版执行
  （两插件经契约 `IVideoSwitcher` 协作）。**待真机对局验证**，详见其 `说明.md`

> 窗口修复/卡结算/热重载/接收对局从旧单文件版（`E:\games\LOL客户端`，止步 v22）迁移而来；
> 「查询战绩」「看视频（手动）」是本仓库原生的新功能。卡片区固定高度 + 滚动条：
> 插件超过 4 个时第 5 个起进滚动区（改 plugin.json 的 `order` 可调顺序）。

## 源码与产物分离

仓库里**只放源码和文档**，任何 exe/dll/设置/日志都不提交。三类东西分开放：

| 目录 | 内容 | 进仓库 |
|---|---|---|
| `src/` `*.md` `*.sln` | 源码、文档、解决方案 | ✅ |
| `.dev/` | 开发迭代产物：`dotnet publish` 到这里，随便改随便删 | ❌ |
| `dist/` | 发布产物：只放验证过的版本（exe + `plugins/`） | ❌ |
| `tools/` | 本地调试脚本（端点探针、模拟按键） | ❌ |

```bash
# 开发迭代：发布到 .dev 然后直接跑
dotnet publish src/LolClientToolbox/LolClientToolbox.csproj -c Release -o .dev
.dev/LOLToolbox.exe

# 出发布版：验证通过后拷到 dist（只放能用的版本，方便整包发给别人）
```

## 运行

需要 **.NET 9 桌面运行时（x64）**，客户端开着。一般不需要管理员；
只有「看视频」的切回游戏需要（见上）。

```bash
dotnet build -c Release
dotnet publish src/LolClientToolbox/LolClientToolbox.csproj -c Release -o publish
# 插件已在仓库里，发布后自动带上；直接运行：
publish/LOLToolbox.exe
```

底部「插件：N 个已加载」显示加载了几个；明细看同目录 `采样日志.txt`。

## 插件开发

见 [插件开发手册.md](插件开发手册.md)：契约、生命周期、plugin.json  schema、
打包放置、故障隔离约定、红线、自测方法。参考实现：`src/Plugins/MatchHistoryPlugin/`（原生）、
`src/Plugins/WindowFixPlugin/` 等四个（旧版迁移）。

## 目录

```
src/
├─ LolClientToolbox/            宿主（纯外壳）
├─ LolClientToolbox.Contracts/  插件契约（含插件间协作接口 IVideoSwitcher）
└─ Plugins/
   ├─ WindowFixPlugin/          恢复窗口大小（旧版迁移）
   ├─ EogSkipPlugin/            跳过卡结算（旧版迁移）
   ├─ ReloadPlugin/             热重载客户端（旧版迁移）
   ├─ AcceptPlugin/             接收对局（旧版迁移）
   ├─ MatchHistoryPlugin/       查询战绩（原生）
   ├─ WatchVideoPlugin/         看视频·手动 v0（原生）
   └─ DeathWatchPlugin/         看视频·自动 v1（原生）
tools/probe/                    测试基建：发布前必跑的探针（进仓库）
tools/release.ps1               发布脚本（进仓库）
tools/（其余）                  本地调试脚本（gitignore）
```

## 开发流程（五阶段门禁）

每次新功能按 `模板-新功能.md` 填一张开发单，五个阶段依次推进，**门禁不满足不进下一步**：

| 阶段 | 产出 | 门禁 |
|---|---|---|
| 需求 | 写进 TODO，含**可观测的验收标准** | 没有验收标准 → 不进设计 |
| 设计 | 数据来源 / 失败模式 / 红线检查（端点先真机实测） | 要读内存或注入 → 直接否决 |
| 开发 | 插件目录（代码 + plugin.json + 说明.md） | `dotnet build -c Release` 有警告 → 先清干净 |
| 测试 | 三层证据：探针 → selftest → 真机 | 真机未实测 → 不进 dist |
| 发布 | `dist/` + tag（+ GitHub Release） | 用户未确认 → 不写 dist |

**测试基建**（发布前必跑，7 项检查一次出结果，全过退出码 0）：

```bash
dotnet run --project tools/probe          # 默认找 .dev/plugins，也可传目录参数
python tools/probe/make_fixture.py        # 对局中重抓 Live 接口夹具（人名已脱敏）
```

覆盖：Live 接口解析（含「认出自己」）/ 插件依赖解析（隔离 ALC 复刻宿主规则）/
SMTC 媒体会话 / 游戏音频会话 / 自动收尾四组合 / OCR 数字识别 / GDI 抓屏。
**探针全过 ≠ 真机没问题** —— 切窗口、真死亡这些物理行为仍要对局验证。

**发布**（一条命令）：

```powershell
powershell -ExecutionPolicy Bypass -File tools\release.ps1 -Version 0.3.0          # 出 dist + tag
powershell -ExecutionPolicy Bypass -File tools\release.ps1 -OutDir .dev -NoTag     # 开发迭代包
```

自动完成：编译（有警告直接中止）→ publish → 装配插件（含 WinRT 依赖、剔除 Contracts、
选最新 TFM 目录）→ 清理发布包（剔除设置/日志/pdb）→ 离屏自测 → tag → zip
（`-Upload` 再发 GitHub Release）。**门禁不在脚本里**：进 dist 仍是「用户确认过」这一条。

## 自测（不弹窗）

```bash
publish/LOLToolbox.exe --selftest <png>                    # 主窗口离屏自渲染
publish/LOLToolbox.exe --selftest-settings <png默认> <png展开>  # 设置窗离屏自渲染 + 控件清单入日志
```

截图存盘后自动退出；设置窗的验证还会把每一行 schema 的「标签 + 控件类型 + 当前值」
写进 `采样日志.txt`（搜「自测·设置行」），比肉眼核对截图可靠。

## 技术备注

- 凭据通过 `NtQueryInformationProcess`（class 60）从 `LeagueClientUx.exe` 命令行读取，
  普通权限即可（国服客户端不写 lockfile）；LCU 是 Riot 非官方接口，个人自用（合规分析见上节）。
- 宿主仍是 framework-dependent 单文件 exe；插件在 `plugins/<id>/` 下动态加载
  （自定义 `AssemblyLoadContext`，Contracts 复用宿主持有的那一份保证类型同一性）。
- 窗口修复算法参考开源项目 `LeagueTavern/fix-lcu-window`（MIT），已内联为 WindowFixPlugin。

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

## 仓库与发布流程

- 仓库：<https://github.com/anonymous-100/LolClientToolbox>（`main` 分支＝可用源码）
- 分支策略：改动直接提交 `main`（单人项目，不折腾 PR）
- 提交前必须：`dotnet build -c Release` 零错误零警告
- **产物纪律**：
  - `.dev/` 放**开发迭代**构建（未验证，随便覆盖）
  - `dist/` 放**已验证**构建（用户确认"没问题"之后才写入）
  - 两者都不进仓库；发布包走 GitHub Releases 附件
- 新增/修改功能时：同步更新该插件的 `plugins/<id>/说明.md`（右键可看的就是它）

### 当前状态（2026-10-25）

7 个插件；「看视频」手动版已实测（切出/切回/静音/自动收尾全通）。**新增自动版（v1）**：
死亡自动切出、复活自动切回 —— 探针 7 项全过，**真机对局验证还没做**（下一局确认）。
技术上当日修掉一个隐性大坑：多个插件各自带 WinRT 投影时，第二份初始化会
炸（进程级全局单例），宿主已改为预载共享运行时。

## 文档索引

| 文档 | 用途 |
|---|---|
| [交接文档.md](交接文档.md) | 上一次交接快照（2026-10-24）：现状、怎么跑、踩过的坑 |
| [TODO.md](TODO.md) | 待办清单（P0/P1/P2 + 已否决项及理由） |
| [需求评估.md](需求评估.md) | 各功能可行性结论与实测证据 |
| [API手册.md](API手册.md) | **挖新功能先看这个**：LCU / Live 两套接口的能力地图、可写端点、候选功能评价 |
| [插件开发手册.md](插件开发手册.md) | 写插件的契约与约定（含「加功能要不要动宿主」判据） |
| [模板-新功能.md](模板-新功能.md) | 开新功能时复制填的开发单（五阶段门禁） |
