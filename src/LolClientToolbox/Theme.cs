using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace LolClientToolbox;

/// <summary>
/// 全界面唯一的视觉 token 表。
///
/// 之前的问题是每个控件自己临时取颜色（出现过七个不同的灰、三种强调色），
/// 层级分不出来，只能靠描边和发光去补，越补越乱。现在：
///   背景靠明度分层（每档差 11/255），文字分三级，全界面只有一个有彩色（品牌金）。
///   语义色（成功/警告/错误/信息）全部压低饱和度，并且每个状态都配形状+文字，不单靠颜色。
/// </summary>
internal static class T
{
    public const string FontName = "Microsoft YaHei UI";

    // ---------- 背景（同色相，明度等差） ----------
    public static readonly Color BgBase = Color.FromArgb(20, 23, 29);      // #14171D 窗体底
    public static readonly Color BgRaised = Color.FromArgb(31, 36, 44);    // #1F242C 卡片 / 状态区
    public static readonly Color BgOverlay = Color.FromArgb(38, 44, 54);   // #262C36 hover / 选中
    public static readonly Color BgPress = Color.FromArgb(46, 53, 66);     // #2E3542 按下
    public static readonly Color BgSunken = Color.FromArgb(14, 16, 20);    // #0E1014 日志底 / 输入框 / 关闭轨

    // ---------- 文字 ----------
    public static readonly Color TxPrimary = Color.FromArgb(230, 234, 242);    // #E6EAF2  14.9:1
    public static readonly Color TxSecondary = Color.FromArgb(162, 171, 185);  // #A2ABB9  6.7:1
    public static readonly Color TxTertiary = Color.FromArgb(122, 132, 150);   // #7A8496  4.8:1
    public static readonly Color TxDisabled = Color.FromArgb(90, 100, 120);    // #5A6478  3.0:1（禁用刻意低）

    // ---------- 边框 ----------
    public static readonly Color BdSubtle = Color.FromArgb(38, 44, 54);    // #262C36
    public static readonly Color BdStrong = Color.FromArgb(57, 65, 79);    // #39414F
    public static readonly Color BdInput = Color.FromArgb(70, 79, 96);     // #464F60 输入框 hover

    // ---------- 语义色 ----------
    public static readonly Color Accent = Color.FromArgb(200, 164, 93);        // #C8A45D 品牌金
    public static readonly Color AccentHover = Color.FromArgb(216, 182, 115);  // #D8B673
    public static readonly Color AccentPress = Color.FromArgb(169, 138, 73);   // #A98A49
    public static readonly Color AccentSoft = Color.FromArgb(36, 31, 20);      // #241F14 选中行底

    public static readonly Color Success = Color.FromArgb(95, 174, 140);       // #5FAE8C
    public static readonly Color SuccessBg = Color.FromArgb(23, 48, 40);       // #173028
    public static readonly Color SuccessBd = Color.FromArgb(46, 106, 87);      // #2E6A57
    public static readonly Color Warning = Color.FromArgb(217, 139, 74);       // #D98B4A
    public static readonly Color Error = Color.FromArgb(221, 107, 96);         // #DD6B60
    public static readonly Color Info = Color.FromArgb(111, 161, 216);         // #6FA1D8

    // 三张卡片不再各用一个色相 —— 色相差异会让人以为它们分属不同类别，
    // 其实它们只是同一个工具箱里的三个动作。统一成一个强调色，
    // 靠标题和图标区分就够了。
    public static readonly Color BarGold = Color.FromArgb(200, 164, 93);   // #C8A45D

