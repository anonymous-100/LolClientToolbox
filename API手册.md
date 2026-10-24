# API 手册（本机实地扫描）

扫描日期：**2026-10-25**，对象：本机 `LeagueClientUx.exe`（端口 14857，客户端在运行）
`/help` 计数：`events=748` `functions=1447` `types=3563`

> 计数字**每次客户端更新都会漂**（10-14 是 721/1444/3540，10-23 是 756/1447/3563）。
> 引用条数必须带日期，别当常数。重扫方法见文末。

---

## 一、有两套 API，别搞混

| | LCU（客户端） | Live Client Data（对局中） |
|---|---|---|
| 地址 | `https://127.0.0.1:<随机端口>` | `https://127.0.0.1:2999` |
| 凭据 | basic auth，端口+口令从**进程命令行**抠 | **不需要凭据** |
| 生效时机 | 客户端开着就有 | **只在游戏对局进行中** |
| 能拿到 | 房间、匹配、选人、结算、好友、战利品、设置… | 对局实时数据：复活倒计时、KDA、装备、金币 |
| 谁在用 | windowfix / eogskip / accept / matchhistory | deathwatch（v1 自动死亡） |

两套都是 https + Riot 自签证书 —— **必须忽略证书校验**，用 http 连不上。

---

## 二、怎么连（国服有个坑）

### 凭据

Riot 客户端写 `lockfile`，**国服（腾讯）不写** —— 实测 `LeagueClient\lockfile` 恒为 0 字节。
凭据只在进程命令行里：

```
--app-port=14857 --remoting-auth-token=<口令>
```

读取方式：`NtQueryInformationProcess` 的 **class 60**（`ProcessCommandLineInformation`），
只要 `PROCESS_QUERY_LIMITED_INFORMATION`，不用读目标进程内存，普通权限即可。
进程名先试 `LeagueClientUx`，再试 `LeagueClient`。

⚠️ **必须排除 `--riotclient-app-port`** —— 那是 Riot Client 自己的端口，用错会连到错的地方。

认证头：`Authorization: Basic base64("riot:" + 口令)`

### 没有官方手册（这条要记牢）

Riot 的原话：

> This service is not officially supported for use with third party applications.
> We provide no guarantees of full documentation, service uptime, or change communication.

swagger 也**已经被 Riot 摘掉**了，本机实测全 404：

```
404  /swagger/v2/swagger.json
404  /swagger/v3/openapi.json
404  /openapi.json
```

**所以权威来源只有一个：客户端自己给的 `/help`。** 网上教程常常过期（我们已经踩过两次：
`activePlayer.respawnTimer` 和 `POST /lol-lobby/v2/ready-check/accept` 都是错的）。

---

## 三、判端口径（省得反复试错）

这一节最实用 —— 状态码的含义和直觉不一样：

| 返回 | 含义 | 怎么办 |
|---|---|---|
| `Invalid URI format` | 这个端点**压根没实现** | 别再调了 |
| `RPC_ERROR` / `XXX_NOT_FOUND` + 业务描述 | 端点**存在**，只是当前没数据 | 可以当"空"正常用 |
| `WRONG_METHOD` / 405 | 端点存在，但你用错 HTTP 动词 | 换动词 |
| `500` | 端点存在但你用错了时机（如无确认框时接受） | 加前置判断 |

---

## 四、能力地图（2026-10-25 实扫，按模块 × 动词）

只列前 25 个模块。`Get`=读，`Post/Put/Delete/Patch`=**能做事**。

