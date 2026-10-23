using System;
using System.Linq;
using System.Threading.Tasks;
using LolClientToolbox.Contracts;

namespace EogSkipPlugin;

/// <summary>
/// 跳过卡结算 —— 结算阶段卡住时自动 POST /lol-lobby/v2/play-again 跳过。
///
/// 【判据来源】2026-10-17 对比真实样本后定值（明细见项目根目录「结算卡住判据.md」）：
///   正常：EndOfGame 停留 2 / 7 / 12 秒，战绩数据「已生成→已清除」保留 2–15 秒
///   卡住：EndOfGame 停留 28 秒，战绩数据从头到尾没有；或保留 0 秒（生成立即被清除）
/// 两条判据：
///   A（快）：战绩数据生成立即被清除，且清除后 2 秒还没离开结算阶段
///   B（慢）：在结算相关阶段里滞留超过 stuckSec 秒，且战绩数据没出来
/// </summary>
public sealed class EogSkipPlugin : ILolPlugin, ISettingsChangedAware
{
    private const string DefaultEndpoint = "/lol-end-of-game/v1/eog-stats-block";
    private const string PlayAgainPath = "/lol-lobby/v2/play-again";

    private IPluginHost _host = null!;
    private IPluginSettings _settings = null!;
    private ICard _card = null!;

    private readonly object _lock = new();
    private bool _auto;
    private int _stuckSec = 15;
    private bool _requireNoStats = true;
    private int _cooldownMs = 60000;
    private string _endpoint = DefaultEndpoint;
    private string _phases = "WaitingForStats,PreEndOfGame,EndOfGame";

    private DateTime _eogSince = DateTime.MinValue;      // 进入结算阶段的时刻
    private DateTime _lastFix = DateTime.MinValue;       // 上次自动跳过的时刻（冷却用）
    private DateTime _statsBornAt = DateTime.MinValue;
    private DateTime _statsGoneAt = DateTime.MinValue;
    private bool _statsVanishedTooFast;
    private bool _inFlight;

    public string Id => "eogskip";
    public string Name => "跳过卡结算";

    public void Init(IPluginHost host, IPluginSettings settings)
    {
        _host = host;
        _settings = settings;

        if (settings.WasFresh)
        {
            settings.Set("auto", host.GetLegacy("AutoEog", true));
            settings.Set("stuckSec", host.GetLegacy("EogStuckAfterSec", 15));
            settings.Set("requireNoStats", host.GetLegacy("EogRequireNoStats", true));
            settings.Set("cooldownMs", host.GetLegacy("EogCooldownMs", 60000));
            settings.Set("endpoint", host.GetLegacy("EogEndpoint", DefaultEndpoint));
            settings.Set("phases", host.GetLegacy("EogPhases", "WaitingForStats,PreEndOfGame,EndOfGame"));
            settings.Save();
        }
        ReloadSettings();

        _card = host.RegisterCard(Name, "结算卡住时强制跳过", showAuto: true)!;
        _card.Clicked += () => _ = SkipOnce();
        _card.AutoToggled += on =>
        {
            _settings.Set("auto", on);
            _settings.Save();
            lock (_lock) _auto = on;
            SyncCard();
            _host.SetStatus(on
                ? $"● 卡结算监控已开启：结算阶段停滞超过 {_stuckSec} 秒会自动跳过"
                : "卡结算监控已关闭");
        };

        host.OnPhaseChanged(OnPhase);
        host.Subscribe(_endpoint, OnStatsEvent);   // 战绩数据生/灭是客观信号
        host.OnTick(OnTick);

        SyncCard();
    }

    public void Start() { }
    public void Stop() { }

    // ---- 设置同步 ----

    private void ReloadSettings()
    {
        lock (_lock)
        {
            _auto = _settings.Get("auto", true);
            _stuckSec = _settings.Get("stuckSec", 15);
            _requireNoStats = _settings.Get("requireNoStats", true);
            _cooldownMs = _settings.Get("cooldownMs", 60000);
            _endpoint = _settings.Get("endpoint", DefaultEndpoint);
            _phases = _settings.Get("phases", "WaitingForStats,PreEndOfGame,EndOfGame");
        }
    }

    private void SyncCard() => _card.SetAuto(_auto, _auto ? "监控中" : "");

    public void OnSettingsChanged()
    {
        ReloadSettings();
        SyncCard();
    }

    // ---- 判定 ----

