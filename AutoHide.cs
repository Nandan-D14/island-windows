using System.Runtime.InteropServices;
using System.Text;

namespace Island;

// Tucks the island into a thin strip when a maximized / full-screen app owns the screen.
public partial class MainWindow
{
    delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int obj, int child, uint thread, uint time);
    [DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr mod, WinEventProc proc, uint pid, uint tid, uint flags);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out Rc r);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr m, ref MonInfo mi);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [StructLayout(LayoutKind.Sequential)] struct Rc { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] struct MonInfo { public int Size; public Rc Monitor, Work; public uint Flags; }

    WinEventProc? _fgProc;   // keep a reference so the GC doesn't collect the callback
    bool _covered;
    static readonly HashSet<string> ShellClasses = ["Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Windows.UI.Core.CoreWindow", "XamlExplorerHostIslandWindow"];

    void StartAutoHide()
    {
        _fgProc = (_, _, _, _, _, _, _) => Dispatcher.InvokeAsync(CheckCovered);
        // EVENT_SYSTEM_FOREGROUND (3) and EVENT_SYSTEM_MINIMIZEEND (0x17), out-of-context = no DLL injection
        SetWinEventHook(0x0003, 0x0003, IntPtr.Zero, _fgProc, 0, 0, 0);
        SetWinEventHook(0x0016, 0x0017, IntPtr.Zero, _fgProc, 0, 0, 0);
        CheckCovered();
    }

    // cheap: a handful of Win32 calls, run on focus change + once per second (catches maximize/restore)
    void CheckCovered()
    {
        bool covered = false;
        var fg = GetForegroundWindow();
        if (fg != IntPtr.Zero && fg != _hwnd && IsWindowVisible(fg))
        {
            var cls = new StringBuilder(64);
            GetClassName(fg, cls, 64);
            if (!ShellClasses.Contains(cls.ToString()) && GetWindowRect(fg, out var r))
            {
                var mi = new MonInfo { Size = Marshal.SizeOf<MonInfo>() };
                GetMonitorInfo(MonitorFromWindow(fg, 2), ref mi);
                bool primary = (mi.Flags & 1) != 0;   // the island lives on the primary monitor
                bool fullscreen = r.L <= mi.Monitor.L && r.T <= mi.Monitor.T && r.R >= mi.Monitor.R && r.B >= mi.Monitor.B;
                covered = primary && (IsZoomed(fg) || fullscreen) && !_ai.AlwaysTop; // "Always on top": never tuck away
            }
        }

        if (covered != _covered)
        {
            _covered = covered;
            // full-screen apps can push themselves above us; re-assert topmost
            SetWindowPos(_hwnd, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 /*NOSIZE|NOMOVE|NOACTIVATE*/);
            Refresh();
        }
        // "Always on top": every second, bring the island back to the very top (some full-screen apps take it)
        if (_ai.AlwaysTop) SetWindowPos(_hwnd, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
    }
}
