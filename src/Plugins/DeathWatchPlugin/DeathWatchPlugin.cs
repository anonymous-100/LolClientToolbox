using System;
using System.Threading;
using System.Threading.Tasks;
using LolClientToolbox.Contracts;

namespace DeathWatchPlugin;

/// <summary>
/// 看视频（自动）—— 检测到死亡就自动切出去，复活自动切回来。
///
/// 与切窗插件（watchvideo）的关系：**这是独立插件，自己不碰任何窗口**。
/// 它只负责「判断死没死」，真要切窗口时通过契约里的 <see cref="IVideoSwitcher"/>
/// 去指挥切窗插件 —— 焦点、媒体键、静音那些坑只需要在一处处理。
/// 拿不到切窗插件（没装 / 清单里禁用）就只记日志、不切窗口，功能降级但不报错。
///
/// 交互约定（2026-10-25 用户拍板）：**整张卡片就是一个开关**。
/// 点左半边 = 切换开关，右边小轨道 = 同一个开关，没有「只触发一次」的入口 ——
/// 「切出去看视频」这种动作手动按一次没有意义，要的就是长期守护，所以别再加单次按钮。
///
/// 数据源两级：
///   ① Live Client Data API（https://127.0.0.1:2999）的 allgamedata → allPlayers[].respawnTimer
///      —— 官方接口、精确，>0 就是死了、数值是还要等几秒。**只在对局中存在**。
///      （注意：activePlayer 里没有这个字段，2026-10-25 真机实测过）
///   ② 屏幕兜底（灰屏 + OCR 倒计时）—— 接口取不到时才用，判据是「画面被去色」。
///
/// 三条纪律（改之前先读）：
///   1. 绝不读游戏内存、不注入游戏进程（红线，Riot 2026-10-06 起封禁）；
///      这里只读官方本地接口和自己屏幕上的像素。
///   2. 「取不到接口」不等于「坏了」—— 绝大多数时候只是没在对局。要区分对待，别刷警告。
///   3. 判断只在**状态翻转**时动作（活着→死、死→活），不做持续触发；
///      否则每次轮询都会去切一遍窗口。
/// </summary>
public sealed class DeathWatchPlugin : ILolPlugin, ISettingsChangedAware, ISelfTestAware
{
    private enum LifeState { Unknown, Alive, Dead }

    /// <summary>这些阶段算「在对局里」（客户端有时报 Reconnect，实际还在局内）。</summary>
    private static readonly string[] InGamePhases = { "GameStart", "InProgress", "Reconnect" };

    private IPluginHost _host = null!;
    private IPluginSettings _settings = null!;
    private ICard _card = null!;
    private LiveClient? _live;

    private volatile bool _auto = true;
    private volatile int _minRespawnSec = 8;
    private volatile int _pollMs = 500;
    private volatile bool _ocrFallback = true;
    private volatile string _endpoint = "/liveclientdata/allgamedata";
    private volatile int _port = 2999;
    private volatile int _timeoutMs = 900;
    private volatile int _assumedRespawnSec = 15;
    // 这三个是 double，C# 不允许 volatile double（CS0677）。
    // 值只在设置变更时改一次、检测线程读，实际不会撕裂出有意义的问题，不值得上锁。
    private double _satThreshold = 40;
    private double _bandTop = 0.55;
    private double _bandBottom = 0.85;
    private volatile int _screenEveryMs = 1200;

    private IVideoSwitcher? _switcher;
    private LifeState _state = LifeState.Unknown;
    private int _apiMiss;
    private DateTime _lastScreen = DateTime.MinValue;
    private DateTime _backAt = DateTime.MinValue;      // 兜底模式的「该回来了」时刻
    private string _lastSource = "";

    private Thread? _thread;
    private volatile bool _stop;
    private DateTime _lastErrLog = DateTime.MinValue;   // 检测线程异常日志节流

    public string Id => "deathwatch";
    public string Name => "看视频（自动）";

