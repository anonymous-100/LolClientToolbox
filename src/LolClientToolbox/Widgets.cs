using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace LolClientToolbox;

internal enum CardState
{
    Idle,
    Running,
    Done,
    Fail,
    Background
}

/// <summary>
/// 一张功能卡片。主体（约 9 成宽）是功能按钮，右侧一条窄轨（约 1 成宽）是「自动」开关。
///
/// 两块是并排的独立命中区：点右侧轨道只切换自动开关，点左侧主体才执行功能，
/// 不会出现「想开自动结果把动作跑了」的误触。
///
/// 视觉上不再用填充色变化表达状态（以前执行中反而变暗，看着像禁用）：
/// 恒定 1px 描边 + 右上角「图标+文字」徽标，形状/文字/颜色三重编码。
/// </summary>
internal sealed class ActionCard : Button
{
    public string CardTitle = "";
    public string CardDesc = "";
    public string AutoNote = "";
    public Color Accent = T.BarGold;

    public bool ShowAuto;
    public bool AutoOn;

    /// <summary>按下时朝强调色混合的比例（见 SetPressStrength）。</summary>
    public double PressMix = 0.60;
    /// <summary>按下时内容上抬与向外扩张的像素数（松开时按这个量内缩）。</summary>
    public int PressLift = 4;
    public int Grow => PressLift;
    /// <summary>按下时底边内侧影子的厚度（像素）。</summary>
    public int PressShadow = 5;
    /// <summary>影子的最深浓度 0–255。</summary>
    public int PressShadowAlpha = 130;

    /// <summary>
    /// 把 0–100 的力度档换算成绘制参数。100 = 夸张，0 = 几乎只剩一点染色。
    /// 功能区按钮和右侧的自动开关共用同一份力度。
    /// </summary>
    public void SetPressStrength(int percent)
    {
        double k = Math.Clamp(percent, 0, 100) / 100.0;
        PressMix = 0.08 + 0.52 * k;
        PressLift = (int)Math.Round(1 + 3 * k);
        PressShadow = (int)Math.Round(1 + 4 * k);
        PressShadowAlpha = (int)Math.Round(40 + 90 * k);
    }

    public event EventHandler? AutoToggled;

    private CardState _state = CardState.Idle;
    private string _resultText = "";
    private bool _hover;
    private bool _pressed;      // 功能主体被按住
    private bool _railHover;
    private bool _railPressed;  // 自动开关被按住（和功能那套完全分开）

    // 松开之后的回弹：按住是「沉下去」，松手不能直接瞬移回原位，
    // 那手感是死的。这里用一条 200ms 的衰减曲线把值从 1 拉回 0，
    // 中途会轻微越过零点（内容往上顶一下），这一下就是「弹」。
    private readonly System.Windows.Forms.Timer _bounce = new() { Interval = 16 };
    private readonly System.Diagnostics.Stopwatch _sw = new();
    private bool _bouncing;
    private bool _bounceRail;   // 这一下弹的是开关那一侧还是功能区
    private float _bounceVal;

    // ---------- 长按拖动排序（安卓桌面那套手感）----------
    // 卡片本身就是个大按钮（点一下＝执行功能），所以要严格区分：
    //   短按松手 → 点击，执行功能
    //   按住 400ms → 进入拖拽待命，之后移动＝拖动、松手＝放下，全程不触发点击
    private const int LongPressMs = 400;
    private readonly System.Windows.Forms.Timer _longPress = new() { Interval = LongPressMs };
    private bool _dragArmed;    // 长按已到位，进入拖拽
    private bool _suppressClick; // 这一轮松手别再当成点击
    private bool _dropHint;      // 别的卡片拖到我头上时的高亮

    /// <summary>长按到位，可以开始拖了。</summary>
    public event EventHandler? DragStarted;
    /// <summary>拖拽中，参数为鼠标的屏幕坐标。</summary>
    public event EventHandler<Point>? DragMoved;
    /// <summary>松手放下（调用方在这里决定插到哪）。</summary>
    public event EventHandler? DragDropped;

