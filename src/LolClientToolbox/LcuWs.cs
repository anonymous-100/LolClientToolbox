using System;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace LolClientToolbox;

/// <summary>一次 WebSocket 推送事件。</summary>
internal sealed class LcuEvent
{
    /// <summary>事件名，形如 OnJsonApiEvent_lol-gameflow_v1_gameflow-phase。</summary>
    public string Topic = "";
    /// <summary>实际路径，形如 /lol-gameflow/v1/gameflow-phase。</summary>
    public string Uri = "";
    public string EventType = "";
    /// <summary>负载的 JSON 原文。</summary>
    public string DataJson = "";
    public DateTime At = DateTime.Now;
}

/// <summary>
/// LCU 的 WAMP over WSS 订阅客户端。
///
/// 握手实测：wss://127.0.0.1:&lt;port&gt;/，子协议 wamp，Authorization: Basic riot:&lt;token&gt;
///   → 返回 101 Switching Protocols（本机已验证）。
/// 订阅发送 [5, "OnJsonApiEvent"]；事件回来形如 [8, "&lt;topic&gt;", {"uri":…,"eventType":…,"data":…}]。
///
/// 设计原则：**订阅只做加速，不做唯一依赖**。
/// 连不上、收不到、断线都会自动重连；上层始终保留一条慢速 HTTP 轮询兜底，
/// 所以就算订阅这条路在某台机器上不通，程序也不会退化成瞎的。
/// </summary>
internal sealed class LcuWatcher : IDisposable
{
    private readonly Func<(string port, string pwd)> _credProvider;
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private int _disposed;

    public LcuWatcher(Func<(string port, string pwd)> credProvider)
    {
        _credProvider = credProvider;
    }

    /// <summary>收到事件（订阅名由调用方决定，这里订阅的是全部事件 OnJsonApiEvent）。</summary>
    public event Action<LcuEvent>? EventReceived;

    /// <summary>连接状态变化：是否已连上 + 一句说明。</summary>
    public event Action<bool, string>? StateChanged;

    public bool Connected { get; private set; }
    public string Note { get; private set; } = "未启动";
    public int EventCount { get; private set; }
    public DateTime? LastEventAt { get; private set; }

    public void Start()
    {
        if (_loop is not null) return;
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { /* 忽略 */ }
    }

    private void SetState(bool connected, string note)
    {
        if (Connected == connected && Note == note) return;
        Connected = connected;
        Note = note;
        StateChanged?.Invoke(connected, note);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        int backoff = 2;
        while (!ct.IsCancellationRequested)
        {
            var (port, pwd) = _credProvider();
            if (string.IsNullOrEmpty(port))
            {
                SetState(false, "客户端未运行");
                await Delay(backoff = Math.Min(backoff * 2, 15), ct);
                continue;
            }

            try
            {
                await SessionAsync(port, pwd, ct);
                backoff = 2;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                SetState(false, "连接失败：" + ex.Message);
            }

            if (ct.IsCancellationRequested) return;
            SetState(false, $"{backoff} 秒后重连");
            await Delay(backoff = Math.Min(backoff * 2, 30), ct);
        }
    }

    private async Task SessionAsync(string port, string pwd, CancellationToken ct)
    {
        var ws = new ClientWebSocket();
        ws.Options.AddSubProtocol("wamp");
        // LCU 自签证书，一律放行
        ws.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        ws.Options.Proxy = null;
        ws.Options.SetRequestHeader(
            "Authorization",
            "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"riot:{pwd}")));
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);

        _ws = ws;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(TimeSpan.FromSeconds(12));
        await ws.ConnectAsync(new Uri($"wss://127.0.0.1:{port}/"), linked.Token);

        SetState(true, "已订阅（等待事件）");

        // 订阅全部事件；上层按 uri 过滤自己关心的
        await SendAsync(ws, "[5,\"OnJsonApiEvent\"]", ct);

        var buf = new byte[64 * 1024];
        while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            var sb = new StringBuilder();
            WebSocketReceiveResult r;
            do
            {
                r = await ws.ReceiveAsync(new ArraySegment<byte>(buf), ct);
                if (r.MessageType == WebSocketMessageType.Close)
                {
                    SetState(false, "服务端关闭连接");
                    return;
                }
                sb.Append(Encoding.UTF8.GetString(buf, 0, r.Count));
            } while (!r.EndOfMessage && sb.Length < 512 * 1024);

            var ev = Parse(sb.ToString());
            if (ev is not null)
            {
                EventCount++;
                LastEventAt = ev.At;
                EventReceived?.Invoke(ev);
            }
        }

        if (ws.State != WebSocketState.Open) SetState(false, "连接已断开");
    }

    private static async Task SendAsync(ClientWebSocket ws, string text, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
    }

    /// <summary>解析 [8, topic, {uri,eventType,data}]，不是这个形态就返回 null。</summary>
    private static LcuEvent? Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text[0] != '[') return null;
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() < 3) return null;
            if (root[0].GetInt32() != 8) return null;

            var ev = new LcuEvent { Topic = root[1].GetString() ?? "" };
            var payload = root[2];
            if (payload.ValueKind == JsonValueKind.Object)
            {
                if (payload.TryGetProperty("uri", out var u) && u.ValueKind == JsonValueKind.String)
                    ev.Uri = u.GetString() ?? "";
                if (payload.TryGetProperty("eventType", out var t) && t.ValueKind == JsonValueKind.String)
                    ev.EventType = t.GetString() ?? "";
                if (payload.TryGetProperty("data", out var d))
                    ev.DataJson = d.GetRawText();
            }
            return ev.Uri.Length == 0 && ev.DataJson.Length == 0 ? null : ev;
        }
        catch
        {
            return null;
        }
    }

    private static async Task Delay(int seconds, CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(seconds), ct); }
        catch (OperationCanceledException) { /* 退出 */ }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Stop();
        try { _ws?.Dispose(); } catch { /* 忽略 */ }
        _cts?.Dispose();
    }
}
