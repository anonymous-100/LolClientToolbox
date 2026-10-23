using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WatchVideoPlugin;

/// <summary>
/// 按进程静音 / 取消静音（Windows Core Audio 的会话级音量）。
///
/// 为什么只静音「League of Legends.exe」而不是整个客户端：
/// 游戏语音（队伍语音）在客户端进程里，静音客户端会把队友声音一起掐掉。
/// 会话 API 正好按进程区分，只动游戏那个会话即可。
///
/// 全部操作只读/写**音量会话**，不碰游戏进程本身。
/// </summary>
internal static class AudioMute
{
    private const int DEVICE_STATE_ACTIVE = 0x00000001;
    private const int E_RENDER = 0, E_CONSOLE = 0;
    private const int CLSCTX_ALL = 23;

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int Item(int index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
                                   [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        [PreserveSig] int OpenPropertyStore(int access, out IntPtr properties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out int state);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        [PreserveSig] int GetAudioSessionControl(ref Guid audioSessionGuid, int streamFlags, out IntPtr sessionControl);
        [PreserveSig] int GetSimpleAudioVolume(ref Guid audioSessionGuid, int streamFlags, out ISimpleAudioVolume audioVolume);
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessionEnum);
        [PreserveSig] int RegisterSessionNotification(IntPtr sessionNotification);
        [PreserveSig] int UnregisterSessionNotification(IntPtr sessionNotification);
        [PreserveSig] int RegisterDuckNotification(string sessionId, IntPtr duckNotification);
        [PreserveSig] int UnregisterDuckNotification(IntPtr duckNotification);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int sessionCount);
        // 直接要 Control2：运行时会替我们 QueryInterface（实测对象都实现了它）
        [PreserveSig] int GetSession(int sessionCount, out IAudioSessionControl2 session);
    }

    // IAudioSessionControl2 继承 IAudioSessionControl，vtable 必须按顺序全部声明
    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2
    {
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, ref Guid eventContext);
        [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
        [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid eventContext);
        [PreserveSig] int GetGroupingParam(out Guid groupingParam);
        [PreserveSig] int SetGroupingParam(ref Guid groupingParam, ref Guid eventContext);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr client);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr client);
        [PreserveSig] int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string ret);
        [PreserveSig] int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string ret);
        [PreserveSig] int GetProcessId(out uint pid);
        [PreserveSig] int IsSystemSoundsSession();
        [PreserveSig] int SetDuckingPreference(bool optOut);
    }

    [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISimpleAudioVolume
    {
        [PreserveSig] int SetMasterVolume(float level, ref Guid eventContext);
        [PreserveSig] int GetMasterVolume(out float level);
        [PreserveSig] int SetMute(bool mute, ref Guid eventContext);
        [PreserveSig] int GetMute(out bool mute);
    }

    /// <summary>
    /// 把指定进程名的音频会话静音 / 取消静音。返回被改动的会话数（0 = 没找到该进程的会话）。
    /// 只处理默认播放设备；多声卡的情况会漏，但游戏一般就用主声卡。
    /// </summary>
    public static int SetMuteByProcessName(string exeNameWithoutExt, bool mute)
    {
        int changed = 0;
        object? enumeratorObj = null;
        try
        {
            enumeratorObj = new MMDeviceEnumeratorComObject();
            var enumerator = (IMMDeviceEnumerator)enumeratorObj;

            if (enumerator.GetDefaultAudioEndpoint(E_RENDER, E_CONSOLE, out var device) != 0) return 0;

            var iid = typeof(IAudioSessionManager2).GUID;
            if (device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out var mgrObj) != 0) return 0;
            var mgr = (IAudioSessionManager2)mgrObj;

            if (mgr.GetSessionEnumerator(out var sessions) != 0) return 0;
            sessions.GetCount(out int count);

            var ev = Guid.Empty;
            for (int i = 0; i < count; i++)
            {
                if (sessions.GetSession(i, out var ctl) != 0 || ctl is null) continue;
                if (ctl is not IAudioSessionControl2 ctl2) continue;
                if (ctl2.GetProcessId(out uint pid) != 0 || pid == 0) continue;

                string proc;
                try { proc = Process.GetProcessById((int)pid).ProcessName; }
                catch { continue; }

                if (!proc.Equals(exeNameWithoutExt, StringComparison.OrdinalIgnoreCase)) continue;
                if (ctl2 is not ISimpleAudioVolume vol) continue;

                if (vol.SetMute(mute, ref ev) == 0) changed++;
            }
        }
        catch
        {
            // 音频子系统出任何问题都不该影响切窗口这个主功能
        }
        finally
        {
            if (enumeratorObj is not null && Marshal.IsComObject(enumeratorObj))
                Marshal.ReleaseComObject(enumeratorObj);
        }
        return changed;
    }
}
