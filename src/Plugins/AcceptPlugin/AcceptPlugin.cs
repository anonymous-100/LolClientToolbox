using System;
using System.Text.Json;
using System.Threading.Tasks;
using LolClientToolbox.Contracts;

namespace AcceptPlugin;

/// <summary>
/// 接收对局 —— 匹配成功弹出确认框（ReadyCheck）时自动点「接受」。
///
/// 这是「插件开发手册」里的参考样例：结构最干净，覆盖了卡片、手动执行、
/// 阶段触发、事件订阅、tick 兜底、设置持久化、旧版迁移全套写法。
///
/// 端点经客户端 /help 核实：POST /lol-matchmaking/v1/ready-check/accept。
/// </summary>
public sealed class AcceptPlugin : ILolPlugin, ISettingsChangedAware, ISelfTestAware
{
    private const string ReadyCheckEventUri = "/lol-matchmaking/v1/ready-check";
    private const string DefaultEndpoint = "/lol-matchmaking/v1/ready-check/accept";

    private IPluginHost _host = null!;
    private IPluginSettings _settings = null!;
    private ICard _card = null!;

    // 三道闸防重复打（确认框可能被阶段推送、ready-check 事件、tick 兜底三路同时触发）
    private readonly object _lock = new();
    private bool _auto;
    private string _endpoint = DefaultEndpoint;
    private bool _done;       // 本次确认框已处理（接受成功，或玩家自己选过了）
    private bool _inFlight;   // 请求在途
    private int _tries;       // 本次确认框已尝试次数
    private DateTime _lastTry = DateTime.MinValue;

    public string Id => "accept";
    public string Name => "接收对局";

    public void Init(IPluginHost host, IPluginSettings settings)
    {
        _host = host;
        _settings = settings;

        // 一次性迁移：v22 及以前，这两个参数住在全局 config.json 里
        if (settings.WasFresh)
        {
            settings.Set("auto", host.GetLegacy("AutoAccept", true));
            settings.Set("endpoint", host.GetLegacy("AcceptEndpoint", DefaultEndpoint));
            settings.Save();
        }
        ReloadSettings();

        _card = host.RegisterCard(Name, "匹配成功时自动接受 ｜ 点左侧手动接受", showAuto: true)!;
        _card.Clicked += () => _ = AcceptOnce();
        _card.AutoToggled += on =>
        {
            _settings.Set("auto", on);
            _settings.Save();
            lock (_lock) _auto = on;
            SyncCard();
            _host.SetStatus(on
                ? "● 自动接收已开启：匹配成功弹出确认框时自动点接受"
                : "自动接收已关闭");
        };

        // 主触发：阶段推送（毫秒级）。确认框有倒计时，不能只靠 tick
        host.OnPhaseChanged(OnPhase);
        // 副触发：确认框状态事件。还能发现「玩家已经手动选过了」
        host.Subscribe(ReadyCheckEventUri, OnReadyCheckEvent);
        // 兜底：推送没到 / 上次 POST 失败时，隔几秒再试
        host.OnTick(OnTick);

        SyncCard();
    }

    public void Start() { /* 没有常驻任务，全靠事件驱动 */ }
    public void Stop() { }

    // ---- 设置同步 ----

    private void ReloadSettings()
    {
        lock (_lock)
        {
            _auto = _settings.Get("auto", true);
            _endpoint = _settings.Get("endpoint", DefaultEndpoint);
        }
    }

    private void SyncCard() => _card.SetAuto(_auto, _auto ? "监听中" : "");

    public void OnSettingsChanged()
    {
        ReloadSettings();
        SyncCard();
    }

    /// <summary>自测截图时把开关摆到「开」，核对金色那一态。</summary>
    public void OnSelfTest()
    {
        lock (_lock) _auto = true;
        SyncCard();
    }

    // ---- 触发 ----

    private void OnPhase(string phase)
    {
        if (phase == "ReadyCheck")
        {
            lock (_lock) { _done = false; _tries = 0; }
            TryAccept();
        }
        else
        {
            // 离开确认框：本次匹配作废，下一次重新计
            lock (_lock) _done = false;
        }
    }

    private void OnReadyCheckEvent(PluginEvent ev)
    {
        if (ev.EventType == "Delete")
        {
            lock (_lock) _done = false;
            return;
        }
        if (ev.DataJson.Length <= 2 || ev.DataJson == "null") return;

        // 玩家已经手动接受/拒绝过 → 打上已处理标记，兜底重试也不再去插手
        if (PlayerAlreadyResponded(ev.DataJson))
        {
            lock (_lock) _done = true;
            return;
        }
        TryAccept();
    }

    private void OnTick(ClientSnapshot snap)
    {
        if (snap.Phase != "ReadyCheck") return;
        bool go;
        lock (_lock)
            go = _auto && !_done && !_inFlight
                 && (_lastTry == DateTime.MinValue
                     || (DateTime.Now - _lastTry) > TimeSpan.FromSeconds(3));
        if (go) TryAccept();
    }

    // ---- 执行 ----

    private void TryAccept()
    {
        int tryNo;
        lock (_lock)
        {
            if (!_auto || _done || _inFlight || _tries >= 3) return;
            _inFlight = true;
            _tries++;
            tryNo = _tries;
            _lastTry = DateTime.Now;
        }
        _ = Task.Run(() => AcceptOnce(tryNo));
    }

    /// <summary>接受一次。manual 入口是卡片点击；自动入口传 tryNo 用于日志。</summary>
    private async Task AcceptOnce(int autoTryNo = 0)
    {
        bool ok;
        int status;
        string body;
        try
        {
            var r = await _host.PostExAsync(_endpoint);
            ok = r.Ok;
            status = r.Status;
            body = r.Body;
        }
        catch (Exception ex)
        {
            ok = false;
            status = -3;
            body = ex.Message;
        }

        lock (_lock)
        {
            _inFlight = false;
            if (ok) _done = true;
        }

        if (ok)
        {
            _host.Log(LogLevel.Auto, autoTryNo > 0 ? "自动：已接受对局" : "手动：已接受对局");
            _host.SetStatus("✓ 已接受对局");
            _card.SetResult(true, "已接受");
        }
        else
        {
            // 404 = 现在没有等待接受的确认框。这是正常情况，不是故障
            string msg = status == 404 ? "当前没有等待接受的对局"
                       : status == -1 ? "客户端未连接"
                       : $"客户端返回 {status}";
            _host.Log(LogLevel.Auto,
                (autoTryNo > 0 ? $"自动接受失败（第 {autoTryNo} 次）" : "手动接受失败") + $"：{msg}");
            _host.SetStatus($"✕ 接受对局失败：{msg}", true);
            _card.SetResult(false, "失败");
        }
        _card.Restore();
    }

    /// <summary>
    /// 确认框数据里玩家是否已经做过选择。playerResponse："None"＝还没选；
    /// Accept / Declined＝选过了。解析不出就当没选过（宁可接受）。
    /// </summary>
    private static bool PlayerAlreadyResponded(string dataJson)
    {
        try
        {
            using var d = JsonDocument.Parse(dataJson);
            if (d.RootElement.ValueKind == JsonValueKind.Object
                && d.RootElement.TryGetProperty("playerResponse", out var pr)
                && pr.ValueKind == JsonValueKind.String)
            {
                var v = pr.GetString() ?? "";
                return v.Length > 0 && !v.Equals("None", StringComparison.OrdinalIgnoreCase);
            }
        }
        catch { /* 解析不了就不拦，交给接受逻辑 */ }
        return false;
    }
}
