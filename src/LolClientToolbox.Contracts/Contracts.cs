using System;

namespace LolClientToolbox.Contracts;

/// <summary>
/// 插件契约层 —— 宿主与插件之间「唯一」的共享类型定义。
///
/// 铁律：**任何跨边界传递的类型必须住在这里**（快照、事件、卡片、设置……）。
/// 插件不许引用宿主程序集；宿主也只许通过本程序集里的接口认识插件。
/// 这样任何一侧单独升级都不会把另一侧拖垮。
/// </summary>
public enum LogLevel
{
    /// <summary>阶段变化（界面里蓝色「阶段」徽章）。</summary>
    Phase,
    /// <summary>一般信息。</summary>
    Info,
    /// <summary>自动化动作（界面里金色「自动」徽章）。</summary>
    Auto,
    /// <summary>警告。</summary>
    Warn,
    /// <summary>错误。</summary>
    Error,
}

/// <summary>客户端一次状态快照。宿主持有采样循环，每个 tick 转发给所有插件。</summary>
public sealed class ClientSnapshot
{
    public bool ClientRunning { get; set; }
    /// <summary>gameflow-phase 原始值，如 "ReadyCheck"。</summary>
    public string Phase { get; set; } = "";
    /// <summary>本地化阶段名，如「等待接受」。</summary>
    public string PhaseText { get; set; } = "";
    /// <summary>结算/战绩可读结论。</summary>
    public string EogState { get; set; } = "";
    /// <summary>主窗口 "宽×高"。</summary>
    public string WindowRect { get; set; } = "";
    /// <summary>诊断串（端口/凭据来源/端点状态码）。</summary>
    public string Diag { get; set; } = "";
    public DateTime TakenAt { get; set; }
}

/// <summary>一条 LCU WebSocket 推送事件（uri / eventType / data 原文）。</summary>
public sealed class PluginEvent
{
    public string Uri { get; set; } = "";
    /// <summary>Create / Update / Delete。</summary>
    public string EventType { get; set; } = "";
    /// <summary>负载 JSON 原文。</summary>
    public string DataJson { get; set; } = "";
    public DateTime At { get; set; } = DateTime.Now;
}

/// <summary>
/// 插件私有设置。持久化到 <c>plugins/&lt;插件id&gt;/settings.json</c>，
/// 与宿主全局 config.json 物理隔离 —— 插件卸载时它的设置跟着一起走，不留垃圾。
/// </summary>
public interface IPluginSettings
{
    /// <summary>本次启动时设置文件是否本来不存在（用于一次性迁移旧配置）。</summary>
    bool WasFresh { get; }
    T Get<T>(string key, T fallback);
    void Set<T>(string key, T value);
    void Save();
}

/// <summary>
/// 一张功能卡片。宿主用自绘 ActionCard 实现；插件只面向接口，不接触任何 WinForms 类型。
/// 所有方法都是线程安全的（插件可以从后台线程直接调用）。
/// </summary>
public interface ICard
{
    string Title { get; }
    bool AutoOn { get; }

    /// <summary>同步自动开关。note 是开关上的小字（如「守护中」「监听中」）。</summary>
    void SetAuto(bool on, string note = "");
    /// <summary>进入「执行中」态。</summary>
    void SetRunning();
    /// <summary>执行结果：ok=true 完成、false 失败；text 是徽章文字（如「已接受」）。</summary>
    void SetResult(bool ok, string text);
    /// <summary>请求宿主在展示一会儿后把卡片还原到空闲态（宿主内部控制时机）。</summary>
    void Restore();

    /// <summary>主体被点＝立刻执行一次。在 UI 线程触发 —— 别在这里做耗时操作，丢到 Task.Run。</summary>
    event Action? Clicked;
    /// <summary>自动开关被切换。在 UI 线程触发。</summary>
    event Action<bool>? AutoToggled;
}