    // ---------- 排版音阶（只准这六档，禁止 Font.Size ± n 派生） ----------
    public static Font Display() => new(FontName, 18f, FontStyle.Bold);   // 24px 阶段大字
    public static Font Title() => new(FontName, 15f, FontStyle.Bold);     // 20px 窗口标题
    public static Font Section() => new(FontName, 10.5f, FontStyle.Bold); // 14px 分区 / 卡片标题
    public static Font Card() => new(FontName, 10.5f, FontStyle.Bold);    // 14px
    public static Font Body() => new(FontName, 9f);                       // 12px 正文
    public static Font BodyBold() => new(FontName, 9f, FontStyle.Bold);
    public static Font Meta() => new(FontName, 8.25f);
    /// <summary>日志专用：11px 中文在 GDI 下显粗糙，提到 12px。</summary>
    public static Font LogFont() => new(FontName, 9f);                    // 11px 元信息
    public static Font MetaBold() => new(FontName, 8.25f, FontStyle.Bold);

    // ---------- 间距 / 圆角 / 尺寸 ----------
    public const int Sp1 = 4, Sp2 = 8, Sp3 = 12, Sp4 = 16, Sp5 = 20, Sp6 = 24;
    public const int RadiusContainer = 10;
    public const int RadiusControl = 6;
    public const int RadiusBadge = 4;
    public const int ControlH = 32;     // 最小点击区
    public const int CardH = 96;
    public const int LogRowH = 26;
    public const int PadOuter = 16;     // 全窗外边距统一 16

    /// <summary>把 96 DPI 下的设计值换算到当前 DPI。</summary>
    public static float Sx(Control c, float v) => v * (c.DeviceDpi / 96f);
    public static int Sxi(Control c, float v) => (int)Math.Round(v * (c.DeviceDpi / 96f));

    /// <summary>只有左侧两角是圆的：右侧要和自动开关直角拼接的主体区用它。</summary>
    public static GraphicsPath LeftRounded(Rectangle b, int radius)
    {
        var path = new GraphicsPath();
        int d = radius * 2;
        if (d > b.Width) d = b.Width;
        if (d > b.Height) d = b.Height;
        path.AddArc(new Rectangle(b.X, b.Y, d, d), 90, 90);
        path.AddLine(b.X, b.Y, b.X + b.Width, b.Y);
        path.AddLine(b.X + b.Width, b.Bottom, b.X, b.Bottom);
        path.CloseFigure();
        return path;
    }

