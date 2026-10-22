using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace LolClientToolbox;

/// <summary>客户端实时状态的一次快照。</summary>
internal sealed class LcuSnapshot
{
    public bool ClientRunning;
    public string Phase = "";              // lol-gameflow/v1/gameflow-phase 原始值
    public string PhaseText = "";          // 本地化后的可读阶段
    public string EogState = "";           // 结算相关接口的可读结论
    public string EogRaw = "";             // 结算接口原始摘要，用于人工确认
    public string WindowRect = "";         // 主窗口 WxH
    public string Error = "";              // 读取失败原因
    public string Diag = "";               // 诊断串（端口/凭据来源/各端点状态码）
    public DateTime TakenAt;

    public string DisplayPhase => !ClientRunning ? "未运行" : (PhaseText.Length > 0 ? PhaseText : "(空)");

    /// <summary>用于判断是否变化的指纹。</summary>
    public string Fingerprint => $"{ClientRunning}|{Phase}|{EogState}|{WindowRect}|{Error}";
}

/// <summary>
/// 宿主全局可调参数，持久化到 exe 同目录的 config.json。
///
/// 注意：**功能参数不在这里**。自 v23 插件化起，每个功能插件（窗口修复、跳过卡结算、
/// 热重载、接收对局）的参数住在插件自己的 settings.json 里，由插件清单 plugin.json
/// 的 schema 描述、宿主设置窗通用渲染。这里只剩「与具体功能无关」的全局项。
/// </summary>
internal sealed class Settings
{
    // —— 采样 ——
    /// <summary>WebSocket 订阅不可用时的兜底轮询间隔。</summary>
    public int PollIntervalMs { get; set; } = 2000;
    public int PollIntervalSec
    {
        get => PollIntervalMs / 1000;
        set => PollIntervalMs = Math.Clamp(value, 1, 60) * 1000;
    }
    /// <summary>已订阅成功时，把轮询放慢到这个间隔（只用于兜底和窗口检测）。</summary>
    public int SubscribedPollMs { get; set; } = 8000;
    public int SubscribedPollSec
    {
        get => SubscribedPollMs / 1000;
        set => SubscribedPollMs = Math.Clamp(value, 1, 120) * 1000;
    }
    /// <summary>是否启用 WebSocket 订阅（关掉就退回纯轮询）。</summary>
    public bool UseWebSocket { get; set; } = true;

    // —— 接口路径（国服与外服不同，界面上可改）——
    /// <summary>阶段查询路径。实测国服 200 可用。</summary>
    public string PhaseEndpoint { get; set; } = "/lol-gameflow/v1/gameflow-phase";
    /// <summary>结算查询路径。实测国服 /lol-end-of-game/v1/state 不存在(404 Invalid URI format)，
    /// 只有 eog-stats-block 存在，所以默认用它。</summary>
    public string EogEndpoint { get; set; } = "/lol-end-of-game/v1/eog-stats-block";
    /// <summary>对局会话路径。实测国服存在，对局中会有数据。</summary>
    public string SessionEndpoint { get; set; } = "/lol-gameflow/v1/session";

    public int HttpTimeoutMs { get; set; } = 4000;

    /// <summary>手动指定的客户端安装目录，留空则自动查找。</summary>
    public string ClientPathHint { get; set; } = "";

    /// <summary>
    /// 设置窗口是否显示「开发者选项」入口（含 Ctrl+` 快捷键）。
    /// 默认 false：里面全是排障项，日常用户不该看见。
    /// 想用得手动把 config.json 里这项改成 true —— 不留界面后门。
    /// </summary>
    public bool ShowDevOptions { get; set; } = false;

    /// <summary>
    /// 按下动效的力度，0–100。
    /// 100 = 夸张（染色 60% + 内容上浮 3px + 明显投影）；0 = 几乎没有，只剩一点点染色。
    /// 它同时作用于功能按钮和右侧的自动开关。
    /// </summary>
    public int PressStrength { get; set; } = 100;

