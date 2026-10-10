using System.Runtime.InteropServices;

namespace Fonoo.Windows.Desktop;

internal sealed class TaskbarStatus(IntPtr mainWindow) : IDisposable
{
    private ITaskbarList3? taskbar;
    private (IntPtr Icon, string Description)? last;
    public void Set(IntPtr icon, string description, bool force = false)
    {
        if (!force && last == (icon, description)) return;
        try
        {
            if (taskbar is null)
            {
                taskbar = (ITaskbarList3)Activator.CreateInstance(Type.GetTypeFromCLSID(new("56FDF344-FD6D-11D0-958A-006097C9A090"), true)!)!;
                Marshal.ThrowExceptionForHR(taskbar.HrInit());
            }
            if (taskbar.SetOverlayIcon(mainWindow, icon, description) >= 0) last = (icon, description);
        }
        catch { /* The notification-area icon remains usable without a taskbar button. */ }
    }
    public void Dispose()
    {
        if (taskbar is null) return;
        try { taskbar.SetOverlayIcon(mainWindow, IntPtr.Zero, ""); } catch { }
        try { Marshal.FinalReleaseComObject(taskbar); } catch { }
        taskbar = null;
    }
    // Exact ITaskbarList3 vtable including inherited ITaskbarList/ITaskbarList2 slots.
    [ComImport, Guid("EA1AFB91-9E28-4B86-90E9-9E9F8A5EEFAF"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList3
    {
        [PreserveSig] int HrInit();
        [PreserveSig] int AddTab(IntPtr window);
        [PreserveSig] int DeleteTab(IntPtr window);
        [PreserveSig] int ActivateTab(IntPtr window);
        [PreserveSig] int SetActiveAlt(IntPtr window);
        [PreserveSig] int MarkFullscreenWindow(IntPtr window, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);
        [PreserveSig] int SetProgressValue(IntPtr window, ulong completed, ulong total);
        [PreserveSig] int SetProgressState(IntPtr window, uint flags);
        [PreserveSig] int RegisterTab(IntPtr tab, IntPtr window);
        [PreserveSig] int UnregisterTab(IntPtr tab);
        [PreserveSig] int SetTabOrder(IntPtr tab, IntPtr before);
        [PreserveSig] int SetTabActive(IntPtr tab, IntPtr window, uint reserved);
        [PreserveSig] int ThumbBarAddButtons(IntPtr window, uint count, IntPtr buttons);
        [PreserveSig] int ThumbBarUpdateButtons(IntPtr window, uint count, IntPtr buttons);
        [PreserveSig] int ThumbBarSetImageList(IntPtr window, IntPtr images);
        [PreserveSig] int SetOverlayIcon(IntPtr window, IntPtr icon, [MarshalAs(UnmanagedType.LPWStr)] string description);
        [PreserveSig] int SetThumbnailTooltip(IntPtr window, [MarshalAs(UnmanagedType.LPWStr)] string tip);
        [PreserveSig] int SetThumbnailClip(IntPtr window, IntPtr rectangle);
    }
}
