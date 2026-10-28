using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;
using LolClientToolbox.Contracts;

namespace LolClientToolbox;

// ==================== 插件清单（plugin.json） ====================

/// <summary>一个设置项的界面描述（宿主据此渲染通用设置行，插件不写任何 UI 代码）。</summary>
internal sealed class SettingSchema
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    /// <summary>bool | int | number | text | choice</summary>
    public string Type { get; set; } = "text";
    public JsonElement? Default { get; set; }
    public double? Min { get; set; }
    public double? Max { get; set; }
    public List<string>? Options { get; set; }
    public string Note { get; set; } = "";
    /// <summary>always = 设置窗默认可见；dev = 收进「开发者选项」。</summary>
    public string Show { get; set; } = "always";
}

/// <summary>plugins/&lt;文件夹&gt;/plugin.json 的结构。</summary>
/// <remarks>
/// 键名是小写（plugin.json 是给人手改的，camelCase 更自然），
/// 所以反序列化必须开 PropertyNameCaseInsensitive —— 默认大小写敏感，
/// 不开的话所有键都映射不上，插件会被静默跳过（2026-10-23 实测踩到）。
/// </remarks>
internal sealed class PluginManifest
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>入口类型 "命名空间.类型名, 程序集名"；缺省则自动找第一个 ILolPlugin 实现。</summary>
    public string Entry { get; set; } = "";
    /// <summary>插件 dll 文件名；缺省用 &lt;Id&gt;.dll。</summary>
    public string Assembly { get; set; } = "";
    /// <summary>卡片排序，越小越靠上。</summary>
    public int Order { get; set; } = 100;
    /// <summary>缺省 true —— 清单里不写 enabled 就是启用，反过来太容易坑人。</summary>
    public bool Enabled { get; set; } = true;
    public string Description { get; set; } = "";
    public List<SettingSchema> Settings { get; set; } = new();
}

/// <summary>plugin.json 的反序列化选项：大小写不敏感 + 允许 // 注释和尾随逗号。</summary>
internal static class ManifestJson
{
    public static readonly JsonSerializerOptions Opts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };
}

internal sealed class LoadedPlugin
{
    public PluginManifest Manifest = null!;
    public ILolPlugin Instance = null!;
    public PluginSettingsStore Store = null!;
    public string Folder = "";
}

/// <summary>给宿主设置窗用的「一个插件的设置分区」。</summary>
internal sealed class PluginSettingsSection
{
    public string PluginId = "";
    public string PluginName = "";
    public IPluginSettings Store = null!;
    public List<SettingSchema> Schemas = new();
}

// ==================== 设置存储 ====================

/// <summary>
/// 插件私有设置：plugins/&lt;id&gt;/settings.json。
/// 用 JsonElement 存值，Get&lt;T&gt; 时再反序列化 —— 插件存什么类型自己说了算，
/// 存错了类型也不会崩，退回 fallback。
/// </summary>
internal sealed class PluginSettingsStore : IPluginSettings
{
    private readonly string _path;
    private readonly List<SettingSchema> _schema;
    private Dictionary<string, JsonElement> _vals = new(StringComparer.Ordinal);

    /// <summary>本次启动时 settings.json 是否本来不存在（插件用它做一次性迁移）。</summary>
    public bool WasFresh { get; }

    public PluginSettingsStore(string path, List<SettingSchema> schema)
    {
        _path = path;
        _schema = schema;
        bool existed = File.Exists(_path);
        WasFresh = !existed;
        if (existed)
        {
            try
            {
                _vals = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(_path))
                        ?? new(StringComparer.Ordinal);
            }
            catch { /* 文件坏了就当没有，下面用默认值重建 */ }
        }
        // 缺键补默认值（不覆盖用户已存的值）
        bool dirty = false;
        foreach (var s in _schema)
        {
            if (s.Default is null) continue;
            if (!_vals.ContainsKey(s.Key)) { _vals[s.Key] = s.Default.Value; dirty = true; }
        }
        if (dirty) Save();
    }

    public T Get<T>(string key, T fallback)
    {
        if (_vals.TryGetValue(key, out var e))
        {
            try
            {
                var v = e.Deserialize<T>();
                if (v is not null) return v;
            }
            catch { /* 类型不匹配就退回 fallback */ }
        }
        return fallback;
    }

    public void Set<T>(string key, T value) =>
        _vals[key] = JsonSerializer.SerializeToElement(value);

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path,
                JsonSerializer.Serialize(_vals, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* 写不进去不影响使用 */ }
    }
}