    /// <summary>拖拽时高亮：「松手会插到这里」。</summary>
    public void SetDropHint(bool on)
    {
        if (_dropHint == on) return;
        _dropHint = on;
        Invalidate();
    }
    private const int BounceMs = 200;

    public ActionCard()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                 ControlStyles.Selectable, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        FlatAppearance.MouseOverBackColor = Color.Transparent;
        FlatAppearance.MouseDownBackColor = Color.Transparent;
        BackColor = T.BgRaised;
        Margin = new Padding(0);
        Cursor = Cursors.Hand;
        Font = T.Card();
        _bounce.Tick += (_, _) => TickBounce();
        _longPress.Tick += (_, _) => ArmDrag();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _bounce.Stop(); _bounce.Dispose();
            _longPress.Stop(); _longPress.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <summary>长按到位：进入拖拽待命。之后移动是拖动、松手是放下，都不算点击。</summary>
    private void ArmDrag()
    {
        _longPress.Stop();
        if (_dragArmed || !_pressed) return;   // 已经松手了就别再进
        _dragArmed = true;
        _suppressClick = true;
        Cursor = Cursors.SizeAll;
        DragStarted?.Invoke(this, EventArgs.Empty);
    }

    private void BeginBounce(bool rail)
    {
        _bounceRail = rail;
        _bouncing = true;
        _bounceVal = 1f;
        _sw.Restart();
        _bounce.Start();
    }

    private void TickBounce()
    {
        double p = _sw.ElapsedMilliseconds / (double)BounceMs;
        if (p >= 1)
        {
            _bouncing = false;
            _bounceVal = 0;
            _bounce.Stop();
        }
        else
        {
            // q² 负责衰减，cos(1.4πp) 负责在 p≈0.36 之后越过零点，形成一次轻微的反向
            double q = 1 - p;
            _bounceVal = (float)(q * q * Math.Cos(Math.PI * 1.4 * p));
        }
        Invalidate();
    }

    public CardState State => _state;
    public bool Busy => _state is CardState.Running;

    public void SetState(CardState state, string resultText = "")
    {
        _state = state;
        _resultText = resultText;
        Cursor = state is CardState.Running ? Cursors.WaitCursor : Cursors.Hand;
        Invalidate();
    }

    public void SetAuto(bool on, string note)
    {
        AutoOn = on;
        AutoNote = note;
        Invalidate();
    }

    // ---------- 分区几何 ----------

    /// <summary>
    /// 右侧轨道宽度：按 10% 取，卡在 [56,72]。
    /// 下限从 44 抬到 56：卡片改两列后单卡变窄（约 430px），44px 装不下「自动」两个字，
    /// 会被画成「自…」（实测截图确认）。
    /// </summary>
    private int RailWidth()
    {
        int w = (int)Math.Round(ClientRectangle.Width * 0.10);
        return Math.Clamp(w, 56, 72);
    }

    /// <summary>
    /// 右侧开关与左侧功能区之间那条缝，3px —— 已经极小，但 2px 在抗锯齿下会被糊成一条线。
    /// </summary>
    private const int RailGap = 3;

    private Rectangle RailRect()
    {
        var r = ClientRectangle;
        int rw = RailWidth();
        return new Rectangle(r.Right - rw, r.Y, rw, r.Height);
    }

    private Rectangle BodyRect()
    {
        var r = ClientRectangle;
        int rw = ShowAuto ? RailWidth() + RailGap : 0;
        return new Rectangle(r.X, r.Y, r.Width - rw, r.Height);
    }

    /// <summary>
    /// 按下的那一块往哪边长大：k=0 松开时内缩，k=1 按下时撑满，k&gt;1 回弹过冲会再大一点。
    /// 关键是<b>不许往邻居那边长</b> —— 左块只动左边/上下，右边界贴着竖线不动；
    /// 右块只动右边/上下，左边界不动。否则两块会互相压过去。
    /// </summary>
    private Rectangle PressedRect(Rectangle zone, float k)
    {
        int g = (int)Math.Round(Grow * (1f - Math.Clamp(k, 0f, 1.35f)));
        return new Rectangle(zone.X + g, zone.Y + g,
                             Math.Max(1, zone.Width - g), Math.Max(1, zone.Height - g * 2));
    }

