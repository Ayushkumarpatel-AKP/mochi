using System.Runtime.InteropServices;
using Avalonia;

namespace Mochi.Services;

/// <summary>Win32 helpers for the cursor position and the global modifier/mouse state.</summary>
public static class Native
{
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);

    private const int VK_LBUTTON = 0x01, VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12;

    public static void OpenUrl(string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); } catch { /* no browser registered */ }
    }

    public static PixelPoint Cursor() => GetCursorPos(out var p) ? new PixelPoint(p.X, p.Y) : default;
    public static bool LeftDown() => Down(VK_LBUTTON);
    public static bool Key(char c) => Down(c);

    public static bool Modifier(string name) => name switch
    {
        "Ctrl" => Down(VK_CONTROL),
        "Shift" => Down(VK_SHIFT),
        "Alt or Shift" => Down(VK_MENU) || Down(VK_SHIFT),
        _ => Down(VK_MENU),
    };

    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr h, uint flags);

    /// <summary>True when the window under the pointer belongs to Explorer (a folder window or the desktop).</summary>
    public static bool OverExplorer()
    {
        if (!GetCursorPos(out var p)) return false;
        var h = WindowFromPoint(p);
        if (h == IntPtr.Zero) return false;
        GetWindowThreadProcessId(GetAncestor(h, 2), out var pid);   // 2 = the top-level window
        try { return System.Diagnostics.Process.GetProcessById((int)pid).ProcessName.Equals("explorer", StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    private static bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();

    /// <summary>The files and folders selected in the Explorer window in front, so Alt+D can convert "what I have selected".</summary>
    public static List<string> ExplorerSelection()
    {
        var list = new List<string>();
        try
        {
            var front = GetForegroundWindow().ToInt64();
            var type = Type.GetTypeFromProgID("Shell.Application");
            if (type is null) return list;
            dynamic shell = Activator.CreateInstance(type)!;
            foreach (dynamic w in shell.Windows())
            {
                try
                {
                    if ((long)w.HWND != front) continue;
                    foreach (dynamic item in w.Document.SelectedItems())
                    {
                        string path = item.Path;
                        if (File.Exists(path) || Directory.Exists(path)) list.Add(path);
                    }
                    if (list.Count > 0) break;   // with tabs, the tab that has a selection
                }
                catch { /* a window that is not a file view */ }
            }
        }
        catch { /* no Explorer to ask */ }
        return list.Distinct().ToList();
    }

    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint from, uint to, bool attach);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

    /// <summary>
    /// Brings a window to the front and gives it the keyboard. Windows only allows that for the program that already has focus,
    /// so the thread is joined to the front window's for a moment: that is what lets a shortcut-opened panel take typing.
    /// </summary>
    public static void ForceForeground(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        var front = GetForegroundWindow();
        var other = front == IntPtr.Zero ? 0 : GetWindowThreadProcessId(front, out _);
        var me = GetCurrentThreadId();
        var joined = other != 0 && other != me && AttachThreadInput(me, other, true);
        SetForegroundWindow(hwnd);
        if (joined) AttachThreadInput(me, other, false);
    }
}