| 模块 | 总数 | Get | Post | Put | Del | 一句话 |
|---|---|---|---|---|---|---|
| Clash | 88 | 43 | 43 | | 2 | 战队赛 |
| Lobby | 84 | 28 | 34 | 15 | 6 | 房间、邀请、自定义 |
| **Chat** | 58 | 31 | 10 | 8 | 9 | 好友、会话、屏蔽 |
| **ChampSelect** | 51 | 26 | 23 | | | **选人：锁英雄/禁用/交换** |
| **Gameflow** | 49 | 22 | 23 | | 4 | **阶段流转、重连、观战** |
| LobbyTeamBuilderChampSelect | 44 | 23 | 19 | | | 组排版选人 |
| Summoner | 39 | 27 | 10 | 2 | | 查人、改资料 |
| Loot | 36 | 25 | 8 | 1 | 2 | 战利品、合成、开箱 |
| PremadeVoice | 31 | 10 | 9 | 10 | 2 | 预组队语音 |
| TftTeamPlanner | 31 | 6 | 11 | 2 | 5 | 云顶阵容规划 |
| **Perks** | 29 | 15 | 5 | 6 | 3 | **符文页** |
| EventHub | 28 | 25 | 3 | | | 活动事件 |
| Cosmetics | 26 | 10 | 8 | | 7 | 皮肤/表情/宠物 |
| Store | 25 | 22 | 3 | | | 商店 |
| Honor | 23 | 13 | 9 | | 1 | 荣誉/点赞 |
| Inventory | 23 | 21 | 2 | | | 资产 |
| Ranked | 19 | 17 | 2 | | | 段位 |
| Settings | 18 | 8 | 4 | 2 | | 客户端设置 |
| Login | 17 | 7 | 6 | 1 | 3 | 登录会话 |
| PlayerBehavior | 17 | 12 | 1 | 2 | 2 | 行为分/惩罚 |
| Challenges | 17 | 14 | 3 | | | 成就 |
| ChampionMastery | 12 | 6 | 5 | | 1 | 英雄熟练度 |
| **Replays** | 10 | 4 | 6 | | | **回放：下载/观看** |
| Missions | 11 | 7 | 1 | 3 | | 任务 |
| **Matchmaking** | 5 | | 3 | 1 | 1 | **排队/接受/取消** |

---

## 五、能「做事」的端点（写操作，挑重点）

### 选人阶段（ChampSelect）——目前完全没用起来

```
Patch  /lol-champ-select/v1/session/my-selection          # 选/预选某个英雄
Patch  /lol-champ-select/v1/session/actions/{id}          # 执行动作（锁 / 禁用 / 传）
Post   /lol-champ-select/v1/session/actions/{id}/complete # 确认（锁定的最后一步）
Post   /lol-champ-select/v1/session/my-selection/reroll   #  reroll（大乱斗）
Post   /lol-champ-select/v1/session/bench-swap/{championId}
Post   /lol-champ-select/v1/session/trades/{id}/accept|decline|request
Post   /lol-champ-select/v1/toggle-player-muted           # 一键静音队友
```
（组排走同一套，前缀换成 `/lol-lobby-team-builder/champ-select/v1/`，实测有 21 个写操作。）

### 排队 / 接受

```
Post   /lol-matchmaking/v1/search            # 开始排队（已在用：自动接收是另一条）
Delete /lol-matchmaking/v1/search            # 取消排队
Post   /lol-matchmaking/v1/ready-check/accept    # ← 现有「接收对局」插件在用
Post   /lol-matchmaking/v1/ready-check/decline
```

### 符文（Perks）

```
Put    /lol-perks/v1/currentpage             # 切当前符文页
Post   /lol-perks/v1/pages                   # 新建符文页
Put    /lol-perks/v1/pages/{id}              # 改符文页
Post   /lol-perks/v1/recommended-pages/position-champion/{championId}/position/{position}
Post   /lol-perks/v1/rune-recommender-auto-select
```

### 回放（Replays）

```
Post   /lol-replays/v1/rofls/{gameId}/download          # 下载回放
Post   /lol-replays/v1/rofls/{gameId}/download-graceful
Post   /lol-replays/v1/rofls/{gameId}/watch             # 观看回放
Post   /lol-replays/v1/rofls/scan                       # 扫本地回放文件
Post   /lol-replays/v2/metadata/{gameId}/create
```

