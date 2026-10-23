using System;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using LolClientToolbox.Contracts;

namespace WindowFixPlugin;

/// <summary>
/// 恢复窗口大小 —— 把 LOL 客户端窗口修成「目标尺寸 × 客户端缩放比」并居中。
///
/// 算法按开源项目 LeagueTavern/fix-lcu-window（MIT）原样内联：
///   FindWindow("RCLIENT","League of Legends") + FindWindowEx(...,"CefBrowserWindow")
///   → 高宽比判据 != 0.5625 → GET /riotclient/zoom-scale → 目标 = base × zoom
///   → 伪造 WM_DPICHANGED → SetWindowPos 居中
/// Win32 部分内联在本文件里（只有这个插件用，不值得为它单开一个共享库）。
/// </summary>
public sealed class WindowFixPlugin : ILolPlugin, ISettingsChangedAware, ISelfTestAware
{
    // ---------------- Win32 ----------------

    private const int WM_DPICHANGED = 0x02E0;
    private const int SW_SHOWMINIMIZED = 2;
    private const int SWP_ASYNCWINDOWPOS = 0x0040;

    [DllImport("user32.dll", EntryPoint = "FindWindow", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll", EntryPoint = "FindWindowEx", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter,
                                              string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll", EntryPoint = "SetWindowPos")]
    private static extern int SetWindowPos(IntPtr hWnd, int hWndInsertAfter,
                                           int x, int y, int width, int height, int flags);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int n);
    private const int SM_CXSCREEN = 0, SM_CYSCREEN = 1;

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, ref RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPLACEMENT
    {
        public int length, flags, showCmd;
        public POINT ptMinPosition, ptMaxPosition;
        public RECT rcNormalPosition;
    }

    private static bool IsMinimized(IntPtr hWnd)
    {
        var p = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
        GetWindowPlacement(hWnd, ref p);
        return p.showCmd == SW_SHOWMINIMIZED;
    }

    /// <summary>给窗口伪造一次 WM_DPICHANGED，让它按新 DPI 重算布局（原版的关键一步）。</summary>
    private static void PatchDpiChangedMessage(IntPtr hWnd)
    {
        uint dpi = GetDpiForWindow(hWnd);
        var wParam = (IntPtr)((int)dpi << 16 | (int)dpi);
        var lParam = Marshal.UnsafeAddrOfPinnedArrayElement(new int[4], 0);
        SendMessage(hWnd, WM_DPICHANGED, wParam, lParam);
    }

    // ---------------- 插件本体 ----------------

    private const string ZoomScalePath = "/riotclient/zoom-scale";

    private IPluginHost _host = null!;
    private IPluginSettings _settings = null!;
    private ICard _card = null!;

    private readonly object _lock = new();
    private bool _auto;
    private int _baseW = 1600;
    private int _baseH = 900;
    private int _minW = 1024;
    private int _minH = 576;
    private double _aspectTol = 0.02;
    private int _holdMs = 3000;
    private int _cooldownMs = 15000;
    private string _inGamePhases = "GameStart,InProgress,Reconnect";

    private DateTime _badSince = DateTime.MinValue;
    private DateTime _lastFix = DateTime.MinValue;
    private bool _inFlight;

    public string Id => "windowfix";
    public string Name => "恢复窗口大小";

    public void Init(IPluginHost host, IPluginSettings settings)
    {
        _host = host;
        _settings = settings;

        if (settings.WasFresh)
        {
            settings.Set("auto", host.GetLegacy("AutoWindow", false));
            settings.Set("baseW", host.GetLegacy("FixBaseWidth", 1600));
            settings.Set("baseH", host.GetLegacy("FixBaseHeight", 900));
            settings.Set("minW", host.GetLegacy("MinWidth", 1024));
            settings.Set("minH", host.GetLegacy("MinHeight", 576));
            settings.Set("aspectTol", host.GetLegacy("AspectTolerance", 0.02));
            settings.Set("holdMs", host.GetLegacy("WindowBadHoldMs", 3000));
            settings.Set("cooldownMs", host.GetLegacy("WindowCooldownMs", 15000));
            settings.Set("inGamePhases", host.GetLegacy("InGamePhases", "GameStart,InProgress,Reconnect"));
            settings.Save();
        }
        ReloadSettings();

        _card = host.RegisterCard(Name, "点左侧立即修正 ｜ 开右侧自动长期守护", showAuto: true)!;
        _card.Clicked += async () =>
        {
            _card.SetRunning();
            _host.Log(LogLevel.Auto, "手动：恢复窗口大小");
            var (ok, msg) = await FixOnce(forced: true);
            _host.Log(LogLevel.Auto, ok ? $"恢复窗口大小完成 · {msg}" : $"恢复窗口大小失败 · {msg}");
            _host.SetStatus(ok ? $"✓ 恢复窗口大小执行完成 · {msg}" : $"✕ 恢复窗口大小：{msg}", !ok);
            _card.SetResult(ok, ok ? "完成" : "失败");
            _card.Restore();
        };
        _card.AutoToggled += on =>
        {
            _settings.Set("auto", on);
            _settings.Save();
            lock (_lock) _auto = on;
            SyncCard();
            _host.SetStatus(on
                ? "● 窗口守护已开启：尺寸不对时自动修（对局进行中会自动跳过，那时窗口缩小是正常的）"
                : "窗口守护已关闭");
        };

        host.OnTick(OnTick);
        SyncCard();
    }

    public void Start() { }
    public void Stop() { }

    private void ReloadSettings()
    {
        lock (_lock)
        {
            _auto = _settings.Get("auto", false);
            _baseW = _settings.Get("baseW", 1600);
            _baseH = _settings.Get("baseH", 900);
            _minW = _settings.Get("minW", 1024);
            _minH = _settings.Get("minH", 576);
            _aspectTol = _settings.Get("aspectTol", 0.02);
            _holdMs = _settings.Get("holdMs", 3000);
            _cooldownMs = _settings.Get("cooldownMs", 15000);
            _inGamePhases = _settings.Get("inGamePhases", "GameStart,InProgress,Reconnect");
        }
    }

    private void SyncCard() => _card.SetAuto(_auto, _auto ? "守护中" : "");

    public void OnSettingsChanged()
    {
        ReloadSettings();
        SyncCard();
    }

    public void OnSelfTest()
    {
        lock (_lock) _auto = true;
        SyncCard();
    }

    // ---- 自动守护 ----

    private void OnTick(ClientSnapshot snap)
    {
        if (!snap.ClientRunning || !_auto) return;

        // 对局进行中客户端窗口本来就会缩到很小（实测 170×47），那是正常行为
        if (IsInGamePhase(snap.Phase)) { lock (_lock) _badSince = DateTime.MinValue; return; }

        var reason = WhyBad();
        if (reason is null) { lock (_lock) _badSince = DateTime.MinValue; return; }

        DateTime badSince;
        lock (_lock)
        {
            if (_badSince == DateTime.MinValue) _badSince = DateTime.Now;
            badSince = _badSince;
        }
        var held = (DateTime.Now - badSince).TotalMilliseconds;
        if (held < _holdMs) return;

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
            var (ok, msg) = await FixOnce(forced: false);
            lock (_lock)
            {
                _inFlight = false;
                _lastFix = DateTime.Now;
                _badSince = DateTime.MinValue;
            }
            _host.Log(LogLevel.Auto, ok
                ? $"自动：检测到窗口异常（{reason}），已修复 · {msg}"
                : $"窗口自动修复失败：{msg}");
            _host.SetStatus(ok ? $"✓ 自动：已修复窗口 · {msg}" : $"✕ 窗口自动修复失败：{msg}", !ok);
            _card.SetResult(ok, ok ? "已修复" : "失败");
            _card.Restore();
        });
    }

    private bool IsInGamePhase(string phase)
    {
        if (string.IsNullOrEmpty(phase)) return false;
        string phases;
        lock (_lock) phases = _inGamePhases;
        return (phases ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(s => phase.Equals(s, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>窗口为什么算异常；正常返回 null。</summary>
    private string? WhyBad()
    {
        var (w, h, _, _, minimized, found) = Measure();
        if (!found || minimized) return null;

        int minW, minH;
        double tol;
        lock (_lock) { minW = _minW; minH = _minH; tol = _aspectTol; }

        if (w < minW || h < minH) return $"尺寸 {w}×{h} 小于设定值";
        double aspect = h == 0 ? 0 : (double)w / h;
        if (Math.Abs(aspect - 16.0 / 9.0) > tol) return $"宽高比 {aspect:F3} 偏离 16:9";
        return null;
    }

    private static (int W, int H, int X, int Y, bool Minimized, bool Found) Measure()
    {
        var hwnd = FindWindow("RCLIENT", "League of Legends");
        var cef = FindWindowEx(hwnd, IntPtr.Zero, "CefBrowserWindow", null);
        if (hwnd == IntPtr.Zero || cef == IntPtr.Zero) return (0, 0, 0, 0, false, false);
        var r = new RECT();
        if (!GetWindowRect(hwnd, ref r)) return (0, 0, 0, 0, false, false);
        return (r.Right - r.Left, r.Bottom - r.Top, r.Left, r.Top, IsMinimized(hwnd), true);
    }

    // ---- 执行 ----

    private async Task<(bool Ok, string Msg)> FixOnce(bool forced)
    {
        int baseW, baseH;
        lock (_lock) { baseW = _baseW; baseH = _baseH; }

        var hwnd = FindWindow("RCLIENT", "League of Legends");
        var cef = FindWindowEx(hwnd, IntPtr.Zero, "CefBrowserWindow", null);
        if (hwnd == IntPtr.Zero || cef == IntPtr.Zero)
            return (false, "找不到客户端窗口（客户端没在运行？）");

        if (IsMinimized(hwnd))
            return (false, "客户端正处于最小化，跳过");

        var r1 = new RECT();
        var r2 = new RECT();
        GetWindowRect(hwnd, ref r1);
        GetWindowRect(cef, ref r2);
        double Ratio(RECT r) => (r.Bottom - r.Top) / (double)(r.Right - r.Left);
        bool need = Math.Abs(Ratio(r1) - 0.5625) > 1e-9 || Math.Abs(Ratio(r2) - 0.5625) > 1e-9;
        if (!need && !forced)
            return (true, "窗口比例正常，无需处理");

        var zoom = await GetZoomAsync();
        if (zoom < 0)
            return (false, "取不到客户端缩放比（凭据失效？）");

        int tw = (int)(baseW * zoom);
        int th = (int)(baseH * zoom);
        // 屏幕尺寸走 GetSystemMetrics 而不是 Screen.PrimaryScreen —— 插件不引用 WinForms，
        // 单显示器 100% 缩放下两者等价；原版引擎用的就是主屏尺寸
        int sw = GetSystemMetrics(SM_CXSCREEN), sh = GetSystemMetrics(SM_CYSCREEN);

        PatchDpiChangedMessage(hwnd);
        PatchDpiChangedMessage(cef);

        SetWindowPos(hwnd, 0, (sw - tw) / 2, (sh - th) / 2, tw, th, SWP_ASYNCWINDOWPOS);
        SetWindowPos(cef, 0, 0, 0, tw, th, SWP_ASYNCWINDOWPOS);
        return (true, $"已设为 {tw}×{th}");
    }

    private async Task<double> GetZoomAsync()
    {
        try
        {
            var r = await _host.GetAsync(ZoomScalePath);
            if (r.Status < 200 || r.Status >= 300) return -1;
            return double.Parse(r.Body.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture);
        }
        catch { return -1; }
    }
}
