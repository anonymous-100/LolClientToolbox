using System.Globalization;
using System.Text.Json;
using LolClientToolbox.Contracts;

namespace MatchHistoryPlugin;

/// <summary>一局比赛的摘要行。</summary>
internal sealed class GameRow
{
    public long GameId;
    public DateTime When;
    public string Mode = "";
    public string Champion = "";
    public bool Win;
    public int Kills, Deaths, Assists, Damage, ChampLevel;
    public int DurationSec;
    public string QueueLabel = "";
    public List<ScoreLine> Scoreboard = new();
}

/// <summary>详情页里的一名玩家。</summary>
internal sealed class ScoreLine
{
    public string Champion = "";
    public string Player = "";
    public int K, D, A, Damage;
    public bool Win;
}

/// <summary>
/// 战绩数据访问。全部走宿主给的 GetAsync（凭据宿主自动带）。
///
/// 端点均经本机实测（2026-10-23）：
///   自己   GET /lol-match-history/v1/products/lol/current-summoner/matches
///   别人   GET /lol-match-history/v1/products/lol/{puuid}/matches
///   别人入口 GET /lol-match-history/v1/recently-played-summoners（最近一起玩过的人）
///   自己身份 GET /lol-summoner/v1/current-summoner
///   英雄名  GET /lol-game-data/assets/v1/champion-summary.json
/// 注意：没有「按名字查战绩」的端点（/lol-summoner/v1/summoners 只按 summonerId 查），
/// 所以「别人」的入口就是最近一起玩过的人列表，不做任意名字输入。
/// </summary>
internal sealed class MatchHistoryService
{
    private const string ChampionsAsset = "/lol-game-data/assets/v1/champion-summary.json";
    private const string RecentSummoners = "/lol-match-history/v1/recently-played-summoners";
    private const string CurrentSummoner = "/lol-summoner/v1/current-summoner";

    private readonly IPluginHost _host;
    private Dictionary<int, string> _champions = new();

    public MatchHistoryService(IPluginHost host) => _host = host;

    private static readonly Dictionary<string, string> ModeNames = new()
    {
        ["KIWI"] = "海克斯大乱斗",
        ["CLASSIC"] = "召唤师峡谷",
        ["ARAM"] = "极地大乱斗",
        ["URF"] = "无限火力",
        ["ONEFORALL"] = "克隆大作战",
        ["TFT"] = "云顶之弈",
        ["PRACTICETOOL"] = "训练模式",
        ["BOT"] = "人机对战",
        ["TUTORIAL"] = "新手教程",
    };

    public static string ModeLabel(string gameMode, int queueId) =>
        ModeNames.TryGetValue(gameMode, out var zh) ? zh : gameMode + $" (#{queueId})";

    /// <summary>自己的 puuid。</summary>
    public async Task<string> GetSelfPuuidAsync()
    {
        var r = await _host.GetAsync(CurrentSummoner);
        if (r.Status < 200 || r.Status >= 300) throw new InvalidOperationException($"读取当前召唤师失败（{r.Status}）");
        using var d = JsonDocument.Parse(r.Body);
        return d.RootElement.GetProperty("puuid").GetString() ?? "";
    }

    /// <summary>最近一起玩过的人（别人战绩的入口）。</summary>
    public async Task<List<(string Puuid, string Label)>> GetRecentSummonersAsync()
    {
        var list = new List<(string, string)>();
        var r = await _host.GetAsync(RecentSummoners);
        if (r.Status < 200 || r.Status >= 300) return list;
        using var d = JsonDocument.Parse(r.Body);
        if (d.RootElement.ValueKind != JsonValueKind.Array) return list;

        foreach (var s in d.RootElement.EnumerateArray())
        {
            var puuid = Str(s, "puuid");
            if (puuid.Length == 0) continue;
            var gameName = Str(s, "gameName");
            var summonerName = Str(s, "summonerName");
            var tag = Str(s, "tagLine");
            var label = gameName.Length > 0 ? gameName : summonerName;
            if (tag.Length > 0) label += "#" + tag;
            if (label.Length == 0) label = puuid[..8] + "…";
            list.Add((puuid, label));
        }
        return list;
    }

