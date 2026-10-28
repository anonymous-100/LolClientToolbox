using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using LolClientToolbox.Contracts;

namespace LolClientToolbox;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // 必须在创建任何窗口之前调用，否则高分屏上会被系统拉伸放大 → 发虚模糊
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        // 隐藏的自测入口（不依赖屏幕前台，游戏全屏盖住也能截，用户看不到）：
        //   --selftest <png>            主窗口离屏自渲染
        //   --selftest-settings <png默认> <png展开1> [png展开2] [png展开3]
        //                               设置窗离屏自渲染：默认态一张；展开开发者选项后
        //                               分段滚着截（离屏窗口高度会被系统钳制，一张截不完）
        //                               （插件设置 schema 的通用渲染必须定期用这个验）
        string? shotPath = args.Length >= 2 && args[0] == "--selftest" ? args[1] : null;
        string? settingsShot = args.Length >= 3 && args[0] == "--selftest-settings" ? args[1] : null;
        var settingsDevShots = args.Length >= 3 && args[0] == "--selftest-settings"
            ? args.Skip(2).ToArray() : Array.Empty<string>();
        Application.Run(new MainForm(shotPath, settingsShot, settingsDevShots));
    }
}

/// <summary>
/// 主窗体 = 纯宿主外壳：状态面板、日志、设置窗、插件加载与扇出。
///
/// 自 v23 起**不含任何功能逻辑** —— 窗口修复、跳过卡结算、热重载、接收对局
/// 全部是 plugins/ 目录下的插件（见 PluginHost.cs 与「插件开发手册.md」）。
/// 宿主的职责只有：连客户端、采状态、画界面、把事件发给插件。
/// </summary>
internal sealed class MainForm : Form, IPluginHost
{
    private const int TitleBarH = 33;

    private readonly Label _status;
    private readonly Label _output;
    private readonly Label _hint;
    private readonly string? _selftestShot;
    private readonly string? _settingsShot;
    private readonly string[] _settingsDevShots;

    // ---------- 自绘标题栏 ----------
    private Panel _titleBar = null!;
    private Rectangle _btnMin = Rectangle.Empty;
    private Rectangle _btnClose = Rectangle.Empty;
    private bool _minHover, _closeHover;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int SendMessage(IntPtr hWnd, int Msg, int wParam, int lParam);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ReleaseCapture();
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_ASYNCWINDOWPOS = 0x4000;
    private const int WM_NCLBUTTONDOWN = 0xA1;
    private const int HTCAPTION = 2;

    private readonly Label _stConn;
    private readonly Label _stConnDot;
    private bool _clientUp;
    private string _subText = "";
    private readonly Label _stPhaseBig;
    private readonly Label _stMinor;
    private readonly DateTime _appStart = DateTime.Now;   // 用于「已运行」累计时长
    private readonly Label _stDiag;
    private readonly Panel _statusPanel;
    private readonly LogList _log;
    private readonly ToolTip _tip = new();
    private LcuWatcher? _watcher;

    // 插件申请的辅助窗口（战绩窗口那种）：宿主只负责摆到右侧和跟随移动
    private readonly List<(IntPtr Hwnd, int W, int H)> _auxWindows = new();

    private readonly Settings _settings;
    private readonly LcuClient _lcu = new();
    private readonly PluginManager _plugins;

    private CancellationTokenSource _cts = new();
    private readonly string _logPath;

    // —— 结构化日志用：只记「变化的部分」，并顺带算出上一阶段停留了多久 ——
    private DateTime _phaseEnteredAt = DateTime.MinValue;
    private string _prevClient = "";
    private string _prevEog = "";
    private string _prevWindow = "";
    private string _prevErr = "";
    private string _lastPhase = "";
    private ClientSnapshot? _lastCs;

    /// <summary>卡片容器。改顺序/显隐后要原地重排，所以留成字段。</summary>
    private Panel? _cardsHost;
    private const int CardCols = 2;
    private const int CardGap = 8;

    /// <summary>正在被长按拖动的那张卡（安卓桌面式排序）。</summary>
    private CardAdapter? _dragSrc;

    private static readonly string AppDirectory =
        Path.GetDirectoryName(Environment.ProcessPath ?? Application.ExecutablePath) ?? AppContext.BaseDirectory;

    // 颜色统一取自 Theme（T），不在界面代码里临时调色
    private static readonly Color Bg = T.BgBase;
    private static readonly Color PanelBg = T.BgRaised;
    private static readonly Color TextMain = T.TxPrimary;
    private static readonly Color TextDim = T.TxTertiary;

