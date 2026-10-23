using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using LolClientToolbox.Contracts;

namespace WatchVideoPlugin;

/// <summary>
/// 看视频（手动版 / v0）—— 按一下热键切到视频，再按一下暂停视频切回游戏。
///
/// 为什么先做手动版：自动检测死亡有另一套不确定性（要读 Live Client Data API 的
/// respawnTimer），把它和「窗口焦点/按键时序」捆在一起调，出问题分不清是谁的锅。
/// 所以 v0 只做纯窗口控制 —— 零检测风险，当天可用；v1 再接检测，只换触发器。
///
/// 三条纪律（踩过的坑换来的，改之前先读）：
/// 1. 切回游戏时的顺序必须是「先暂停视频 → 再切游戏」。反了就会把媒体键/空格
///    打进游戏窗口（LoL 里 Space 是视角回中，直接抖镜头）。
/// 2. 媒体键是「切换式」，所以自己记账 _pausedByUs：切回时暂停、下次切出时恢复。
///    用户如果手动暂停过，状态会对不上 —— 这是已知限制，写在设置项的注释里。
/// 3. 所有 Win32 调用都可能因权限/UIPI 失败（游戏若以管理员运行就抢不到前台），
///    一律 try/catch + 日志，绝不把异常抛回宿主。
/// </summary>
public sealed class WatchVideoPlugin : ILolPlugin, ISettingsChangedAware
{
    /// <summary>默认视频地址。用户可在设置里改成任何网址（B站/YouTube/抖音网页版都行）。</summary>
    private const string DefaultUrl = "https://www.douyin.com/";
    private const string DefaultHotkey = "Ctrl+F8";

    /// <summary>这些进程都当浏览器认（国内外常见的那几个）。</summary>
    private static readonly HashSet<string> Browsers = new(StringComparer.OrdinalIgnoreCase)
    {
        "msedge", "chrome", "firefox", "brave", "opera", "vivaldi", "chromium",
        "360se", "360chrome", "qqbrowser", "sogouexplorer", "2345explorer",
        "liebao", "maxthon", "yandex",
    };

    private const int HotkeyId = 0x4C56;   // 'LV'

    private IPluginHost _host = null!;
    private IPluginSettings _settings = null!;
    private ICard _card = null!;

    private volatile string _url = DefaultUrl;
    private volatile string _hotkey = DefaultHotkey;
    private volatile bool _pauseOnReturn = true;
    private volatile bool _muteGame = true;

    private volatile bool _pausedByUs;       // 视频是我们暂停的吗（媒体键是切换式，得记账）
    private IntPtr _browserHwnd;
    private readonly object _toggleLock = new();

    private Thread? _hotkeyThread;
    private volatile bool _pollStop;

    public string Id => "watchvideo";
    public string Name => "看视频（手动）";

    public void Init(IPluginHost host, IPluginSettings settings)
    {
        _host = host;
        _settings = settings;
        ReloadSettings();

        _card = host.RegisterCard(Name,
            $"热键 {_hotkey}：切到视频 / 切回游戏", showAuto: false)!;
        _card.Clicked += () => Toggle("卡片");
    }

    public void Start()
    {
        if (_host.IsSelfTest) return;   // 自测模式不起轮询线程

        WarnIfClientElevated();

        _pollStop = false;
        _hotkeyThread = new Thread(HotkeyLoop)
        {
            IsBackground = true,
            Name = "watchvideo-hotkey",
        };
        _hotkeyThread.Start();
    }

