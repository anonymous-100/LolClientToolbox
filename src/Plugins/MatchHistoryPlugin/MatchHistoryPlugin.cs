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

    public void Start() { }
    public void Stop() { }

    private void OpenWindow()
    {
        // 插件自己的窗口出任何问题都自己兜住 —— 绝不把异常抛回宿主
        try
        {
            using var f = new HistoryForm(_svc);
            f.ShowDialog();
        }
        catch (Exception ex)
        {
            _host.Log(LogLevel.Error, "查询战绩窗口打开失败：" + ex.Message);
            _host.SetStatus("✕ 查询战绩窗口打开失败", true);
        }
    }
}