    public MainForm(string? selftestShot = null, string? settingsShot = null, string[]? settingsDevShots = null)
    {
        _selftestShot = selftestShot;
        _settingsShot = settingsShot;
        _settingsDevShots = settingsDevShots ?? Array.Empty<string>();
        _logPath = Path.Combine(AppDirectory, "采样日志.txt");
        _settings = Settings.Load(AppDirectory);
        _lcu.ClientPathHint = _settings.ClientPathHint;
        _lcu.Cfg = _settings;
        _plugins = new PluginManager(this, (lvl, msg, note) => Log(MapKind(lvl), msg, note));

        Text = "LOL 客户端工具箱";
        // 自测模式把窗口画到屏幕外：截图依然真实（窗口已实际创建并布局），
        // 但用户看不到 —— 不会像之前那样反复弹窗打扰人
        if (selftestShot is not null || settingsShot is not null)
        {
            StartPosition = FormStartPosition.Manual;
            Location = new Point(-3000, -3000);
            ShowInTaskbar = false;
        }
        else
        {
            StartPosition = FormStartPosition.CenterScreen;
        }
        // 标题栏自己画：系统标题栏在部分 Win10/Win11 上无法强制暗色
        // （DWM 属性 20 时灵时不灵），而它是整个窗口里最亮的一块，和暗色界面冲突。
        // 自绘之后颜色完全可控，预览和实际也才真正对得上。
        FormBorderStyle = FormBorderStyle.None;
        MaximizeBox = false;
        MinimizeBox = true;
        // 功能变多后单列放不下，窗口加宽到 900，卡片改两列（见下方卡片区）
        ClientSize = new Size(900, 780 + TitleBarH);
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = Bg;
        Font = T.Body();
        DoubleBuffered = true;
        Icon = SystemIcons.Application;

        // ---------- 客户端状态面板 ----------
        // 三层：① 连接状态 ② 当前阶段（大字号）③ 次要信息。
        // 诊断是排障用的，默认收起，出问题才展开。
        // 高度固定 132：诊断行常驻占位（没内容就是空行），不再出现 132↔150 的抖动
        _statusPanel = new Panel
        {
            Dock = DockStyle.Top, Height = 132, BackColor = PanelBg,
            Padding = new Padding(T.PadOuter, 12, T.PadOuter, 12),
        };

        // 第一行：左边是连接状态，右边是设置。同行放，不再单独给它一整行。
        var connRow = new Panel { Dock = DockStyle.Top, Height = 30, BackColor = PanelBg };
        _stConn = new Label
        {
            Text = "客户端 检测中…", AutoSize = true, Dock = DockStyle.Left,
            ForeColor = T.TxTertiary, Font = T.Meta(),
            Padding = new Padding(4, 6, 0, 0),
        };
        _stConnDot = new Label
        {
            Text = "●", AutoSize = true, Dock = DockStyle.Left,
            ForeColor = T.TxDisabled, Font = T.Meta(),
            Padding = new Padding(0, 6, 0, 0),
        };
        // Dock 从集合末尾往回停靠：最右边的要先 Add 才会落在最左边，
        // 而最右边的设置按钮反而要最后 Add（最先贴住右缘）。
        var btnSettings = MakeGhostButton("设置", 72, 28);
        btnSettings.Click += (_, _) => OpenSettings();
        connRow.Controls.Add(_stConn);
        connRow.Controls.Add(_stConnDot);
        connRow.Controls.Add(btnSettings);

        _stPhaseBig = new Label
        {
            Text = "—", AutoSize = false, Dock = DockStyle.Top, Height = 36,
            ForeColor = TextMain, BackColor = PanelBg,
            Font = T.Display(),
        };
        // 原始英文阶段名不再单独占一行（它和中文重复），改成鼠标悬停时看
        _tip.SetToolTip(_stPhaseBig, "把鼠标停在这里可以看到接口返回的原始阶段名");

        _stMinor = new Label
        {
            Text = "窗口 — ｜ 已运行 —",
            AutoSize = false, Dock = DockStyle.Top, Height = 18,
            ForeColor = T.TxTertiary, BackColor = PanelBg,
            Font = T.Meta(),
        };
        _stDiag = new Label
        {
            Text = "", AutoSize = false, Dock = DockStyle.Top, Height = 18,
            ForeColor = T.TxTertiary, BackColor = PanelBg,
            Font = T.Meta(),
        };

        _statusPanel.Controls.Add(_stDiag);
        _statusPanel.Controls.Add(_stMinor);
        _statusPanel.Controls.Add(_stPhaseBig);
        _statusPanel.Controls.Add(connRow);

        // ---------- 运行日志：固定 4 行 ----------
        // 只在「有东西变了」时记一行，并标出上一阶段停留多久。
        // 以前是 Dock.Fill 吃剩余高度；卡片区改成「固定高 + 滑动」之后高低归滚动条管，
        // 日志固定 4 行（标题行 26 + 4×26 = 130）—— 永远看得见最近几条，也不跟卡片区抢高度。
        var logPanel = new Panel
        {
            Dock = DockStyle.Bottom, Height = 26 + T.LogRowH * 5,
            BackColor = Bg, Padding = new Padding(T.PadOuter, 0, T.PadOuter, 0),
        };
        var logHead = new Panel { Dock = DockStyle.Top, Height = 26, BackColor = Bg };
        var logTitle = new Label
        {
            Text = "运行日志 · 仅记录变化", AutoSize = true, Dock = DockStyle.Left,
            ForeColor = T.TxTertiary, Font = T.Section(), Padding = new Padding(0, 3, 0, 0),
        };
        var logNote = new Label
        {
            Text = "右列＝上一阶段停留时长", AutoSize = true, Dock = DockStyle.Right,
            ForeColor = T.TxTertiary, Font = T.Meta(), Padding = new Padding(0, 3, 0, 0),
        };
        logHead.Controls.Add(logNote);
        logHead.Controls.Add(logTitle);
        _log = new LogList { Dock = DockStyle.Fill };
        logPanel.Controls.Add(_log);
        logPanel.Controls.Add(logHead);

        // ---------- 卡片区：两列网格，超出高度就滑动 ----------
        // 卡片由各插件在 Init 里 RegisterCard 注册。显示顺序与显隐由 config.json 的
        // CardOrder / CardHidden 决定（设置窗「卡片管理」可改），没配就按 plugin.json 的 order。
        _cardsHost = new Panel
        {
            Dock = DockStyle.Top, AutoScroll = true,
            BackColor = Bg, Padding = new Padding(0),
        };

        // 加载插件（Init 里会 RegisterCard，所以必须晚于 _log 创建）。
        // 先把程序目录写进日志：仓库里可能同时存在 .dev / .dev-new / dist 好几份构建，
        // 跑错实例时「功能明明写了却不生效」极难排查（2026-10-25 就踩过一次）。
        Log(LogKind.Info, "启动", $"程序目录 {AppDirectory}");
        _plugins.LoadAll(Path.Combine(AppDirectory, "plugins"), _settings.DisabledPlugins);

        RelayoutCards();

        // ---------- 底部 ----------
        var footer = new Panel { Dock = DockStyle.Bottom, Height = 76, BackColor = PanelBg, Padding = new Padding(T.PadOuter, 8, T.PadOuter, 8) };

        _status = new Label
        {
            Text = "就绪 · 点卡片立即执行，或打开卡片右侧的开关",
            AutoSize = false, Dock = DockStyle.Top, Height = 22,
            ForeColor = TextMain, Font = T.BodyBold(),
        };
        // 原来挂在标题下面的使用说明，挪到这里
        var guide = new Label
        {
            Text = "点卡片＝执行一次 ｜ 右侧开关＝交给程序长期盯着",
            AutoSize = false, Dock = DockStyle.Top, Height = 20,
            ForeColor = TextDim, Font = T.Meta(),
        };
        // 第三行做成左右结构：左边「插件：N 个已加载」，右边「导出日志」
        var engineRow = new Panel { Dock = DockStyle.Top, Height = 18, BackColor = PanelBg };
        _output = new Label
        {
            Text = $"插件：{_plugins.Plugins.Count} 个已加载",
            AutoSize = false, Dock = DockStyle.Left, Height = 18,
            ForeColor = TextDim, Font = T.Meta(),
        };
        var btnExport = new Label
        {
            Text = "导出日志", AutoSize = true, Dock = DockStyle.Right, Height = 18,
            ForeColor = T.TxTertiary, Font = T.Meta(),
            TextAlign = ContentAlignment.MiddleRight, Cursor = Cursors.Hand,
        };
        btnExport.Click += (_, _) => ExportLog();
        btnExport.MouseEnter += (_, _) => btnExport.ForeColor = T.TxPrimary;
        btnExport.MouseLeave += (_, _) => btnExport.ForeColor = T.TxTertiary;
        // Fill 的 Label 先 Add 才能拿到剩余宽度，右对齐的按钮后 Add 才贴住右缘
        engineRow.Controls.Add(_output);
        engineRow.Controls.Add(btnExport);
        _hint = new Label
        {
            AutoSize = false, Dock = DockStyle.Top, Height = 20, Visible = false,
            ForeColor = T.TxTertiary, Font = T.Meta(),
        };
        footer.Controls.Add(_hint);
        footer.Controls.Add(engineRow);
        footer.Controls.Add(guide);
        footer.Controls.Add(_status);

        // Dock 是从后往前沿 z-order 停靠的，所以 Fill 的日志必须最先 Add 才能拿到剩余空间
        // 标题栏：一条暗条 + 文字 + 右上角最小化/关闭
        _titleBar = new Panel { Dock = DockStyle.Top, Height = TitleBarH, BackColor = T.BgBase };
        _titleBar.Paint += TitleBar_Paint;
        _titleBar.MouseDown += TitleBar_MouseDown;
        _titleBar.MouseMove += TitleBar_MouseMove;
        // 插件申请的辅助窗口跟着主窗口一起动（战绩窗口那种）
        LocationChanged += (_, _) => RepositionAuxWindows();
        _titleBar.MouseLeave += (_, _) =>
        {
            if (_minHover || _closeHover) { _minHover = _closeHover = false; _titleBar.Invalidate(); }
        };

        // Dock 从集合末尾往回停靠：标题栏最后 Add 才会贴在最顶上
        Controls.Add(logPanel);
        Controls.Add(_cardsHost);
        Controls.Add(_statusPanel);
        Controls.Add(footer);
        Controls.Add(_titleBar);

        // 无边框窗口借这一条拿回系统阴影
        Shown += (_, _) =>
        {
            // 标题栏暗色必须等窗口可见之后再设一次：句柄刚创建时设会被丢掉
            T.DarkTitle(this);

            // 「已运行」每秒刷一次：副行的窗口部分也一起刷，
            // 否则打开应用后时长卡在 0s 不动（状态指纹不变时不会触发刷新）
            var up = new System.Windows.Forms.Timer { Interval = 1000 };
            up.Tick += (_, _) =>
                _stMinor.Text = $"窗口 {WindowInfo.GetClientWindowRect()} ｜ 已运行 {FmtUptime(DateTime.Now - _appStart)}";
            up.Start();
            UpdateHint();
            ApplyPressStrength();

            // 插件全部就绪后再启动：先 StartAll（订阅/后台任务），再开采样和订阅
            _plugins.StartAll();
            if (selftestShot is not null)
            {
                // 自测模式：让插件把自动开关摆到「开」，截图才能核对金色那一态
                foreach (var p in _plugins.Plugins)
                    if (p.Instance is ISelfTestAware st) st.OnSelfTest();
            }
            _ = Task.Run(() => AutoLoop(_cts.Token));
            StartWatcher();

            if (selftestShot is not null)
            {
                // 自测模式：等首帧稳定后自渲染存盘，再自动退出
                var t = new System.Windows.Forms.Timer { Interval = 800 };
                t.Tick += (_, _) =>
                {
                    t.Stop();
                    try
                    {
                        // 对 Form 调 DrawToBitmap 会连非客户区一起画，所以位图用含边框的整窗尺寸
                        using var bmp = new Bitmap(Width, Height);
                        DrawToBitmap(bmp, new Rectangle(Point.Empty, bmp.Size));
                        bmp.Save(selftestShot, System.Drawing.Imaging.ImageFormat.Png);
                    }
                    catch { /* 存不了就算了，别挡退出 */ }
                    Close();
                };
                t.Start();
            }

            if (settingsShot is not null)
            {
                // 设置窗离屏渲染：默认态一张；展开「开发者选项」后分段滚着截。
                // 插件设置的 schema 通用渲染（分区/勾选/数字框/文本框/滑杆）全靠这个定期验证。
                var t = new System.Windows.Forms.Timer { Interval = 800 };
                t.Tick += (_, _) =>
                {
                    t.Stop();
                    Form? dlg = null;
                    try
                    {
                        // 测试专用：临时翻开开发者选项开关（仅内存，不落盘）——
                        // 否则按钮不可见，而 PerformClick 走 CanSelect，不可见时点了没反应
                        _settings.ShowDevOptions = true;
                        dlg = new SettingsForm(_settings, _plugins.PluginSections());
                        dlg.StartPosition = FormStartPosition.Manual;
                        dlg.Location = new Point(-3000, -3000);
                        dlg.ShowInTaskbar = false;
                        dlg.Show();

                        var step2 = new System.Windows.Forms.Timer { Interval = 400 };
                        step2.Tick += (_, _) =>
                        {
                            step2.Stop();
                            try { ShootForm(dlg, settingsShot); } catch { }
                            if (_settingsDevShots.Length == 0) { dlg.Close(); Close(); return; }

                            var step3 = new System.Windows.Forms.Timer { Interval = 400 };
                            step3.Tick += (_, _) =>
                            {
                                step3.Stop();
                                try
                                {
                                    FindDevButton(dlg)?.PerformClick();   // 展开开发者选项
                                    dlg.PerformLayout();

                                    // 像素截图只作留证；真正的验证是下面的控件清单——
                                    // 每一行 schema 的标签、控件类型、当前值全部导进日志，
                                    // 比肉眼截图可靠（DrawToBitmap 还不认滚动偏移，截不全）
                                    if (_settingsDevShots.Length > 0)
                                        ShootForm(dlg, _settingsDevShots[0]);
                                    DumpSettingsForm(dlg);
                                    Log(LogKind.Info, "自测", "设置窗渲染：控件清单已入日志");
                                }
                                catch (Exception ex)
                                {
                                    Log(LogKind.Warn, "自测", "设置窗展开态验证失败：" + ex.Message);
                                }
                                dlg.Close();
                                Close();
                            };
                            step3.Start();
                        };
                        step2.Start();
                    }
                    catch
                    {
                        dlg?.Close();
                        Close();
                    }
                };
                t.Start();
            }
        };

        DpiChanged += (_, _) => UpdateHint();
        FormClosing += (_, _) =>
        {
            Log(LogKind.Info, "退出", "开始收尾（插件停止 → 断开客户端订阅）");
            LogClientLocaleBeforeExit();
            _cts.Cancel();
            _plugins.StopAll();
            _watcher?.Dispose();
            _lcu.Dispose();
        };
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        T.DarkTitle(this);
    }