    private bool InEogPhase(string phase)
    {
        if (string.IsNullOrEmpty(phase)) return false;
        string phases;
        lock (_lock) phases = _phases;
        return (phases ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(s => phase.Equals(s, StringComparison.OrdinalIgnoreCase));
    }

    private void OnPhase(string phase)
    {
        if (!InEogPhase(phase))
        {
            // 一离开结算相关阶段，两套判据的计时/标记全部作废
            lock (_lock)
            {
                _eogSince = DateTime.MinValue;
                _statsVanishedTooFast = false;
                _statsGoneAt = DateTime.MinValue;
            }
            return;
        }
        lock (_lock)
        {
            if (_eogSince == DateTime.MinValue) _eogSince = DateTime.Now;
        }
    }

    /// <summary>战绩数据接口的推送：有 data = 战绩出来了；Delete = 被清掉。</summary>
    private void OnStatsEvent(PluginEvent ev)
    {
        bool has = ev.EventType != "Delete" && ev.DataJson.Length > 2 && ev.DataJson != "null";
        if (has)
        {
            lock (_lock) _statsBornAt = DateTime.Now;
            _host.Log(LogLevel.Info, "战绩数据 已生成");
            return;
        }

        DateTime born;
        lock (_lock) born = _statsBornAt;
        if (born == DateTime.MinValue) return;   // 本来就没有过，谈不上「生成立即清除」

        double kept = (DateTime.Now - born).TotalSeconds;
        // 16 个正常样本保留 2–15 秒；00:30 那次卡住是 0 秒。阈值取 1.0 秒
        lock (_lock)
        {
            _statsVanishedTooFast = kept < 1.0;
            _statsGoneAt = DateTime.Now;
            _statsBornAt = DateTime.MinValue;
        }
        _host.Log(LogLevel.Info, "战绩数据 已清除",
            $"保留 {kept:0.0}s" + (_statsVanishedTooFast ? "  ⚠生成立即清除＝异常" : ""));
    }

    private void OnTick(ClientSnapshot snap)
    {
        if (!snap.ClientRunning || !_auto) return;
        if (!InEogPhase(snap.Phase)) return;

        // 战绩数据到底出来没有：自己 GET 一次（客观信号，不解析宿主的字符串）
        var r = _host.GetAsync(_endpoint).GetAwaiter().GetResult();
        bool hasStats = r.Status >= 200 && r.Status < 300;
        if (_requireNoStats && hasStats)
        {
            lock (_lock) _eogSince = DateTime.MinValue;
            return;
        }

        // —— 判据 A（快）：生成立即被清除，清除后 2 秒还没离开结算阶段 ——
        DateTime gone;
        bool fast;
        lock (_lock)
        {
            fast = _statsVanishedTooFast;
            gone = _statsGoneAt;
        }
        if (fast && gone != DateTime.MinValue && (DateTime.Now - gone).TotalSeconds >= 2)
        {
            Trigger($"战绩数据生成立即被清除，清除后 {(DateTime.Now - gone).TotalSeconds:0} 秒仍未离开结算");
            return;
        }

        // —— 判据 B（慢）：滞留超过阈值秒 ——
        DateTime since;
        lock (_lock) since = _eogSince;
        if (since == DateTime.MinValue) return;
        var secs = (DateTime.Now - since).TotalSeconds;
        if (secs < _stuckSec) return;

        Trigger($"{snap.Phase} 已持续 {secs:0} 秒" + (_requireNoStats ? " 且无战绩数据" : ""));
    }

    private void Trigger(string why)
    {
        bool go;
        lock (_lock)
        {
            var cooled = _lastFix == DateTime.MinValue
                         || (DateTime.Now - _lastFix).TotalMilliseconds > _cooldownMs;
            go = cooled && !_inFlight;
            if (go) _inFlight = true;
        }
        if (!go) return;

        _ = Task.Run(async () =>
        {
            bool ok;
            string msg;
            try
            {
                var r = await _host.PostExAsync(PlayAgainPath);
                ok = r.Ok;
                msg = r.Ok ? "已发送跳过指令" : $"客户端返回 {r.Status}";
            }
            catch (Exception ex)
            {
                ok = false;
                msg = ex.Message;
            }

            lock (_lock)
            {
                _inFlight = false;
                _lastFix = DateTime.Now;
                _eogSince = DateTime.MinValue;
            }

            _host.Log(LogLevel.Auto, ok ? $"自动：结算疑似卡住（{why}），已跳过" : $"自动跳过失败：{msg}");
            _host.SetStatus(ok ? $"✓ 自动：已跳过卡住的结算（{why}）" : $"✕ 自动跳过失败：{msg}", !ok);
            _card.SetResult(ok, ok ? "已跳过" : "失败");
            _card.Restore();
        });
    }

    // ---- 手动 ----

    private async Task SkipOnce()
    {
        _card.SetRunning();
        _host.Log(LogLevel.Auto, "手动：跳过卡结算");
        bool ok;
        string msg;
        try
        {
            var r = await _host.PostExAsync(PlayAgainPath);
            ok = r.Ok;
            msg = r.Ok ? "已发送跳过指令" : $"客户端返回 {r.Status}";
        }
        catch (Exception ex)
        {
            ok = false;
            msg = ex.Message;
        }

        _host.Log(LogLevel.Auto, ok ? $"跳过卡结算完成 · {msg}" : $"跳过卡结算失败 · {msg}");
        _host.SetStatus(ok ? $"✓ 跳过卡结算执行完成 · {msg}" : $"✕ 跳过卡结算：{msg}", !ok);
        _card.SetResult(ok, ok ? "完成" : "失败");
        _card.Restore();
    }
}
