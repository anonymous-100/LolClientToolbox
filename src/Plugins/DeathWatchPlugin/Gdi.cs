using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace DeathWatchPlugin;

/// <summary>
/// 屏幕兜底要用到的 Win32：找对局窗口、抓画面（GDI BitBlt）。
///
/// 为什么不用 System.Drawing：插件是纯类库（没引 WinForms），System.Drawing.Common
/// 在 .NET 6+ 已经从共享框架里挪出去，单独引又多一份依赖。这里只用 P/Invoke 就能拿到
/// BGRA 像素，正好也是 WinRT SoftwareBitmap 要的格式（Bgra8），中间不用转码。
///
/// 抓图的坑（必读）：
///   · 优先用**窗口 DC**（GetDC(hwnd)）—— 窗口被别的窗口盖住也能抓到。
///   · DirectX 游戏在窗口 DC 上常抓出全黑，这时退回**屏幕 DC**（GetDC(0)）+ 窗口矩形。
///     代价：窗口被别的窗口挡住时会抓到挡住的那块。
///   · 独占全屏两种都抓不到（画面走的是翻转链，不进 GDI），只能报「截不到画面」。
/// </summary>
internal static class Gdi
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;      // 负数 = 自上而下（省得后面再翻转）
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    private const uint BI_RGB = 0;
    private const uint SRCCOPY = 0x00CC0020;
    private const uint CAPTUREBLT = 0x40000000;   // 带上被分层窗口盖住的内容

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hwnd, out RECT lpRect);
    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr hwnd, StringBuilder lpString, int nMaxCount);
    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint lpdwProcessId);

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr hdcDest, int xDest, int yDest, int w, int h,
        IntPtr hdcSrc, int xSrc, int ySrc, uint rop);
    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr hdc, IntPtr hbmp, uint uStartScan, uint cScanLines,
        byte[] lpvBits, ref BITMAPINFOHEADER lpbmi, uint uUsage);

    /// <summary>对局进程在不在（League of Legends.exe 只在对局期间存在）。</summary>
    public static bool GameProcessRunning()
    {
        try { return Process.GetProcessesByName("League of Legends").Length > 0; }
        catch { return false; }
    }

    /// <summary>找对局窗口（进程名 League of Legends，枚举出来是小写带空格）。</summary>
    public static IntPtr FindGameWindow()
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((hwnd, _) =>
        {
            if (found != IntPtr.Zero) return false;
            if (!IsWindowVisible(hwnd)) return true;
            GetWindowThreadProcessId(hwnd, out var pid);
            string proc = "";
            try { proc = Process.GetProcessById((int)pid).ProcessName.ToLowerInvariant(); }
            catch { /* 进程可能刚退出 */ }
            if (proc == "league of legends") found = hwnd;
            return true;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>
    /// 抓窗口画面。
    /// </summary>
    /// <returns>
    /// 成功返回 true，<paramref name="pixels"/> 是 <c>w*h*4</c> 的 BGRA 数据（自上而下）。
    /// <paramref name="fromScreen"/> 说明最终是用哪种 DC 抓到的（诊断用）。
    /// </returns>
    public static bool Capture(IntPtr hwnd, out int w, out int h, out byte[] pixels, out bool fromScreen)
    {
        w = h = 0;
        pixels = Array.Empty<byte>();
        fromScreen = false;
        if (hwnd == IntPtr.Zero) return false;
        if (!GetWindowRect(hwnd, out var r) || r.Width <= 8 || r.Height <= 8) return false;

        // 先试窗口 DC；抓出来几乎全黑（DirectX 常见）就换屏幕 DC 再试一次
        if (TryCapture(hwnd, GetDC(hwnd), 0, 0, r.Width, r.Height, out pixels) && !MostlyBlack(pixels))
        {
            w = r.Width; h = r.Height;
            return true;
        }

        fromScreen = true;
        if (TryCapture(IntPtr.Zero, GetDC(IntPtr.Zero), r.Left, r.Top, r.Width, r.Height, out pixels)
            && !MostlyBlack(pixels))
        {
            w = r.Width; h = r.Height;
            return true;
        }
        return false;
    }

    /// <param name="releaseHwnd">GetDC 用的那个窗口句柄（屏幕 DC 传 0）。</param>
    private static bool TryCapture(IntPtr releaseHwnd, IntPtr hdcSrc, int x, int y, int w, int h,
                                   out byte[] pixels)
    {
        pixels = Array.Empty<byte>();
        if (hdcSrc == IntPtr.Zero) return false;

        IntPtr memDc = IntPtr.Zero, bmp = IntPtr.Zero, old = IntPtr.Zero;
        try
        {
            memDc = CreateCompatibleDC(hdcSrc);
            if (memDc == IntPtr.Zero) return false;
            bmp = CreateCompatibleBitmap(hdcSrc, w, h);
            if (bmp == IntPtr.Zero) return false;
            old = SelectObject(memDc, bmp);

            if (!BitBlt(memDc, 0, 0, w, h, hdcSrc, x, y, SRCCOPY | CAPTUREBLT)) return false;

            var bi = new BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = w,
                biHeight = -h,        // 负高度 = 自上而下
                biPlanes = 1,
                biBitCount = 32,
                biCompression = BI_RGB,
            };
            var buf = new byte[w * h * 4];
            int got = GetDIBits(memDc, bmp, 0, (uint)h, buf, ref bi, BI_RGB);
            if (got != h) return false;
            pixels = buf;
            return true;
        }
        finally
        {
            if (old != IntPtr.Zero) SelectObject(memDc, old);
            if (bmp != IntPtr.Zero) DeleteObject(bmp);
            if (memDc != IntPtr.Zero) DeleteDC(memDc);
            if (hdcSrc != IntPtr.Zero) ReleaseDC(releaseHwnd, hdcSrc);
        }
    }

    /// <summary>画面是不是几乎全黑（抓失败的典型特征，独占全屏/DirectX 窗口 DC 常见）。</summary>
    public static bool MostlyBlack(byte[] px)
    {
        if (px.Length == 0) return true;
        int step = 4 * 37;                  // 稀疏抽样，不用看每个像素
        int total = 0, dark = 0;
        for (int i = 0; i + 2 < px.Length; i += step)
        {
            total++;
            if (px[i] < 12 && px[i + 1] < 12 && px[i + 2] < 12) dark++;
        }
        return total > 0 && (double)dark / total > 0.98;
    }

    /// <summary>
    /// 平均饱和度（0-255）。英雄「死亡灰屏」就是整个画面被去色，
    /// 这个值会掉到很低 —— 是比 OCR 更稳的死亡信号（不依赖任何文字识别）。
    /// </summary>
    public static double MeanSaturation(byte[] px)
    {
        if (px.Length == 0) return 0;
        long sum = 0;
        int n = 0;
        int step = 4 * 11;
        for (int i = 0; i + 2 < px.Length; i += step)
        {
            int b = px[i], g = px[i + 1], r = px[i + 2];
            int mx = Math.Max(r, Math.Max(g, b));
            int mn = Math.Min(r, Math.Min(g, b));
            sum += mx - mn;
            n++;
        }
        return n == 0 ? 0 : (double)sum / n;
    }

    /// <summary>
    /// 裁一块出来（相对坐标 0-1）。死亡倒计时在游戏画面的中下部，
    /// 只 OCR 这一条带子：快、且不会把队友 ID、技能说明都识别进来。
    /// </summary>
    public static (int W, int H, byte[] Pixels) Crop(byte[] px, int w, int h,
                                                    double top, double bottom)
    {
        int y0 = (int)(h * top);
        int y1 = (int)(h * bottom);
        if (y1 <= y0 || w <= 0) return (0, 0, Array.Empty<byte>());
        int ch = y1 - y0;
        var outBuf = new byte[w * ch * 4];
        Array.Copy(px, y0 * w * 4, outBuf, 0, outBuf.Length);
        return (w, ch, outBuf);
    }

    /// <summary>把 BGRA 放大（最近邻）。倒计时数字在 1080p 下也不过几十像素高，
    /// OCR 对太小的字识别率很差，放大 2-3 倍再喂进去。</summary>
    public static (int W, int H, byte[] Pixels) Scale(byte[] px, int w, int h, int factor)
    {
        if (factor <= 1) return (w, h, px);
        int nw = w * factor, nh = h * factor;
        var outBuf = new byte[nw * nh * 4];
        for (int y = 0; y < nh; y++)
        {
            int sy = y / factor;
            int dst = y * nw * 4;
            int src = sy * w * 4;
            for (int x = 0; x < nw; x++)
            {
                int sx = x / factor;
                outBuf[dst + x * 4 + 0] = px[src + sx * 4 + 0];
                outBuf[dst + x * 4 + 1] = px[src + sx * 4 + 1];
                outBuf[dst + x * 4 + 2] = px[src + sx * 4 + 2];
                outBuf[dst + x * 4 + 3] = 255;
            }
        }
        return (nw, nh, outBuf);
    }

    /// <summary>把 alpha 全填成 255 —— GDI 抓出来的 alpha 常常是 0，
    /// 交给 WinRT 时会按「预乘」解释成纯透明（等于全黑），OCR 什么都认不出来。</summary>
    public static void FillAlpha(byte[] px)
    {
        for (int i = 3; i < px.Length; i += 4) px[i] = 255;
    }
}