    /// <summary>查战绩。puuid 为空 = 自己（自动取 current-summoner 的 puuid）。</summary>
    public async Task<List<GameRow>> GetMatchesAsync(string puuid)
    {
        await EnsureChampionsAsync();

        // 查自己也走具体 puuid：入口端点只认 puuid 匹配，统一逻辑少一条分支
        if (puuid.Length == 0) puuid = await GetSelfPuuidAsync();

        var path = $"/lol-match-history/v1/products/lol/{Uri.EscapeDataString(puuid)}/matches";
        var r = await _host.GetAsync(path);
        if (r.Status < 200 || r.Status >= 300)
            throw new InvalidOperationException(MatchErrorMessage(r.Status));

        var rows = new List<GameRow>();
        using var d = JsonDocument.Parse(r.Body);
        if (!d.RootElement.TryGetProperty("games", out var gamesObj)
            || gamesObj.ValueKind != JsonValueKind.Object
            || !gamesObj.TryGetProperty("games", out var arr)
            || arr.ValueKind != JsonValueKind.Array)
            return rows;

        foreach (var g in arr.EnumerateArray())
        {
            var row = new GameRow();
            row.GameId = Long(g, "gameId");   // 局号，按局拉完整计分板用
            row.Mode = ModeLabel(Str(g, "gameMode"), Int(g, "queueId"));
            row.DurationSec = Int(g, "gameDuration");

            var dateStr = Str(g, "gameCreationDate");
            if (DateTime.TryParse(dateStr, CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt))
                row.When = dt.ToLocalTime();

            // 找目标玩家：participantIdentities 里 puuid 匹配 → participantId → participants
            int targetPid = 0;
            if (g.TryGetProperty("participantIdentities", out var ids) && ids.ValueKind == JsonValueKind.Array)
            {
                foreach (var id in ids.EnumerateArray())
                {
                    if (!id.TryGetProperty("player", out var player)) continue;
                    if (Str(player, "puuid") == puuid)
                    {
                        targetPid = Int(id, "participantId");
                        break;
                    }
                }
            }
            if (targetPid == 0) continue;   // 这局里没有目标玩家（观赛/重开等），跳过

            if (g.TryGetProperty("participants", out var parts) && parts.ValueKind == JsonValueKind.Array)
            {
                foreach (var p in parts.EnumerateArray())
                {
                    int pid = Int(p, "participantId");
                    var champ = ChampName(Int(p, "championId"));
                    var st = p.TryGetProperty("stats", out var s) ? s : default;

                    var line = new ScoreLine
                    {
                        Champion = champ,
                        Player = PlayerName(ids, pid),
                        K = Int(st, "kills"),
                        D = Int(st, "deaths"),
                        A = Int(st, "assists"),
                        Damage = Int(st, "totalDamageDealtToChampions"),
                        Win = Bool(st, "win"),
                    };

                    if (pid == targetPid)
                    {
                        row.Champion = champ;
                        row.Win = line.Win;
                        row.Kills = line.K;
                        row.Deaths = line.D;
                        row.Assists = line.A;
                        row.Damage = line.Damage;
                        row.ChampLevel = Int(st, "champLevel");
                    }
                    row.Scoreboard.Add(line);
                }
            }
            if (targetPid != 0) rows.Add(row);
        }
        return rows;
    }

    // 查自己时 puuid 在 GetMatchesAsync 内部自动取，这里不再需要缓存

