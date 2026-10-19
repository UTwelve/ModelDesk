using System.ComponentModel;
using System.Runtime.InteropServices;

namespace ModelDesk.Services;

public sealed class TrayIcon : IDisposable
{
    private const uint CallbackMessage = 0x8000 + 73;
    private const uint RestoreCommand = 101, FloatCommand = 102, ExitCommand = 103, StartCommand = 104, StopCommand = 105;
    private readonly nint hwnd;
    private readonly SubclassProc callback;
    private readonly Action restore, floating, exit, start, stop;
    private readonly Func<(bool Start, bool Stop)> actions;
    private readonly uint taskbarCreated;
    private bool registered;
    public bool Available => registered;
    public TrayIcon(nint window, Action restoreMain, Action showFloating, Action requestExit, Action startModel, Action stopModel, Func<(bool Start, bool Stop)> availability)
    {
        hwnd = window; restore = restoreMain; floating = showFloating; exit = requestExit; start = startModel; stop = stopModel; actions = availability;
        callback = HandleMessage;
        taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        if (!SetWindowSubclass(hwnd, callback, 1, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
        Register();
    }
    private void Register()
    {
        var data = Data();
        data.uFlags = 1 | 2 | 4;
        data.uCallbackMessage = CallbackMessage;
        data.hIcon = LoadIcon(0, new nint(32512));
        data.szTip = "ModelDesk · 双击恢复，右键菜单";
        registered = Shell_NotifyIcon(0, ref data);
    }
    private NotifyIconData Data() => new() { cbSize = (uint)Marshal.SizeOf<NotifyIconData>(), hWnd = hwnd, uID = 1, szTip = "", szInfo = "", szInfoTitle = "" };
    private nint HandleMessage(nint window, uint message, nuint wParam, nint lParam, nuint id, nuint data)
    {
        if (message == taskbarCreated) Register();
        if (message == CallbackMessage)
        {
            uint mouse = (uint)((long)lParam & 0xffff);
            if (mouse == 0x0203) restore();
            if (mouse == 0x0205 || mouse == 0x007B) ShowMenu();
            return 0;
        }
        return DefSubclassProc(window, message, wParam, lParam);
    }
    private void ShowMenu()
    {
        nint menu = CreatePopupMenu();
        try
        {
            AppendMenu(menu, 0, RestoreCommand, "打开主窗口");
            AppendMenu(menu, 0, FloatCommand, "切换悬浮监控");
            var enabled = actions();
            AppendMenu(menu, enabled.Start ? 0u : 1u, StartCommand, "启动模型");
            AppendMenu(menu, enabled.Stop ? 0u : 1u, StopCommand, "关闭模型");
            AppendMenu(menu, 0x800, 0, "");
            AppendMenu(menu, 0, ExitCommand, "退出 ModelDesk");
            GetCursorPos(out var point);
            SetForegroundWindow(hwnd);
            uint command = TrackPopupMenu(menu, 0x100 | 0x2, point.X, point.Y, 0, hwnd, 0);
            PostMessage(hwnd, 0, 0, 0);
            if (command == RestoreCommand) restore();
            else if (command == FloatCommand) floating();
            else if (command == ExitCommand) exit();
            else if (command == StartCommand && enabled.Start) start();
            else if (command == StopCommand && enabled.Stop) stop();
        }
        finally { DestroyMenu(menu); }
    }
    public void Dispose()
    {
        if (registered) { var data = Data(); Shell_NotifyIcon(2, ref data); registered = false; }
        RemoveWindowSubclass(hwnd, callback, 1);
        GC.KeepAlive(callback);
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint cbSize; public nint hWnd; public uint uID, uFlags, uCallbackMessage; public nint hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState, dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags; public Guid guidItem; public nint hBalloonIcon;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate nint SubclassProc(nint hwnd, uint message, nuint wParam, nint lParam, nuint id, nuint data);
    [DllImport("comctl32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowSubclass(nint hwnd, SubclassProc proc, nuint id, nuint data);
    [DllImport("comctl32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool RemoveWindowSubclass(nint hwnd, SubclassProc proc, nuint id);
    [DllImport("comctl32.dll")] private static extern nint DefSubclassProc(nint hwnd, uint message, nuint wParam, nint lParam);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Shell_NotifyIcon(uint message, ref NotifyIconData data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint LoadIcon(nint instance, nint icon);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string message);
    [DllImport("user32.dll")] private static extern nint CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AppendMenu(nint menu, uint flags, nuint item, string text);
    [DllImport("user32.dll")] private static extern uint TrackPopupMenu(nint menu, uint flags, int x, int y, int reserved, nint hwnd, nint rect);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PostMessage(nint hwnd, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyMenu(nint menu);
}
