using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;

namespace Probe;

/// <summary>各项检查的实现。每项一个方法，返回 (通过?, 说明)。</summary>
internal static class Checks
{
    // ================= 1. Live 接口解析（deathwatch） =================

    /// <summary>
    /// 用真机抓回来的样本验证解析：respawnTimer 在 allPlayers[] 里、不在 activePlayer 里，
    /// 且必须认出「哪个人是我」—— 夹具里自己活着（0.0）、队友有 5.2/31.6 的，
    /// 如果解析错拿别人的倒计时，断言会当场炸出来。
    /// </summary>
    public static (bool, string) LiveParse(LoadedPlugin p)
    {
        // BaseDirectory = tools/probe/bin/<配置>/<TFM>/，上三级回到 tools/probe/
        var fixture = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "testdata", "allgamedata_sample.json"));
        if (!File.Exists(fixture))
            return (false, $"夹具不在：{fixture}（用 tools/make_fixture.py 重新生成）");
        var json = File.ReadAllText(fixture);

        var t = p.FindType("LiveClient");
        if (t is null) return (false, "找不到 LiveClient 类型");
        var m = t.GetMethod("Parse", BindingFlags.NonPublic | BindingFlags.Static);
        if (m is null) return (false, "找不到 Parse 方法");

        // ① 正常样本：allgamedata 结构
        var r = m.Invoke(null, new object[] { json })!;
        double? timer = (double?)TupleMember(r, "Item1")!;
        string reason = (string)TupleMember(r, "Item2")!;
        if (timer != 0.0)
            return (false, $"夹具里自己是活着的，应解析出 0.0，实际 {timer}（{reason}）——认人逻辑可能拿了别人的倒计时");

        // ② 旧口径兜底：根对象直接带 respawnTimer
        var r2 = m.Invoke(null, new object[] { """{"respawnTimer": 12.5}""" })!;
        double? t2 = (double?)TupleMember(r2, "Item1")!;
        if (t2 != 12.5) return (false, $"根对象 respawnTimer 应解析出 12.5，实际 {t2}");

        // ③ 空响应 / 纯文本（接口还没起时游戏可能回别的）
        var r3 = m.Invoke(null, new object[] { "not a json" })!;
        double? t3 = (double?)TupleMember(r3, "Item1")!;
        if (t3 is not null) return (false, "非法 JSON 应返回 null");

        return (true, $"自识别成功（{reason}）；根对象兜底、非法输入兜底均正常");
    }

    /// <summary>ValueTuple 的成员是**字段**不是属性，反射取名两样都得试。</summary>
    private static object? TupleMember(object tuple, string name) =>
        tuple.GetType().GetField(name)?.GetValue(tuple)
        ?? tuple.GetType().GetProperty(name)?.GetValue(tuple);

    // ================= 2. 插件依赖解析（隔 ALC 加载） =================

    /// <summary>
    /// 检查带 WinRT 依赖的插件（watchvideo / deathwatch）能不能在宿主的 ALC 规则下
    /// 解析出全部依赖。当年 Microsoft.Windows.SDK.NET.dll 没跟着部署，
    /// 三个症状一个异常 —— 这条检查就是防它回归的。
    /// </summary>
    public static (bool, string) DependencyResolve(LoadedPlugin p)
    {
        Type? pluginType = p.Asm.GetTypes().FirstOrDefault(t =>
            t.Name.EndsWith("Plugin", StringComparison.Ordinal) && !t.IsAbstract);
        if (pluginType is null) return (false, "找不到插件类型");
        try
        {
            var inst = Activator.CreateInstance(pluginType);
            if (inst is null) return (false, "插件类型无法实例化");
            return (true, $"{pluginType.Name} 可实例化");
        }
        catch (Exception ex)
        {
            return (false, $"{pluginType.Name} 实例化失败（典型原因：缺 WinRT 投影 dll）：{ex.InnerException?.Message ?? ex.Message}");
        }
    }

    // ================= 3. 系统媒体会话（watchvideo.Playback） =================

    public static (bool, string) Smtc(LoadedPlugin p)
    {
        var t = p.FindType("Playback");
        if (t is null) return (false, "找不到 Playback 类型");
        var m = t.GetMethod("IsPlaying", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        if (m is null) return (false, "找不到 IsPlaying");
        try
        {
            var r = m.Invoke(null, Array.Empty<object>());
            string desc = r switch
            {
                null => "当前没有可判断的媒体会话（返回 null）",
                true => "有会话且在播放",
                false => "有会话且已暂停",
                _ => $"返回值类型不认识（{r}）",
            };
            // 不论结果都算通过 —— 这条验证的是「WinRT 通路是通的」，不是「视频在播」
            return (true, $"SMTC 通路可用：{desc}");
        }
        catch (Exception ex)
        {
            return (false, "SMTC 调用失败：" + (ex.InnerException?.Message ?? ex.Message));
        }
    }

    // ================= 4. 游戏音频会话（watchvideo.AudioMute） =================

    public static (bool, string) AudioMuteProbe(LoadedPlugin p)
    {
        var t = p.FindType("AudioMute");
        if (t is null) return (false, "找不到 AudioMute 类型");
        var m = t.GetMethod("SetMuteByProcessName",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        if (m is null) return (false, "找不到 SetMuteByProcessName");
        try
        {
            // 反向操作（取消静音）做探针：就算游戏不在，也只应返回 0，不应抛异常
            var r = m.Invoke(null, new object[] { "League of Legends", false });
            int n = (int)(r ?? -1);
            return (true, n > 0 ? $"命中 {n} 个游戏音频会话（已确保未静音）"
                                : "没命中游戏会话（多半没在对局，通路本身是通的）");
        }
        catch (Exception ex)
        {
            return (false, "COM 音频通路失败：" + (ex.InnerException?.Message ?? ex.Message));
        }
    }

    // ================= 5. 自动收尾判断（watchvideo.ShouldAutoClose） =================

    public static (bool, string) ShouldAutoClose(LoadedPlugin p, IntPtr gameHwnd, string gameLabel)
    {
        var t = p.FindType("WatchVideoPlugin");
        if (t is null) return (false, "找不到 WatchVideoPlugin 类型");
        var inst = Activator.CreateInstance(t);
        var m = t.GetMethod("ShouldAutoClose", BindingFlags.NonPublic | BindingFlags.Instance);
        var autoF = t.GetField("_autoSwitchBack", BindingFlags.NonPublic | BindingFlags.Instance);
        var modeF = t.GetField("_inVideoMode", BindingFlags.NonPublic | BindingFlags.Instance);
        if (m is null || autoF is null || modeF is null)
            return (false, "ShouldAutoClose/_autoSwitchBack/_inVideoMode 有一个没找到（实现被重构了？同步改探针）");

        var pars = m.GetParameters();
        bool Call(IntPtr fg)
        {
            var args = new object?[pars.Length];
            args[0] = fg;
            if (pars.Length > 1 && pars[1].ParameterType.IsByRef) args[1] = "";
            var ok = (bool)m.Invoke(inst, args)!;
            return ok;
        }

        // 四种组合（和当年手工验证的口径一致）
        autoF.SetValue(inst, true);
        modeF.SetValue(inst, true);
        bool c1 = gameHwnd != IntPtr.Zero && Call(gameHwnd);        // 视频模式 + 前台是游戏 → True
        IntPtr other = ProbeWin.ForeignWindow();                    // 一个肯定不是游戏的窗口
        bool c2 = other != IntPtr.Zero ? Call(other) : !c1;         // 视频模式 + 前台是别的 → False
        modeF.SetValue(inst, false);
        bool c3 = gameHwnd != IntPtr.Zero && !Call(gameHwnd);       // 非视频模式 → False
        modeF.SetValue(inst, true);
        autoF.SetValue(inst, false);
        bool c4 = gameHwnd != IntPtr.Zero && !Call(gameHwnd);       // 自动收尾关 → False

        if (gameHwnd == IntPtr.Zero)
            return (false, "没找到游戏/客户端窗口，四组合没法全跑（跳过真窗断言）——对局里再跑一次");
        if (c1 && !c2 && c3 && c4)
            return (true, $"四组合全对（前台={gameLabel}）");
        return (false, $"四组合结果异常：{c1}/{!c2}/{c3}/{c4}");
    }

    // ================= 6. OCR 数字识别（deathwatch.ScreenProbe） =================

    /// <summary>
    /// 画一张含「23」的图，走完整的抓屏像素 → WinRT 解码 → OCR → 取整数链路。
    /// 真机 OCR 死亡倒计时能不能认出来仍要在对局里验证，但「引擎可用 + 解析正确」
    /// 这两步可以离线锁死。
    /// </summary>
    public static (bool, string) OcrDigits(LoadedPlugin p)
    {
        var t = p.FindType("ScreenProbe");
        if (t is null) return (false, "找不到 ScreenProbe 类型");
        var m = t.GetMethod("RecognizeSeconds", BindingFlags.NonPublic | BindingFlags.Static);
        var pick = t.GetMethod("PickSeconds", BindingFlags.NonPublic | BindingFlags.Static);
        if (m is null || pick is null) return (false, "RecognizeSeconds/PickSeconds 有一个没找到");

        // PickSeconds：区间过滤（1-90），挡掉伤害数字
        var a1 = pick.Invoke(null, new object?[] { "伤害 1234  击杀 18" });
        if ((int?)a1 != 18) return (false, $"PickSeconds 应挑出 18，实际 {a1}");
        var a2 = pick.Invoke(null, new object?[] { "no digits here" });
        if ((int?)a2 is not null) return (false, "没有数字应返回 null");

        // 画布上写数字，走完整 OCR 链路
        const string text = "23";
        using var bmp = new Bitmap(320, 140);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
            g.Clear(Color.FromArgb(20, 20, 24));
            using var f = new Font("Segoe UI", 64, FontStyle.Bold);
            var sz = g.MeasureString(text, f);
            g.DrawString(text, f, Brushes.White, (bmp.Width - sz.Width) / 2, (bmp.Height - sz.Height) / 2);
        }
        var (bgra, w, h) = ToBgra(bmp);
        var r = m.Invoke(null, new object?[] { bgra, w, h });
        if (r is null)
        {
            var status = t.GetMethod("EngineStatus", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
            string diag = status is not null ? (string)status.Invoke(null, Array.Empty<object>())! : "";
            return (false, "OCR 没读到数字（引擎不可用或识别失败）。" + diag);
        }
        if ((int)r != 23) return (false, $"OCR 读到 {r}，期望 23");
        return (true, "OCR 链路完整：合成图 → WinRT 解码 → 识别 → 取数，读到 23");
    }

    private static (byte[] px, int w, int h) ToBgra(Bitmap bmp)
    {
        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var px = new byte[bmp.Width * bmp.Height * 4];
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, px, 0, px.Length);
            return (px, bmp.Width, bmp.Height);
        }
        finally { bmp.UnlockBits(data); }
    }

    // ================= 7. 抓屏 + 灰度统计（deathwatch.Gdi） =================

    public static (bool, string) ScreenCapture(LoadedPlugin p)
    {
        var gdi = p.FindType("Gdi");
        if (gdi is null) return (false, "找不到 Gdi 类型");
        var cap = gdi.GetMethod("Capture", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        var sat = gdi.GetMethod("MeanSaturation", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        var black = gdi.GetMethod("MostlyBlack", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        if (cap is null || sat is null || black is null) return (false, "Capture/MeanSaturation/MostlyBlack 有一个没找到");

        var hwnd = ProbeWin.AnyVisibleWindow();
        if (hwnd == IntPtr.Zero) return (false, "找不到可抓的窗口");
        var args = new object?[] { hwnd, null, null, null, null };
        var ok = (bool)cap.Invoke(null, args)!;
        if (!ok) return (false, $"抓窗口 {hwnd} 失败");
        int w = (int)args[1]!, h = (int)args[2]!;
        var px = (byte[])args[3]!;
        double s = (double)sat.Invoke(null, new object?[] { px })!;
        bool isBlack = (bool)black.Invoke(null, new object?[] { px })!;
        if (px.Length != w * h * 4) return (false, $"像素长度不对：{px.Length} ≠ {w}×{h}×4");
        if (isBlack) return (false, "抓到的画面全黑（DirectX 窗口？Capture 内部应已换屏幕 DC 重试）");
        return (true, $"抓到 {w}×{h}，平均饱和度 {s:F1}（GDI 链路可用）");
    }
}

/// <summary>探针自己用的窗口工具。</summary>
internal static class ProbeWin
{
    public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumProc cb, IntPtr lParam);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder sb, int max);

    /// <summary>找一个「肯定不是游戏」的可见窗口（探针的替代前台）。</summary>
    public static IntPtr ForeignWindow()
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, _) =>
        {
            if (found != IntPtr.Zero) return false;
            if (!IsWindowVisible(h)) return true;
            var sb = new System.Text.StringBuilder(256);
            GetWindowText(h, sb, 256);
            if (sb.Length == 0) return true;
            if (sb.ToString().Contains("League", StringComparison.OrdinalIgnoreCase)) return true;
            GetWindowThreadProcessId(h, out var pid);
            if (pid == Environment.ProcessId) return true;   // 跳过探针自己的窗口
            found = h;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>随便一个可见窗口（抓屏自检用）。</summary>
    public static IntPtr AnyVisibleWindow() => ForeignWindow() != IntPtr.Zero
        ? ForeignWindow()
        : IntPtr.Zero;

    /// <summary>找游戏/客户端窗口（有就拿真句柄跑 ShouldAutoClose 的四组合）。</summary>
    public static (IntPtr Hwnd, string Label) FindGameLike()
    {
        (IntPtr, string) found = default;
        EnumWindows((h, _) =>
        {
            if (found.Item1 != IntPtr.Zero) return false;
            if (!IsWindowVisible(h)) return true;
            var sb = new System.Text.StringBuilder(256);
            GetWindowText(h, sb, 256);
            var title = sb.ToString();
            GetWindowThreadProcessId(h, out var pid);
            string proc = "";
            try { proc = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName.ToLowerInvariant(); }
            catch { }
            if (proc == "league of legends") { found = (h, "对局窗口(League of Legends.exe)"); return false; }
            if (proc == "leagueclientux" && title.Length > 0) { found = (h, "客户端窗口(LeagueClientUx.exe)"); return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }
}