    /// <summary>
    /// 开发/排障用的隐藏入口：Ctrl+Shift+D 导出状态快照。
    /// 这是给排查问题的人用的，不是给用户的功能，所以界面上不放按钮。
    /// </summary>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.Shift | Keys.D))
        {
            _ = ExportSnapshot();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    // ================= IPluginHost 实现 =================

    string IPluginHost.AppDirectory => AppDirectory;
    bool IPluginHost.IsSelfTest => _selftestShot is not null;
    ClientSnapshot? IPluginHost.LastSnapshot => _lastCs;
    string IPluginHost.CurrentPhase => _lastPhase;
    bool IPluginHost.ClientRunning => _clientUp;

    // 注意：显式接口实现里不写默认值（写了也无效，调用方走接口时用契约里的默认值）
    void IPluginHost.Log(LogLevel level, string message, string note) =>
        Log(MapKind(level), message, note);

    void IPluginHost.SetStatus(string text, bool warn) =>
        SafeInvoke(this, () => SetStatus(text, warn));

    // 插件实际拿到的是 PluginInitContext（它带 manifest，才能把卡片和插件对上号），
    // 宿主自己这条只是接口占位，真被调用就是 bug。
    ICard? IPluginHost.RegisterCard(string title, string desc, bool showAuto) =>
        throw new InvalidOperationException("RegisterCard 必须经由插件上下文调用");

    void IPluginHost.OnPhaseChanged(Action<string> handler) => _plugins.AddPhaseHandler(handler);
    void IPluginHost.OnTick(Action<ClientSnapshot> handler) => _plugins.AddTickHandler(handler);
    void IPluginHost.Subscribe(string uri, Action<PluginEvent> handler) =>
        _plugins.AddEventHandler(uri, handler);

    async Task<(int Status, string Body)> IPluginHost.GetAsync(string path)
    {
        var r = await _lcu.GetExAsync(path);
        return (r.Status, r.Body);
    }

    Task<bool> IPluginHost.PostAsync(string path, string body) =>
        _lcu.PostAsync(path, body);

    async Task<(bool Ok, int Status, string Body)> IPluginHost.PostExAsync(string path, string body)
    {
        var r = await _lcu.PostExAsync(path, body);
        return (r.Ok, r.Status, r.Body);
    }

    bool IPluginHost.EnsureClient() => _lcu.IsAttached || _lcu.TryAttach();

    /// <summary>
    /// 把插件的窗口摆到主窗口右侧。宿主不接管窗口：插件自己 Show()，
    /// 这里只负责定位 + 主窗移动时带着走。窗口关掉后下一轮重排自动清掉。
    /// </summary>
    void IPluginHost.ShowAuxWindow(IntPtr hwnd, int width, int height)
    {
        if (hwnd == IntPtr.Zero) return;
        if (!_auxWindows.Any(a => a.Hwnd == hwnd))
            _auxWindows.Add((hwnd, width, height));
        RepositionAuxWindows();
    }

    /// <summary>把全部辅助窗口重新摆到主窗口右侧（贴右边、顶端对齐，越界就钳回屏幕内）。</summary>
    private void RepositionAuxWindows()
    {
        if (_auxWindows.Count == 0) return;
        var wa = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
        for (int i = _auxWindows.Count - 1; i >= 0; i--)
        {
            var (hwnd, w, h) = _auxWindows[i];
            if (!IsWindow(hwnd)) { _auxWindows.RemoveAt(i); continue; }   // 窗口已关，清掉
            int x = Right + 8;
            int y = Top;
            if (x + w > wa.Right) x = Math.Max(wa.Left, wa.Right - w);     // 右边放不下就贴屏幕右缘
            if (y + h > wa.Bottom) y = Math.Max(wa.Top, wa.Bottom - h);
            SetWindowPos(hwnd, IntPtr.Zero, x, y, w, h,
                SWP_NOZORDER | SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS);
        }
    }

    /// <summary>
    /// 【仅迁移用】读旧版 config.json 文件里的键（直接解析文件，不经过 Settings 类 ——
    /// v23 起功能参数已迁出全局配置，类里没有这些属性了，但老文件里还有）。
    /// </summary>
    T IPluginHost.GetLegacy<T>(string key, T fallback)
    {
        try
        {
            var path = Path.Combine(AppDirectory, "config.json");
            if (!File.Exists(path)) return fallback;
            using var d = JsonDocument.Parse(File.ReadAllText(path));
            if (d.RootElement.ValueKind == JsonValueKind.Object
                && d.RootElement.TryGetProperty(key, out var v))
            {
                var r = v.Deserialize<T>();
                if (r is not null) return r;
            }
        }
        catch { /* 读不出来就给 fallback */ }
        return fallback;
    }

    /// <summary>
    /// 插件间服务发现：从已加载的插件里按接口找（见契约里的 IVideoSwitcher）。
    /// </summary>
    /// <remarks>
    /// 这里写成 public 而不是显式接口实现，是因为显式实现里的 <c>T?</c> 会被
    /// 编译器当成 <c>Nullable&lt;T&gt;</c>（CS0453）—— 约束虽然从接口继承，
    /// 但它判断「T? 是可空引用还是可空值类型」时不认。写成普通 public 方法就没这个歧义。
    /// </remarks>
    public T? GetService<T>() where T : class => _plugins.FindService<T>();

    private static LogKind MapKind(LogLevel level) => level switch
    {
        LogLevel.Phase => LogKind.Phase,
        LogLevel.Auto => LogKind.Auto,
        LogLevel.Warn => LogKind.Warn,
        LogLevel.Error => LogKind.Error,
        _ => LogKind.Info,
    };

    // ================= 订阅 =================

    /// <summary>
    /// 启动 WebSocket 订阅。订阅是「加速器」不是「唯一数据源」——
    /// 连不上会自动重连，同时 AutoLoop 的慢速轮询始终保留，所以订阅失败不会让程序变瞎。
    /// </summary>
    private void StartWatcher()
    {
        if (_watcher is not null) return;
        if (!_settings.UseWebSocket)
        {
            _subText = "轮询取数（订阅已关）";
            UpdateConn();
            return;
        }

        _watcher = new LcuWatcher(() => _lcu.IsAttached || _lcu.TryAttach() ? _lcu.Credentials : ("", ""));
        _watcher.StateChanged += (on, note) => SafeInvoke(this, () =>
        {
            _subText = on ? "实时订阅" : note;
            UpdateConn();
        });
        _watcher.EventReceived += ev => OnLcuEvent(ev);
        _watcher.Start();
    }

    /// <summary>
    /// 收到推送：阶段变化宿主自己处理（要记日志），其余全部转给插件。
    /// 插件按 uri 订阅，只收到自己关心的那几条。
    /// </summary>
    private void OnLcuEvent(LcuEvent ev)
    {
        if (ev.Uri.Length == 0) return;

        if (ev.Uri.Equals(_settings.PhaseEndpoint, StringComparison.OrdinalIgnoreCase))
        {
            var phase = LcuClient.PlainJsonString(ev.DataJson);
            if (!string.IsNullOrEmpty(phase)) OnPhaseChanged(phase);
        }

        _plugins.FireEvent(new PluginEvent
        {
            Uri = ev.Uri, EventType = ev.EventType, DataJson = ev.DataJson,
        });
    }

    /// <summary>
    /// 阶段变化的唯一入口（订阅和轮询都走这里，所以不会重复记两遍）。
    /// 顺手算出「上一个阶段停留了多少秒」写进日志 —— 卡没卡，看这个数最直接。
    /// 处理完再扇出给全部插件。
    /// </summary>
    private void OnPhaseChanged(string phase)
    {
        if (phase == _lastPhase) return;
        _lastPhase = phase;

        var text = LcuClient.LocalizePhase(phase);
        string note = "";
        if (_phaseEnteredAt != DateTime.MinValue)
            note = "停留 " + FmtDuration(DateTime.Now - _phaseEnteredAt);
        _phaseEnteredAt = DateTime.Now;

        SafeInvoke(this, () => _tip.SetToolTip(_stPhaseBig, "原始阶段名：" + phase));

        Log(LogKind.Phase, phase + (text.Length > 0 ? $"（{text}）" : ""), note);
        _plugins.FirePhase(phase);
    }

    /// <summary>
    /// 低调的次要按钮（设置这类偶尔才点的）。
    /// height 可以压到 24 —— 它现在挤在状态行里，再保持 32 会把那一行撑高，
    /// 但它的可点_HEIGHT 是连着上下留白一起算的，实际并不难按。
    /// </summary>
    private static Button MakeGhostButton(string text, int width, int height = T.ControlH)
    {
        var b = new Button
        {
            Text = text, Width = width, Height = height, Dock = DockStyle.Right,
            FlatStyle = FlatStyle.Flat, BackColor = T.BgOverlay,
            ForeColor = T.TxSecondary,
            Font = T.Meta(), Cursor = Cursors.Hand,
        };
        b.FlatAppearance.BorderColor = T.BdStrong;
        return b;
    }

    // ================= 自动化 =================

    private async Task AutoLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await AutoTick(ct);
            }
            catch
            {
                /* 一轮出错不影响下一轮 */
            }
            // 订阅连上之后轮询只作兜底（以及窗口检测，窗口尺寸没有事件可订阅），所以放慢
            int wait = (_watcher?.Connected == true)
                ? Math.Max(1000, _settings.SubscribedPollMs)
                : Math.Max(500, _settings.PollIntervalMs);
            try
            {
                await Task.Delay(wait, ct);
            }
            catch
            {
                return;
            }
        }
    }

    private async Task AutoTick(CancellationToken ct)
    {
        var snap = await _lcu.SampleAsync(ct);
        UpdateStatusPanel(snap);
        LogChangedFields(snap);

        if (!snap.ClientRunning)
        {
            ResetTrackers();
            return;
        }

        // 阶段变化统一走 OnPhaseChanged —— 订阅和轮询共用一个入口，所以不会重复记两遍
        if (!string.IsNullOrEmpty(snap.Phase)) OnPhaseChanged(snap.Phase);

        // 转发给插件：阶段事件之外，每个 tick 都送一份最新快照
        var cs = new ClientSnapshot
        {
            ClientRunning = snap.ClientRunning,
            Phase = snap.Phase,
            PhaseText = snap.PhaseText,
            EogState = snap.EogState,
            WindowRect = snap.WindowRect,
            Diag = snap.Diag,
            TakenAt = snap.TakenAt,
        };
        _lastCs = cs;
        _plugins.FireTick(cs);
    }

    private void ResetTrackers()
    {
        _lastPhase = "";
        _phaseEnteredAt = DateTime.MinValue;
    }

    /// <summary>
    /// 记一行日志（界面列表 + 磁盘各一份）。
    /// Note 放补充信息，界面上右对齐显示，最常见的是「上一阶段停留多久」。
    /// </summary>
    private void Log(LogKind kind, string text, string note = "")
    {
        var entry = new LogEntry { Kind = kind, Text = text, Note = note };
        SafeInvoke(this, () => _log.Push(entry));
        try
        {
            // 行首带日期：以前只有 HH:mm:ss，跨天之后完全看不出某条样本是哪天的，
            // 想复盘"哪天卡住过"根本对不上。加日期是零成本，收益是日志可归档。
            TrimLogFile();
            File.AppendAllText(_logPath,
                $"{entry.Time:MM-dd HH:mm:ss}  [{entry.KindLabel}]  {text}"
                + (string.IsNullOrEmpty(note) ? "" : "   " + note)
                + Environment.NewLine);
        }
        catch { /* 写不进去不影响界面 */ }
    }

    /// <summary>
    /// 只把「真正变化了的字段」记进日志。
    /// 以前是每次都把阶段+结算+窗口整行重写一遍，同一件事被刷成两三条，
    /// 看的人还得自己比对哪列变了。现在一行一个变化，干净得多。
    /// </summary>
    private void LogChangedFields(LcuSnapshot snap)
    {
        string client = snap.ClientRunning ? "已连接" : "未运行";
        if (client != _prevClient)
        {
            Log(snap.ClientRunning ? LogKind.Info : LogKind.Error,
                "客户端 " + client + (snap.ClientRunning ? $"（{_lcu.CredSource}）" : ""));
            _prevClient = client;
        }

        string eog = snap.EogState.Length > 0 ? snap.EogState : "-";
        if (snap.ClientRunning && eog != _prevEog)
        {
            Log(LogKind.Info, "战绩 " + eog);
            _prevEog = eog;
        }

        if (snap.WindowRect.Length > 0 && snap.WindowRect != _prevWindow)
        {
            Log(LogKind.Info, "窗口 " + snap.WindowRect);
            _prevWindow = snap.WindowRect;
        }

        string err = snap.Error ?? "";
        if (err != _prevErr)
        {
            if (err.Length > 0) Log(LogKind.Error, err);
            _prevErr = err;
        }
    }

    // ---------- 自绘标题栏 ----------

    private void TitleBar_Paint(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
        int w = _titleBar.Width, h = _titleBar.Height;
        float s = DeviceDpi / 96f;

        T.FillRect(g, T.BgBase, new Rectangle(0, 0, w, h));
        // 底部一条细线，把标题栏和状态区分开
        using (var pen = new Pen(T.BdSubtle, Math.Max(1f, s)))
            g.DrawLine(pen, 0, h - 1, w, h - 1);

        using var font = T.Meta();
        var ts = T.MeasureText("LOL 客户端工具箱", font);
        T.DrawText(g, "LOL 客户端工具箱", font,
            new Point((int)(16 * s), (h - ts.Height) / 2), T.TxTertiary);

        // 右上角两个按钮：最小化 / 关闭
        int bw = (int)(46 * s);
        _btnMin = new Rectangle(w - bw * 2, 0, bw, h);
        _btnClose = new Rectangle(w - bw, 0, bw, h);
        if (_minHover) T.FillRect(g, T.BgOverlay, _btnMin);
        if (_closeHover) T.FillRect(g, Color.FromArgb(160, 88, 78), _btnClose);

        using var bf = T.Body();
        T.DrawText(g, "—", bf, CenterIn(_btnMin, T.MeasureText("—", bf)), T.TxSecondary);
        T.DrawText(g, "✕", bf, CenterIn(_btnClose, T.MeasureText("✕", bf)),
                   _closeHover ? Color.White : T.TxSecondary);
    }

    private static Point CenterIn(Rectangle r, Size text) =>
        new(r.X + (r.Width - text.Width) / 2, r.Y + (r.Height - text.Height) / 2);

    private void TitleBar_MouseMove(object? sender, MouseEventArgs e)
    {
        bool mh = _btnMin.Contains(e.Location), ch = _btnClose.Contains(e.Location);
        if (mh != _minHover || ch != _closeHover)
        {
            _minHover = mh; _closeHover = ch;
            _titleBar.Invalidate();
        }
    }

    private void TitleBar_MouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        if (_btnClose.Contains(e.Location)) Close();
        else if (_btnMin.Contains(e.Location)) WindowState = FormWindowState.Minimized;
        else
        {
            // 按标题栏空白处＝拖动窗口（伪装成系统标题栏的按下）
            ReleaseCapture();
            SendMessage(Handle, WM_NCLBUTTONDOWN, HTCAPTION, 0);
        }
    }

    /// <summary>无边框窗口默认没有投影，借 ClassStyle 拿回来。</summary>
    protected override CreateParams CreateParams
    {
        get
        {
            const int CS_DROPSHADOW = 0x00020000;
            var cp = base.CreateParams;
            cp.ClassStyle |= CS_DROPSHADOW;
            return cp;
        }
    }

    /// <summary>
    /// 连接行只有一句：状态点 + 「客户端 X · 取数方式」。
    /// 订阅不是独立指标，它只是取数更快的一条路，掉了会自动退回轮询，
    /// 所以不单独占一个位置，也不给它单独的状态色。
    /// </summary>
    private void UpdateConn()
    {
        if (_stConn is null) return;
        bool ok = _clientUp;
        _stConn.Text = ok ? "客户端 已连接" : "客户端 未运行";
        if (ok && _subText.Length > 0) _stConn.Text += " · " + _subText;
        _stConn.ForeColor = ok ? T.TxSecondary : T.TxTertiary;
        _stConnDot.ForeColor = ok ? T.Success : T.TxDisabled;
    }

    private void UpdateStatusPanel(LcuSnapshot snap)
    {
        SafeInvoke(this, () =>
        {
            _clientUp = snap.ClientRunning;
            UpdateConn();

            if (!snap.ClientRunning)
            {
                _stPhaseBig.Text = "未连接";
                _tip.SetToolTip(_stPhaseBig, "");
            }
            else
            {
                var eog = snap.EogState.Length > 0 ? snap.EogState : "";
                // 大字直接显示接口原文（战绩/会话），本地化阶段名挪到悬停提示里 ——
                // 两行都显示"房间中"是重复的，大字给信息、小字给出处的分工更清楚
                _stPhaseBig.Text = eog.Length > 0 ? "战绩 " + eog
                                 : (snap.PhaseText.Length > 0 ? snap.PhaseText
                                 : (snap.Phase.Length > 0 ? snap.Phase : "(空)"));
                _tip.SetToolTip(_stPhaseBig, "原始阶段名：" + snap.Phase);
            }

            _stMinor.Text = $"窗口 {snap.WindowRect} ｜ 已运行 {FmtDuration(DateTime.Now - _appStart)}";

            // 诊断行常驻占位：没内容就是空行，面板高度不再来回跳
            var diag = snap.Diag ?? "";
            bool hasDiag = !string.IsNullOrEmpty(snap.Error) && !string.IsNullOrEmpty(diag);
            _stDiag.Text = hasDiag ? "⚠ " + diag : "";
            _stDiag.ForeColor = hasDiag ? T.Warning : T.TxTertiary;
        });
    }

    private static string FmtDuration(TimeSpan t)
    {
        if (t.TotalSeconds < 1) return "<1s";
        if (t.TotalMinutes < 1) return $"{t.TotalSeconds:0}s";
        return $"{(int)t.TotalMinutes}m{t.Seconds:00}s";
    }

    /// <summary>「已运行」用的一套格式：1h05m / 5m03s / 42s。</summary>
    private static string FmtUptime(TimeSpan t)
    {
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}h{t.Minutes:00}m";
        if (t.TotalMinutes >= 1) return $"{t.Minutes}m{t.Seconds:00}s";
        return $"{t.Seconds}s";
    }

    // ================= 界面杂项 =================

    private async Task ExportSnapshot()
    {
        var snap = await _lcu.SampleAsync();
        var path = Path.Combine(AppDirectory, $"状态快照_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
        var sb = new StringBuilder();
        sb.AppendLine($"时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"lockfile：{_lcu.LockfilePath}");
        sb.AppendLine($"客户端运行：{snap.ClientRunning}");
        sb.AppendLine($"阶段(gameflow-phase)：{snap.Phase}");
        sb.AppendLine($"结算状态：{snap.EogState}");
        sb.AppendLine($"结算接口原始：{snap.EogRaw}");
        sb.AppendLine($"窗口：{snap.WindowRect}");
        if (!string.IsNullOrEmpty(snap.Error)) sb.AppendLine($"错误：{snap.Error}");
        File.WriteAllText(path, sb.ToString());
        SetStatus($"✓ 已导出：{Path.GetFileName(path)}");
        try { Process.Start("explorer.exe", $"/select,\"{path}\""); } catch { /* 忽略 */ }
    }

    /// <summary>
    /// 退出前记一条客户端语言快照（**只读**）。
    ///
    /// 起因：2026-10-25 用户报「关掉工具箱后客户端语言好像掉了（不确定）」。
    /// 全仓库没有任何写客户端 locale 的代码（对客户端的写操作只有「窗口尺寸修正」和
    /// 用户手动点的「热重载客户端」两处，都不碰语言），但光靠读代码证明不了，
    /// 所以退出瞬间把客户端自己报的 region-locale 记一行：
    /// 下次再出现就看这条 —— locale 变了，是我们这边的嫌疑；locale 没变而界面是英文/缺字，
    /// 那就是客户端自己的事，与工具箱无关。有了这条才好判断，不用再靠猜。
    /// 只读 + 500ms 超时，客户端没响应就不记，绝不拖慢退出。
    /// </summary>
    private void LogClientLocaleBeforeExit()
    {
        try
        {
            // 丢到线程池里跑：GetExAsync 内部 await 会回到 UI 上下文，直接在 UI 线程 Wait 会互相等死
            var task = Task.Run(() => _lcu.GetExAsync("/riotclient/region-locale"));
            if (!task.Wait(500))
            {
                Log(LogKind.Info, "退出 · 客户端语言快照", "超时（客户端没响应，可能已经关了）");
                return;
            }

            var (status, body, err) = task.Result;
            var text = body.Trim();
            if (text.Length == 0) text = err;
            if (text.Length > 240) text = text[..240] + "…";
            Log(LogKind.Info, "退出 · 客户端语言快照", $"HTTP {status} {text}");
        }
        catch (Exception ex)
        {
            Log(LogKind.Info, "退出 · 客户端语言快照", "取不到：" + ex.Message);
        }
    }

    private void OpenSettings()
    {
        using var dlg = new SettingsForm(_settings, _plugins.PluginSections());
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            _lcu.ClientPathHint = _settings.ClientPathHint;
            _settings.Save(AppDirectory);
            // 插件设置也在同一个对话框里保存，通知插件把卡片状态同步过去
            _plugins.NotifySettingsChanged();
            ApplyPressStrength();
            RelayoutCards();          // 卡片管理的顺序/显隐可能改了，重新摆放
            SetStatus("✓ 参数已保存");
        }
    }

    /// <summary>把配置里的力度档下发到所有插件注册的卡片。</summary>
    private void ApplyPressStrength()
    {
        foreach (var a in _plugins.Cards)
            a.Control?.SetPressStrength(_settings.PressStrength);
    }

    /// <summary>
    /// 日志文件限长滚动：超过 5000 行就砍掉最旧的只留尾部。
    /// 不无限涨（没人会去清一个自己不知道会涨的文件），
    /// 也正因为会滚，右下角「导出日志」才是永久留档的正道。
    /// </summary>
    private void TrimLogFile()
    {
        try
        {
            if (!File.Exists(_logPath)) return;
            var lines = File.ReadAllLines(_logPath);
            if (lines.Length <= 5000) return;
            File.WriteAllLines(_logPath, lines.Skip(lines.Length - 5000));
        }
        catch { /* 读不出去就跳过这一次，别影响记日志 */ }
    }

    /// <summary>
    /// 导出采样日志：文件名带时间戳（采样日志_2026-10-17_03-14-02.txt），
    /// 默认定位到桌面。导的是**原样拷贝**，不带任何修改。
    /// </summary>
    private void ExportLog()
    {
        try
        {
            if (!File.Exists(_logPath))
            {
                SetStatus("还没有日志文件可导出", true);
                return;
            }
            using var dlg = new SaveFileDialog
            {
                Title = "导出采样日志",
                FileName = $"采样日志_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.txt",
                DefaultExt = "txt",
                Filter = "文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;

            File.Copy(_logPath, dlg.FileName, overwrite: true);
            SetStatus($"✓ 日志已导出：{dlg.FileName}");
        }
        catch (Exception ex)
        {
            SetStatus($"导出失败：{ex.Message}", true);
        }
    }

    private void UpdateHint() => _hint.Text = HintText();

    /// <summary>顺带显示当前渲染缩放：如果这里显示 100% 而系统实际是 150%，那界面就是被拉伸糊掉的。</summary>
    private string HintText()
    {
        int percent = (int)Math.Round(DeviceDpi * 100.0 / 96.0);
        return $"需要 .NET 9 桌面运行时 ｜ 当前渲染缩放 {percent}%";
    }

    private void SetStatus(string text, bool warn = false)
    {
        _status.Text = text;
        // 状态不只靠颜色：✓ / ⚠ / ✕ 前缀本身就是一层编码
        _status.ForeColor = warn ? T.Error : text.StartsWith("✓") ? T.Success : TextMain;
    }

    // ---------- 卡片右键：功能说明 / 该功能的设置 ----------

    /// <summary>每张卡片的右键菜单：看这个功能做什么、只打开这个功能的设置。</summary>
    private ContextMenuStrip BuildCardMenu(CardAdapter card)
    {
        var menu = new ContextMenuStrip { Font = T.Body() };
        menu.Items.Add("查看功能说明", null, (_, _) => ShowPluginHelp(card.PluginId, card.PluginName));
        menu.Items.Add("打开此功能设置…", null, (_, _) => OpenSettingsForPlugin(card.PluginId));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("上移", null, (_, _) => MoveCard(card.PluginId, -1));
        menu.Items.Add("下移", null, (_, _) => MoveCard(card.PluginId, +1));
        return menu;
    }

    /// <summary>
    /// 按 config.json 的 CardOrder / CardHidden 算出「该显示哪些卡片、按什么顺序」。
    ///
    /// 四条容错，缺一条就会出现「装个新插件顺序全乱」这类问题：
    ///   ① 按 CardOrder 里出现的先后排；
    ///   ② CardOrder 里没出现的（新装的插件）按自己的 order 追加到末尾；
    ///   ③ CardOrder 里有、但当前没加载的 id 直接忽略（插件被删或禁用是正常情况）；
    ///   ④ CardOrder 为空 → 完全按 plugin.json 的 order，跟以前一样。
    /// </summary>
    private List<CardAdapter> ApplyCardLayout(IReadOnlyList<CardAdapter> cards)
    {
        var hidden = new HashSet<string>(_settings.CardHidden, StringComparer.OrdinalIgnoreCase);

        var rank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < _settings.CardOrder.Count; i++)
            rank[_settings.CardOrder[i]] = i;

        return cards
            .Where(c => !hidden.Contains(c.PluginId))
            .OrderBy(c => rank.TryGetValue(c.PluginId, out int r) ? r : int.MaxValue)
            .ThenBy(c => c.Order)
            .ToList();
    }

    /// <summary>
    /// 本次该用几列：设置里填了就用填的（1–6）；填 0 按卡片数自动算。
    /// 自动规则：≤3 张单列（卡宽一点好读）、4–8 张两列、9 张以上三列。
    /// </summary>
    private int ColumnsFor(int count)
    {
        if (_settings.CardColumns > 0) return Math.Clamp(_settings.CardColumns, 1, 6);
        if (count <= 3) return 1;
        if (count <= 8) return 2;
        return 3;
    }

    /// <summary>
    /// 按当前配置重排卡片区。首次布局、拖动排序、设置窗改显隐，都走这里。
    /// 先摘掉所有卡片控件再按新顺序放回去，所以被关掉的功能自然就消失了。
    /// </summary>
    private void RelayoutCards()
    {
        if (_cardsHost is null) return;

        var ordered = ApplyCardLayout(_plugins.Cards);
        int cols = ColumnsFor(ordered.Count);
        int innerW = Math.Max(320, ClientSize.Width - T.PadOuter * 2);
        int cardW = (innerW - CardGap * (cols - 1)) / cols;
        int rows = (ordered.Count + cols - 1) / cols;
        int contentH = rows == 0 ? 0 : rows * T.CardH + (rows - 1) * CardGap;
        bool allHidden = ordered.Count == 0;

        _cardsHost.SuspendLayout();
        try
        {
            // 先清场：所有卡片控件都摘下来
            foreach (var a in _plugins.Cards)
            {
                if (a.Control.Parent == _cardsHost) _cardsHost.Controls.Remove(a.Control);
            }
            // 「全部隐藏」的提示也清掉，防止反复重排时叠出一堆
            foreach (var leftover in _cardsHost.Controls.OfType<Label>().ToList())
                _cardsHost.Controls.Remove(leftover);

            for (int i = 0; i < ordered.Count; i++)
            {
                var a = ordered[i];
                var c = a.Control;
                c.Dock = DockStyle.None;
                c.Margin = new Padding(0);
                c.Size = new Size(cardW, T.CardH);
                c.Location = new Point(T.PadOuter + (i % cols) * (cardW + CardGap),
                                       T.Sp3 + (i / cols) * (T.CardH + CardGap));
                c.ContextMenuStrip = BuildCardMenu(a);

                // 长按拖动排序：先解绑再绑，避免每次重排累积一堆处理器
                a.DragStarted -= CardDragStarted;
                a.DragStarted += CardDragStarted;
                a.DragMoved -= CardDragMoved;
                a.DragMoved += CardDragMoved;
                a.DragDropped -= CardDragDropped;
                a.DragDropped += CardDragDropped;

                _cardsHost.Controls.Add(c);
            }

            // 全部隐藏时别让人对着一片空白发呆
            if (allHidden)
            {
                _cardsHost.Controls.Add(new Label
                {
                    Text = "所有卡片已隐藏 —— 打开「设置 → 卡片管理」把它们请回来",
                    AutoSize = false,
                    Location = new Point(T.PadOuter, T.Sp3),
                    Size = new Size(innerW, 24),
                    ForeColor = TextDim,
                    Font = T.Meta(),
                });
            }

            _cardsHost.Height = allHidden
                ? T.Sp3 * 2 + 26                                    // 留一行放提示
                : Math.Min(contentH + T.Sp3 * 2, 330);              // 最多约 3 行，再多就滚
        }
        finally
        {
            _cardsHost.ResumeLayout();
        }
    }

    /// <summary>
    /// 把某张卡片往前/后挪一位：写进 CardOrder 并立即重排。
    ///
    /// 第一次移动时会先把「当前实际顺序」整体写进 CardOrder ——
    /// 否则配置原本是空表，只写一个 id 会让其余卡片按 order 重排，用户看到的顺序会跳。
    /// </summary>
    private void MoveCard(string pluginId, int delta)
    {
        var cur = ApplyCardLayout(_plugins.Cards).Select(c => c.PluginId).ToList();
        int i = cur.IndexOf(pluginId);
        int j = i + delta;
        if (i < 0 || j < 0 || j >= cur.Count) return;   // 已经在头/尾，不动

        (cur[i], cur[j]) = (cur[j], cur[i]);
        _settings.CardOrder = cur;
        _settings.Save(AppDirectory);
        RelayoutCards();
    }

    // ---------- 长按拖动排序（安卓桌面那套） ----------

    /// <summary>长按到位：记下拖的是哪张卡。</summary>
    private void CardDragStarted(object? sender, EventArgs e)
    {
        _dragSrc = sender as CardAdapter;
        if (_dragSrc is null) return;
        SetStatus("松手放到目标位置即可调整顺序");
    }

    /// <summary>
    /// 拖动中：① 让被拖的卡片跟着鼠标走（这就是「预览」，不用截图/悬浮窗 ——
    /// 直接改这张卡的 Location 最稳，也不会闪）；② 把鼠标底下那张卡标成落点。
    /// 这期间其他卡片位置一概不动，只有松手才重排。
    /// </summary>
    private void CardDragMoved(object? sender, Point screenPt)
    {
        if (_dragSrc is null) return;

        var c = _dragSrc.Control;
        if (c is not null && _cardsHost is not null)
        {
            var local = _cardsHost.PointToClient(screenPt);
            c.BringToFront();                                  // 别被别的卡压住
            c.Location = new Point(local.X - c.Width / 2, local.Y - c.Height / 2);
        }

        var target = AdapterAtScreen(screenPt);
        foreach (var a in _plugins.Cards)
            a.Control?.SetDropHint(!ReferenceEquals(a, _dragSrc) && ReferenceEquals(a, target));
    }

    /// <summary>松手：插到落点那张卡的位置；落在空白/自己身上就复位。</summary>
    private void CardDragDropped(object? sender, EventArgs e)
    {
        var src = _dragSrc;
        _dragSrc = null;
        foreach (var a in _plugins.Cards) a.Control?.SetDropHint(false);

        if (src is null) return;

        var target = AdapterAtScreen(Control.MousePosition);
        if (target is not null && !ReferenceEquals(target, src))
            MoveCardBefore(src.PluginId, target.PluginId);   // 内部会重排
        else
            RelayoutCards();                                  // 拖着走了一圈又放下：归位
    }

    /// <summary>屏幕坐标落在哪张卡上；不在任何卡上返回 null。</summary>
    private CardAdapter? AdapterAtScreen(Point screenPt)
    {
        foreach (var a in _plugins.Cards)
        {
            var c = a.Control;
            if (c is null || c.IsDisposed || !c.Visible) continue;
            if (c.RectangleToScreen(c.ClientRectangle).Contains(screenPt)) return a;
        }
        return null;
    }

    /// <summary>
    /// 把 src 插到 target 原来的位置。
    /// 拖动排序是「插入」不是「交换」—— 交换会让被拖的卡跳来跳去，手感很怪。
    /// </summary>
    private void MoveCardBefore(string srcId, string targetId)
    {
        var cur = ApplyCardLayout(_plugins.Cards).Select(c => c.PluginId).ToList();
        int from = cur.IndexOf(srcId);
        int to = cur.IndexOf(targetId);
        if (from < 0 || to < 0 || from == to) return;

        cur.RemoveAt(from);
        if (from < to) to--;              // 拔掉之后，目标索引要前移一位
        cur.Insert(to, srcId);

        _settings.CardOrder = cur;
        _settings.Save(AppDirectory);
        RelayoutCards();
        SetStatus("✓ 顺序已保存");
    }

    /// <summary>
    /// 显示插件的说明文件。约定放在 plugins/&lt;id&gt;/说明.md，
    /// 找不到就显示一段提示，不弹错误框（缺说明是内容问题，不是故障）。
    /// </summary>
    private void ShowPluginHelp(string pluginId, string pluginName)
    {
        string text = "";
        try
        {
            var folder = _plugins.PluginFolder(pluginId);
            if (folder is not null)
            {
                foreach (var name in new[] { "说明.md", "README.md" })
                {
                    var p = Path.Combine(folder, name);
                    if (File.Exists(p)) { text = File.ReadAllText(p); break; }
                }
            }
        }
        catch (Exception ex) { text = "读取说明失败：" + ex.Message; }

        if (text.Trim().Length == 0)
            text = $"{pluginName}\n\n这个功能还没有写说明文件。\n\n按约定应放在：plugins/{pluginId}/说明.md";

        using var dlg = new HelpForm($"{pluginName} · 功能说明", text);
        dlg.ShowDialog(this);
    }

    private void OpenSettingsForPlugin(string pluginId)
    {
        try
        {
            using var dlg = new SettingsForm(_settings, _plugins.PluginSections(), pluginId);
            dlg.ShowDialog(this);
            ApplyPressStrength();
        }
        catch (Exception ex)
        {
            Log(LogKind.Error, "打开设置失败", ex.Message);
        }
    }

    private static void SafeInvoke(Control c, Action a)
    {
        try
        {
            if (c.IsDisposed) return;
            if (c.InvokeRequired) c.BeginInvoke(a);
            else a();
        }
        catch
        {
            /* 关闭窗口时不抛异常 */
        }
    }

    /// <summary>自测用：把（屏幕外的）窗口自渲染存盘。</summary>
    private static void ShootForm(Form f, string path)
    {
        using var bmp = new Bitmap(f.Width, f.Height);
        f.DrawToBitmap(bmp, new Rectangle(Point.Empty, bmp.Size));
        bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }

    /// <summary>自测用：递归找设置窗里那个「开发者选项」折叠按钮。</summary>
    private static Button? FindDevButton(Control root)
    {
        foreach (Control c in root.Controls)
        {
            if (c is Button b && c.Text.Contains("开发者选项")) return b;
            var hit = FindDevButton(c);
            if (hit is not null) return hit;
        }
        return null;
    }

    /// <summary>
    /// 自测用：把设置窗里每个 TableLayoutPanel 的每一行（标签 + 控件类型 + 当前值）
    /// 导进日志。插件设置的 schema 通用渲染是否正确，看这个清单就行 ——
    /// 比肉眼核对截图可靠，也不会漏（DrawToBitmap 截不全滚动区）。
    /// </summary>
    private void DumpSettingsForm(Form dlg)
    {
        foreach (var tbl in AllControls(dlg).OfType<TableLayoutPanel>())
        {
            for (int r = 0; r < tbl.RowCount; r++)
            {
                var label = tbl.GetControlFromPosition(0, r) as Label;
                var input = tbl.GetControlFromPosition(1, r);
                if (label is null && input is null) continue;
                string val = input switch
                {
                    CheckBox cb => cb.Checked ? "✓开" : "✗关",
                    NumericUpDown n => n.Value.ToString(),
                    TextBox t => $"\"{t.Text}\"",
                    ComboBox c => c.SelectedItem?.ToString() ?? "(空)",
                    TrackBar tb => tb.Value + "%",
                    null => "(无控件)",
                    _ => input.GetType().Name,
                };
                Log(LogKind.Info, "自测·设置行", $"[{label?.Text ?? "?"}] {input?.GetType().Name ?? "—"} = {val}");
            }
        }
    }

    private static IEnumerable<Control> AllControls(Control root)
    {
        foreach (Control c in root.Controls)
        {
            yield return c;
            foreach (var sub in AllControls(c))
                yield return sub;
        }
    }
}