    /// <summary>
    /// 按局号拉「当局所有人」的完整计分板。
    ///
    /// 【实测】列表端点（matches）每局只带 1 个 participant（被查询者本人），
    /// 2026-10-24 本机验证：participants=1。完整 10 人数据在另一个端点：
    /// GET /lol-match-history/v1/games/{gameId} → participants=10，字段与列表里一致。
    /// </summary>
    public async Task<List<ScoreLine>> GetScoreboardAsync(long gameId)
    {
        var r = await _host.GetAsync($"/lol-match-history/v1/games/{gameId}");
        if (r.Status < 200 || r.Status >= 300)
            throw new InvalidOperationException(MatchErrorMessage(r.Status));

        var list = new List<ScoreLine>();
        using var d = JsonDocument.Parse(r.Body);
        if (!d.RootElement.TryGetProperty("participantIdentities", out var ids)
            || !d.RootElement.TryGetProperty("participants", out var parts)
            || parts.ValueKind != JsonValueKind.Array)
            return list;

        foreach (var p in parts.EnumerateArray())
        {
            var st = p.TryGetProperty("stats", out var s) ? s : default;
            list.Add(new ScoreLine
            {
                Champion = ChampName(Int(p, "championId")),
                Player = PlayerName(ids, Int(p, "participantId")),
                K = Int(st, "kills"),
                D = Int(st, "deaths"),
                A = Int(st, "assists"),
                Damage = Int(st, "totalDamageDealtToChampions"),
                Win = Bool(st, "win"),
            });
        }
        return list;
    }

    private static string PlayerName(JsonElement ids, int pid)
    {
        if (ids.ValueKind != JsonValueKind.Array) return $"玩家#{pid}";
        foreach (var id in ids.EnumerateArray())
        {
            if (Int(id, "participantId") != pid) continue;
            if (!id.TryGetProperty("player", out var p)) break;
            var gameName = Str(p, "gameName");
            var tag = Str(p, "tagLine");
            var name = gameName.Length > 0 ? gameName : Str(p, "summonerName");
            if (tag.Length > 0) name += "#" + tag;
            // 三级兜底：gameName → summonerName → 玩家#pid（国服偶发匿名，不能显示成空白）
            return name.Length > 0 ? name : $"玩家#{pid}";
        }
        return $"玩家#{pid}";
    }

    private async Task EnsureChampionsAsync()
    {
        if (_champions.Count > 0) return;
        var r = await _host.GetAsync(ChampionsAsset);
        if (r.Status < 200 || r.Status >= 300) return;
        try
        {
            using var d = JsonDocument.Parse(r.Body);
            if (d.RootElement.ValueKind != JsonValueKind.Array) return;
            foreach (var c in d.RootElement.EnumerateArray())
                _champions[Int(c, "id")] = Str(c, "name");
        }
        catch { /* 英雄名拿不到就用 id 显示，不阻断功能 */ }
    }

    private string ChampName(int id) =>
        _champions.TryGetValue(id, out var n) ? n : $"英雄{id}";

    /// <summary>
    /// 战绩接口的报错文案。按状态码说准话 —— 这个接口在客户端重启后 + 对局中会 500，
    /// 凭据轮换后会 401，都说成「没连上」会把人带跑偏。
    ///
    /// 【2026-10-25 实测补充】500/503 绝大多数时候**不是本机问题**：
    /// 客户端会把请求转给腾讯的 match-history-query 服务
    /// （hn1-k8s-sgp.lol.qq.com:21019），该服务故障时客户端在 70ms 内就拿到 500，
    /// 自己 + 同场其他人 + 好友全部查不到。以前文案写「对局中会偶发」会让人以为
    /// 是自己客户端坏了，白折腾重启。这里改成直说服务端。
    /// </summary>
    private static string MatchErrorMessage(int status) => status switch
    {
        -1 => "客户端未连接（客户端没开？）",
        401 => "凭据失效（客户端重启过），稍后重试或重启客户端",
        404 => "没有对局记录",
        500 => "腾讯战绩服务器返回 500（服务端故障，不是本机问题），过一会儿再试",
        503 => "腾讯战绩服务器暂时不可用（503，服务端问题），过一会儿再试",
        _ => $"战绩接口返回 {status}",
    };

    // ---- JSON 小工具（缺字段/类型不对都返回默认值，绝不当众崩掉） ----
    private static string Str(JsonElement e, string prop) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v)
        && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static int Int(JsonElement e, string prop) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v)
        && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : 0;

    /// <summary>局号是 long（实测值 11402794228，超 int 范围，用 Int32 解析会得 0）。</summary>
    private static long Long(JsonElement e, string prop) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v)
        && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;

    private static bool Bool(JsonElement e, string prop) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v)
        && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False)
        && v.GetBoolean();
}