    /// <summary>
    /// 客户端（含游戏）以管理员权限运行时，Windows 会：
    /// ① 忽略普通权限进程注册的全局热键 ② 禁止普通权限进程抢它的前台
    /// ③ 甚至不把键盘状态暴露给普通权限进程（GetAsyncKeyState 也读不到）
    /// 三件事叠加的结果就是「热键完全没反应」。启动时先喊一声，省得用户以为是插件坏了。
    /// </summary>
    private void WarnIfClientElevated()
    {
        try
        {
            var hwnd = FindGameWindow();
            if (hwnd == IntPtr.Zero) return;   // 客户端没开，等它开了再说

            int clientIl = Win32.IntegrityLevel(Win32.ProcessIdOf(hwnd));
            int myIl = Win32.IntegrityLevel(Environment.ProcessId);
            if (clientIl > myIl && myIl > 0)
            {
                _host.Log(LogLevel.Warn, "客户端以管理员权限运行，热键会被 Windows 拦截",
                    $"客户端={IlName(clientIl)}，本程序={IlName(myIl)}。"
                    + "要么以普通权限启动 Riot 客户端（推荐，一次解决），要么让本程序同样以管理员运行");
                _host.SetStatus("⚠ 客户端是管理员权限，热键会被拦——建议用普通权限启动客户端", true);
            }
        }
        catch { /* 诊断失败不影响功能 */ }
    }

    public void Stop()
    {
        _pollStop = true;
        try { _hotkeyThread?.Join(600); } catch { /* 退出时不计较 */ }
    }

    public void OnSettingsChanged()
    {
        ReloadSettings();
        // 热键改了：停掉旧线程再起一条（轮询线程读的是启动时解析出来的键）
        RestartHotkeyThread();
    }

    private void ReloadSettings()
    {
        _url = _settings.Get("videoUrl", DefaultUrl);
        _hotkey = _settings.Get("hotkey", DefaultHotkey);
        _pauseOnReturn = _settings.Get("pauseOnReturn", true);
        _muteGame = _settings.Get("muteGame", true);
        if (string.IsNullOrWhiteSpace(_url)) _url = DefaultUrl;
        if (string.IsNullOrWhiteSpace(_hotkey)) _hotkey = DefaultHotkey;
    }

    // ================= 热键线程 =================

    /// <summary>
    /// 热键用**轮询键盘状态**实现，不用 RegisterHotKey。
    ///
    /// 原因（2026-10-24 实测）：游戏进程是 High 完整性（管理员权限运行），工具箱是 Medium。
    /// UIPI 规定低完整性进程注册的全局热键在**高完整性窗口有焦点时被系统忽略** ——
    /// 表现就是「切进游戏后 Ctrl+F8 完全没反应」，而同一时刻在桌面上按是好的。
    /// GetAsyncKeyState 读的是键盘物理状态，不经过前台窗口，也不受 UIPI 限制，所以能穿透。
    ///
    /// 代价：35ms 轮询（约 28 次/秒，开销可忽略），最坏 35ms 延迟，肉眼看不出。
    /// </summary>
    private void HotkeyLoop()
    {
        var (mods, vk) = ParseHotkey(_hotkey);
        _host.Log(LogLevel.Info, $"热键 {_hotkey} 已启用",
            "轮询键盘状态（游戏提权时 RegisterHotKey 会被屏蔽，所以不用它）");

        bool wasDown = false;
        while (!_pollStop)
        {
            bool down = false;
            try { down = HotkeyDown(mods, vk); } catch { /* 轮询绝不能抛 */ }

            if (down && !wasDown) Toggle("热键");   // 只在按下的那一瞬触发，长按不重复
            wasDown = down;

            Thread.Sleep(35);
        }
    }

    /// <summary>修饰键必须「该按的按下、没写的没按」，避免 Ctrl+Shift+F8 之类误触发。</summary>
    private static bool HotkeyDown(uint mods, uint vk)
    {
        bool ctrl = Down(VK_CONTROL);
        bool alt = Down(VK_MENU);
        bool shift = Down(VK_SHIFT);
        bool win = Down(VK_LWIN) || Down(VK_RWIN);

        if (((mods & Win32.MOD_CONTROL) != 0) != ctrl) return false;
        if (((mods & Win32.MOD_ALT) != 0) != alt) return false;
        if (((mods & Win32.MOD_SHIFT) != 0) != shift) return false;
        if (((mods & Win32.MOD_WIN) != 0) != win) return false;

        return Down((int)vk);
    }