// ==================== 程序集加载 ====================

/// <summary>
/// 插件程序集加载上下文。
/// 两条规则：① 默认上下文已加载的（Contracts、BCL）一律复用，保证类型同一性；
/// ② 否则在插件自己的目录里找依赖 dll。找不到返回 null，让错误暴露成 TypeLoadException
/// —— 由 PluginManager 捕获后记日志，不会带崩宿主。
/// </summary>
internal sealed class PluginLoadContext : AssemblyLoadContext
{
    private readonly string _dir;

    public PluginLoadContext(string dllPath)
        : base("plugin:" + Path.GetFileNameWithoutExtension(dllPath))
    {
        _dir = Path.GetDirectoryName(Path.GetFullPath(dllPath))!;
    }

    protected override Assembly? Load(AssemblyName name)
    {
        foreach (var a in AssemblyLoadContext.Default.Assemblies)
        {
            if (string.Equals(a.GetName().Name, name.Name, StringComparison.OrdinalIgnoreCase))
                return a;
        }
        var p = Path.Combine(_dir, name.Name + ".dll");
        return File.Exists(p) ? LoadFromAssemblyPath(p) : null;
    }
}

// ==================== 卡片适配器 ====================

/// <summary>
/// 把宿主自绘的 ActionCard 包成契约里的 ICard。
/// 全部方法内部都 marshalling 回 UI 线程（插件可以从后台线程直接调用）。
/// </summary>
internal sealed class CardAdapter : ICard
{
    private readonly ActionCard _card;
    private bool _restoring;

    /// <summary>卡片排序号，来自 plugin.json 的 order。</summary>
    public int Order { get; }
    /// <summary>注册这张卡的插件 id / 名称：右键菜单靠它找说明文件和设置分区。</summary>
    public string PluginId { get; }
    public string PluginName { get; }

    public CardAdapter(ActionCard card, int order, string pluginId = "", string pluginName = "")
    {
        _card = card;
        Order = order;
        PluginId = pluginId;
        PluginName = pluginName;
    }

    public string Title => _card.CardTitle;
    public bool AutoOn => _card.AutoOn;

    /// <summary>背后的自绘控件，宿主排版（Dock/高度）时用。</summary>
    public ActionCard Control => _card;

    public event Action? Clicked;
    public event Action<bool>? AutoToggled;

    /// <summary>长按开始拖动（宿主在这里记下「拖的是哪张卡」）。</summary>
    public event EventHandler? DragStarted;
    /// <summary>拖动中，参数是鼠标的屏幕坐标。</summary>
    public event EventHandler<Point>? DragMoved;
    /// <summary>松手放下。</summary>
    public event EventHandler? DragDropped;

    /// <summary>由宿主在创建卡片后调用：把控件事件接到契约事件上。</summary>
    public void Wire()
    {
        _card.Click += (_, _) => Clicked?.Invoke();
        _card.AutoToggled += (_, _) => AutoToggled?.Invoke(_card.AutoOn);
        _card.DragStarted += (_, _) => DragStarted?.Invoke(this, EventArgs.Empty);
        _card.DragMoved += (_, pt) => DragMoved?.Invoke(this, pt);
        _card.DragDropped += (_, _) => DragDropped?.Invoke(this, EventArgs.Empty);
    }

    private void Ui(Action a)
    {
        try
        {
            if (_card.IsDisposed) return;
            if (_card.InvokeRequired) _card.BeginInvoke(a);
            else a();
        }
        catch { /* 窗口正在关闭 */ }
    }

    public void SetAuto(bool on, string note = "") => Ui(() => _card.SetAuto(on, note));
    public void SetRunning() => Ui(() => _card.SetState(CardState.Running));
    public void SetResult(bool ok, string text) =>
        Ui(() => _card.SetState(ok ? CardState.Done : CardState.Fail, text));

    /// <summary>结果展示 2.2 秒后自动还原（原来宿主里 RestoreLater 的逻辑，搬进适配器）。</summary>
    public void Restore()
    {
        if (_restoring) return;
        _restoring = true;
        _ = Task.Run(async () =>
        {
            await Task.Delay(2200);
            Ui(() =>
            {
                _restoring = false;
                if (!_card.IsDisposed) _card.SetState(CardState.Idle);
            });
        });
    }
}

// ==================== 插件管理器 ====================

