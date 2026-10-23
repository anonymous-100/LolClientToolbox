using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace WatchVideoPlugin;

/// <summary>
/// 这个插件要的 Win32 全部在这：全局热键、窗口枚举、前台切换、媒体键。
///
/// 为什么不用 WinForms：插件是净 net9.0 类库，拿不到 Form 的消息循环。
/// RegisterHotKey 传 hWnd=IntPtr.Zero 时，WM_HOTKEY 会投递到**调用它的线程**的消息队列，
/// 所以只要一条自己跑的 GetMessage 线程就够了 —— 不用造隐藏窗口。
/// </summary>
internal static class Win32
{
    // ---- 热键 / 消息 ----
    public const int WM_HOTKEY = 0x0312;
    public const uint WM_QUIT = 0x0012;

    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_WIN = 0x0008;

    // ---- 窗口 ----
    public const int SW_RESTORE = 9;
    public const int SW_MINIMIZE = 6;
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_SHOWWINDOW = 0x0040;

    // ---- 键盘 ----
    public const uint VK_MEDIA_PLAY_PAUSE = 0xB3;
    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
    }

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    /// <summary>
    /// 键盘物理状态。不经过前台窗口，也不受 UIPI 限制 —— 游戏以管理员运行时
    /// RegisterHotKey 注册的热键会被系统屏蔽（实测 2026-10-24），但这条能读到。
    /// </summary>
    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32.dll")]
    public static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);
    [DllImport("user32.dll")]
    public static extern bool TranslateMessage(ref MSG lpMsg);
    [DllImport("user32.dll")]
    public static extern IntPtr DispatchMessage(ref MSG lpMsg);
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool PostThreadMessage(uint idThread, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")]
    public static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")]
    public static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")]
    public static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")]
    public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);
    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    // ---- 完整性级别（判断对方是不是提权进程，决定能不能抢它的前台）----
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint TOKEN_QUERY = 0x0008;
    private const int TokenIntegrityLevel = 25;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr hProcess, uint dwDesiredAccess, out IntPtr hToken);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr hToken, int cls, IntPtr buf, int len, out int retLen);

    /// <summary>
    /// 进程完整性级别：0x2000=Medium（普通）、0x3000=High（管理员）。取不到返回 -1。
    /// UIPI 规则：普通权限进程不能把管理员权限窗口拉到前台 —— 抢不到时靠这个给准确提示。
    /// </summary>
    public static int IntegrityLevel(int pid)
    {
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return -1;
        try
        {
            if (!OpenProcessToken(h, TOKEN_QUERY, out var token)) return -1;
            try
            {
                GetTokenInformation(token, TokenIntegrityLevel, IntPtr.Zero, 0, out int len);
                if (len <= 0) return -1;
                IntPtr buf = Marshal.AllocHGlobal(len);
                try
                {
                    if (!GetTokenInformation(token, TokenIntegrityLevel, buf, len, out _)) return -1;
                    IntPtr sid = Marshal.ReadIntPtr(buf);          // TOKEN_MANDATORY_LABEL.Label.Sid
                    if (sid == IntPtr.Zero) return -1;
                    byte subCount = Marshal.ReadByte(sid, 1);      // SID 子权限个数
                    // SID = 修订号(1) + 个数(1) + 机构(6) + 子权限[count]*4；最后一个就是 IL
                    return Marshal.ReadInt32(sid, 8 + (subCount - 1) * 4);
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            finally { CloseHandle(token); }
        }
        finally { CloseHandle(h); }
    }

    public static int ProcessIdOf(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out var pid);
        return (int)pid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL, wParamH;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    /// <summary>发一次「播放/暂停」媒体键。浏览器（Chrome/Edge）会把它路由到当前媒体会话。</summary>
    public static bool SendMediaPlayPause()
    {
        int size = Marshal.SizeOf<INPUT>();
        var down = new INPUT { type = INPUT_KEYBOARD };
        down.U.ki.wVk = (ushort)VK_MEDIA_PLAY_PAUSE;
        var up = down;
        up.U.ki.dwFlags = KEYEVENTF_KEYUP;
        return SendInput(2, new[] { down, up }, size) == 2;
    }

    public static string Title(IntPtr hwnd)
    {
        var sb = new StringBuilder(512);
        GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    public static string ClassName(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    /// <summary>把窗口拉到前台。Windows 有前台锁，AttachThreadInput 是通行的绕法。</summary>
    public static bool ForceForeground(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return false;
        if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);

        uint me = GetCurrentThreadId();
        uint fg = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        uint target = GetWindowThreadProcessId(hwnd, out _);

        bool a1 = false, a2 = false;
        if (fg != 0 && fg != me) a1 = AttachThreadInput(me, fg, true);
        if (target != 0 && target != me) a2 = AttachThreadInput(me, target, true);
        try
        {
            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);
            SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
        }
        finally
        {
            if (a2) AttachThreadInput(me, target, false);
            if (a1) AttachThreadInput(me, fg, false);
        }
        return GetForegroundWindow() == hwnd;
    }

    /// <summary>枚举全部有标题的可见顶层窗口，返回 (句柄, 进程名小写, 标题)。</summary>
    public static List<(IntPtr Hwnd, string Proc, string Title)> TopLevelWindows()
    {
        var list = new List<(IntPtr, string, string)>();
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            var title = Title(hwnd);
            if (title.Length == 0) return true;
            GetWindowThreadProcessId(hwnd, out var pid);
            string proc = "";
            try { proc = Process.GetProcessById((int)pid).ProcessName.ToLowerInvariant(); }
            catch { /* 进程可能刚退出 */ }
            list.Add((hwnd, proc, title));
            return true;
        }, IntPtr.Zero);
        return list;
    }
}
