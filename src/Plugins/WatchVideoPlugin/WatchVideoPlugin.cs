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

    private volatile bool _pausedByUs;       // 视频是我们暂停的吗（媒体键是切换式，得记账）
    private IntPtr _browserHwnd;
    private readonly object _toggleLock = new();

    private Thread? _hotkeyThread;
    private uint _hotkeyThreadId;

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
        if (_host.IsSelfTest) return;   // 自测模式不注册全局热键

        _hotkeyThread = new Thread(HotkeyLoop)
        {
            IsBackground = true,
            Name = "watchvideo-hotkey",
        };
        _hotkeyThread.Start();
    }

    public void Stop()
    {
        try
        {
            if (_hotkeyThreadId != 0)
                Win32.PostThreadMessage(_hotkeyThreadId, Win32.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        }
        catch { /* 退出时不计较 */ }
    }

    public void OnSettingsChanged()
    {
        ReloadSettings();
        // 热键改了要重注册：最简单可靠的做法是让旧线程退出、再起一条新的
        RestartHotkeyThread();
    }

    private void ReloadSettings()
    {
        _url = _settings.Get("videoUrl", DefaultUrl);
        _hotkey = _settings.Get("hotkey", DefaultHotkey);
        _pauseOnReturn = _settings.Get("pauseOnReturn", true);
        if (string.IsNullOrWhiteSpace(_url)) _url = DefaultUrl;
        if (string.IsNullOrWhiteSpace(_hotkey)) _hotkey = DefaultHotkey;
    }

    // ================= 热键线程 =================

    /// <summary>
    /// 热键跑在自己的线程上：RegisterHotKey 传 hWnd=0 时 WM_HOTKEY 投递到调用线程的
    /// 消息队列，所以这条线程必须一直 GetMessage 泵着，否则热键没反应。
    /// </summary>
    private void HotkeyLoop()
    {
        _hotkeyThreadId = Win32.GetCurrentThreadId();
        var (mods, vk) = ParseHotkey(_hotkey);
        bool ok = false;
        try
        {
            ok = Win32.RegisterHotKey(IntPtr.Zero, HotkeyId, mods, vk);
        }
        catch (Exception ex)
        {
            _host.Log(LogLevel.Error, "热键注册异常", ex.Message);
        }

        if (!ok)
        {
            _host.Log(LogLevel.Warn,
                $"热键 {_hotkey} 注册失败", "可能被其他程序占用；改设置里的「热键」再试");
            return;
        }

        _host.Log(LogLevel.Info, $"热键 {_hotkey} 已注册", "切到视频 / 切回游戏");

        try
        {
            while (true)
            {
                int r = Win32.GetMessage(out var msg, IntPtr.Zero, 0, 0);
                if (r == 0 || r == -1) break;          // WM_QUIT 或错误
                if (msg.message == Win32.WM_HOTKEY) Toggle("热键");
            }
        }
        finally
        {
            try { Win32.UnregisterHotKey(IntPtr.Zero, HotkeyId); } catch { }
        }
    }

    private void RestartHotkeyThread()
    {
        try
        {
            if (_hotkeyThreadId != 0)
            {
                Win32.PostThreadMessage(_hotkeyThreadId, Win32.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
                _hotkeyThread?.Join(800);
            }
        }
        catch { }
        _hotkeyThreadId = 0;
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
        Thread.Sleep(120);   // 给浏览器一点时间拿到焦点，媒体键才认

        bool resumed = false;
        if (_pauseOnReturn && _pausedByUs)
        {
            Win32.SendMediaPlayPause();
            _pausedByUs = false;
            resumed = true;
        }

        _card.SetResult(true, "已切出");
        _card.Restore();

        var note = (launched ? $"已启动 {_url}" : "聚焦已有浏览器窗口")
                   + (resumed ? " · 已恢复播放" : "")
                   + (focused ? "" : " · ⚠前台切换未确认");
        _host.Log(LogLevel.Auto, $"切出看视频（{trigger}）", note);
        _host.SetStatus("▶ 已切到视频 · 再按一次热键切回游戏");
    }

    /// <summary>
    /// 切回游戏。顺序不能变：先找到游戏窗口 → 确认前台是浏览器 → 暂停视频 → 再切前台。
    /// 媒体键发给「当前前台窗口」，所以发之前必须确认前台真的是浏览器，
    /// 否则会被打进游戏（实测 2026-10-24 03:27 就发生过一次：游戏在前台，媒体键给了游戏）。
    /// </summary>
    private void SwitchBack(string trigger)
    {
        _card.SetRunning();

        var hwnd = FindGameWindow();
        if (hwnd == IntPtr.Zero)
            throw new InvalidOperationException("没找到游戏窗口，也没找到客户端窗口");

        var fgProc = ProcOf(Win32.GetForegroundWindow());
        bool browserInFront = IsBrowserProcess(fgProc);

        bool paused = false;
        if (_pauseOnReturn && browserInFront)
        {
            Win32.SendMediaPlayPause();   // 前台是浏览器，键才会落到视频上
            _pausedByUs = true;
            paused = true;
            Thread.Sleep(80);
        }

        bool focused = Win32.ForceForeground(hwnd);

        _card.SetResult(true, focused ? "已切回" : "已暂停");
        _card.Restore();

        var title = Win32.Title(hwnd);
        var note = (focused ? "已聚焦" : "⚠前台切换未确认") + $" · {title}"
                   + (paused ? " · 已暂停视频" : "")
                   + (!_pauseOnReturn ? "" : (browserInFront ? "" : " · 前台不是浏览器，已跳过媒体键"));

        // 抢不到前台时给准确原因：UIPI 规则下普通权限进程拉不动管理员权限窗口
        if (!focused)
        {
            var why = BlockReason(hwnd);
            if (why.Length > 0) note += " · " + why;
        }

        _host.Log(LogLevel.Auto, $"切回游戏（{trigger}）", note);
        _host.SetStatus(focused
            ? "◀ 已切回游戏"
            : "⚠ 切回失败：" + (BlockReason(hwnd) is { Length: > 0 } r ? r : "前台被其他窗口占用"), true);
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
