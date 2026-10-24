using System;
using System.Threading;
using System.Threading.Tasks;
using LolClientToolbox.Contracts;

namespace DeathWatchPlugin;

/// <summary>
/// 看视频（自动 / v1）—— 检测到死亡就自动切出去，复活自动切回来。
///
/// 与 v0（手动热键版）的关系：**这是独立插件，自己不碰任何窗口**。
/// 它只负责「判断死没死」，真要切窗口时通过契约里的 <see cref="IVideoSwitcher"/>
/// 去指挥 v0 插件 —— 焦点、媒体键、静音那些坑只需要在一处处理。
/// 拿不到 v0（没装 / 清单里禁用）就只记日志、不切窗口，功能降级但不报错。
///
/// 数据源两级：
///   ① Live Client Data API（https://127.0.0.1:2999）的 activePlayer.respawnTimer
///      —— 官方接口、精确，>0 就是死了、数值是还要等几秒。**只在对局中存在**。
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

    public string Id => "deathwatch";
    public string Name => "看视频（自动）";

    public void Init(IPluginHost host, IPluginSettings settings)
    {
        _host = host;
        _settings = settings;
        ReloadSettings();

        _card = host.RegisterCard(Name,
            "对局中死亡自动切到视频、复活自动切回", showAuto: true)!;
        _card.Clicked += () => ProbeOnce();
    }

    public void Start()
    {
        // 手动版插件提供「切窗口」的能力。这一步必须在 Start 里做 ——
        // Init 阶段后面的插件还没加载，找得到找不到全看运气。
        _switcher = _host.GetService<IVideoSwitcher>();
        if (_switcher is null)
        {
            _host.Log(LogLevel.Warn, "没找到「看视频（手动）」插件，只检测不切窗口",
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
        ReloadSettings();
        _live?.Dispose();
        _live = null;      // 端口/超时可能改了，下次 Tick 会按新值重建
    }

    public void OnSelfTest() => _card.SetAuto(_auto, _auto ? "守护中" : "");

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
            catch
            {
                // 检测线程绝不能因为一次异常就死掉
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

    // ================= 手动采样（卡片点击 / 真机验证靠它） =================

    /// <summary>
    /// 立刻采样一次并把结论写进日志 + 状态行。
    ///
    /// 为什么要有这个：死亡检测对不对，只有真对局里死了才知道；与其让用户猜，
    /// 不如随时能点一下看「现在读到几秒」。这也是本插件唯一能离线自查的入口。
    /// </summary>
    private void ProbeOnce()
    {
        _card.SetRunning();
        Task.Run(async () =>
        {
            string line;
            try
            {
                bool inGame = InGame();
                var (t, reason) = await ReadLiveAsync().ConfigureAwait(false);
                var sb = new System.Text.StringBuilder();
                sb.Append(inGame ? "在对局中" : "不在对局中");
                sb.Append(t.HasValue
                    ? $" · Live 复活倒计时 {t.Value:0.#}s（{(t.Value > 0.5 ? "死亡" : "活着")}）"
                    : $" · Live 接口取不到（{reason}）");

                if (_ocrFallback && inGame)
                {
                    var hwnd = Gdi.FindGameWindow();
                    if (hwnd == IntPtr.Zero) sb.Append(" · 没找到对局窗口");
                    else
                    {
                        var s = ScreenProbe.Probe(hwnd, _satThreshold, _bandTop, _bandBottom);
                        sb.Append(" · 屏幕：").Append(s.Note);
                        if (s.Dead is not null) sb.Append(s.Dead.Value ? " → 灰屏（死亡）" : " → 有颜色（活着）");
                    }
                }
                sb.Append(_switcher is null ? " · ⚠没接上手动版插件" : "");
                line = sb.ToString();
            }
            catch (Exception ex)
            {
                line = "采样失败：" + ex.Message;
            }

            _host.Log(LogLevel.Info, "死亡检测采样（手动）", line);
            _host.SetStatus(line);
            _card.SetResult(true, "已采样");
            _card.Restore();
        });
    }
}