    // ---------- 命中判定 ----------

    protected override void OnMouseMove(MouseEventArgs e)
    {
        // 拖拽中：把屏幕坐标交给宿主去算「现在悬在哪张卡上」
        if (_dragArmed)
        {
            DragMoved?.Invoke(this, PointToScreen(e.Location));
            return;
        }

        var was = _railHover;
        _railHover = ShowAuto && RailRect().Contains(e.Location);
        if (was != _railHover) Invalidate();
        base.OnMouseMove(e);
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        _hover = true;
        base.OnMouseEnter(e);
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        bool wasLeft = _pressed, wasRail = _railPressed;
        _hover = false;
        _pressed = false;
        _railHover = false;
        _railPressed = false;
        base.OnMouseLeave(e);
        if (wasLeft || wasRail) BeginBounce(wasRail);
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        // 两套点击区互不牵连：按住右边就不许把自己当成「功能被按下」
        if (ShowAuto && RailRect().Contains(e.Location)) _railPressed = true;
        else _pressed = true;

        // 只有按在功能主体上才开始算长按；按开关不参与拖拽
        if (e.Button == MouseButtons.Left && _pressed)
        {
            _dragArmed = false;
            _suppressClick = false;
            _longPress.Start();
        }

        base.OnMouseDown(e);
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        _longPress.Stop();

        bool wasLeft = _pressed, wasRail = _railPressed;
        _pressed = false;
        _railPressed = false;

        bool wasDrag = _dragArmed;
        if (wasDrag)
        {
            _dragArmed = false;
            Cursor = Cursors.Hand;
        }

        base.OnMouseUp(e);
        if (wasDrag) DragDropped?.Invoke(this, EventArgs.Empty);   // 先放下，再回弹
        if (wasLeft || wasRail) BeginBounce(wasRail);
        Invalidate();
    }

    protected override void OnClick(EventArgs e)
    {
        // 刚拖完那一下不算点击：卡片主体本来就是「点一下执行功能」，
        // 不拦的话每次拖动都会顺手触发一次功能（比如打开战绩窗口）。
        if (_suppressClick)
        {
            _suppressClick = false;
            return;
        }

        var pt = PointToClient(MousePosition);
        if (ShowAuto && RailRect().Contains(pt))
        {
            AutoOn = !AutoOn;
            Invalidate();
            AutoToggled?.Invoke(this, EventArgs.Empty);
            return;
        }
        base.OnClick(e);
    }

    // ---------- 绘制 ----------

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        // Half 像素对齐：HighQuality 会把 1px 描边抹成 2px 模糊边，
        // 这是「程序里边框发虚、浏览器里边框锐利」的直接原因之一
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        float s = DeviceDpi / 96f;
        int P(float v) => (int)Math.Round(v * s);

        var full = ClientRectangle;
        full.Inflate(-1, -1);
        int radius = Math.Max(2, P(T.RadiusContainer));
        int railW = ShowAuto ? RailWidth() : 0;
        int gapW  = ShowAuto ? RailGap : 0;
        // 两块各自独立成块，中间留 RailGap(2px) 的缝 —— 缝本身就是分界，不画线。
        // 分开了，「按下去变大」才有地方可去：整块连体时是做不到只让一半撑大的。
        var leftZone = new Rectangle(full.X, full.Y, full.Width - railW - gapW, full.Height);
        var railZone = new Rectangle(full.Right - railW, full.Y, railW, full.Height);

        var idle = _state == CardState.Idle;

        using (var path = T.Rounded(full, radius))
        {
            // 底色必须是暗的（Sunken）：它是两块之间那条 2px 缝里露出来的颜色，
            // 之前错用了和块一样的 Raised，缝直接隐形 —— 预览里看得见、程序里看不见，根子在这。
            // 整卡不再描边 —— 预览里没有这一圈，外轮廓由两块各自的边框拼出来
            T.Fill(g, T.BgSunken, path);
        }