### 荣誉 / 点赞（Honor）

```
Post   /lol-honor-v2/v1/honor-player
Post   /lol-honor-v2/v1/ballot-refresh
Get    /lol-honor-v2/v1/ballot
```

### 游戏流程（Gameflow）

```
Post   /lol-gameflow/v1/reconnect            # 重连
Post   /lol-gameflow/v1/session/dodge        # 秒退（有惩罚，见第八节）
Post   /lol-gameflow/v1/spectate-launch      # 观战
Post   /lol-gameflow/v1/ack-failed-to-launch
Delete /lol-gameflow/v1/early-exit-notifications/eog
```

### 聊天 / 好友（Chat）

```
Post   /lol-chat/v1/conversations/{id}/messages   # 发消息
Post   /lol-chat/v1/player-mutes                  # 屏蔽玩家
Put    /lol-chat/v1/friends/{id}                  # 改好友备注/分组
```

### 战利品（Loot）——涉及资源，谨慎

```
Post   /lol-loot/v1/recipes/{recipeName}/craft   # 合成（开箱、分解都走这个）
Post   /lol-loot/v1/craft-mass                   # 批量合成
Post   /lol-loot/v1/player-loot/{lootName}/redeem
Post   /lol-loot/v1/milestones/{id}/claim        # 领里程碑奖励
```

---

## 六、可订阅事件（比轮询强）

WebSocket：`wss://127.0.0.1:<port>/`，子协议 `wamp`，握手后发 `[5, "OnJsonApiEvent_<名字>"]`。
事件名里的下划线换成斜杠就是精确路径（`/help` 的 `events` 字段 748 条）。

事件最多的模块：`platform-config`(389)、`champ-select`(16)、`chat`(16)、`clash`(12)、
`honor-v2`(12)、`lobby`(10)、`gameflow`(9)。

已在使用 / 值得订阅的：

```
/lol-gameflow/v1/gameflow-phase          阶段变化（已在用）
/lol-matchmaking/v1/ready-check          确认框（已在用）
/lol-champ-select/v1/session             选人会话全程
/lol-honor-v2/v1/ballot                  点赞候选
/lol-replays/v1/metadata                 回放就绪
```

**重要陷阱**：客户端静止时**一条事件都不推**。不要用「没收到帧」判断订阅是否可用，
必须等一次真实状态变化。设计上订阅只做加速，轮询留着兜底。

---

## 七、还能做什么（我的建议，按推荐度排）

评判标准：**省事程度 × 频率 ÷ 风险**。红线（读内存/注入/消费）一律不做。

### ★★★ 自动锁英雄（ChampSelect）

- **痛点**：排队时人不在座位，回来英雄没锁、被随机，或者干脆秒退扣了分。
  这个痛点是**高频**的（每局都会经历选人），而且我们已有"自动接收"把前半段接通了。
- **端点**：`Patch .../session/my-selection` → `Patch .../session/actions/{id}` → `Post .../actions/{id}/complete`
- **配套**：`/lol-champ-select/v1/session` 能读到当前该谁行动、倒计时多少
- **风险低**：只是替你点按钮；但要做"预设英雄列表 + 位置优先级"，别瞎锁
- **收益**：和现有「接收对局」串起来 = 从匹配到进游戏全程不用管

### ★★★ 战绩插件接回放（Replays × MatchHistory）

- **痛点**：现在「查询战绩」只能看数据，看完想复盘还得自己找回放
- **端点**：`Post /lol-replays/v1/rofls/{gameId}/download` + `/watch`
- **凭什么能做**：MatchHistoryPlugin 已经在拉近 20 局，**gameId 本来就在手边**，
  给列表每行加一个「回放」按钮即可，是现有插件的自然延伸
- **风险低**：下载/播放都是客户端自己的功能

### ★★ 符文页自动套用（Perks）