/// <summary>
/// 参数设置对话框。
///
/// 两类内容：① 宿主全局项（采样/接口路径/客户端位置/手感）—— 硬编码的几个分区；
/// ② 插件设置 —— 按各插件 plugin.json 里的 schema 由 <see cref="MakeInput"/> 通用渲染，
/// 插件自己一行 UI 代码都不用写。默认可见区放 show=always 的项，
/// 开发者选项里放全局排障项 + show=dev 的插件项。
/// </summary>
/// <summary>
/// 功能说明查看窗：深色、只读、可拖动缩放、Ctrl+A 全选复制。
/// 内容来自 plugins/&lt;id&gt;/说明.md —— 功能多了以后「这是干啥的」得能在程序里查到。
/// </summary>
internal sealed class HelpForm : Form
{
    public HelpForm(string title, string text)
    {
        T.DarkTitle(this);
        Text = title;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.Sizable;
        ClientSize = new Size(720, 620);
        MinimumSize = new Size(460, 320);
        BackColor = T.BgBase;
        ForeColor = T.TxPrimary;
        Font = T.Body();

        var box = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            BorderStyle = BorderStyle.None,
            BackColor = T.BgSunken,
            ForeColor = T.TxPrimary,
            Font = new Font("Microsoft YaHei UI", 10f),
            Text = text.Replace("\r\n", "\n").Replace("\n", "\r\n"),
        };