    /// <summary>JSON 里允许 // 注释和尾随逗号 —— config.json 是给人看的，必须能写注释。</summary>
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static Settings Load(string dir)
    {
        var path = Path.Combine(dir, "config.json");

        // 目录下没有 config.json：首次开启，直接生成一份带注释的默认配置
        if (!File.Exists(path))
        {
            var fresh = new Settings();
            fresh.Save(dir);
            return fresh;
        }

        try
        {
            var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(path), JsonOpts);
            if (s is not null)
            {
                // 升级过版本、config 里缺新字段时补存一次，让用户看到完整的配置项
                var raw = File.ReadAllText(path);
                bool missing = typeof(Settings).GetProperties()
                    .Any(pr => !raw.Contains($"\"{pr.Name}\""));
                if (missing) s.Save(dir);
                return s;
            }
        }
        catch
        {
            /* 配置坏了就用默认值，别因此起不来 */
        }
        return new Settings();
    }

    /// <summary>每个配置项的中文说明，Save 时作为注释写进 config.json。</summary>
    private static readonly Dictionary<string, string> Notes = new()
    {
        ["PollIntervalMs"] = "轮询间隔（毫秒）。订阅断开时的兜底取数节奏",
        ["PollIntervalSec"] = "轮询间隔（秒），界面显示用，实际以毫秒那个为准",
        ["SubscribedPollMs"] = "订阅连通后的轮询间隔（毫秒）。订阅本来就实时，轮询只是保险",
        ["SubscribedPollSec"] = "订阅后轮询（秒），界面显示用",
        ["UseWebSocket"] = "用 WebSocket 订阅客户端事件（比轮询快得多）。关了也能用，只是慢一点",
        ["PhaseEndpoint"] = "游戏阶段接口路径",
        ["EogEndpoint"] = "结算数据接口路径",
        ["SessionEndpoint"] = "对局会话接口路径（里面有 phase 字段，界面大字显示的就是它）",
        ["HttpTimeoutMs"] = "单次接口请求超时（毫秒）",
        ["ClientPathHint"] = "客户端安装目录，留空则自动查找",
        ["PressStrength"] = "按钮按下去的动效力度，0-100",
        ["ShowDevOptions"] = "true = 设置窗口显示「开发者选项」入口（含 Ctrl+` 快捷键）。排障时才开",
    };

    /// <summary>
    /// 存成带注释的 JSON —— config.json 是给用户手改的，没注释等于没写。
    /// （标准 JSON 不支持注释，读取时用 ReadCommentHandling.Skip 兼容。）
    /// </summary>
    public void Save(string dir)
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("{");
            var props = typeof(Settings).GetProperties()
                .Where(pr => pr.CanRead && pr.CanWrite)
                .OrderBy(pr => pr.Name, StringComparer.Ordinal);

            foreach (var pr in props)
            {
                var v = pr.GetValue(this);
                string json = v switch
                {
                    bool b => b ? "true" : "false",
                    string str => JsonSerializer.Serialize(str),
                    _ => JsonSerializer.Serialize(v),
                };
                if (Notes.TryGetValue(pr.Name, out var note))
                    sb.AppendLine($"  // {note}");
                sb.AppendLine($"  \"{pr.Name}\": {json},");
            }

            sb.AppendLine("}");
            File.WriteAllText(Path.Combine(dir, "config.json"), sb.ToString());
        }
        catch
        {
            /* 写不进去也不影响使用 */
        }
    }
}

/// <summary>LCU 本地接口客户端。凭据来自进程命令行（本机客户端不写 lockfile）。</summary>
internal sealed class LcuClient : IDisposable
{
    private readonly HttpClient _http;
    private string? _lockPath;
    private string _port = "";
    private string _pwd = "";
    private DateTime _lastScan = DateTime.MinValue;
    private int _failStreak;

    public Settings Cfg = new();
    public string LockfilePath => _lockPath ?? "";
    public string ClientPathHint = "";

    public LcuClient()
    {
        var handler = new HttpClientHandler
        {
            // LCU 用的是自签证书，一律放行
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
            CheckCertificateRevocationList = false,
            // 本地回环不要走任何代理（这台机器 ProxyServer 指向 127.0.0.1:7890）
            UseProxy = false,
            Proxy = null,
            // LCU 返回的压缩内容偶发解压失败，直接要明文
            AutomaticDecompression = DecompressionMethods.None,
            SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            AllowAutoRedirect = false,
        };
        // 超时用 CTS 控制（HttpClient.Timeout 一旦发过请求就不能改了，而界面上要能调）
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
    }

    /// <summary>凭据来源，用于在界面上说明当前是靠哪种方式连上的。</summary>
    public string CredSource = "";

    /// <summary>上一次请求的诊断信息（端口、状态码、异常），直接显示给使用者看。</summary>
    public string LastDiag = "";

    // ---------------- 凭据 ----------------

