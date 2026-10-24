using System;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace DeathWatchPlugin;

/// <summary>
/// 屏幕兜底：Live 接口取不到时（没在对局 / 接口被关 / 版本变了），改成**看自己的屏幕**。
///
/// 两条信号，独立判断、都写进日志：
///  ① 死亡灰屏（主判据）—— 英雄死后整个画面被去色，平均饱和度会掉到很低。
///     比 OCR 稳：不认识字也能判断。
///  ② 倒计时读数（辅助）—— OCR 画面中下部那条带子里的数字，用来判断「还剩几秒」，
///     决定值不值得切出去。认不出来就用 assumedRespawnSec 顶上。
///
/// 合规边界（重要）：只读自己屏幕上的像素，OCR 也是 Windows 自带的组件。
/// 不读游戏内存、不注入游戏进程 —— 那两条是红线（Riot 2026-10-06 已封，
/// 已批准工具 2027-04 前也必须迁走）。
/// </summary>
internal static class ScreenProbe
{
    /// <summary>一次屏幕采样的结果。</summary>
    public sealed class Sample
    {
        /// <summary>true=灰屏（判定死亡）；false=有颜色（判定活着）；null=没采到（截不到画面）。</summary>
        public bool? Dead { get; init; }
        /// <summary>OCR 读到的倒计时秒数；读不到为 null。</summary>
        public int? Seconds { get; init; }
        public double Saturation { get; init; }
        public string Note { get; init; } = "";
    }

    private static OcrEngine? _engine;
    private static bool _engineTried;
    private static readonly object _engineLock = new();

    /// <summary>诊断用：这台机器的 OCR 能力（探针会调，报告里能看到缺什么）。</summary>
    internal static string EngineStatus()
    {
        try
        {
            var langs = OcrEngine.AvailableRecognizerLanguages;
            var names = string.Join(",", langs.Select(l => l.LanguageTag));
            return $"可用 OCR 语言：{(names.Length == 0 ? "（无）" : names)}"
                   + $" · en-US 支持：{OcrEngine.IsLanguageSupported(new Windows.Globalization.Language("en-US"))}";
        }
        catch (Exception ex)
        {
            return "OCR 能力查询失败：" + ex.Message;
        }
    }

    /// <summary>
    /// 采样一次。调用方（后台线程）自己控制频率 —— 抓屏 + OCR 有实际开销，别每 500ms 做。
    /// </summary>
    public static Sample Probe(IntPtr hwnd, double satThreshold, double bandTop, double bandBottom)
    {
        if (!Gdi.Capture(hwnd, out int w, out int h, out var px, out bool fromScreen))
            return new Sample { Note = "截不到画面（独占全屏？）——屏幕兜底不可用" };

        double sat = Gdi.MeanSaturation(px);
        bool grey = sat < satThreshold;

        int? sec = null;
        string ocrNote = "";
        if (grey)
        {
            // 只在灰屏时才 OCR：有颜色的画面里数字多，容易读错
            var (cw, ch, crop) = Gdi.Crop(px, w, h, bandTop, bandBottom);
            if (cw > 0 && ch > 0)
            {
                var (sw, sh, scaled) = Gdi.Scale(crop, cw, ch, 3);
                Gdi.FillAlpha(scaled);
                try
                {
                    sec = RecognizeSeconds(scaled, sw, sh);
                    ocrNote = sec.HasValue ? $"OCR 读到 {sec}s" : "OCR 没读到数字";
                }
                catch (Exception ex)
                {
                    ocrNote = "OCR 失败：" + ex.Message;
                }
            }
        }

        var note = $"饱和度 {sat:F1}（阈值 {satThreshold}）"
                   + (fromScreen ? " · 用屏幕 DC" : " · 用窗口 DC")
                   + (ocrNote.Length > 0 ? " · " + ocrNote : "");
        return new Sample { Dead = grey, Seconds = sec, Saturation = sat, Note = note };
    }

    /// <summary>OCR 一条横向画面带，返回里面最像「复活倒计时」的整数秒。</summary>
    private static int? RecognizeSeconds(byte[] bgra, int w, int h)
    {
        var engine = EnsureEngine();
        if (engine is null) return null;

        // 走「编码 PNG → 解码成 SoftwareBitmap」这条路的理由：
        // SoftwareBitmap 没有「从 byte[] 直接建」的托管入口（CopyFromBuffer 要 IBuffer，
        // .NET 5+ 已经不带 AsBuffer() 扩展了），而 BitmapEncoder.SetPixelData 直接吃 byte[]。
        // 一次内存里的 PNG 编解码开销远小于 OCR 本身，不值得为它写 COM interop。
        var ras = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        var enc = BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, ras)
                               .AsTask().GetAwaiter().GetResult();
        enc.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                         (uint)w, (uint)h, 96, 96, bgra);
        enc.FlushAsync().AsTask().GetAwaiter().GetResult();
        ras.Seek(0);

        var dec = BitmapDecoder.CreateAsync(ras).AsTask().GetAwaiter().GetResult();
        using var bmp = dec.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8,
                                                   BitmapAlphaMode.Premultiplied)
                           .AsTask().GetAwaiter().GetResult();
        var result = engine.RecognizeAsync(bmp).AsTask().GetAwaiter().GetResult();
        return PickSeconds(result.Text);
    }

    /// <summary>
    /// 从 OCR 文本里挑一个 1-90 的整数。死亡倒计时不会超过 90 秒。
    /// 用「前后都不是数字」的锚定（(?&lt;!\\d)\\d{1,2}(?!\\d)）—— 这样
    /// 伤害数字 1234 不会被错拆成 12/34，LV18 这种连写的也碰不到（字母和数字之间没有边界）。
    /// </summary>
    internal static int? PickSeconds(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        foreach (Match m in Regex.Matches(text, @"(?<!\d)\d{1,2}(?!\d)"))
        {
            if (int.TryParse(m.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)
                && v >= 1 && v <= 90)
                return v;
        }
        return null;
    }

    /// <summary>取 OCR 引擎（延迟创建，失败只试一次，之后每次直接返回 null 免得刷日志）。</summary>
    private static OcrEngine? EnsureEngine()
    {
        lock (_engineLock)
        {
            if (_engine is not null) return _engine;
            if (_engineTried) return null;
            _engineTried = true;

            // en-US 一定装了；中文包不一定。数字识别用英文引擎反而更准。
            try
            {
                var en = OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("en-US"));
                if (en is not null) { _engine = en; return _engine; }
            }
            catch { /* 换下一种方式 */ }

            try
            {
                var any = OcrEngine.TryCreateFromUserProfileLanguages();
                if (any is not null) { _engine = any; return _engine; }
            }
            catch { /* 这台机器没有可用的 OCR 语言包 */ }

            return null;
        }
    }
}