    public void Init(IPluginHost host, IPluginSettings settings)
    {
        _host = host;
        _settings = settings;
        ReloadSettings();

        _card = host.RegisterCard(Name,
            "点卡片＝开 / 关自动看视频 ｜ 开着时死亡自动切出、复活自动切回", showAuto: true)!;
        // 两个点击区接同一个开关：
        //   左半边（老版本是「立刻采样一次」）→ 现在也是开关，因为单次采样对用户没意义；
        //   右边小轨道 → ActionCard 自己先翻转 AutoOn，再把新值回传。
        // 之前这里只接了 Clicked（采样），AutoToggled 根本没接 → 轨道开关点了什么也不会发生，
        // 表现就是「自动看视频关不掉」（2026-10-25 用户报的 bug）。
        _card.Clicked += () => SetAuto(!_auto, "卡片");
        _card.AutoToggled += on => SetAuto(on, "开关");
        SyncCard();
    }

    /// <summary>
    /// 开关的唯一入口（卡片左半边 / 右侧轨道 / 设置窗勾选，最后都走这里）。
    ///
    /// 三件事必须一起做，少一件就会「看起来关了其实还在跑」：
    ///   ① 改内存里的 _auto（检测线程立刻按它走）
    ///   ② 写回 settings.json（重启后还是关的）—— 以前漏了这一步，关掉后一重启又自己开了
    ///   ③ 关掉时收尾：自动切回是本插件负责的，撒手不管会把人留在浏览器里、游戏还静音着
    /// </summary>
    private void SetAuto(bool on, string trigger)
    {
        if (_auto == on) { SyncCard(); return; }   // 重复点不重复记日志

        _auto = on;
        _settings.Set("auto", on);
        _settings.Save();
        SyncCard();

        if (on)
        {
            _host.Log(LogLevel.Info, "自动看视频已开启", $"（{trigger}）对局中死亡自动切出、复活自动切回");
            _host.SetStatus("● 自动看视频已开启：死亡自动切出、复活自动切回");
            return;
        }

        _host.Log(LogLevel.Info, "自动看视频已关闭", $"（{trigger}）不再切换窗口");
        try { _switcher?.SwitchBack("自动看视频已关闭"); } catch { /* 收尾失败不影响关闭 */ }
        _state = LifeState.Unknown;
        _host.SetStatus("自动看视频已关闭");
    }

    /// <summary>把内存里的状态同步到卡片开关上（改设置、自测、开关本身都要调）。</summary>
    private void SyncCard() => _card.SetAuto(_auto, _auto ? "守护中" : "");

    public void Start()
    {
        // 切窗插件（watchvideo）提供「切窗口」的能力。这一步必须在 Start 里做 ——
        // Init 阶段后面的插件还没加载，找得到找不到全看运气。
        _switcher = _host.GetService<IVideoSwitcher>();
        if (_switcher is null)
        {
            _host.Log(LogLevel.Warn, "没找到切窗插件（watchvideo），只检测不切窗口",
                "自动检测仍然工作（日志会写），但不会替你切出去 —— 装上/启用 watchvideo 插件即可联动");
        }

        if (_host.IsSelfTest) return;

        _stop = false;
        _thread = new Thread(Loop) { IsBackground = true, Name = "deathwatch" };
        _thread.Start();
    }

    public void Stop()
    {
        _stop = true;
        try { _thread?.Join(800); } catch { /* 退出时不计较 */ }
        _live?.Dispose();
        _live = null;
    }

    public void OnSettingsChanged()
    {
        bool wasAuto = _auto;
        ReloadSettings();
        _live?.Dispose();
        _live = null;      // 端口/超时可能改了，下次 Tick 会按新值重建
        SyncCard();

        // 在设置窗里把「自动看视频」取消勾选，同样要立刻收尾（和点卡片一个道理）
        if (wasAuto && !_auto)
        {
            try { _switcher?.SwitchBack("设置里关掉了自动看视频"); } catch { /* 收尾失败不影响关闭 */ }
            _state = LifeState.Unknown;
        }
    }

    public void OnSelfTest()
    {
        _auto = true;
        SyncCard();
    }