        // 拖拽落点高亮：整卡描一圈强调色。比改底色醒目，也不会污染原有配色
        if (_dropHint)
        {
            using var hp = new Pen(T.Accent, Math.Max(2f, P(2)));
            using var hpath = T.Rounded(full, radius);
            g.DrawPath(hp, hpath);
        }

        // 按哪边，哪边浮 —— 另一边完全不参与。
        // 按钮连在一起不代表反馈也要连在一起：这是两个操作，各自给各自的那份回应。
        // k 是当前动画进度：按住=1，松手后在 200ms 里回落到 0，中途越过零点再回来。
        float leftDown = _pressed ? 1f : (_bouncing && !_bounceRail ? _bounceVal : 0f);
        float railDown = _railPressed ? 1f : (_bouncing && _bounceRail ? _bounceVal : 0f);
        // 两块各自的绘制矩形：静止时只往「外侧」缩，绝不去挤邻居
        var lRect = PressedRect(leftZone, leftDown);
        var rRect = PressedRect(railZone, railDown);
        // 只有左边功能区走「浮起」；右边自动区不调 DrawZone，它只填充颜色（见 DrawRail）
        DrawZone(g, lRect, Accent, radius, P, idle, leftDown, on: false);

        // 动效**不联动内部内容**：以前按下去文字跟着上抬 4px，观感是字体在跳，很丑。
        // 现在文字钉死不动，只有卡片本身变大/变色/回弹。
        int lift = 0;
        // 必须用 lRect（内缩后的块）而不是 leftZone，否则文字和条相对块的边框偏 Grow 像素
        DrawAccentBar(g, lRect, P, idle, lift);
        DrawBody(g, lRect, P, idle, s, lift);
        if (ShowAuto) DrawRail(g, railZone, rRect, radius, P, idle, s);
        // 焦点虚线框已删除：鼠标点完会留一个金色虚线框常驻，预览里也没有这东西。
        // 这个工具以鼠标操作为主，键盘可达性由 Tab 顺序保证，不需要画出来。
    }

    /// <summary>
    /// 「浮起来」这一半：染上自己的色 + 内容上抬 + 底边内侧留一道影子。
    ///
    /// 不做真实的几何放大 —— 两侧按钮是连体的，把其中一半撑大就把卡片撕开了，
    /// 而且自绘控件本来也画不到自己的客户区外面。改用三层视觉线索表达「抬起来」：
    /// 颜色变亮、内容往上顶一点、脚下压一道渐深的暗边。
    /// </summary>
    private void DrawZone(Graphics g, Rectangle rect, Color accent, int radius,
                          Func<float, int> P, bool idle, float k, bool on)
    {
        double t = Math.Clamp(PressMix * k, 0, 1);
        Color fill = on
            // 开启态本身就是金块：按下时轻微压暗，抬起时回到纯金
            ? T.Mix(T.Accent, T.BgBase, 0.22 * Math.Clamp(1f - k, 0f, 1f))
            // 关闭态是常规牌面：按下时染上这张卡的强调色
            : T.Mix(_hover ? T.BgOverlay : T.BgRaised, accent, t);

        // 四角全圆：验收图里缝的两侧都是圆弧（圆—缝—圆），
        // 之前用 LeftRounded 只圆外侧两角，缝旁边成直角，和验收图对不上
        using (var path = T.Rounded(rect, radius))
        {
            T.Fill(g, fill, path);
            T.Stroke(g, idle ? (_hover ? T.BdStrong : T.BdSubtle) : StateColor(), path);
        }

        if (k <= 0.02f) return;   // 归位了就不用画光影了

        // 顶边高光 + 底边内侧渐深：抬起感的两条线索
        int hl = (int)Math.Round(70 * Math.Clamp(k, 0, 1));
        using (var hp = new Pen(Color.FromArgb(hl, 255, 255, 255), Math.Max(1f, P(1))))
            g.DrawLine(hp, rect.X + radius, rect.Y + 1, rect.Right - radius, rect.Y + 1);

        int rows = (int)Math.Round(P(PressShadow) * Math.Clamp(k, 0, 1));
        for (int i = 0; i < rows; i++)
        {
            int a = (int)Math.Round(PressShadowAlpha * k * (1.0 - i / (double)Math.Max(1, rows)));
            using var pen = new Pen(Color.FromArgb(Math.Clamp(a, 0, 255), 0, 0, 0), Math.Max(1f, P(1)));
            g.DrawLine(pen, rect.X + radius, rect.Bottom - 1 - i, rect.Right - radius, rect.Bottom - 1 - i);
        }
    }

    private Color StateColor() => _state switch
    {
        CardState.Done => T.Success,
        CardState.Fail => T.Error,
        CardState.Running => T.Info,
        CardState.Background => T.Info,
        _ => T.BdSubtle
    };

    private void DrawAccentBar(Graphics g, Rectangle r, Func<float, int> P, bool idle, int lift)
    {
        Color c = idle ? Accent : StateColor();
        var bar = new Rectangle(r.X + P(12), r.Y + P(12) - lift, Math.Max(2, P(3)),
                                Math.Max(2, r.Height - P(24)));
        using var path = T.Rounded(bar, Math.Max(1, P(2)));
        T.Fill(g, c, path);
    }

    private void DrawBody(Graphics g, Rectangle r, Func<float, int> P, bool idle, float s, int lift)
    {
        var body = r;
        float textX = r.X + P(24);

        using var titleFont = T.Card();
        using var descFont = T.Body();

        // 说明行删除后只剩标题：单行垂直居中
        float blockH = 18 * s;
        float y0 = r.Y + (r.Height - blockH) / 2f - lift;

        T.DrawText(g, CardTitle, titleFont, new Point((int)textX, (int)y0),
                   idle ? T.TxPrimary : T.TxSecondary);

        // 描述行已按用户要求删除（三个卡片的说明文字全删），只留标题。
        // descFont 保留声明，免得以后想加回来又找不到字体定义。

        DrawBadge(g, body, y0, P, idle, s);
    }

    /// <summary>右上角状态徽标：图标 + 文字，色盲/夜间也能读（不单靠颜色）。</summary>
    private void DrawBadge(Graphics g, Rectangle body, float y0, Func<float, int> P, bool idle, float s)
    {
        string text;
        Color color;
        if (!idle)
        {
            (text, color) = _state switch
            {
                CardState.Running => ("● 执行中…", T.Info),
                CardState.Done => ("✓ " + (_resultText.Length > 0 ? _resultText : "完成"), T.Success),
                CardState.Fail => ("✕ " + (_resultText.Length > 0 ? _resultText : "失败"), T.Error),
                CardState.Background => ("● " + (_resultText.Length > 0 ? _resultText : "后台运行"), T.Info),
                _ => ("", T.TxTertiary)
            };
        }
        else if (ShowAuto && AutoOn)
        {
            text = "✓ " + (AutoNote.Length > 0 ? AutoNote : "自动中");
            color = T.Success;
        }
        else
        {
            text = "›";
            color = T.TxTertiary;
        }
        if (text.Length == 0) return;

        using var font = text == "›" ? T.Body() : T.MetaBold();
        var size = T.MeasureText(text, font);
        float x = body.Right - size.Width - P(12);
        float y = text == "›" ? y0 - 2 * s : y0 + P(3);
        T.DrawText(g, text, font, new Point((int)x, (int)y), color);
    }

    /// <summary>
    /// 9:1 里的「1」：右侧整块是一个开关，里面两行 —— 上面「自动」，下面 ON / OFF。
    ///
    /// 它<b>没有浮动</b>：松开不抬、按下不放、更不放大。
    /// 按下去的唯一反馈就是整块填满颜色，一按一填，跟左边的动作按钮手感彻底分开 ——
    /// 那边是「执行一个动作」，这边是「切换一个状态」，本来就不该长得一样。
    /// </summary>
    private void DrawRail(Graphics g, Rectangle rail, Rectangle rect, int radius, Func<float, int> P, bool idle, float s)
    {
        // 常态：关 = 和左块同底色（预览里就是这样，之前错用了近黑，观感上两块不像一套）
        Color normal = AutoOn
            ? (_railHover ? T.AccentHover : T.Accent)
            : (_railHover ? T.BgOverlay : T.BgRaised);

        // 按下：关的染成金，开的压暗一档 —— 两种状态都要看得出来按了
        Color fill = _railPressed
            ? (AutoOn ? T.Mix(T.Accent, T.BgBase, 0.30) : T.Mix(normal, Accent, PressMix))
            : normal;

        using (var path = T.Rounded(rect, radius))
        {
            T.Fill(g, fill, path);
            T.Stroke(g, AutoOn ? T.Accent : (_railHover ? T.BdStrong : T.BdSubtle), path, Math.Max(1f, s));
        }

        // 上「自动」、下「ON/OFF」—— 标签负责说这是什么，下边负责说现在什么状态
        DrawRailText(g, rail, P, s, "自动", AutoOn ? T.BgBase : (_railHover ? T.TxSecondary : T.TxTertiary), false, -8);
        DrawRailText(g, rail, P, s, AutoOn ? "ON" : "OFF", AutoOn ? T.BgBase : T.TxTertiary, true, 8);
    }

    private void DrawRailText(Graphics g, Rectangle rail, Func<float, int> P, float s,
                              string text, Color color, bool bold, int dy)
    {
        using var font = bold ? T.MetaBold() : T.Meta();
        var txt = T.Clip(text, font, rail.Width - 6 * s);
        if (txt.Length == 0) return;
        var size = T.MeasureText(txt, font);
        T.DrawText(g, txt, font,
            new Point(rail.X + (rail.Width - size.Width) / 2,
                      (int)Math.Round(rail.Y + (rail.Height - size.Height) / 2f + dy * s)), color);
    }
}

