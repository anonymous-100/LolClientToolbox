using System;
using System.Threading.Tasks;
using LolClientToolbox.Contracts;

namespace ReloadPlugin;

/// <summary>
/// 热重载客户端 —— POST /riotclient/kill-and-restart-ux 重启客户端 UX。
/// 纯手动功能：没有「自动」开关（没人希望客户端被自动重启），所以 showAuto: false。
/// </summary>
public sealed class ReloadPlugin : ILolPlugin
{
    private const string RestartUxPath = "/riotclient/kill-and-restart-ux";

    private IPluginHost _host = null!;
    private ICard _card = null!;

    public string Id => "reload";
    public string Name => "热重载客户端";

    public void Init(IPluginHost host, IPluginSettings settings)
    {
        _host = host;
        _card = host.RegisterCard(Name, "重启客户端界面", showAuto: false)!;
        _card.Clicked += () => _ = ReloadOnce();
    }

    public void Start() { }
    public void Stop() { }

    private async Task ReloadOnce()
    {
        _card.SetRunning();
        _host.Log(LogLevel.Auto, "手动：热重载客户端");
        bool ok;
        string msg;
        try
        {
            var r = await _host.PostExAsync(RestartUxPath);
            ok = r.Ok;
            msg = r.Ok ? "已发送重载指令" : $"客户端返回 {r.Status}";
        }
        catch (Exception ex)
        {
            ok = false;
            msg = ex.Message;
        }

        _host.Log(LogLevel.Auto, ok ? $"热重载客户端完成 · {msg}" : $"热重载客户端失败 · {msg}");
        _host.SetStatus(ok ? $"✓ 热重载客户端执行完成 · {msg}" : $"✕ 热重载客户端：{msg}", !ok);
        _card.SetResult(ok, ok ? "完成" : "失败");
        _card.Restore();
    }
}