    /// <summary>两个颜色按比例混合（按下染色、hover 过渡都靠它）。</summary>
    public static Color Mix(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0.0, 1.0);
        return Color.FromArgb(
            (int)Math.Round(a.R + (b.R - a.R) * t),
            (int)Math.Round(a.G + (b.G - a.G) * t),
            (int)Math.Round(a.B + (b.B - a.B) * t));
    }

    // ---------- 几何 helper ----------

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);

    /// <summary>
    /// 把系统标题栏切成暗色（Win10 1809+/Win11）。
    ///
    /// 坑：只在句柄刚创建时设一次，属性会被丢掉（标题栏仍是白的）——
    /// 必须在窗口可见之后再设一次，并且用 SWP_FRAMECHANGED 强制重绘边框，否则要等窗口移动才生效。
    /// 20 = DWMWA_USE_IMMERSIVE_DARK_MODE；19 是少数 Win10 早期版本的备用号。
    /// </summary>
    public static void DarkTitle(Form f)
    {
        if (f.Handle == IntPtr.Zero) return;
        try
        {
            int on = 1;
            _ = DwmSetAttribute(f.Handle, 20, ref on, sizeof(int));
            _ = DwmSetAttribute(f.Handle, 19, ref on, sizeof(int));
            const uint SWP_FRAMECHANGED = 0x0020, SWP_NOMOVE = 0x0002,
                       SWP_NOSIZE = 0x0001, SWP_NOZORDER = 0x0004;
            _ = SetWindowPos(f.Handle, IntPtr.Zero, 0, 0, 0, 0,
                             SWP_FRAMECHANGED | SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER);
        }
        catch { /* 老系统没这个属性，标题栏保持系统默认 */ }
    }

    public static GraphicsPath Rounded(Rectangle b, int radius)
    {
        var path = new GraphicsPath();
        int d = radius * 2;
        if (d > b.Width) d = b.Width;
        if (d > b.Height) d = b.Height;
        var arc = new Rectangle(b.Location, new Size(d, d));
        path.AddArc(arc, 180, 90);
        path.AddArc(new Rectangle(b.X + b.Width - d, b.Y, d, d), 270, 90);
        path.AddArc(new Rectangle(b.X + b.Width - d, b.Y + b.Height - d, d, d), 0, 90);
        path.AddArc(new Rectangle(b.X, b.Y + b.Height - d, d, d), 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>只有右侧两角是圆的（用于贴在卡片右缘的开关轨道）。</summary>
    public static GraphicsPath RightRounded(Rectangle b, int radius)
    {
        var path = new GraphicsPath();
        int d = radius * 2;
        if (d > b.Width) d = b.Width;
        if (d > b.Height) d = b.Height;
        path.AddLine(b.X, b.Y, b.X + b.Width - d, b.Y);
        path.AddArc(new Rectangle(b.X + b.Width - d, b.Y, d, d), 270, 90);
        path.AddArc(new Rectangle(b.X + b.Width - d, b.Y + b.Height - d, d, d), 0, 90);
        path.AddLine(b.X, b.Bottom, b.X, b.Top);
        path.CloseFigure();
        return path;
    }

    public static void Fill(Graphics g, Color color, GraphicsPath path) =>
        g.FillPath(new SolidBrush(color), path);

    public static void FillRect(Graphics g, Color color, Rectangle r) =>
        g.FillRectangle(new SolidBrush(color), r);

    /// <summary>画一个 1px 整数宽的描边（1.5 / 1.2 这类非整数在 100% DPI 下必糊）。</summary>
    public static void Stroke(Graphics g, Color color, GraphicsPath path, float w = 1f)
    {
        using var pen = new Pen(color, w);
        g.DrawPath(pen, path);
    }

    /// <summary>
    /// 文本一律走 TextRenderer（GDI 路径），不走 GDI+ 的 DrawString。
    /// DrawString 自己做光栅化，字距偏大、边缘发糊，和系统 UI / 浏览器的观感差一截；
    /// TextRenderer 用的就是系统 UI 那套渲染，ClearType 直出。测量也必须配套用
    /// MeasureText —— 两套API的宽度算法不一样，混用必然对不齐。
    /// </summary>
    public static Size MeasureText(string text, Font f) => TextRenderer.MeasureText(text, f);

    public static void DrawText(Graphics g, string text, Font f, Point pt, Color color) =>
        TextRenderer.DrawText(g, text, f, pt, color);

    /// <summary>超出宽度就截断加省略号（中英文混排下比自己算字符数靠谱）。</summary>
    public static string Clip(string text, Font f, float max)
    {
        if (max <= 12) return "";
        if (MeasureText(text, f).Width <= max) return text;
        for (int i = text.Length - 1; i > 0; i--)
        {
            var t = text[..i] + "…";
            if (MeasureText(t, f).Width <= max) return t;
        }
        return "";
    }

    /// <summary>画一个圆角徽章（类别标签用），返回它的右边界 x。</summary>
    public static float Badge(Graphics g, string text, Font f, Color fore, Color back,
                              Rectangle row, float x, float padX = 4)
    {
        var size = MeasureText(text, f);
        var r = new RectangleF(x, row.Y + (row.Height - size.Height - 4) / 2f,
                               size.Width + padX * 2, size.Height + 4);
        var ri = new Rectangle((int)Math.Round(r.X), (int)Math.Round(r.Y),
                               (int)Math.Round(r.Width), (int)Math.Round(r.Height));
        using var p = Rounded(ri, RadiusBadge);
        Fill(g, back, p);
        DrawText(g, text, f, new Point(ri.X + (int)padX, ri.Y + 2), fore);
        return r.Right;
    }
}