// ================= 日志 =================

internal enum LogKind
{
    Phase,   // 游戏阶段变化
    Auto,    // 自动动作已执行
    Sub,     // WebSocket 订阅推送
    Warn,    // 警告（插件加载失败之类，不致命）
    Error,   // 出错 / 异常
    Info     // 其他
}

/// <summary>
/// 一条日志。Note 放补充信息（最常见的是「上一个阶段停留了多少秒」）——
/// 判「卡没卡」全靠这个数字，所以必须显示出来，不能只在脑子里算。
/// </summary>
internal sealed class LogEntry
{
    public DateTime Time = DateTime.Now;
    public LogKind Kind = LogKind.Info;
    public string Text = "";
    public string Note = "";

    public string KindLabel => Kind switch
    {
        LogKind.Phase => "阶段",
        LogKind.Auto => "自动",
        LogKind.Sub => "订阅",
        LogKind.Warn => "警告",
        LogKind.Error => "异常",
        _ => "信息"
    };
}

/// <summary>
/// 自绘日志列表。列宽固定、中文用雅黑（以前用 Consolas 画中文会回退字体，列必然对不齐）。
/// 有 hover / 选中 / 焦点三种可视状态，键盘可用。
/// </summary>
internal sealed class LogList : ListBox
{
    private int _hoverIndex = -1;

