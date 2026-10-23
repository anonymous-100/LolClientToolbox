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
/// 三条判据（2026-10-24 重构，修「开屏即卡结算不触发」）：
///   A（快，5 秒）：战绩数据生成立即被清除 → 起 5 秒一次性定时器，到点仍未离开结算集合
///      且战绩没再出来就触发。【事件驱动】不再等 8 秒的轮询 tick —— 旧版在 tick 里
///      检查，2 秒的触发窗口经常整窗错过（06:58:52 日志实证：条件成立过，客户端 4 秒自愈）。
///   B1（单阶段 15 秒）：任一结算子阶段连续滞留超阈值且无战绩数据。
///      抓「EndOfGame 卡 28 秒」那种，比集合级快一倍。
///   B2（集合级 45 秒）：进入结算集合开始算，只有离开集合或战绩出现才重置。
///      专治子阶段间来回抖（WaitingForStats↔PreEndOfGame↔EndOfGame 跳来跳去，
///      单阶段永远攒不够 15 秒）。阈值依据：2026-10-24 采集的 10 局正常样本，
///      集合总耗时 7–13 秒，45 秒 = 3.4 倍余量；且正常局进 WaitingForStats 约 3 秒
///      内战绩就生成，「无战绩数据」门槛下不会误吞正在看战绩的人。
/// 开屏即武装：OnPhase 只在阶段「变化」时触发，开屏就在结算里时不会被调用 ——
/// Start() 里手动武装两套计时器并记日志（旧版这个场景永远不触发，用户实测反馈）。
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
    private int _stuckSec = 15;      // 判据 B1：单阶段阈值
    private int _setSec = 45;        // 判据 B2：结算集合阈值
    private bool _requireNoStats = true;
    private int _cooldownMs = 60000;
    private string _endpoint = DefaultEndpoint;
    private string _phases = "WaitingForStats,PreEndOfGame,EndOfGame";

    private DateTime _setSince = DateTime.MinValue;        // 进入结算集合的时刻（B2 用）
    private DateTime _phaseSince = DateTime.MinValue;      // 进入当前子阶段的时刻（B1 用）
    private DateTime _lastFix = DateTime.MinValue;         // 上次自动跳过的时刻（冷却用）
    private DateTime _statsBornAt = DateTime.MinValue;
    private DateTime _statsGoneAt = DateTime.MinValue;
    private bool _diagLogged;                              // 30 秒诊断行只记一次
    private System.Threading.Timer? _fastTimer;            // 判据 A 的 5 秒一次性定时器
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
            settings.Set("setSec", 45);
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

    public void Start()
    {
        // 开屏即武装：OnPhase 只在阶段「变化」时触发，开屏就在结算里时根本不会被调用。
        // 旧版在这个场景两套计时器都躺平（_eogSince 永远是 MinValue），用户实测
        // 「打开应用后无法自动跳过」。这里补上，并记一行让用户知道监控已在线。
        var phase = _host.CurrentPhase;
        if (!InEogPhase(phase)) return;
        var now = DateTime.Now;
        lock (_lock)
        {
            _setSince = now;
            _phaseSince = now;
        }
        _host.Log(LogLevel.Info, "进入结算阶段，开始监控", "开屏即在结算中，两套判据已武装");
    }

    public void Stop()
    {
        CancelFastTimer();
    }

    // ---- 设置同步 ----

    private void ReloadSettings()
    {
        lock (_lock)
        {
            _auto = _settings.Get("auto", true);
            _stuckSec = _settings.Get("stuckSec", 15);
            _setSec = _settings.Get("setSec", 45);
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
        bool inSet = InEogPhase(phase);
        var now = DateTime.Now;
        lock (_lock)
        {
            _phaseSince = now;                                        // 单阶段计时：每次变化重新开始
            if (inSet && _setSince == DateTime.MinValue) _setSince = now;  // 集合计时：只在进入集合时起算
            if (!inSet)
            {
                // 一离开结算相关阶段，两套计时全部作废
                _setSince = DateTime.MinValue;
                _phaseSince = DateTime.MinValue;
                _statsGoneAt = DateTime.MinValue;
                _diagLogged = false;
            }
        }
        if (!inSet) CancelFastTimer();
    }

    /// <summary>战绩数据接口的推送：有 data = 战绩出来了；Delete = 被清掉。</summary>
    private void OnStatsEvent(PluginEvent ev)
    {
        bool has = ev.EventType != "Delete" && ev.DataJson.Length > 2 && ev.DataJson != "null";
        if (has)
        {
            lock (_lock)
            {
                _statsBornAt = DateTime.Now;
                _statsGoneAt = DateTime.MinValue;
            }
            CancelFastTimer();   // 战绩出来了 = 结算页是好的，快判据作废
            _host.Log(LogLevel.Info, "战绩数据 已生成");
            return;
        }

        DateTime born;
        lock (_lock) born = _statsBornAt;
        if (born == DateTime.MinValue) return;   // 本来就没有过，谈不上「生成立即清除」

        double kept = (DateTime.Now - born).TotalSeconds;
        bool tooFast = kept < 1.0;
        lock (_lock)
        {
            _statsGoneAt = DateTime.Now;
            _statsBornAt = DateTime.MinValue;
        }
        _host.Log(LogLevel.Info, "战绩数据 已清除",
            $"保留 {kept:0.0}s" + (tooFast ? "  ⚠生成立即清除＝异常" : ""));

        if (!tooFast) return;

        // 判据 A 事件驱动：起 5 秒一次性定时器，不再等 8 秒的轮询 tick。
        // 旧版在 tick 里检查「清除后满 2 秒」，2 秒的窗口在 8 秒的 tick 颗粒下
        // 经常整窗错过（06:58:52 实证：条件成立过，客户端 4 秒自愈，下一个 tick 到来时
        // 早就离开结算阶段了，白瞎）。
        CancelFastTimer();
        _fastTimer = new System.Threading.Timer(
            _ => FastFire(), null,
            TimeSpan.FromSeconds(5), System.Threading.Timeout.InfiniteTimeSpan);
    }

    /// <summary>判据 A 定时器到点：仍在结算集合、战绩没再出来 → 触发。</summary>
    private void FastFire()
    {
        try
        {
            if (!_auto) return;
            if (!InEogPhase(_host.CurrentPhase)) return;
            lock (_lock)
            {
                if (_statsBornAt != DateTime.MinValue) return;   // 战绩又出来了，情况好转
            }
            double gone;
            lock (_lock)
            {
                gone = _statsGoneAt == DateTime.MinValue
                    ? 5
                    : (DateTime.Now - _statsGoneAt).TotalSeconds;
            }
            Trigger($"战绩数据生成立即被清除，{gone:0} 秒后仍未离开结算");
        }
        catch
        {
            /* 定时器回调绝不把异常抛到线程池 */
        }
    }

    private void CancelFastTimer()
    {
        lock (_lock)
        {
            _fastTimer?.Dispose();
            _fastTimer = null;
        }
    }

    private void OnTick(ClientSnapshot snap)
    {
        if (!snap.ClientRunning || !_auto) return;
        var phase = snap.Phase ?? "";
        if (!InEogPhase(phase)) return;

        int stuckSec, setSec;
        bool requireNoStats;
        lock (_lock)
        {
            stuckSec = _stuckSec;
            setSec = _setSec;
            requireNoStats = _requireNoStats;
        }

        // 战绩数据到底出来没有：自己 GET 一次（客观信号，不解析宿主的字符串）
        var r = _host.GetAsync(_endpoint).GetAwaiter().GetResult();
        bool hasStats = r.Status >= 200 && r.Status < 300;
        if (requireNoStats && hasStats)
        {
            // 有战绩 = 结算页是好的，两套计时都作废
            lock (_lock)
            {
                _setSince = DateTime.MinValue;
                _phaseSince = DateTime.MinValue;
                _diagLogged = false;
            }
            return;
        }

        var now = DateTime.Now;

        // 懒初始化兜底：万一 Start() 的武装错过了（插件比首帧晚加载之类），这里补上
        DateTime setSince, phaseSince;
        lock (_lock)
        {
            if (_setSince == DateTime.MinValue) _setSince = now;
            if (_phaseSince == DateTime.MinValue) _phaseSince = now;
            setSince = _setSince;
            phaseSince = _phaseSince;
        }

        double setSecs = (now - setSince).TotalSeconds;
        double phaseSecs = (now - phaseSince).TotalSeconds;

        // 诊断日志：集合级计时过了 30 秒还没触发，记一笔（只记一次）——
        // 下次再遇到「没触发」的反馈，这行能直接告诉我们是哪条判据在走
        bool diag;
        lock (_lock) diag = _diagLogged;
        if (setSecs > 30 && !diag)
        {
            lock (_lock) _diagLogged = true;
            _host.Log(LogLevel.Warn,
                $"结算阶段已持续 {setSecs:0} 秒且无战绩数据，等待判定（集合级阈值 {setSec} 秒）");
        }

        // —— 判据 B1（单阶段）：任一结算子阶段连续滞留 ——
        if (phaseSecs >= stuckSec)
        {
            Trigger($"{phase} 已持续 {phaseSecs:0} 秒" + (requireNoStats ? " 且无战绩数据" : ""));
            return;
        }

        // —— 判据 B2（集合级）：子阶段间来回抖时单阶段永远攒不够，靠这条兜底 ——
        if (setSecs >= setSec)
        {
            Trigger($"结算阶段合计已持续 {setSecs:0} 秒" + (requireNoStats ? " 且无战绩数据" : ""));
        }
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
                _setSince = DateTime.MinValue;
                _phaseSince = DateTime.MinValue;
                _diagLogged = false;
            }
            CancelFastTimer();

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
