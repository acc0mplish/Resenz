using System.Runtime.InteropServices;
using System.Text;

namespace Resenz.App.Mini;

internal static class WindowChrome
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_APPWINDOW = 0x00040000;
    private const int WS_EX_LAYERED = 0x00080000;

    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_NOSENDCHANGING = 0x0400;
    private const uint LWA_ALPHA = 0x00000002;

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    private const uint WM_NCCALCSIZE = 0x0083;

    private static SUBCLASSPROC? _subclassProc;

    public static void SetToolWindow(IntPtr hwnd)
    {
        int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        ex |= WS_EX_TOOLWINDOW;
        ex &= ~WS_EX_APPWINDOW;
        SetWindowLong(hwnd, GWL_EXSTYLE, ex);
    }

    public static void SetTopmost(IntPtr hwnd)
    {
        _ = SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOSENDCHANGING);
    }

    /// <summary>
    /// 창을 topmost 그룹 내 작업 표시줄 바로 아래에 삽입해 작업 표시줄이 창을 가리게 하고,
    /// 창이 작업 표시줄 뒤로 올라오는 듯한 느낌을 줌. 작업 표시줄을 찾지 못하면 순수 topmost로 강등.
    /// 반드시 BringToForeground 이후에 호출. 그러지 않으면 활성화가 창을 다시 작업 표시줄 위로 끌어올림.
    /// </summary>
    public static void PlaceBelowTaskbar(IntPtr hwnd)
    {
        IntPtr taskbar = FindTaskbarForWindow(hwnd);
        if (taskbar == IntPtr.Zero)
        {
            SetTopmost(hwnd);
            return;
        }

        // topmost 그룹 안에 먼저 들어 있어야 삽입이 적용됨.
        SetTopmost(hwnd);
        _ = SetWindowPos(hwnd, taskbar, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOSENDCHANGING);
    }

    /// <summary>
    /// 창과 같은 모니터의 작업 표시줄을 찾음: 주 화면은 Shell_TrayWnd,보조 화면은 Shell_SecondaryTrayWnd.
    /// </summary>
    private static IntPtr FindTaskbarForWindow(IntPtr hwnd)
    {
        IntPtr monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);

        IntPtr primary = FindWindowW("Shell_TrayWnd", null);
        if (primary != IntPtr.Zero && MonitorFromWindow(primary, MONITOR_DEFAULTTONEAREST) == monitor)
            return primary;

        IntPtr found = IntPtr.Zero;
        EnumWindows((candidate, _) =>
        {
            var sb = new StringBuilder(64);
            GetClassNameW(candidate, sb, sb.Capacity);
            if (sb.ToString() != "Shell_SecondaryTrayWnd") return true;
            if (MonitorFromWindow(candidate, MONITOR_DEFAULTTONEAREST) != monitor) return true;
            found = candidate;
            return false;
        }, IntPtr.Zero);

        return found != IntPtr.Zero ? found : primary;
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    public static void SetWindowAlpha(IntPtr hwnd, int alpha)
    {
        int clamped = Math.Clamp(alpha, 0, 255);
        int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        if ((ex & WS_EX_LAYERED) == 0)
            SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_LAYERED);
        _ = SetLayeredWindowAttributes(hwnd, 0, (byte)clamped, LWA_ALPHA);
    }

    public static int GetWindowAlpha(IntPtr hwnd)
    {
        int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        if ((ex & WS_EX_LAYERED) == 0) return 255;
        if (!GetLayeredWindowAttributes(hwnd, out _, out byte alpha, out _)) return 255;
        return alpha;
    }

    public static void RemoveNonClientFrame(IntPtr hwnd)
    {
        _subclassProc = SubclassProc;
        _ = SetWindowSubclass(hwnd, _subclassProc, 1, IntPtr.Zero);
    }

    private static IntPtr SubclassProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, uint id, IntPtr data)
    {
        if (msg == WM_NCCALCSIZE && wParam != IntPtr.Zero)
            return IntPtr.Zero;
        return DefSubclassProc(hWnd, msg, wParam, lParam);
    }

    public static void SetRoundCorner(IntPtr hwnd)
    {
        int pref = DWMWCP_ROUND;
        _ = DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
    }

    public static void BringToForeground(IntPtr hwnd)
    {
        _ = SetForegroundWindow(hwnd);
        if (GetForegroundWindow() == hwnd) return;

        uint targetThread = GetWindowThreadProcessId(hwnd, IntPtr.Zero);
        uint foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), IntPtr.Zero);
        if (targetThread == foregroundThread)
        {
            _ = SetForegroundWindow(hwnd);
            return;
        }

        _ = AttachThreadInput(targetThread, foregroundThread, true);
        _ = SetForegroundWindow(hwnd);
        _ = AttachThreadInput(targetThread, foregroundThread, false);
    }

    private delegate IntPtr SUBCLASSPROC(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, uint uIdSubclass, IntPtr dwRefData);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetLayeredWindowAttributes(IntPtr hWnd, uint crKey, byte bAlpha, uint dwFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetLayeredWindowAttributes(IntPtr hWnd, out uint crKey, out byte bAlpha, out uint dwFlags);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool SetWindowSubclass(IntPtr hWnd, SUBCLASSPROC pfnSubclass, uint uIdSubclass, IntPtr dwRefData);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr pid);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowW(string lpClassName, string? lpWindowName);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    private const uint MONITOR_DEFAULTTONEAREST = 2;
}
