Add-Type -AssemblyName System.Windows.Forms
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

public static class MonInfo
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll")]
    static extern IntPtr MonitorFromPoint(POINT pt, uint flags);

    [DllImport("shcore.dll")]
    static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    public static List<string> List()
    {
        var result = new List<string>();
        int index = 0;
        foreach (var screen in System.Windows.Forms.Screen.AllScreens)
        {
            index++;
            var p = new POINT { X = screen.Bounds.Left + 50, Y = screen.Bounds.Top + 50 };
            var hmon = MonitorFromPoint(p, 2);
            var mi = new MONITORINFO();
            mi.cbSize = Marshal.SizeOf(typeof(MONITORINFO));
            GetMonitorInfo(hmon, ref mi);
            uint dx, dy;
            int hr = GetDpiForMonitor(hmon, 0, out dx, out dy);
            result.Add(string.Format(
                "monitor {0}: {1} principal={2} bounds=({3},{4})-({5},{6}) areaUtil=({7},{8})-({9},{10}) dpi={11} escala={12}%",
                index, screen.DeviceName, screen.Primary,
                screen.Bounds.Left, screen.Bounds.Top, screen.Bounds.Right, screen.Bounds.Bottom,
                mi.rcWork.Left, mi.rcWork.Top, mi.rcWork.Right, mi.rcWork.Bottom,
                hr == 0 ? dx : 0, hr == 0 ? (int)(dx / 96.0 * 100) : 0));
        }
        return result;
    }
}
'@

[MonInfo]::List() | ForEach-Object { $_ }