        var bottom = new Panel
        {
            Dock = DockStyle.Bottom, Height = 52, BackColor = T.BgBase,
            Padding = new Padding(T.PadOuter, 10, T.PadOuter, 8),
        };
        var close = new Button
        {
            Text = "关闭", Width = 90, Dock = DockStyle.Right,
            FlatStyle = FlatStyle.Flat, BackColor = T.BgRaised, ForeColor = T.TxPrimary,
            Height = T.ControlH,
        };
        close.FlatAppearance.BorderColor = T.TxTertiary;
        close.Click += (_, _) => Close();
        bottom.Controls.Add(close);

        Controls.Add(box);
        Controls.Add(bottom);
        KeyPreview = true;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Close(); };
    }
}

internal sealed class SettingsForm : Form
{
    private readonly Settings _s;
    private readonly List<PluginSettingsSection> _sections;
    /// <summary>插件设置控件与存储的绑定，点保存时统一写回。</summary>
    private readonly List<(Control Input, IPluginSettings Store, SettingSchema Schema)> _bind = new();
    private Action? _toggleDev;   // 由构造函数末尾赋值，供快捷键调用

    // 功能管理的「草稿」：点保存才写回 _s，点取消就当没改过
    private HashSet<string> _draftDisabled = new(StringComparer.OrdinalIgnoreCase);