/// <summary>
/// 插件总管：扫描 plugins/ 目录 → 读清单 → 加载 → Init/Start/Stop，
/// 并把宿主的阶段变化 / 周期 tick / WS 事件扇出给所有插件。
/// 单个插件出任何问题都只记日志，绝不影响宿主和其他插件。
/// </summary>
internal sealed class PluginManager
{
    private readonly IPluginHost _host;
    private readonly Action<LogLevel, string, string> _log;
    private readonly List<LoadedPlugin> _plugins = new();
    private readonly List<CardAdapter> _cards = new();
    private readonly List<Action<string>> _phaseHandlers = new();
    private readonly List<Action<ClientSnapshot>> _tickHandlers = new();
    private readonly List<(string Uri, Action<PluginEvent> Handler)> _eventHandlers = new();

    public PluginManager(IPluginHost host, Action<LogLevel, string, string> log)
    {
        _host = host;
        _log = log;
    }

    public IReadOnlyList<LoadedPlugin> Plugins => _plugins;
    public IReadOnlyList<CardAdapter> Cards => _cards;

    /// <summary>按卡片顺序导出各插件的设置分区，供宿主设置窗渲染。</summary>
    public List<PluginSettingsSection> PluginSections() =>
        _plugins
            .OrderBy(p => p.Manifest.Order)
            .Select(p => new PluginSettingsSection
            {
                PluginId = p.Manifest.Id,
                PluginName = p.Manifest.Name,
                Store = p.Store,
                Schemas = p.Manifest.Settings,
            })
            .ToList();

    // ---------- 加载 ----------

    /// <param name="disabled">
    /// 用户在设置里关掉的功能（插件 id）。**关掉 = 根本不加载**：
    /// 不起线程、不订阅事件、也不会有卡片。比「只藏卡片」彻底，代价是要重启才生效。
    /// </param>
    public void LoadAll(string pluginsRoot, IReadOnlyCollection<string>? disabled = null)
    {
        if (!Directory.Exists(pluginsRoot))
        {
            _log(LogLevel.Warn, "插件", "plugins 目录不存在，功能插件全部不可用");
            return;
        }

        var off = new HashSet<string>(disabled ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        PreloadSharedRuntime(pluginsRoot);

        foreach (var dir in Directory.GetDirectories(pluginsRoot).OrderBy(d => d, StringComparer.Ordinal))
        {
            var mfPath = Path.Combine(dir, "plugin.json");
            if (!File.Exists(mfPath)) continue;

            PluginManifest? m = null;
            try
            {
                m = JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(mfPath), ManifestJson.Opts);
            }
            catch (Exception ex)
            {
                _log(LogLevel.Error, "插件", $"{Path.GetFileName(dir)} 清单读不出：{ex.Message}");
                continue;
            }
            if (m is null || string.IsNullOrEmpty(m.Id)) continue;
            if (!m.Enabled)
            {
                _log(LogLevel.Info, "插件", $"{m.Name} 已在清单里禁用（enabled=false）");
                continue;
            }
            if (off.Contains(m.Id))
            {
                _log(LogLevel.Info, "插件", $"{m.Name} 已在设置里关掉，本次不加载");
                continue;
            }

            try
            {
                LoadOne(dir, m);
            }
            catch (Exception ex)
            {
                // 插件炸了只记日志：宿主和其他插件照常工作
                _log(LogLevel.Error, "插件", $"{m.Name} 加载失败：{ex.Message}");
            }
        }

        _log(LogLevel.Info, "插件", $"已加载 {_plugins.Count} 个功能插件");
    }

    /// <summary>
    /// 预载插件们共享的 WinRT 运行时（Microsoft.Windows.SDK.NET.dll / WinRT.Runtime.dll）。
    ///
    /// 为什么必须有：多个插件都可能带这两个 dll（各自目录一份）。它们内部注册
    /// 进程级全局单例（ComWrappers），**加载两份时第二份初始化会直接抛
    /// 「Attempt to update previously set global instance」**（2026-10-25 实测：
    /// 「看视频（手动）」先加载没事，deathwatch 一调 OCR 就炸）。
    /// 处理思路和 Contracts 一样：先找一份进默认上下文，各插件 ALC 的解析规则
    /// 看到默认上下文里已经有了就复用那一份，永远只有一份真正初始化。
    /// </summary>
    private void PreloadSharedRuntime(string pluginsRoot)
    {
        string[] shared = { "Microsoft.Windows.SDK.NET.dll", "WinRT.Runtime.dll" };
        foreach (var name in shared)
        {
            var shortName = Path.GetFileNameWithoutExtension(name);
            if (AssemblyLoadContext.Default.Assemblies.Any(a =>
                    string.Equals(a.GetName().Name, shortName, StringComparison.OrdinalIgnoreCase)))
                continue;

            try
            {
                var found = Directory.GetFiles(pluginsRoot, name, SearchOption.AllDirectories)
                                     .FirstOrDefault();
                if (found is null) continue;
                Assembly.LoadFrom(found);
                _log(LogLevel.Info, "插件", $"共享运行时 {name} 已预载（默认上下文）");
            }
            catch (Exception ex)
            {
                _log(LogLevel.Warn, "插件", $"共享运行时 {name} 预载失败：{ex.Message}");
            }
        }
    }