    private void ReloadSettings()
    {
        _auto = _settings.Get("auto", true);
        _minRespawnSec = Math.Max(1, _settings.Get("minRespawnSec", 8));
        _pollMs = Math.Clamp(_settings.Get("pollMs", 500), 200, 5000);
        _ocrFallback = _settings.Get("ocrFallback", true);
        // ⚠ 默认值必须是 allgamedata：2026-10-25 真机实测 respawnTimer 不在
        // /liveclientdata/activeplayer 里（那个端点没有死亡信息），只在 allPlayers[] 中，
        // 而 allPlayers 只有 allgamedata 才有。回退成 activeplayer 会永远取不到数据，
        // 表现为「日志里一直说接口取不到，却从不切出去」。
        _endpoint = _settings.Get("endpoint", "/liveclientdata/allgamedata");
        _port = Math.Clamp(_settings.Get("port", 2999), 1, 65535);
        _timeoutMs = Math.Clamp(_settings.Get("timeoutMs", 900), 200, 5000);
        _assumedRespawnSec = Math.Clamp(_settings.Get("assumedRespawnSec", 15), 1, 90);
        _satThreshold = _settings.Get("satThreshold", 40.0);
        _bandTop = _settings.Get("bandTop", 0.55);
        _bandBottom = _settings.Get("bandBottom", 0.85);
        _screenEveryMs = Math.Clamp(_settings.Get("screenEveryMs", 1200), 300, 10000);
        if (string.IsNullOrWhiteSpace(_endpoint)) _endpoint = "/liveclientdata/allgamedata";
    }

    // ================= 主循环 =================

    private void Loop()
    {
        while (!_stop)
        {
            try
            {
                TickAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                // 检测线程绝不能因为一次异常就死掉；但也别完全吞掉 ——
                // 完全静默会让「明明开着却什么都不做」变成没法排查的黑洞（同一个错 30 秒只记一次）
                if ((DateTime.Now - _lastErrLog).TotalSeconds >= 30)
                {
                    _lastErrLog = DateTime.Now;
                    _host.Log(LogLevel.Warn, "死亡检测线程异常（已忽略）", ex.Message);
                }
            }
            Thread.Sleep(_pollMs);
        }
    }

    private async Task TickAsync()
    {
        if (!_auto) return;

        if (!InGame())
        {
            if (_state == LifeState.Dead)
            {
                _state = LifeState.Unknown;
                _host.Log(LogLevel.Info, "不在对局中，重置死亡状态");
            }
            if (_switcher?.InVideoMode == true) _switcher.SwitchBack("对局结束");
            _apiMiss = 0;
            _backAt = DateTime.MinValue;
            return;
        }

        // ① 主数据源：Live Client Data API
        var (timer, reason) = await ReadLiveAsync().ConfigureAwait(false);
        if (timer.HasValue)
        {
            if (_apiMiss != 0)
            {
                _apiMiss = 0;
                _host.Log(LogLevel.Info, "Live 接口恢复", $"端点 {_endpoint}");
            }
            ApplyLive(timer.Value);
            return;
        }

        // ② 取不到：先别急着报警，绝大多数情况只是「没在对局里」或接口还没起
        _apiMiss++;
        if (_apiMiss == 4)
        {
            _host.Log(LogLevel.Warn, "Live 接口取不到",
                $"{reason}；" + (_ocrFallback ? "改用屏幕兜底（灰屏 + OCR）" : "屏幕兜底已关闭，只记录不切换"));
        }

        // Live 模式下失联太久（对局结束/崩溃）而人还在看视频 —— 把人送回去，别让他错过复活
        if (_switcher?.InVideoMode == true && _apiMiss >= 8 && _lastSource == "Live")
        {
            _switcher.SwitchBack("Live 接口失联");
            _state = LifeState.Unknown;
            return;
        }

        if (!_ocrFallback) return;

        // 兜底模式下「该回来了」的时刻到了就回去（OCR 只能估个大概）
        if (_switcher?.InVideoMode == true && _backAt != DateTime.MinValue && DateTime.Now >= _backAt)
        {
            _backAt = DateTime.MinValue;
            _switcher.SwitchBack("兜底计时到点");
            _state = LifeState.Unknown;
            return;
        }

        // 在视频模式时不采样屏幕：那时游戏窗口被浏览器盖着，
        // 屏幕 DC 抓到的是浏览器（有颜色）→ 会被误判成「已复活」，立刻把人切回去。
        if (_switcher?.InVideoMode == true) return;

        if ((DateTime.Now - _lastScreen).TotalMilliseconds < _screenEveryMs) return;
        _lastScreen = DateTime.Now;

        var hwnd = Gdi.FindGameWindow();
        if (hwnd == IntPtr.Zero) return;

        var s = ScreenProbe.Probe(hwnd, _satThreshold, _bandTop, _bandBottom);
        ApplyScreen(s);
    }

