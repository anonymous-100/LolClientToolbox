using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Probe;

/// <summary>
/// 连 LCU（客户端本地接口）拿凭据、拉接口清单。
///
/// 存在的理由：写 API 手册 / 挖新功能时，最权威的端点列表是客户端自己给的
/// /help，而不是网上抄的（版本一变就对不上）。国服客户端不写 lockfile
/// （实测恒 0 字节），凭据只在进程命令行里，所以照搬宿主 Lcu.cs 的读法。
///
/// 用法：
///   dotnet run --project tools/probe -- cred    输出端口和口令
///   dotnet run --project tools/probe -- api     输出全部端点（按前缀分组）
/// </summary>
internal static class Lcu
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

    private static string? ReadCommandLine(int pid)
    {
        IntPtr h = IntPtr.Zero, buf = IntPtr.Zero;
        try
        {
            h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return null;

            buf = Marshal.AllocHGlobal(8192);
            int st = NtQueryInformationProcess(h, ProcessCommandLineInformation, buf, 8192, out _);
            if (st == 0)
            {
                var us = Marshal.PtrToStructure<UNICODE_STRING>(buf);
                if (us.Buffer != IntPtr.Zero && us.Length > 0)
                {
                    int len = Math.Min((int)us.Length, 8192);
                    var bytes = new byte[len];
                    if (ReadProcessMemory(h, us.Buffer, bytes, len, out _))
                        return System.Text.Encoding.Unicode.GetString(bytes);
                }
            }
            return null;
        }
        catch { return null; }
        finally
        {
            if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);
            if (h != IntPtr.Zero) CloseHandle(h);
        }
    }

    /// <summary>命令行拿不到时退回 PEB（需要更高权限，通常失败）。</summary>
    private static string? ReadCommandLinePeb(int pid)
    {
        var h = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            int st = NtQueryInformationProcess(h, ProcessBasicInformation, out var pbi,
                Marshal.SizeOf<PROCESS_BASIC_INFORMATION>(), out _);
            if (st != 0 || pbi.PebBaseAddress == IntPtr.Zero) return null;
            return null;   // 拿不到就算了，凭据主要靠 class 60
        }
        catch { return null; }
        finally { CloseHandle(h); }
    }

    /// <summary>返回 (端口, 口令, 来源说明)；客户端没开返回 null。</summary>
    public static (string Port, string Pwd, string Source)? Attach()
    {
        // 先 LeagueClientUx 再 LeagueClient，和宿主一致
        foreach (var name in new[] { "LeagueClientUx", "LeagueClient" })
        {
            Process[] procs;
            try { procs = Process.GetProcessesByName(name); }
            catch { continue; }

            foreach (var p in procs)
            {
                try
                {
                    var cmd = ReadCommandLine(p.Id) ?? ReadCommandLinePeb(p.Id);
                    if (string.IsNullOrEmpty(cmd)) { p.Dispose(); continue; }

                    // 必须排除 --riotclient-app-port，那是 Riot Client 自己的端口
                    var mp = Regex.Match(cmd, @"(?<![\w-])--app-port=""?([0-9]+)");
                    var mt = Regex.Match(cmd, @"(?<![\w-])--remoting-auth-token=""?([^\s""]+)");
                    if (mp.Success && mt.Success)
                    {
                        var src = $"{name}(pid {p.Id}) 命令行";
                        p.Dispose();
                        return (mp.Groups[1].Value, mt.Groups[1].Value, src);
                    }
                }
                catch { /* 单个进程失败就跳过 */ }
                p.Dispose();
            }
        }
        return null;
    }

    /// <summary>带 basic auth 的 LCU 客户端（自签证书必须忽略）。</summary>
    public static HttpClient Client(string port, string pwd)
    {
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
        };
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        var raw = System.Text.Encoding.ASCII.GetBytes("riot:" + pwd);
        http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", Convert.ToBase64String(raw));
        http.BaseAddress = new Uri($"https://127.0.0.1:{port}");
        return http;
    }

    public static async Task<(int Status, string Body)> GetAsync(HttpClient http, string path)
    {
        try
        {
            var r = await http.GetAsync(path);
            return ((int)r.StatusCode, await r.Content.ReadAsStringAsync());
        }
        catch (Exception ex) { return (-1, ex.Message); }
    }
}
