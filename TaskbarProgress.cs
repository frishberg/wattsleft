using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace BatteryChecker;

/// <summary>
/// The level shown on the taskbar button itself. Windows picks a packaged
/// app's taskbar icon from the package, so we can't redraw that; what it
/// does allow is the progress fill that downloads use, so the button shows
/// the battery level as a fill.
/// </summary>
public static partial class TaskbarProgress
{
    private const int TBPF_NORMAL = 2;
    private static ITaskbarList3? _taskbar;
    private static int _lastPercent = -1;

    public static void Show(nint hwnd, int percent)
    {
        if (percent == _lastPercent)
            return;
        _lastPercent = percent;
        try
        {
            _taskbar ??= Create();
            _taskbar.SetProgressState(hwnd, TBPF_NORMAL);
            _taskbar.SetProgressValue(hwnd, (ulong)Math.Clamp(percent, 0, 100), 100);
        }
        catch
        {
            // No taskbar (or an odd shell): the button just shows the icon. Not worth crashing over.
        }
    }

    private static ITaskbarList3 Create()
    {
        Guid clsid = new("56FDF344-FD6D-11d0-958A-006097C9A090");
        Guid iid = new("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf");
        int hr = CoCreateInstance(in clsid, 0, 1 /* CLSCTX_INPROC_SERVER */, in iid, out nint ptr);
        if (hr < 0) throw new COMException("TaskbarList", hr);
        var taskbar = (ITaskbarList3)new StrategyBasedComWrappers().GetOrCreateObjectForComInstance(ptr, CreateObjectFlags.None);
        Marshal.Release(ptr);
        taskbar.HrInit();
        return taskbar;
    }

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(in Guid clsid, nint outer, uint context, in Guid iid, out nint result);

    [GeneratedComInterface]
    [Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf")]
    internal partial interface ITaskbarList3
    {
        void HrInit();
        void AddTab(nint hwnd);
        void DeleteTab(nint hwnd);
        void ActivateTab(nint hwnd);
        void SetActiveAlt(nint hwnd);
        void MarkFullscreenWindow(nint hwnd, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);
        void SetProgressValue(nint hwnd, ulong completed, ulong total);
        void SetProgressState(nint hwnd, int flags);
    }
}