    public bool TryAttach()
    {
        // 本机实测：LeagueClient\lockfile 恒为 0 字节，客户端改走进程命令行参数。
        // 先试命令行（最准），失败再退回 lockfile。
        if (TryAttachCommandLine()) return true;
        return TryAttachLockfile() && IsAttached;
    }

    private bool TryAttachCommandLine()
    {
        foreach (var name in new[] { "LeagueClientUx", "LeagueClient" })
        {
            Process[] procs;
            try { procs = Process.GetProcessesByName(name); }
            catch { continue; }

            foreach (var p in procs)
            {
                try
                {
                    var cmd = ProcCommandLine.Read(p.Id);
                    if (string.IsNullOrEmpty(cmd)) { p.Dispose(); continue; }

                    // 注意必须排除 --riotclient-app-port，那个是 Riot Client 自己的端口
                    var mp = Regex.Match(cmd, @"(?<![\w-])--app-port=""?([0-9]+)");
                    var mt = Regex.Match(cmd, @"(?<![\w-])--remoting-auth-token=""?([^\s""]+)");
                    if (mp.Success && mt.Success)
                    {
                        _port = mp.Groups[1].Value;
                        _pwd = mt.Groups[1].Value;
                        CredSource = $"{name}(pid {p.Id}) 命令行";
                        _lockPath = null;
                        p.Dispose();
                        return true;
                    }
                }
                catch
                {
                    /* 忽略单个进程 */
                }
                p.Dispose();
            }
        }
        return false;
    }

    private bool TryAttachLockfile()
    {
        var p = FindLockfile();
        if (p is null) return false;
        try
        {
            var raw = File.ReadAllText(p).Trim();
            if (string.IsNullOrEmpty(raw) || raw.Count(c => c == ':') < 4) return false;
            var parts = raw.Split(':');
            _port = parts[2];
            _pwd = parts[3];
            _lockPath = p;
            CredSource = "lockfile";
            return int.TryParse(_port, out _);
        }
        catch
        {
            return false;
        }
    }

    public bool IsAttached => !string.IsNullOrEmpty(_port);

    /// <summary>给 WebSocket 订阅用的凭据（端口, 口令）。</summary>
    public (string port, string pwd) Credentials => (_port, _pwd);

