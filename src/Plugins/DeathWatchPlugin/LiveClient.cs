using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace DeathWatchPlugin;

/// <summary>
/// Live Client Data API 的读取器 —— 死亡检测的主数据源。
///
/// 两个必须记住的事实（都踩过）：
///  ① 是 **https**（自签证书），不是 http。用 http 探只会得到超时/连接失败，
///     那种"探不到"的结论是无效的（2026-10-24 用 http 探过一次，白费一轮）。
///  ② 只在**对局进行中**由游戏进程提供，端口 2999。对局一结束接口就消失，
///     所以「取不到」要先区分「不在对局」还是「接口挂了」，别一律当故障报。
///
/// 这条接口是 Riot 官方给第三方用的（读自己账号在对局里的状态），
/// 属于官方机制那一侧 —— 不读内存、不注入，符合红线。
/// </summary>
internal sealed class LiveClient : IDisposable
{
    private readonly HttpClient _http;
    private bool _disposed;

    public LiveClient(int port, int timeoutMs)
    {
        var handler = new HttpClientHandler
        {
            // 证书是游戏自己签的，校验必然失败 —— 只连 127.0.0.1，接受它。
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(timeoutMs) };
        _http.BaseAddress = new Uri($"https://127.0.0.1:{port}");
    }

    /// <summary>
    /// 读复活倒计时（秒）。
    /// </summary>
    /// <returns>
    /// &gt;0 = 死亡，数字是还要等几秒；0 = 活着（或刚复活）；
    /// null = 取不到（不在对局 / 接口没起 / 解析失败），reason 说明是哪种。
    /// </returns>
    public async Task<(double? Timer, string Reason)> RespawnTimer(string endpoint)
    {
        if (_disposed) return (null, "已停止");
        try
        {
            var body = await _http.GetStringAsync(endpoint).ConfigureAwait(false);
            return Parse(body);
        }
        catch (Exception ex)
        {
            // 超时/连不上/非 2xx 都算「取不到」—— 绝大多数时候只是没在对局
            return (null, ex.Message);
        }
    }

    /// <summary>
    /// 从响应里抠**自己**的 respawnTimer。
    ///
    /// ⚠ 2026-10-25 真机实测推翻了 TODO 里的假设：
    ///   · <c>activePlayer</c> 里**没有** respawnTimer（只有技能/金币/等级/符文）；
    ///   · respawnTimer 在 <c>allPlayers[]</c> 每个条目上（0 = 活着，&gt;0 = 死亡倒计时），
    ///     所以必须先认出「哪个人是我」：用 activePlayer.riotIdGameName 对
    ///     allPlayers[].riotIdGameName（实测唯一命中），对不上再比 summonerName。
    /// 兼容写法：万一哪天接口又把 respawnTimer 放回根对象/activePlayer，也会直接读。
    /// </summary>
    internal static (double? Timer, string Reason) Parse(string json)
    {
        JsonElement root;
        try
        {
            // Clone() 让元素脱离 using 的 doc 独立持有数据，后面用的时候不会踩已释放内存
            using var doc = JsonDocument.Parse(json);
            root = doc.RootElement.Clone();
        }
        catch { return (null, "响应不是 JSON"); }

        // 旧口径兜底：根对象或 activePlayer 上直接有 respawnTimer
        if (TryRead(root, out var direct)) return (direct, "根对象");
        if (root.TryGetProperty("activePlayer", out var ap))
        {
            if (TryRead(ap, out var v)) return (v, "activePlayer");
        }

        if (!root.TryGetProperty("allPlayers", out var players)
            || players.ValueKind != JsonValueKind.Array
            || players.GetArrayLength() == 0)
            return (null, "响应里没有 allPlayers");

        // 认自己：riotIdGameName 优先（实测唯一），再试 summonerName / riotId
        string meName = "";
        if (root.TryGetProperty("activePlayer", out var me))
        {
            meName = StrOf(me, "riotIdGameName") is { Length: > 0 } n ? n : StrOf(me, "summonerName") ?? "";
        }
        if (meName.Length == 0) return (null, "认不出自己（activePlayer 缺名字字段）");

        JsonElement? mine = null;
        foreach (var p in players.EnumerateArray())
        {
            var n1 = StrOf(p, "riotIdGameName");
            var n2 = StrOf(p, "summonerName");
            var n3 = StrOf(p, "riotId");
            if ((n1 != null && n1 == meName)
                || (n2 != null && n2 == meName)
                || (n3 != null && n3.StartsWith(meName + '#', StringComparison.Ordinal)))
            {
                mine = p;
                break;
            }
        }
        if (mine is null) return (null, $"allPlayers 里找不到自己（{meName}）");

        if (!mine.Value.TryGetProperty("respawnTimer", out var t))
            return (null, "allPlayers[自己] 里没有 respawnTimer");

        if (t.ValueKind == JsonValueKind.Number) return (t.GetDouble(), "allPlayers");
        if (t.ValueKind == JsonValueKind.String
            && double.TryParse(t.GetString(), out var parsed)) return (parsed, "allPlayers");
        return (null, "respawnTimer 类型不认识");
    }

    private static string? StrOf(JsonElement el, string key) =>
        el.ValueKind == JsonValueKind.Object
        && el.TryGetProperty(key, out var v)
        && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool TryRead(JsonElement el, out double value)
    {
        value = 0;
        if (el.ValueKind != JsonValueKind.Object) return false;
        if (!el.TryGetProperty("respawnTimer", out var t)) return false;
        if (t.ValueKind == JsonValueKind.Number) { value = t.GetDouble(); return true; }
        if (t.ValueKind == JsonValueKind.String
            && double.TryParse(t.GetString(), out var parsed)) { value = parsed; return true; }
        return false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _http.Dispose();
    }
}