    private void LoadOne(string dir, PluginManifest m)
    {
        var dll = Path.Combine(dir, string.IsNullOrEmpty(m.Assembly) ? m.Id + ".dll" : m.Assembly);
        if (!File.Exists(dll))
            throw new FileNotFoundException($"找不到插件程序集 {Path.GetFileName(dll)}");

        var alc = new PluginLoadContext(dll);
        var asm = alc.LoadFromAssemblyPath(dll);

        Type? type = null;
        if (!string.IsNullOrEmpty(m.Entry))
        {
            // entry 形如 "AcceptPlugin.AcceptPlugin, AcceptPlugin"（类型, 程序集）
            var typeName = m.Entry.Split(',')[0].Trim();
            type = asm.GetType(typeName, throwOnError: false);
        }
        type ??= asm.GetTypes().FirstOrDefault(t =>
            typeof(ILolPlugin).IsAssignableFrom(t) && t is { IsAbstract: false, IsInterface: false });

        if (type is null)
            throw new InvalidOperationException("找不到 ILolPlugin 实现（检查 plugin.json 的 entry）");

        var inst = Activator.CreateInstance(type) as ILolPlugin
            ?? throw new InvalidOperationException("插件类型无法实例化（需要 public 无参构造）");

        var store = new PluginSettingsStore(Path.Combine(dir, "settings.json"), m.Settings);
        var loaded = new LoadedPlugin { Manifest = m, Instance = inst, Store = store, Folder = dir };

        // 先挂 manager，插件 Init 里 RegisterCard 才找得到地方放
        _plugins.Add(loaded);
        // 注意递给插件的是 PluginInitContext（只暴露契约面），不是宿主本体
        inst.Init(new PluginInitContext(this, m, _host), store);

        var extra = string.IsNullOrEmpty(m.Description) ? "" : $"（{m.Description}）";
        _log(LogLevel.Info, "插件", $"{m.Name} 就绪{extra}");
    }

    // ---------- 卡片注册（由 IPluginHost.RegisterCard 调用） ----------

    internal CardAdapter? RegisterCard(string title, string desc, bool showAuto, int order,
                                       string pluginId, string pluginName)
    {
        var card = new ActionCard
        {
            CardTitle = title,
            CardDesc = desc,
            Accent = T.BarGold,
            ShowAuto = showAuto,
            Dock = DockStyle.Top,
            Height = T.CardH,
            Margin = new Padding(0, 0, 0, 4),
        };
        var adapter = new CardAdapter(card, order, pluginId, pluginName);
        adapter.Wire();
        _cards.Add(adapter);
        return adapter;
    }

    /// <summary>插件目录（右键「查看说明」从这里读 说明.md）。</summary>
    public string? PluginFolder(string pluginId) =>
        _plugins.FirstOrDefault(p => p.Manifest.Id == pluginId)?.Folder;

    /// <summary>
    /// 按接口找已加载插件提供的服务（插件间协作的唯一通道，见契约里的 IVideoSwitcher）。
    /// 多个插件都实现了就取加载顺序里第一个；没有返回 null。
    /// </summary>
    public T? FindService<T>() where T : class
    {
        foreach (var p in _plugins)
        {
            if (p.Instance is T svc) return svc;
        }
        return null;
    }

    // ---------- 事件订阅（由 IPluginHost 调用） ----------

    internal void AddPhaseHandler(Action<string> h) => _phaseHandlers.Add(h);
    internal void AddTickHandler(Action<ClientSnapshot> h) => _tickHandlers.Add(h);
    internal void AddEventHandler(string uri, Action<PluginEvent> h) =>
        _eventHandlers.Add((uri, h));