    private string? FindLockfile()
    {
        var candidates = new List<string>();

        if (!string.IsNullOrWhiteSpace(ClientPathHint))
        {
            candidates.Add(Path.Combine(ClientPathHint, "lockfile"));
            candidates.Add(Path.Combine(ClientPathHint, "LeagueClient", "lockfile"));
        }
        if (_lockPath is not null) candidates.Add(_lockPath);

        foreach (var c in candidates)
        {
            try
            {
                if (File.Exists(c) && new FileInfo(c).Length > 0) return c;
            }
            catch
            {
                /* 忽略 */
            }
        }

        // 限频扫描常见安装目录
        if ((DateTime.Now - _lastScan).TotalSeconds < 30) return null;
        _lastScan = DateTime.Now;

        var roots = new List<string>();
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed))
        {
            roots.Add(Path.Combine(drive.RootDirectory.FullName, "games", "Tencent"));
            roots.Add(Path.Combine(drive.RootDirectory.FullName, "Riot Games"));
            roots.Add(Path.Combine(drive.RootDirectory.FullName, "Program Files", "Tencent"));
        }

        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            try
            {
                // 必须过滤：Riot Client 自己也有一个同名 lockfile，用错了会连到错误的端口
                var hit = Directory.EnumerateFiles(root, "lockfile", SearchOption.AllDirectories)
                    .FirstOrDefault(f =>
                    {
                        try
                        {
                            return new FileInfo(f).Length > 0
                                   && f.Contains("LeagueClient", StringComparison.OrdinalIgnoreCase);
                        }
                        catch { return false; }
                    });
                if (hit is not null) return hit;
            }
            catch
            {
                /* 目录无权访问，跳过 */
            }
        }
        return null;
    }

    // ---------------- 请求 ----------------

    /// <summary>GET 一个路径，返回 (状态码, 正文, 错误信息)。</summary>
    public async Task<(int Status, string Body, string Err)> GetExAsync(string path, CancellationToken ct = default)
    {
        if (!IsAttached && !TryAttach()) return (-1, "", "未连上客户端");

        try
        {
            var req = new HttpRequestMessage(HttpMethod.Get, $"https://127.0.0.1:{_port}{path}");
            req.Headers.Authorization = new AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"riot:{_pwd}")));
            req.Headers.Accept.Clear();
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            req.Version = HttpVersion.Version11;
            req.VersionPolicy = HttpVersionPolicy.RequestVersionOrLower;

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(Math.Max(500, Cfg.HttpTimeoutMs));

            using var resp = await _http.SendAsync(req, linked.Token);
            var body = await resp.Content.ReadAsStringAsync(linked.Token);
            return ((int)resp.StatusCode, body, "");
        }
        catch (TaskCanceledException)
        {
            return (-2, "", "超时");
        }
        catch (Exception ex)
        {
            var inner = ex.InnerException?.Message ?? "";
            return (-3, "", $"{ex.GetType().Name}: {ex.Message} {inner}".Trim());
        }
    }

    public async Task<string?> GetAsync(string path, CancellationToken ct = default)
    {
        var r = await GetExAsync(path, ct);
        return r.Status >= 200 && r.Status < 300 ? r.Body : null;
    }

    public async Task<bool> PostAsync(string path, string body = "{}", CancellationToken ct = default)
    {
        if (!IsAttached && !TryAttach()) return false;
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Post, $"https://127.0.0.1:{_port}{path}")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            req.Headers.Authorization = new AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"riot:{_pwd}")));
            using var resp = await _http.SendAsync(req, ct);
            var txt = await resp.Content.ReadAsStringAsync(ct);
            LastDiag = $"POST {path} => {(int)resp.StatusCode} {txt}";
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            LastDiag = $"POST {path} => 失败 {ex.Message}";
            return false;
        }
    }

    /// <summary>POST 并带回状态码和正文 —— 插件需要区分「404=没数据」和「真失败」时用。</summary>
    public async Task<(bool Ok, int Status, string Body)> PostExAsync(string path, string body = "{}", CancellationToken ct = default)
    {
        if (!IsAttached && !TryAttach()) return (false, -1, "未连上客户端");
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Post, $"https://127.0.0.1:{_port}{path}")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            req.Headers.Authorization = new AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"riot:{_pwd}")));
            using var resp = await _http.SendAsync(req, ct);
            var txt = await resp.Content.ReadAsStringAsync(ct);
            LastDiag = $"POST {path} => {(int)resp.StatusCode} {txt}";
            return (resp.IsSuccessStatusCode, (int)resp.StatusCode, txt);
        }
        catch (Exception ex)
        {
            LastDiag = $"POST {path} => 失败 {ex.Message}";
            return (false, -3, ex.Message);
        }
    }

    /// <summary>采一次完整状态。</summary>
    public async Task<LcuSnapshot> SampleAsync(CancellationToken ct = default)
    {
        var snap = new LcuSnapshot { TakenAt = DateTime.Now };

        if (!IsAttached && !TryAttach())
        {
            snap.ClientRunning = false;
            snap.Error = "找不到客户端进程";
            snap.WindowRect = WindowInfo.GetClientWindowRect();
            snap.Diag = "无凭据";
            return snap;
        }
        snap.ClientRunning = true;

        var diag = new List<string> { $"port={_port}", $"凭据={CredSource}" };

        var (ps, pb, pe) = await GetExAsync(Cfg.PhaseEndpoint, ct);
        diag.Add($"{Short(Cfg.PhaseEndpoint)}={StatusCodeText(ps, pe)}");
        if (ps >= 200 && ps < 300)
        {
            snap.Phase = JsonText(pb);
            snap.PhaseText = LocalizePhase(snap.Phase);
            _failStreak = 0;
        }
        else
        {
            snap.Error = $"阶段接口 {StatusCodeText(ps, pe)}";
            if (++_failStreak >= 3)
            {
                // 客户端多半重启过，端口变了 —— 丢弃旧凭据，下次重新拿
                _port = "";
                _pwd = "";
                _failStreak = 0;
            }
        }

        var (es, eb, ee) = await GetExAsync(Cfg.EogEndpoint, ct);
        diag.Add($"{Short(Cfg.EogEndpoint)}={StatusCodeText(es, ee)}");
        if (es >= 200 && es < 300)
        {
            snap.EogRaw = eb.Length > 240 ? eb[..240] + "…" : eb;
            snap.EogState = "有结算数据";
        }
        else if (es == 404)
        {
            // 国服实测：没有结算数据时就是 404 + "No end of game stats available"
            snap.EogState = "无结算数据";
            snap.EogRaw = eb.Length > 120 ? eb[..120] + "…" : eb;
        }
        else
        {
            snap.EogState = "";
        }

        var (ss, sb, se) = await GetExAsync(Cfg.SessionEndpoint, ct);
        diag.Add($"{Short(Cfg.SessionEndpoint)}={StatusCodeText(ss, se)}");
        if (ss >= 200 && ss < 300 && sb.Length > 4)
        {
            var p = ExtractJsonString(sb, "phase");
            if (!string.IsNullOrEmpty(p)) snap.EogState = $"会话:{LocalizePhase(p)}";
        }

        snap.WindowRect = WindowInfo.GetClientWindowRect();
        snap.Diag = string.Join("  ", diag);
        LastDiag = snap.Diag;
        return snap;
    }

    private static string Short(string p)
    {
        var seg = p.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return seg.Length == 0 ? p : string.Join("/", seg.Skip(Math.Max(0, seg.Length - 2)));
    }

    private static string StatusCodeText(int code, string err)
    {
        if (code == -1) return "未连接";
        if (code == -2) return "超时";
        if (code == -3) return err;
        return code.ToString();
    }

    /// <summary>把可能是 JSON 字符串的负载还原成纯文本（"Lobby" → Lobby）。</summary>
    public static string PlainJsonString(string raw)
    {
        var t = (raw ?? "").Trim();
        if (t.Length >= 2 && t[0] == '"' && t[^1] == '"')
        {
            try
            {
                using var d = JsonDocument.Parse(t);
                if (d.RootElement.ValueKind == JsonValueKind.String) return d.RootElement.GetString() ?? "";
            }
            catch { return t.Trim('"'); }
        }
        return t;
    }

    private static string JsonText(string raw)
    {
        var t = raw.Trim();
        if (t.Length >= 2 && t[0] == '"' && t[^1] == '"')
        {
            try
            {
                using var d = JsonDocument.Parse(t);
                if (d.RootElement.ValueKind == JsonValueKind.String) return d.RootElement.GetString() ?? "";
            }
            catch { return t.Trim('"'); }
        }
        return t;
    }

    private static string ExtractJsonString(string json, string prop)
    {
        try
        {
            using var d = JsonDocument.Parse(json);
            if (d.RootElement.ValueKind == JsonValueKind.Object
                && d.RootElement.TryGetProperty(prop, out var v)
                && v.ValueKind == JsonValueKind.String)
                return v.GetString() ?? "";
        }
        catch { /* 忽略 */ }
        return "";
    }

    /// <summary>把 LCU 的原始阶段值翻译成中文，方便一眼看懂。</summary>
    public static string LocalizePhase(string p) => p switch
    {
        "" or "None" => "大厅/空闲",
        "Lobby" => "房间中",
        "Matchmaking" => "匹配中",
        "ReadyCheck" => "等待接受",
        "ChampSelect" => "选择英雄",
        "GameStart" => "游戏启动中",
        "InProgress" => "对局进行中",
        "Reconnect" => "重连中",
        "WaitingForStats" => "等待战绩",
        "PreEndOfGame" or "PreEndOfGameLobby" => "结算前",
        "EndOfGame" => "结算",
        "TerminatedInError" => "异常结束",
        _ => p,
    };

    public void Dispose() => _http.Dispose();
}