    private Task<(double? Timer, string Reason)> ReadLiveAsync()
    {
        _live ??= new LiveClient(_port, _timeoutMs);
        return _live.RespawnTimer(_endpoint);
    }

    /// <summary>Live 接口的结论：>0 = 死了（数值＝还要等几秒），0 = 活着。</summary>
    private void ApplyLive(double timer)
    {
        _lastSource = "Live";
        bool dead = timer > 0.5;

        if (dead && _state != LifeState.Dead)
        {
            _state = LifeState.Dead;
            if (timer >= _minRespawnSec)
            {
                _host.Log(LogLevel.Auto, $"检测到死亡（Live · 复活倒计时 {timer:0.#}s）",
                    $"≥门槛 {_minRespawnSec}s，切出去看视频");
                _switcher?.SwitchOut($"死亡 {timer:0.#}s");
            }
            else
            {
                _host.Log(LogLevel.Info, $"死亡但只剩 {timer:0.#}s",
                    $"低于门槛 {_minRespawnSec}s，不切（来回切更烦）");
            }
        }
        else if (!dead && _state == LifeState.Dead)
        {
            _state = LifeState.Alive;
            _host.Log(LogLevel.Auto, "已复活（Live · 倒计时归零）");
            _switcher?.SwitchBack("已复活");
        }
        else if (!dead)
        {
            _state = LifeState.Alive;
        }
    }

    /// <summary>屏幕兜底的结论：灰屏 = 死了。</summary>
    private void ApplyScreen(ScreenProbe.Sample s)
    {
        if (s.Dead is null)
        {
            // 截不到画面（独占全屏最常见）—— 只报一次，别刷屏
            if (_lastSource != "screen-fail")
            {
                _lastSource = "screen-fail";
                _host.Log(LogLevel.Warn, "屏幕兜底采不到画面", s.Note);
            }
            return;
        }

        _lastSource = "screen";
        if (s.Dead.Value && _state != LifeState.Dead)
        {
            _state = LifeState.Dead;
            int sec = s.Seconds ?? _assumedRespawnSec;
            if (sec >= _minRespawnSec)
            {
                _host.Log(LogLevel.Auto, $"检测到死亡（屏幕 · 约 {sec}s）", s.Note);
                _switcher?.SwitchOut($"死亡 约{sec}s");
                // 兜底没有精确复活时刻：按读到的倒计时定时把人送回去
                _backAt = DateTime.Now.AddSeconds(sec);
            }
            else
            {
                _host.Log(LogLevel.Info, $"死亡但只剩约 {sec}s", "低于门槛，不切");
            }
        }
        else if (!s.Dead.Value && _state == LifeState.Dead)
        {
            _state = LifeState.Alive;
            _host.Log(LogLevel.Auto, "灰屏消失，判定已复活（屏幕）");
            _switcher?.SwitchBack("已复活");
            _backAt = DateTime.MinValue;
        }
        else if (!s.Dead.Value)
        {
            _state = LifeState.Alive;
        }
    }

    private bool InGame()
    {
        if (Gdi.GameProcessRunning()) return true;
        var phase = _host.CurrentPhase ?? "";
        foreach (var p in InGamePhases)
        {
            if (string.Equals(phase, p, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    // 原来的「点卡片立刻采样一次」（ProbeOnce）已删，2026-10-25 用户拍板：
    // 卡片只当一个开关用，单次采样对用户没有意义，反而让人以为开关坏了。
    // 需要看读数的时候看日志（判据来源和饱和度都写在里面），或者跑 tools/probe。
}