    // ---------- 扇出 ----------

    public void StartAll()
    {
        foreach (var p in _plugins)
        {
            try { p.Instance.Start(); }
            catch (Exception ex) { _log(LogLevel.Error, "插件", $"{p.Manifest.Name} 启动失败：{ex.Message}"); }
        }
    }

    public void StopAll()
    {
        foreach (var p in _plugins)
        {
            try { p.Instance.Stop(); }
            catch { /* 退出时不计较 */ }
        }
    }

    /// <summary>设置窗保存后调用：让实现了 ISettingsChangedAware 的插件同步状态。</summary>
    public void NotifySettingsChanged()
    {
        foreach (var p in _plugins)
        {
            if (p.Instance is not ISettingsChangedAware aware) continue;
            try { aware.OnSettingsChanged(); }
            catch (Exception ex) { _log(LogLevel.Error, "插件", $"{p.Manifest.Name} 同步设置失败：{ex.Message}"); }
        }
    }

    public void FirePhase(string phase)
    {
        foreach (var h in _phaseHandlers)
        {
            try { h(phase); }
            catch (Exception ex) { _log(LogLevel.Error, "插件", $"阶段回调异常：{ex.Message}"); }
        }
    }

    public void FireTick(ClientSnapshot snap)
    {
        foreach (var h in _tickHandlers)
        {
            try { h(snap); }
            catch (Exception ex) { _log(LogLevel.Error, "插件", $"tick 回调异常：{ex.Message}"); }
        }
    }

    public void FireEvent(PluginEvent ev)
    {
        foreach (var (uri, h) in _eventHandlers)
        {
            if (!string.Equals(uri, ev.Uri, StringComparison.OrdinalIgnoreCase)) continue;
            try { h(ev); }
            catch (Exception ex) { _log(LogLevel.Error, "插件", $"事件回调异常（{ev.Uri}）：{ex.Message}"); }
        }
    }
}

/// <summary>
/// Init 时递给插件的宿主上下文。之所以不把 MainForm 直接给插件：
/// 插件只能看到契约接口，看不到宿主的任何内部类型。
/// </summary>
internal sealed class PluginInitContext : IPluginHost
{
    private readonly PluginManager _mgr;
    private readonly PluginManifest _manifest;
    private readonly IPluginHost _inner;

    public PluginInitContext(PluginManager mgr, PluginManifest manifest, IPluginHost inner)
    {
        _mgr = mgr;
        _manifest = manifest;
        _inner = inner;
    }

    public string AppDirectory => _inner.AppDirectory;
    public bool IsSelfTest => _inner.IsSelfTest;
    public ClientSnapshot? LastSnapshot => _inner.LastSnapshot;
    public string CurrentPhase => _inner.CurrentPhase;
    public bool ClientRunning => _inner.ClientRunning;

    public void Log(LogLevel level, string message, string note = "") => _inner.Log(level, message, note);
    public void SetStatus(string text, bool warn = false) => _inner.SetStatus(text, warn);

    public ICard? RegisterCard(string title, string desc, bool showAuto) =>
        _mgr.RegisterCard(title, desc, showAuto, _manifest.Order, _manifest.Id, _manifest.Name);

    public void OnPhaseChanged(Action<string> handler) => _mgr.AddPhaseHandler(handler);
    public void OnTick(Action<ClientSnapshot> handler) => _mgr.AddTickHandler(handler);
    public void Subscribe(string uri, Action<PluginEvent> handler) => _mgr.AddEventHandler(uri, handler);

    public Task<(int Status, string Body)> GetAsync(string path) => _inner.GetAsync(path);
    public Task<bool> PostAsync(string path, string body = "{}") => _inner.PostAsync(path, body);
    public Task<(bool Ok, int Status, string Body)> PostExAsync(string path, string body = "{}") =>
        _inner.PostExAsync(path, body);
    public bool EnsureClient() => _inner.EnsureClient();
    public void ShowAuxWindow(IntPtr hwnd, int width, int height) =>
        _inner.ShowAuxWindow(hwnd, width, height);
    public T GetLegacy<T>(string key, T fallback) => _inner.GetLegacy<T>(key, fallback);

    /// <summary>插件间服务发现：从已加载的插件里按接口找（Manager 持有全部插件）。</summary>
    public T? GetService<T>() where T : class => _mgr.FindService<T>();
}