    private static Color KindColor(LogKind k) => k switch
    {
        LogKind.Phase => T.Info,
        LogKind.Auto => T.Success,
        LogKind.Sub => T.Info,
        LogKind.Warn => T.Warning,
        LogKind.Error => T.Error,
        _ => T.TxSecondary
    };

    public LogList()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
        DrawMode = DrawMode.OwnerDrawFixed;
        BorderStyle = BorderStyle.None;
        BackColor = T.BgSunken;
        ForeColor = T.TxSecondary;
        IntegralHeight = false;
        Font = T.LogFont();        // 12px：11px 中文在 GDI 下显粗糙
        ItemHeight = ScaledHeight();
    }

    private int ScaledHeight()
    {
        float s = DeviceDpi / 96f;
        return Math.Max(16, (int)Math.Round(T.LogRowH * s));
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        ItemHeight = ScaledHeight();
    }

    protected override void OnDpiChangedBeforeParent(EventArgs e)
    {
        base.OnDpiChangedBeforeParent(e);
        ItemHeight = ScaledHeight();
    }

    /// <summary>从头部插入一条（最新的在最上面），并限制总条数。</summary>
    public void Push(LogEntry entry, int max = 300)
    {
        Items.Insert(0, entry);
        while (Items.Count > max) Items.RemoveAt(Items.Count - 1);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int i = IndexFromPoint(e.Location);
        if (i != _hoverIndex)
        {
            _hoverIndex = i;
            Invalidate();
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        if (_hoverIndex != -1) { _hoverIndex = -1; Invalidate(); }
        base.OnMouseLeave(e);
    }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= Items.Count) return;
        if (Items[e.Index] is not LogEntry en) return;

        var g = e.Graphics;
        float s = DeviceDpi / 96f;
        var row = e.Bounds;
        bool selected = (e.State & DrawItemState.Selected) != 0;

        // 斑马纹：每两行一条，扫长列表时不容易串行
        Color bg = selected ? T.AccentSoft
                 : e.Index == _hoverIndex ? T.BgOverlay
                 : (e.Index % 2 == 1) ? Color.FromArgb(17, 19, 23)
                 : BackColor;
        T.FillRect(g, bg, row);

        if (selected)   // 选中：左侧 2px 品牌金竖条
            T.FillRect(g, T.Accent, new Rectangle(row.X, row.Y, Math.Max(2, (int)Math.Round(2 * s)), row.Height));

        using var f = T.LogFont();

        // 三列全部按**实测宽度**排，不再用写死的 56/36 ——
        // 字体从 11px 提到 12px、或 DPI 缩放到 125% 时，写死的列宽会让时间和徽章压在一起
        float x = row.X + 8 * s;
        var timeText = en.Time.ToString("HH:mm:ss");
        float timeW = Math.Max(56 * s, T.MeasureText(timeText, f).Width + 10 * s);

        // 垂直位置按实际字高算，不用估的 15*s
        float fontH = T.MeasureText("测", f).Height;
        float textTop = row.Y + (row.Height - fontH) / 2f;

        // 时间
        T.DrawText(g, timeText, f, new Point((int)x, (int)textTop), T.TxTertiary);

        // 类别徽章：连续同类只在最上面那条画。
        // 一局下来阶段要变十几次，每行都挂一个「阶段」等于刷屏，
        // 而且看不出哪一条才是一段新事件的开头 —— 成组之后读起来才是一条条的事。
        float kindRight = x + timeW + 36 * s;   // 徽章缺省宽度，够放两字
        bool sameAsAbove = e.Index > 0
                           && Items[e.Index - 1] is LogEntry above
                           && above.Kind == en.Kind;
        if (!sameAsAbove)
        {
            var kc = KindColor(en.Kind);
            // Badge 返回自己的右边界，正文从那儿接着排，永远不会叠
            kindRight = T.Badge(g, en.KindLabel, f, kc, Color.FromArgb(64, kc), row, x + timeW);
        }

        // 正文
        float noteW = 0;
        if (!string.IsNullOrEmpty(en.Note)) noteW = T.MeasureText(en.Note, f).Width + 12 * s;

        float tx = kindRight + 6 * s;
        T.DrawText(g, T.Clip(en.Text, f, row.Right - tx - 8 * s - noteW), f,
            new Point((int)tx, (int)textTop), en.Kind == LogKind.Error ? T.Error : T.TxPrimary);

        // 备注右对齐（最常见的是「停留 XXs」）
        if (noteW > 0)
            T.DrawText(g, en.Note, f,
                new Point((int)(row.Right - noteW + 4 * s), (int)textTop), T.TxTertiary);

        if ((e.State & DrawItemState.Focus) != 0 && Focused)
            ControlPaint.DrawFocusRectangle(g, row);
    }
}