/// <summary>
/// 读另一个进程的命令行。
/// 首选 NtQueryInformationProcess(ProcessCommandLineInformation=60) —— 只要
/// PROCESS_QUERY_LIMITED_INFORMATION，普通权限就能读，实测本机可用。
/// 兜底走读 PEB（需要 PROCESS_VM_READ，得有管理员），与 Fix-LCU-Window 引擎同一套思路。
/// </summary>
internal static class ProcCommandLine
{
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint PROCESS_QUERY_INFORMATION = 0x0400;
    private const uint PROCESS_VM_READ = 0x0010;
    private const int ProcessCommandLineInformation = 60;
    private const int ProcessBasicInformation = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct UNICODE_STRING
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_BASIC_INFORMATION
    {
        public IntPtr Reserved1;
        public IntPtr PebBaseAddress;
        public IntPtr Reserved2;
        public IntPtr Reserved3;
        public UIntPtr UniqueProcessId;
        public IntPtr Reserved4;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(IntPtr h, IntPtr addr, byte[] buf, int size, out int read);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr h);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr h, int infoClass,
        IntPtr buf, int size, out int retLen);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr h, int infoClass,
        out PROCESS_BASIC_INFORMATION pbi, int size, out int retLen);

    public static string? Read(int pid)
    {
        var s = TryClass60(pid);
        if (!string.IsNullOrEmpty(s)) return s;
        return TryPeb(pid);
    }

    private static string? TryClass60(int pid)
    {
        IntPtr h = IntPtr.Zero;
        IntPtr buf = IntPtr.Zero;
        try
        {
            h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return null;

            buf = Marshal.AllocHGlobal(8192);
            int st = NtQueryInformationProcess(h, ProcessCommandLineInformation, buf, 8192, out _);
            if (st != 0) return null;

            var us = Marshal.PtrToStructure<UNICODE_STRING>(buf);
            if (us.Buffer == IntPtr.Zero || us.Length == 0) return null;
            int len = Math.Min((int)us.Length, 8192);
            return Marshal.PtrToStringUni(us.Buffer, len / 2);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);
            if (h != IntPtr.Zero) CloseHandle(h);
        }
    }

    private static string? TryPeb(int pid)
    {
        var h = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            int st = NtQueryInformationProcess(h, ProcessBasicInformation, out var pbi,
                Marshal.SizeOf<PROCESS_BASIC_INFORMATION>(), out _);
            if (st != 0 || pbi.PebBaseAddress == IntPtr.Zero) return null;

            if (!ReadIntPtr(h, pbi.PebBaseAddress + 0x20, out var paramPtr) || paramPtr == IntPtr.Zero)
                return null;

            var us = new byte[16];
            if (!ReadProcessMemory(h, paramPtr + 0x70, us, 16, out _)) return null;
            int len = BitConverter.ToUInt16(us, 0);
            var bufPtr = new IntPtr(BitConverter.ToInt64(us, 8));
            if (len <= 0 || len > 32768 || bufPtr == IntPtr.Zero) return null;

            var raw = new byte[len];
            if (!ReadProcessMemory(h, bufPtr, raw, len, out _)) return null;
            return Encoding.Unicode.GetString(raw);
        }
        catch
        {
            return null;
        }
        finally
        {
            CloseHandle(h);
        }
    }

    private static bool ReadIntPtr(IntPtr h, IntPtr addr, out IntPtr value)
    {
        value = IntPtr.Zero;
        var buf = new byte[8];
        if (!ReadProcessMemory(h, addr, buf, 8, out _)) return false;
        value = new IntPtr(BitConverter.ToInt64(buf, 0));
        return true;
    }
}