    private const int VK_SHIFT = 0x10;
    private const int VK_CONTROL = 0x11;
    private const int VK_MENU = 0x12;      // Alt
    private const int VK_LWIN = 0x5B;
    private const int VK_RWIN = 0x5C;

    private static bool Down(int vk) => (Win32.GetAsyncKeyState(vk) & 0x8000) != 0;

    private void RestartHotkeyThread()
    {
        Stop();
        Start();
    }

    /// <summary>解析 "Ctrl+Alt+F8" 这类写法 → (修饰键, 虚拟键)。认不出来的回落到 Ctrl+F8。</summary>
    private static (uint Mods, uint Vk) ParseHotkey(string text)
    {
        uint mods = 0;
        uint vk = 0x77;   // F8
        if (string.IsNullOrWhiteSpace(text)) return (Win32.MOD_CONTROL, vk);

        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var part = raw.Trim();
            if (part.Equals("Ctrl", StringComparison.OrdinalIgnoreCase)
                || part.Equals("Control", StringComparison.OrdinalIgnoreCase)) mods |= Win32.MOD_CONTROL;
            else if (part.Equals("Alt", StringComparison.OrdinalIgnoreCase)) mods |= Win32.MOD_ALT;
            else if (part.Equals("Shift", StringComparison.OrdinalIgnoreCase)) mods |= Win32.MOD_SHIFT;
            else if (part.Equals("Win", StringComparison.OrdinalIgnoreCase)) mods |= Win32.MOD_WIN;
            else if (part.Length >= 2 && (part[0] == 'F' || part[0] == 'f')
                     && int.TryParse(part[1..], out var fn) && fn >= 1 && fn <= 24)
                vk = (uint)(0x70 + fn - 1);                  // VK_F1 = 0x70
            else if (part.Length == 1 && char.IsLetterOrDigit(part[0]))
                vk = char.ToUpperInvariant(part[0]);         // 字母数字直接用 ASCII 当 VK
        }