/// <summary>
/// 宿主提供给插件的全部服务。
///
/// 线程模型（重要，写插件前必读）：
///   · <see cref="OnPhaseChanged"/> / <see cref="Subscribe"/> 的回调在 WebSocket 接收线程；
///   · <see cref="OnTick"/> 在宿主持有的轮询线程；
///   · <see cref="ICard.Clicked"/> / <see cref="ICard.AutoToggled"/> 在 UI 线程。
///   · <see cref="Log"/> / <see cref="SetStatus"/> / <see cref="ICard"/> 的全部方法都是线程安全的。
///   · <see cref="GetAsync"/> / <see cref="PostAsync"/> 内部是共享 HttpClient，可并发调用。
/// </summary>
public interface IPluginHost
{
    /// <summary>exe 所在目录。</summary>
    string AppDirectory { get; }
    /// <summary>是否处于 --selftest 自测模式（截图用，不要弹窗）。</summary>
    bool IsSelfTest { get; }
    /// <summary>最近一次快照；可能为 null（还没采到过）。</summary>
    ClientSnapshot? LastSnapshot { get; }
    string CurrentPhase { get; }
    bool ClientRunning { get; }

    /// <summary>写一行日志（界面列表 + 磁盘文件各一份）。线程安全。</summary>
    void Log(LogLevel level, string message, string note = "");
    /// <summary>更新底部状态行。线程安全。</summary>
    void SetStatus(string text, bool warn = false);

    /// <summary>
    /// 注册一张卡片。排列顺序由插件清单 plugin.json 的 "order" 决定（越小越靠上），
    /// 部署方改 json 就能调顺序，不用重新编译。
    /// 必须在 <see cref="ILolPlugin.Init"/> 里调用；同一插件重复注册会被拒绝并返回 null。
    /// </summary>
    ICard? RegisterCard(string title, string desc, bool showAuto);

    /// <summary>订阅阶段变化。handler 在后台线程被调用。</summary>
    void OnPhaseChanged(Action<string> handler);
    /// <summary>订阅周期 tick（宿主采样节奏，订阅连通后约 8 秒一次）。handler 在后台线程。</summary>
    void OnTick(Action<ClientSnapshot> handler);
    /// <summary>订阅 LCU WebSocket 事件，uri 形如 /lol-matchmaking/v1/ready-check。handler 在 WS 线程。</summary>
    void Subscribe(string uri, Action<PluginEvent> handler);

    /// <summary>LCU GET。凭据由宿主自动带上；返回 (状态码, 正文)。未连上客户端时状态码为 -1。</summary>
    Task<(int Status, string Body)> GetAsync(string path);
    /// <summary>LCU POST。</summary>
    Task<bool> PostAsync(string path, string body = "{}");
    /// <summary>
    /// LCU POST（带状态码）。需要区分「成功 / 404 没数据 / 其他错误」时用这个 ——
    /// 比如接受对局确认框时，404 是「现在没有确认框」，属于正常情况不是故障。
    /// </summary>
    Task<(bool Ok, int Status, string Body)> PostExAsync(string path, string body = "{}");
    /// <summary>确保已连上客户端（会尝试重新读取凭据）。没连上返回 false。</summary>
    bool EnsureClient();

    /// <summary>
    /// 【仅限迁移用】读旧版全局 config.json 里的键（按文件内容读，不经过宿主设置类）。
    /// 新插件不要用这个 —— 自己的设置放在 <see cref="ILolPlugin.Init"/> 拿到的
    /// <see cref="IPluginSettings"/> 里。
    /// </summary>
    T GetLegacy<T>(string key, T fallback);
}

/// <summary>插件入口。每个插件 DLL 里必须有一个 public 无参构造的类型实现它。</summary>
public interface ILolPlugin
{
    string Id { get; }
    string Name { get; }
    /// <summary>
    /// 初始化：注册卡片、读设置、订阅事件。宿主加载插件时调用一次。
    /// <paramref name="settings"/> 是本插件的私有设置存储（plugins/&lt;id&gt;/settings.json），
    /// 与宿主及其他插件物理隔离。
    /// </summary>
    void Init(IPluginHost host, IPluginSettings settings);
    /// <summary>全部插件加载完后调用。可以开始跑后台任务。</summary>
    void Start();
    /// <summary>程序退出时调用。取消任务、保存设置。</summary>
    void Stop();
}

/// <summary>
/// 可选接口：自测模式（--selftest）时宿主会调用，用于把自动化开关摆到「开」的状态再截图。
/// 正常运行时不会被调用。
/// </summary>
public interface ISelfTestAware
{
    void OnSelfTest();
}

/// <summary>
/// 可选接口：宿主设置窗点「保存」后调用。插件在这里把卡片开关、
/// 缓存的上次设置等同步到最新值（比如用户在设置窗里改了「自动接收」勾选）。
/// </summary>
public interface ISettingsChangedAware
{
    void OnSettingsChanged();
}
