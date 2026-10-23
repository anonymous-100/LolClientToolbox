using LolClientToolbox.Contracts;

namespace MatchHistoryPlugin;

/// <summary>
/// 查询战绩 —— 查自己或「最近一起玩过的人」的对局记录。
///
/// 按需求只做这一个功能；符文推荐只记录不实现（见仓库根目录 需求评估.md）。
/// 这个插件同时是「插件开发手册」的第二个参考样例：演示插件自带 UI 窗口的写法。
/// </summary>
public sealed class MatchHistoryPlugin : ILolPlugin
{
    private IPluginHost _host = null!;
    private ICard _card = null!;
    private MatchHistoryService _svc = null!;
    private HistoryForm? _win;

    public string Id => "matchhistory";
    public string Name => "查询战绩";

    public void Init(IPluginHost host, IPluginSettings settings)
    {
        _host = host;
        _svc = new MatchHistoryService(host);

        // 纯查询功能，没有「自动」一说，所以右侧不放自动开关
        _card = host.RegisterCard(Name, "查自己或最近一起玩过的人", showAuto: false)!;
        _card.Clicked += OpenWindow;
    }

    public void Start()
    {
        // 自检模式（测试专用，不进正常流程）：
        //   set LCT_SELFTEST_HISTORY=1 && LOLToolbox.exe
        // 自动打开战绩窗口拉真实客户端数据，把「对局行数 / 计分板行数」写进日志后退出。
        // 用来在没有真人点击的情况下端到端验证整条链路（接口→解析→渲染）。
        if (_host.IsSelfTest
            || Environment.GetEnvironmentVariable("LCT_SELFTEST_HISTORY") != "1") return;
        try
        {
            OpenWindow();
            var t = new System.Windows.Forms.Timer { Interval = 5000 };
            int tries = 0;
            t.Tick += (_, _) =>
            {
                tries++;
                if (_win is not { IsDisposed: false } f)
                {
                    t.Stop();
                    Application.Exit();
                    return;
                }
                var (games, scores) = f.Counts;
                _host.Log(LogLevel.Info, "自检", $"战绩窗口（第 {tries} 次采样）：对局 {games} 行 · 计分板 {scores} 行");
                _host.Log(LogLevel.Info, "自检", $"计分板表头：{f.ScoreHeadText}");
                _host.Log(LogLevel.Info, "自检", $"战绩窗口位置：{f.WindowPos}（宿主应已摆到主窗右侧）");
                if (games > 0 || tries >= 3)
                {
                    t.Stop();
                    Application.Exit();
                }
            };
            t.Start();
        }
        catch (Exception ex)
        {
            _host.Log(LogLevel.Error, "自检失败：" + ex.Message);
            Application.Exit();
        }
    }

    public void Stop() { }

    /// <summary>
    /// 打开战绩窗口。两个刻意的设计：
    /// ① 非模态 Show()，不用 ShowDialog —— 模态会挡住主窗口的其他功能；
    /// ② 位置交给宿主（ShowAuxWindow 摆到主窗右侧并跟随移动），插件不自己定位。
    /// </summary>
    private void OpenWindow()
    {
        // 插件自己的窗口出任何问题都自己兜住 —— 绝不把异常抛回宿主
        try
        {
            // 已经开着就只激活 + 重新摆位，不重复开
            if (_win is { IsDisposed: false })
            {
                _win.Activate();
                _host.ShowAuxWindow(_win.Handle, 780, 620);
                return;
            }

            var f = new HistoryForm(_svc);
            _win = f;
            f.Show();                                    // 非模态
            _host.ShowAuxWindow(f.Handle, 780, 620);     // 宿主摆到主窗右侧
        }
        catch (Exception ex)
        {
            _host.Log(LogLevel.Error, "查询战绩窗口打开失败：" + ex.Message);
            _host.SetStatus("✕ 查询战绩窗口打开失败", true);
        }
    }
}