- **痛点**：选完英雄还要手点符文页，尤其常玩多英雄时
- **端点**：`Put /lol-perks/v1/currentpage`，配合
  `Post /lol-perks/v1/recommended-pages/position-champion/{championId}/position/{position}`
- **风险低**：但要注意只在选人阶段写，别在排队时改

### ★★ 结算后自动点赞（Honor）

- **端点**：`Post /lol-honor-v2/v1/honor-player`
- **价值中等**：TODO 里曾判"价值低"，我同意它不解决痛点，但成本极低，
  且配合「跳过卡结算」能凑成"打完就走"的完整闭环
- **风险低**：只是点赞

### ★ 自动排队（Matchmaking）

- **端点**：`Post /lol-matchmaking/v1/search`
- **价值看人**：喜欢连排的人有用，但容易变成"不想排的时候也在排"
- **建议**：只在「上一局刚结束且开启了连排」时触发，默认关

### ★ 一键静音队友（ChampSelect）

- `Post /lol-champ-select/v1/toggle-player-muted`
- 小功能，选人界面加个按钮。价值一般

### 已评估不做的

| 想法 | 理由 |
|---|---|
| 秒退 dodge | 有排队惩罚和扣分，替用户做这个决策不合适 |
| 开箱/分解/购买 | 涉及真实资源消耗，误操作不可逆 |
| 自动发消息 | 以你的名义说话，边界问题 |
| 读取游戏内存 | Riot 2026-10-06 起封禁，见 TODO 已否决项 |

---

## 八、怎么自己查（别信二手文档）

```bash
# 列出可订阅事件（默认关键词：gameflow/end-of-game/lobby/matchmaking/replay）
python E:/games/LOL客户端/tools/lcu_help.py

# 自定义关键词
python E:/games/LOL客户端/tools/lcu_help.py --kw champ-select

# 查函数清单（含 HTTP 动词）—— 找「能做事」的端点靠这个
python E:/games/LOL客户端/tools/lcu_help.py --fn champ-select

# 实测具体路径的状态码（判端口径见第三节）
python E:/games/LOL客户端/tools/lcu_help.py --probe /lol-champ-select/v1/session

# 全量事件路径落盘
python E:/games/LOL客户端/tools/lcu_help.py --dump
```

新仓库这边也留了入口（凭据 + 端点清单，走的是同一套 class 60 读法）：

```bash
dotnet run --project tools/probe -- cred    # 打印端口和口令
dotnet run --project tools/probe -- api     # 拉 /help 存到 %TEMP%/lcu_help.json
```

> 注意：`-- cred/api` 依赖读客户端进程命令行。若你的客户端是**管理员权限**运行的，
> 普通权限进程读不到（我这边实测就是这样，最后靠旧目录里那套 `lcu_http_test.py` 拿到的）。
> 客户端用普通权限启动即可 —— 反正「看视频」插件也要求这个（UIPI 限制）。

---

## 九、Live Client Data API（对局中）

`https://127.0.0.1:2999`，无凭据，只在游戏进行中可用。

| 端点 | 内容 |
|---|---|
| `/liveclientdata/allgamedata` | 全部（含 `allPlayers[]`、`activePlayer`、`events`、`gameData`） |
| `/liveclientdata/activeplayer` | 自己：技能、等级、金币、战绩（**不含复活倒计时**，实测确认） |
| `/liveclientdata/playerlist` | 十个人列表 |
| `/liveclientdata/gamestats` | 游戏模式、时长 |

⚠️ **`respawnTimer` 在 `allPlayers[]` 里，不在 `activePlayer` 里**（2026-10-25 真机实测，
网上教程写的 `activePlayer.respawnTimer` 是错的）。要先按 `activePlayer.riotIdGameName`
认出「哪个人是我」，再读那个人的 `respawnTimer`（0=活着，>0=死亡倒计时秒数）。
详见 `src/Plugins/DeathWatchPlugin/说明.md`。
