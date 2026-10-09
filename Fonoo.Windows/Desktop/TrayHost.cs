using System.Runtime.InteropServices;
using Fonoo.Windows.Telephony;

namespace Fonoo.Windows.Desktop;

// One hidden window on the WinUI thread receives Shell and power messages.
// Keep the delegate rooted until DestroyWindow; never replace WinUI's WndProc.
public sealed class TrayHost : IDisposable
{
    private readonly WndProc callback;
    private readonly string className = "Fonoo.Tray." + Guid.NewGuid().ToString("N");
    private readonly IntPtr module = GetModuleHandle(null);
    private readonly uint taskbarCreated = RegisterWindowMessage("TaskbarCreated");
    private IntPtr window;
    private IntPtr icon;
    private NotifyIcon data;
    private SipSnapshot state = new(false, false, false, false, "Nicht angemeldet", "");
    public bool Available { get; private set; }
    public event Action<int>? Command;
    public event Action? Resumed;
    public event Action? Unavailable;
    public TrayHost(string iconPath)
    {
        callback = WindowProc;
        var definition = new WindowClass { Size = (uint)Marshal.SizeOf<WindowClass>(), Procedure = callback, Instance = module, ClassName = className };
        if (RegisterClassEx(ref definition) == 0) throw new InvalidOperationException();
        window = CreateWindowEx(0, className, "Fonoo notifications", 0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, module, IntPtr.Zero);
        icon = LoadImage(IntPtr.Zero, iconPath, 1, 32, 32, 0x10);
        if (window == IntPtr.Zero || icon == IntPtr.Zero) { Dispose(); throw new InvalidOperationException(); }
        data = new NotifyIcon { Size = (uint)Marshal.SizeOf<NotifyIcon>(), Window = window, Id = 1, Flags = 1 | 2 | 4 | 0x20 | 0x80,
            CallbackMessage = 0x8001, Icon = icon, Tip = "Fonoo", Info = "", InfoTitle = "", Guid = new("8a1c510e-67bc-44ba-b7cd-6c2ad230f667") };
        AddIcon();
    }
    private void AddIcon()
    {
        Available = ShellNotifyIcon(0, ref data);
        if (Available) { data.Version = 4; ShellNotifyIcon(4, ref data); }
        else Unavailable?.Invoke();
    }
    public void Update(SipSnapshot snapshot, string duration = "")
    {
        state = snapshot;
        var text = snapshot.InCall ? $"Fonoo · {snapshot.Peer} · {duration} · {snapshot.Status}" : "Fonoo · " + snapshot.Status;
        data.Tip = text[..Math.Min(127, text.Length)];
        if (Available && !ShellNotifyIcon(1, ref data)) { Available = false; Unavailable?.Invoke(); }
    }
    private IntPtr WindowProc(IntPtr handle, uint message, UIntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (message == taskbarCreated) { AddIcon(); return IntPtr.Zero; }
            if (message == 0x8001)
            {
                var action = (uint)lParam.ToInt64() & 0xffff;
                if (action is 0x400 or 0x401 or 0x203) Command?.Invoke(1); // NIN_SELECT, NIN_KEYSELECT, double click.
                else if (action is 0x7b or 0x205) ShowMenu();
                return IntPtr.Zero;
            }
            if (message == 0x218 && wParam.ToUInt64() is 7 or 18) Resumed?.Invoke();
        }
        catch { /* An exception must not cross a native window callback. */ }
        return DefWindowProc(handle, message, wParam, lParam);
    }
    private void ShowMenu()
    {
        var menu = CreatePopupMenu(); if (menu == IntPtr.Zero) return;
        try
        {
            Add(0, state.Status, false);
            Add(1, "Fonoo öffnen");
            if (state.InCall)
            {
                Add(2, "Kleines Gesprächsfenster");
                if (state.Incoming) Add(3, "Annehmen");
                Add(4, state.Muted ? "Mikrofon aktivieren" : "Stummschalten", state.Active);
                Add(5, state.Held ? "Fortsetzen" : "Halten", (state.Active || state.Held) && !state.HoldPending && !state.Consulting && !state.TransferPending);
                Add(6, state.Incoming ? "Ablehnen" : "Auflegen");
            }
            Add(7, state.DoNotDisturb ? "Nicht stören ausschalten" : "Nicht stören einschalten");
            AppendMenu(menu, 0x800, UIntPtr.Zero, null);
            Add(8, "Fonoo beenden");
            GetCursorPos(out var point); SetForegroundWindow(window);
            var selected = TrackPopupMenuEx(menu, 0x100 | 0x2, point.X, point.Y, window, IntPtr.Zero);
            PostMessage(window, 0, UIntPtr.Zero, IntPtr.Zero);
            if (selected != 0) Command?.Invoke((int)selected);
            void Add(uint id, string text, bool enabled = true) => AppendMenu(menu, enabled ? 0u : 1u, new UIntPtr(id), text);
        }
        finally { DestroyMenu(menu); }
    }
    public static void RequestAttention(IntPtr handle)
    {
        var flash = new FlashInfo { Size = (uint)Marshal.SizeOf<FlashInfo>(), Window = handle, Flags = 3 | 12, Count = 3 };
        FlashWindowEx(ref flash);
    }
    public void Dispose()
    {
        if (Available) ShellNotifyIcon(2, ref data);
        Available = false;
        if (window != IntPtr.Zero) { DestroyWindow(window); window = IntPtr.Zero; }
        if (icon != IntPtr.Zero) { DestroyIcon(icon); icon = IntPtr.Zero; }
        UnregisterClass(className, module);
        GC.KeepAlive(callback);
    }
    private delegate IntPtr WndProc(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct WindowClass
    {
        public uint Size, Style; public WndProc Procedure; public int ClassExtra, WindowExtra; public IntPtr Instance, Icon, Cursor, Background;
        public string? MenuName; public string ClassName; public IntPtr SmallIcon;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct NotifyIcon
    {
        public uint Size; public IntPtr Window; public uint Id, Flags, CallbackMessage; public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags; public Guid Guid; public IntPtr BalloonIcon;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct FlashInfo { public uint Size; public IntPtr Window; public uint Flags, Count, Timeout; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegisterClassExW")] private static extern ushort RegisterClassEx(ref WindowClass data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateWindowExW")] private static extern IntPtr CreateWindowEx(uint extended, string name, string title, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "DefWindowProcW")] private static extern IntPtr DefWindowProc(IntPtr hwnd, uint message, UIntPtr w, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegisterWindowMessageW")] private static extern uint RegisterWindowMessage(string message);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "UnregisterClassW")] private static extern bool UnregisterClass(string name, IntPtr module);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetModuleHandleW")] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "LoadImageW")] private static extern IntPtr LoadImage(IntPtr instance, string name, uint type, int x, int y, uint flags);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "Shell_NotifyIconW")] private static extern bool ShellNotifyIcon(uint message, ref NotifyIcon data);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "AppendMenuW")] private static extern bool AppendMenu(IntPtr menu, uint flags, UIntPtr id, string? text);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] private static extern uint TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr owner, IntPtr parameters);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll", EntryPoint = "PostMessageW")] private static extern bool PostMessage(IntPtr window, uint message, UIntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern bool FlashWindowEx(ref FlashInfo info);
}