/// <summary>读取 LOL 客户端主窗口的实际尺寸（物理像素）。</summary>
internal static class WindowInfo
{
    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    // 与 Fix-LCU-Window 引擎保持一致：客户端主窗口类名 RCLIENT、标题 League of Legends
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    public static (int W, int H, int X, int Y, bool Minimized, bool Found) GetRaw()
    {
        try
        {
            // 首选：按类名/标题找（与引擎同一套口径）
            var h = FindWindow("RCLIENT", "League of Legends");
            var m = Measure(h);
            if (m.Found) return m;

            // 兜底：进程主窗口句柄
            foreach (var name in new[] { "LeagueClientUx", "LeagueClient" })
            {
                var procs = Process.GetProcessesByName(name);
                foreach (var p in procs)
                {
                    var ph = p.MainWindowHandle;
                    p.Dispose();
                    var mm = Measure(ph);
                    if (mm.Found) return mm;
                }
            }
        }
        catch
        {
            /* 忽略 */
        }
        return (0, 0, 0, 0, false, false);
    }

    private static (int W, int H, int X, int Y, bool Minimized, bool Found) Measure(IntPtr h)
    {
        if (h == IntPtr.Zero) return (0, 0, 0, 0, false, false);
        var minimized = IsIconic(h);
        if (!GetWindowRect(h, out var r)) return (0, 0, 0, 0, false, false);
        int w = r.Right - r.Left, hh = r.Bottom - r.Top;
        if (w <= 0 || hh <= 0) return (0, 0, 0, 0, false, false);
        return (w, hh, r.Left, r.Top, minimized, true);
    }

    public static string GetClientWindowRect()
    {
        var r = GetRaw();
        if (!r.Found) return "无窗口";
        return r.Minimized ? $"{r.W}×{r.H} (最小化)" : $"{r.W}×{r.H}";
    }
}