        if (mods == 0) mods = Win32.MOD_CONTROL;             // 不带修饰键容易和游戏键冲突
        if (vk == 0) vk = 0x77;
        return (mods, vk);
    }

    // ================= 主流程 =================

    private void Toggle(string trigger)
    {
        // 连按两下不要打架：加锁串行化（热键线程和卡片点击可能同时来）
        lock (_toggleLock)
        {
            try
            {
                // 方向看真实前台，不看内部标志 —— 用户会手动 Alt+Tab，标志早就失真。
                // 实测 2026-10-24 03:27 就因此做反了方向：进对局后第一次按，去执行「切回」，
                // 结果什么也没发生（还白给游戏发了媒体键）。
                bool browserInFront = IsBrowserProcess(ProcOf(Win32.GetForegroundWindow()));
                if (browserInFront) SwitchBack(trigger);
                else SwitchOut(trigger);
            }
            catch (Exception ex)
            {
                _card.SetResult(false, "失败");
                _card.Restore();
                _host.Log(LogLevel.Error, $"切视频失败（{trigger}）：{ex.Message}");
                _host.SetStatus("✕ 切视频失败：" + ex.Message, true);
            }
        }
    }

    /// <summary>切出去看视频：找浏览器（没有就启动）→ 聚焦 → 上次是我们暂停的就恢复播放。</summary>
    private void SwitchOut(string trigger)
    {
        _card.SetRunning();

        IntPtr hwnd = FindBrowserWindow();
        bool launched = false;
        if (hwnd == IntPtr.Zero)
        {
            Process.Start(new ProcessStartInfo(_url) { UseShellExecute = true });
            launched = true;
            // 等浏览器起窗口：最多 ~4 秒，200ms 一探
            for (int i = 0; i < 20 && hwnd == IntPtr.Zero; i++)
            {
                Thread.Sleep(200);
                hwnd = FindBrowserWindow();
            }
        }
        if (hwnd == IntPtr.Zero)
            throw new InvalidOperationException("没找到浏览器窗口，也没能启动浏览器");

        bool focused = Win32.ForceForeground(hwnd);
        _browserHwnd = hwnd;
        Thread.Sleep(120);   // 给浏览器一点时间拿到焦点

        // 恢复播放：优先用系统媒体会话「精确置为播放」（已经是播放状态就什么也不做），
        // 拿不到会话才回退到切换式媒体键。
        bool resumed = false;
        if (_pauseOnReturn)
        {
            if (Playback.SetPlaying(true)) resumed = true;
            else if (_pausedByUs) { Win32.SendMediaPlayPause(); resumed = true; }
            _pausedByUs = false;
        }

        // 只静音游戏进程 —— 队伍语音在客户端进程里，不能连客户端一起静音
        bool muted = false;
        if (_muteGame && IsGameRunning())
        {
            muted = AudioMute.SetMuteByProcessName("League of Legends", true) > 0;
        }

        _card.SetResult(true, "已切出");
        _card.Restore();

        var note = (launched ? $"已启动 {_url}" : "聚焦已有浏览器窗口")
                   + (resumed ? " · 视频在播" : "")
                   + (muted ? " · 已静音游戏" : "")
                   + (focused ? "" : " · ⚠前台切换未确认");
        _host.Log(LogLevel.Auto, $"切出看视频（{trigger}）", note);
        _host.SetStatus("▶ 已切到视频（游戏已静音）· 再按一次热键切回");
    }

    /// <summary>
    /// 关闭视频、回到游戏。**不再去抢游戏的前台** —— 而是把视频窗口最小化，
    /// Windows 会自己把焦点交给 Z 序下一个窗口（也就是全屏的游戏）。
    ///
    /// 为什么要这样：实测游戏进程是 High 完整性（提权运行），工具箱是 Medium，
    /// UIPI 不允许低权限进程把高权限窗口拉到前台 —— 直接 SetForegroundWindow(游戏)
    /// 必然失败。而「最小化自己的前台窗口」完全不涉及那个高权限窗口，
    /// 因此**不需要管理员权限**。这是不用提权也能一键切回的关键。
    /// </summary>
    private void SwitchBack(string trigger)
    {
        _card.SetRunning();

        IntPtr fg = Win32.GetForegroundWindow();
        if (fg == IntPtr.Zero || !IsBrowserProcess(ProcOf(fg)))
            throw new InvalidOperationException("当前前台不是浏览器，没有可关闭的视频窗口");

        // 先暂停视频。**精确置为暂停**，不再用切换式媒体键 ——
        // 用户手动暂停过的话，切换式会把视频「切成播放」，切回游戏后后台还有声音（实测 bug）。
        bool paused = false;
        if (_pauseOnReturn)
        {
            if (Playback.SetPlaying(false)) paused = true;
            else if (Playback.IsPlaying() is not false)
            {
                // 拿不到媒体会话：退回媒体键，但只在确认前台是浏览器时才发
                Win32.SendMediaPlayPause();
                _pausedByUs = true;
                paused = true;
            }
            Thread.Sleep(80);
        }

        // 取消游戏静音（必须在切回游戏前做，否则你回来时听不到任何声音）
        bool unmuted = false;
        if (_muteGame && IsGameRunning())
            unmuted = AudioMute.SetMuteByProcessName("League of Legends", false) > 0;

        // 最小化（不是关闭，视频进度/页面都保留着，下次一键就回来）
        Win32.ShowWindow(fg, Win32.SW_MINIMIZE);
        Thread.Sleep(160);

        var now = Win32.GetForegroundWindow();
        string nowProc = ProcOf(now);
        bool backToGame = IsGameProcess(nowProc);

        // 兜底：焦点没落到游戏（比如工具箱窗口正压在游戏上面）时再试一次抢前台。
        // 游戏是 High 时这次会失败（UIPI），但无害；游戏不是提权运行时这次就能救回来。
        if (!backToGame)
        {
            var game = FindGameWindow();
            if (game != IntPtr.Zero && Win32.ForceForeground(game))
            {
                now = Win32.GetForegroundWindow();
                nowProc = ProcOf(now);
                backToGame = IsGameProcess(nowProc);
            }
        }

        _card.SetResult(true, backToGame ? "已回到游戏" : "已关闭");
        _card.Restore();

        var note = (backToGame ? "已回到游戏" : $"⚠焦点落在 {nowProc}" + Win32.Title(now))
                   + " · 视频窗口已最小化"
                   + (paused ? " · 已暂停" : "")
                   + (unmuted ? " · 游戏已恢复声音" : "")
                   + (!backToGame && FindGameWindow() == IntPtr.Zero ? " · 没找到游戏窗口" : "");
        _host.Log(LogLevel.Auto, $"关闭视频（{trigger}）", note);
        _host.SetStatus(backToGame
            ? "◀ 已关闭视频，回到游戏"
            : $"⚠ 视频已关，但焦点不在游戏（{nowProc}）——按一下游戏窗口即可", !backToGame);
    }

    /// <summary>抢前台失败的原因诊断。目前唯一确定的原因是权限层级差（UIPI）。</summary>
    private static string BlockReason(IntPtr target)
    {
        try
        {
            int targetIl = Win32.IntegrityLevel(Win32.ProcessIdOf(target));
            int myIl = Win32.IntegrityLevel(Environment.ProcessId);
            if (targetIl > 0 && myIl > 0 && targetIl > myIl)
                return $"目标窗口是{IlName(targetIl)}，本程序是{IlName(myIl)}，"
                       + "Windows 不允许低权限进程抢高权限窗口的前台 → 右键以管理员身份运行工具箱";
        }
        catch { /* 诊断失败不影响主流程 */ }
        return "";
    }

    private static string IlName(int il) => il switch
    {
        >= 0x4000 => "系统权限",
        >= 0x3000 => "管理员权限",
        >= 0x2000 => "普通权限",
        _ => "低权限",
    };

    private static string ProcOf(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return "";
        try { return Process.GetProcessById(Win32.ProcessIdOf(hwnd)).ProcessName.ToLowerInvariant(); }
        catch { return ""; }
    }

    private static bool IsBrowserProcess(string proc) => proc.Length > 0 && Browsers.Contains(proc);

    /// <summary>对局进程在不在（静音/取消静音之前先看，免得给不存在的进程查音频会话）。</summary>
    private static bool IsGameRunning()
    {
        try { return Process.GetProcessesByName("League of Legends").Length > 0; }
        catch { return false; }
    }

    /// <summary>对局窗口（League of Legends.exe）或客户端窗口（LeagueClientUx.exe）。</summary>
    private static bool IsGameProcess(string proc) =>
        proc == "league of legends" || proc == "leagueclientux";

    // ================= 找窗口 =================

    private IntPtr FindBrowserWindow()
    {
        // 上次那个还活着就优先用它（避免多开浏览器时抓到错的窗口）
        if (_browserHwnd != IntPtr.Zero && Win32.IsWindow(_browserHwnd))
        {
            var t = Win32.Title(_browserHwnd);
            if (t.Length > 0) return _browserHwnd;
        }

        foreach (var w in Win32.TopLevelWindows())
        {
            if (Browsers.Contains(w.Proc)) return w.Hwnd;
        }
        return IntPtr.Zero;
    }

    /// <summary>优先对局窗口（League of Legends.exe），其次客户端窗口（LeagueClientUx.exe）。</summary>
    private static IntPtr FindGameWindow()
    {
        var windows = Win32.TopLevelWindows();

        // 对局窗口：进程名 League of Legends（枚举出来是小写带空格）
        foreach (var w in windows)
        {
            if (w.Proc == "league of legends") return w.Hwnd;
        }

        // 客户端主窗口：标题就叫 League of Legends
        foreach (var w in windows)
        {
            if (w.Proc == "leagueclientux"
                && w.Title.Contains("League of Legends", StringComparison.OrdinalIgnoreCase))
                return w.Hwnd;
        }

        // 兜底：客户端其它顶层窗口（有时标题是「英雄联盟」之类）
        foreach (var w in windows)
        {
            if (w.Proc == "leagueclientux") return w.Hwnd;
        }
        return IntPtr.Zero;
    }
}