    public SettingsForm(Settings s, List<PluginSettingsSection> sections, string? onlyPluginId = null)
    {
        _s = s;
        // 右键某张卡片进来的：只渲染这个插件的分区，别的都不显示
        _sections = string.IsNullOrEmpty(onlyPluginId)
            ? sections
            : sections.Where(x => x.PluginId == onlyPluginId).ToList();
        var scopeName = _sections.FirstOrDefault()?.PluginName;
        T.DarkTitle(this);   // HandleCreated 之前调用也生效（DWM 会在句柄创建时应用）
        Text = string.IsNullOrEmpty(onlyPluginId) ? "参数设置" : $"{scopeName} · 参数设置";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false;
        ClientSize = new Size(640, 760);
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = T.BgBase;
        Font = T.Body();
        ForeColor = T.TxPrimary;

        var root = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(T.PadOuter, 12, T.PadOuter, 8) };

        var tbl = new TableLayoutPanel
        {
            Dock = DockStyle.Top, ColumnCount = 2, AutoSize = true,
            BackColor = T.BgBase,
        };
        tbl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58f));
        tbl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42f));

        void Section(string text) => SectionIn(tbl, text);
        void Row(string label, Control input, string tip = "") => RowIn(tbl, label, input, tip);

        // 跨两列、按内容自动撑高的整块（卡片管理这种自定义列表用它）
        void AutoBlock(TableLayoutPanel host, Control c)
        {
            c.Dock = DockStyle.Top;
            host.Controls.Add(c, 0, host.RowCount - 0);
            host.SetColumnSpan(c, 2);
            host.RowCount++;
            host.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        }

        // ---- 功能管理（启用 / 关闭）----
        // 管的是「功能」不是「卡片」：纯服务插件（如 watchvideo）也列在这里 ——
        // 它虽然不画卡片，但关掉它，「看视频（自动）」就切不了窗口了。
        // 卡片排布是另一回事：在主界面上长按卡片拖动。
        // 只在完整设置窗里出现；右键某个插件进设置时不显示（那样改不全）。
        if (string.IsNullOrEmpty(onlyPluginId) && _sections.Count > 0)
        {
            Section("功能 · 启用 / 关闭");

            _draftDisabled = new HashSet<string>(_s.DisabledPlugins, StringComparer.OrdinalIgnoreCase);

            var listBox = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                BackColor = T.BgBase,
                Margin = new Padding(0),
            };

            void RebuildList()
            {
                listBox.SuspendLayout();
                try
                {
                    listBox.Controls.Clear();
                    foreach (var sec in OrderedSections())
                    {
                        var row = new FlowLayoutPanel
                        {
                            FlowDirection = FlowDirection.LeftToRight,
                            WrapContents = false,
                            AutoSize = true,
                            Height = 30,
                            Margin = new Padding(0, 0, 0, 4),
                            BackColor = T.BgBase,
                        };
                        var on = new CheckBox
                        {
                            Text = sec.PluginName,
                            Checked = !_draftDisabled.Contains(sec.PluginId),
                            Width = 320, Height = 28,
                            ForeColor = T.TxPrimary, Font = T.Body(),
                            Tag = sec.PluginId,
                        };
                        on.CheckedChanged += (_, _) =>
                        {
                            if (on.Checked) _draftDisabled.Remove(sec.PluginId);
                            else _draftDisabled.Add(sec.PluginId);
                        };
                        row.Controls.Add(on);
                        listBox.Controls.Add(row);
                    }
                }
                finally
                {
                    listBox.ResumeLayout();
                }
            }

            RebuildList();
            AutoBlock(tbl, listBox);

            AutoBlock(tbl, new Label
            {
                Text = "关掉的功能不会加载（不占资源、不起线程），保存后需要重启程序才生效。\n卡片顺序不在这里调 —— 在主界面上长按卡片拖动。",
                AutoSize = false, Height = 38, Dock = DockStyle.Top,
                ForeColor = T.TxTertiary, Font = T.Meta(),
                Margin = new Padding(0, 2, 0, T.Sp2),
            });
        }

        // ---- 卡片区布局 ----
        NumericUpDown? numCols = null;
        if (string.IsNullOrEmpty(onlyPluginId))
        {
            Section("卡片区布局");
            numCols = new NumericUpDown
            {
                Minimum = 0,
                Maximum = 6,
                Value = Math.Clamp(_s.CardColumns, 0, 6),
                Width = 80,
            };
            Row("每行几列", numCols, "0 = 自动：≤3 张单列、4–8 张两列、再多三列。功能变多以后嫌排太长就手动填");
        }

        // ---- 插件设置（默认可见项）----
        // 顺序与卡片一致：按 order 从小到大。没有 always 项的插件不占位。
        foreach (var sec in _sections)
        {
            var always = sec.Schemas.Where(x => x.Show != "dev").ToList();
            if (always.Count == 0) continue;
            Section(sec.PluginName);
            foreach (var sc in always)
                Row(sc.Label, MakeInput(sc, sec.Store), sc.Note);
        }

        // ---- 开发者选项容器：默认收起 ----
        // 采样接口、排障项这些塞进折叠区，点标题才展开。
        var devHost = new Panel
        {
            Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Visible = false, BackColor = T.BgBase,
            Padding = new Padding(T.Sp4, 0, 0, T.Sp2),
        };
        var devTbl = new TableLayoutPanel
        {
            Dock = DockStyle.Top, ColumnCount = 2, AutoSize = true, BackColor = T.BgBase,
        };
        devTbl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58f));
        devTbl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42f));
        devHost.Controls.Add(devTbl);

        void SectionD(string text) => SectionIn(devTbl, text);
        void RowD(string label, Control input, string tip = "") => RowIn(devTbl, label, input, tip);

        void SectionIn(TableLayoutPanel host, string text)
        {
            var l = new Label
            {
                Text = text, AutoSize = false, Height = T.ControlH, Dock = DockStyle.Top,
                ForeColor = T.TxTertiary,
                Font = T.Section(),
                TextAlign = ContentAlignment.MiddleLeft,
            };
            host.Controls.Add(l, 0, host.RowCount - 0);
            host.SetColumnSpan(l, 2);
            host.RowCount++;
            host.RowStyles.Add(new RowStyle(SizeType.Absolute, T.ControlH + T.Sp2));
        }

        void RowIn(TableLayoutPanel host, string label, Control input, string tip = "")
        {
            var l = new Label
            {
                Text = label, AutoSize = false, Height = T.ControlH, Dock = DockStyle.Top,
                ForeColor = T.TxSecondary, TextAlign = ContentAlignment.MiddleLeft,
            };
            input.Dock = DockStyle.Top;
            input.Height = T.ControlH;
            host.Controls.Add(l, 0, host.RowCount - 0);
            host.Controls.Add(input, 1, host.RowCount - 0);
            host.RowCount++;
            host.RowStyles.Add(new RowStyle(SizeType.Absolute, T.ControlH + T.Sp1));

            if (!string.IsNullOrEmpty(tip))
            {
                var t = new Label
                {
                    Text = tip, AutoSize = false, Height = 20, Dock = DockStyle.Top,
                    ForeColor = T.TxTertiary,
                    Font = T.Meta(),
                };
                host.Controls.Add(t, 0, host.RowCount - 0);
                host.SetColumnSpan(t, 2);
                host.RowCount++;
                host.RowStyles.Add(new RowStyle(SizeType.Absolute, 24f));
            }
        }

        NumericUpDown Num(int min, int max, int val)
        {
            return new NumericUpDown
            {
                Minimum = min, Maximum = max, Value = val,
                BackColor = T.BgSunken, ForeColor = T.TxPrimary,
            };
        }

        TextBox Field(string text) => new()
        {
            Text = text, BackColor = T.BgSunken, ForeColor = T.TxPrimary,
            BorderStyle = BorderStyle.FixedSingle,
        };

        CheckBox Check(string text, bool on) => new()
        {
            Text = text, Checked = on, ForeColor = T.TxPrimary,
            Height = T.ControlH, Dock = DockStyle.Top,
        };

        SectionD("采样与订阅");
        var cbWs = Check("用 WebSocket 订阅", _s.UseWebSocket);
        var numPoll = Num(1, 60, _s.PollIntervalSec);
        var numSubPoll = Num(1, 120, _s.SubscribedPollSec);
        RowD("订阅推送", cbWs, "阶段变化实时推送；关掉就退回纯轮询");
        RowD("轮询间隔（秒）", numPoll, "订阅未连上时用这个；也是窗口检测的频率");
        RowD("订阅后轮询（秒）", numSubPoll, "订阅已连上时放慢到这个，只作兜底");

        SectionD("接口路径（国服与外服不同，实测可改）");
        var tbPhase = Field(_s.PhaseEndpoint);
        var tbEog = Field(_s.EogEndpoint);
        var tbSess = Field(_s.SessionEndpoint);
        var numTimeout = Num(500, 30000, _s.HttpTimeoutMs);
        RowD("阶段接口", tbPhase, "实测国服返回字符串，例如 \"None\"（大厅）");
        RowD("结算接口", tbEog, "国服没有 /lol-end-of-game/v1/state（404 Invalid URI format）");
        RowD("会话接口", tbSess, "对局/结算时里面带 phase 字段，作为辅助判断");
        RowD("请求超时（毫秒）", numTimeout, "本机 4 秒够用；调大可避免误报超时");

        SectionD("客户端位置");
        var tbPath = Field(_s.ClientPathHint);
        RowD("安装目录（可留空）", tbPath, "自动找不到时才需要填，填客户端安装目录，例如 D:\\Games\\League of Legends");

        SectionD("手感");
        var strengthHost = new Panel { BackColor = T.BgBase, Height = T.ControlH, Dock = DockStyle.Top };
        var tbStrength = new TrackBar
        {
            Minimum = 0, Maximum = 100, Value = Math.Clamp(_s.PressStrength, 0, 100),
            Dock = DockStyle.Fill, TickStyle = TickStyle.None, BackColor = T.BgBase,
        };
        var lblStrength = new Label
        {
            Text = tbStrength.Value + "%", Width = 42, Dock = DockStyle.Right,
            ForeColor = T.TxPrimary, Font = T.Meta(), TextAlign = ContentAlignment.MiddleRight,
        };
        tbStrength.ValueChanged += (_, _) => lblStrength.Text = tbStrength.Value + "%";
        // Fill 的滑杆要先 Add 才拿得到剩余宽度，读数标签后 Add 会先贴住右缘
        strengthHost.Controls.Add(tbStrength);
        strengthHost.Controls.Add(lblStrength);
        RowD("按下力度", strengthHost, "按下去的反馈强度。100 = 夸张，0 = 几乎没反应");

        // ---- 各插件的 dev 项 ----
        foreach (var sec in _sections)
        {
            var devs = sec.Schemas.Where(x => x.Show == "dev").ToList();
            if (devs.Count == 0) continue;
            SectionD($"{sec.PluginName}（详细）");
            foreach (var sc in devs)
                RowD(sc.Label, MakeInput(sc, sec.Store), sc.Note);
        }

        // 折叠开关放在默认表最后一行的位置，展开的内容紧跟在它下面
        // 开发者选项默认收起，且不放大按钮 —— 只留一行小字提示快捷键。
        devHost.Visible = false;
        var btnDev = new Button
        {
            Text = "[Ctrl+`] 打开开发者选项（混乱）", Height = T.ControlH, Dock = DockStyle.Top,
            FlatStyle = FlatStyle.Flat, BackColor = T.BgBase, ForeColor = T.TxTertiary,
            Font = T.Meta(), TextAlign = ContentAlignment.MiddleLeft, Cursor = Cursors.Hand,
        };
        btnDev.FlatAppearance.BorderSize = 0;
        btnDev.FlatAppearance.MouseOverBackColor = T.BgOverlay;
        btnDev.FlatAppearance.MouseDownBackColor = T.BgPress;
        void ToggleDev()
        {
            bool open = !devHost.Visible;
            devHost.Visible = open;
            btnDev.Text = open ? "[-] 收起开发者选项" : "[Ctrl+`] 打开开发者选项（混乱）";
            btnDev.ForeColor = open ? T.TxSecondary : T.TxTertiary;
        }
        btnDev.Click += (_, _) => ToggleDev();
        _toggleDev = ToggleDev;
        tbl.Controls.Add(btnDev, 0, tbl.RowCount - 0);

        // 开发者选项默认完全不可见：入口由 config.json 的 "ShowDevOptions" 控制
        // （里面全是排障项，日常用户不该看见，更不该不小心点进去改坏）。
        // 没授权时连 Ctrl+` 快捷键一并失效，不留后门。
        if (!_s.ShowDevOptions)
        {
            btnDev.Visible = false;
            devHost.Visible = false;
            _toggleDev = null;
        }
        tbl.SetColumnSpan(btnDev, 2);
        tbl.RowCount++;
        tbl.RowStyles.Add(new RowStyle(SizeType.Absolute, T.ControlH + T.Sp2));

        // Dock.Top 从集合末尾往回停靠：devHost 先 Add 才会落到 tbl 下面
        root.Controls.Add(devHost);
        root.Controls.Add(tbl);

        // 渲染缩放挪到这里：它是排障信息，不该占主界面一行
        int percent = (int)Math.Round(DeviceDpi * 100.0 / 96.0);
        var hint = new Label
        {
            Text = $"需要 .NET 9 桌面运行时 ｜ 当前渲染缩放 {percent}%",
            AutoSize = false, Dock = DockStyle.Bottom, Height = 22,
            ForeColor = T.TxTertiary, Font = T.Meta(),
            TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(T.PadOuter, 4, 0, 0),
        };

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, Height = 56, FlowDirection = FlowDirection.RightToLeft,
            BackColor = T.BgRaised, Padding = new Padding(0, T.Sp3, T.PadOuter, 0),
        };
        var ok = new Button
        {
            Text = "保存", Width = 96, Height = T.ControlH, FlatStyle = FlatStyle.Flat,
            // 品牌金 + 深色字，对比度 7.6:1（白字只有 4.0:1，不达标）
            BackColor = T.Accent, ForeColor = T.BgBase, Font = T.BodyBold(),
            DialogResult = DialogResult.OK, Cursor = Cursors.Hand,
        };
        ok.FlatAppearance.BorderSize = 0;
        var cancel = new Button
        {
            Text = "取消", Width = 96, Height = T.ControlH, FlatStyle = FlatStyle.Flat,
            BackColor = T.BgRaised, ForeColor = T.TxSecondary,
            DialogResult = DialogResult.Cancel, Cursor = Cursors.Hand,
        };
        cancel.FlatAppearance.BorderColor = T.BdStrong;
        buttons.Controls.Add(ok);
        buttons.Controls.Add(cancel);
        AcceptButton = ok;
        CancelButton = cancel;

        Controls.Add(root);
        Controls.Add(hint);
        Controls.Add(buttons);

        ok.Click += (_, _) =>
        {
            // —— 宿主全局项 ——
            _s.UseWebSocket = cbWs.Checked;
            _s.PollIntervalSec = (int)numPoll.Value;
            _s.SubscribedPollSec = (int)numSubPoll.Value;
            _s.PhaseEndpoint = tbPhase.Text.Trim();
            _s.EogEndpoint = tbEog.Text.Trim();
            _s.SessionEndpoint = tbSess.Text.Trim();
            _s.HttpTimeoutMs = (int)numTimeout.Value;
            _s.ClientPathHint = tbPath.Text.Trim();
            _s.PressStrength = tbStrength.Value;

            // —— 功能开关 / 卡片列数（右键单插件进来时不动，避免只写了一半）——
            if (string.IsNullOrEmpty(onlyPluginId) && _sections.Count > 0)
            {
                _s.DisabledPlugins = _draftDisabled.ToList();
            }
            if (numCols is not null) _s.CardColumns = (int)numCols.Value;

            // —— 插件项：按绑定写回各自的 settings.json ——
            foreach (var (input, store, sc) in _bind)
                WriteBack(input, store, sc);
            foreach (var sec in _sections)
                sec.Store.Save();
        };
    }

    // ---------- schema → 控件 / 控件 → 值 ----------

    private static int SchemaInt(JsonElement? d)
    {
        try { return d.HasValue && d.Value.ValueKind == JsonValueKind.Number ? d.Value.GetInt32() : 0; }
        catch { return 0; }
    }

    private static double SchemaDouble(JsonElement? d)
    {
        try { return d.HasValue && d.Value.ValueKind == JsonValueKind.Number ? d.Value.GetDouble() : 0d; }
        catch { return 0d; }
    }

    private static string SchemaText(JsonElement? d)
    {
        try { return d.HasValue && d.Value.ValueKind == JsonValueKind.String ? d.Value.GetString() ?? "" : ""; }
        catch { return ""; }
    }

    /// <summary>按 schema 类型造控件，并读初始值。同时登记写回绑定。</summary>
    private Control MakeInput(SettingSchema sc, IPluginSettings store)
    {
        double lo = sc.Min ?? 0, hi = sc.Max ?? 999999;
        Control input = (sc.Type ?? "text").ToLowerInvariant() switch
        {
            "bool" => new CheckBox
            {
                Checked = store.Get(sc.Key, sc.Default?.ValueKind == JsonValueKind.True),
                ForeColor = T.TxPrimary,
            },
            "int" => new NumericUpDown
            {
                Minimum = (decimal)lo,
                Maximum = (decimal)hi,
                Value = Math.Clamp(store.Get(sc.Key, SchemaInt(sc.Default)),
                                   (decimal)lo, (decimal)hi),
                BackColor = T.BgSunken, ForeColor = T.TxPrimary,
            },
            "number" => new NumericUpDown
            {
                Minimum = (decimal)lo,
                Maximum = (decimal)hi,
                DecimalPlaces = 3, Increment = 0.005m,
                Value = (decimal)Math.Clamp(store.Get(sc.Key, SchemaDouble(sc.Default)), lo, hi),
                BackColor = T.BgSunken, ForeColor = T.TxPrimary,
            },
            "choice" => new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = T.BgSunken, ForeColor = T.TxPrimary,
            },
            _ => new TextBox
            {
                Text = store.Get(sc.Key, SchemaText(sc.Default)),
                BackColor = T.BgSunken, ForeColor = T.TxPrimary,
                BorderStyle = BorderStyle.FixedSingle,
            },
        };

        if (input is ComboBox cb && sc.Options is not null)
        {
            cb.Items.AddRange(sc.Options.ToArray());
            var cur = store.Get(sc.Key, sc.Options.Count > 0 ? sc.Options[0] : "");
            if (!string.IsNullOrEmpty(cur) && cb.Items.Contains(cur)) cb.SelectedItem = cur;
            else if (cb.Items.Count > 0) cb.SelectedIndex = 0;
        }

        _bind.Add((input, store, sc));
        return input;
    }

    private static void WriteBack(Control input, IPluginSettings store, SettingSchema sc)
    {
        switch ((sc.Type ?? "text").ToLowerInvariant())
        {
            case "bool":
                if (input is CheckBox c) store.Set(sc.Key, c.Checked);
                break;
            case "int":
                if (input is NumericUpDown n) store.Set(sc.Key, (int)n.Value);
                break;
            case "number":
                if (input is NumericUpDown d) store.Set(sc.Key, (double)d.Value);
                break;
            case "choice":
                if (input is ComboBox cm) store.Set(sc.Key, cm.SelectedItem?.ToString() ?? "");
                break;
            default:
                if (input is TextBox t) store.Set(sc.Key, t.Text.Trim());
                break;
        }
    }

    /// <summary>
    /// 卡片管理列表的当前顺序：先按 config 里存的 CardOrder，没列到的（新插件）
    /// 保持 _sections 原本的次序（它已按 plugin.json 的 order 排好）追加到末尾。
    /// </summary>
    private List<PluginSettingsSection> OrderedSections()
    {
        var rank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < _s.CardOrder.Count; i++)
            rank[_s.CardOrder[i]] = i;

        return _sections
            .Select((sec, idx) => (sec, idx))
            .OrderBy(t => rank.TryGetValue(t.sec.PluginId, out int r) ? r : int.MaxValue)
            .ThenBy(t => t.idx)
            .Select(t => t.sec)
            .ToList();
    }

    /// <summary>
    /// Ctrl+`（反引号）＝切换开发者选项。
    /// 选它的理由：游戏内不会按到这个组合；60% 小键盘也有 ` 键；不和 Ctrl+Shift+D 冲突。
    /// </summary>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.Oemtilde))
        {
            _toggleDev?.Invoke();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }
}
