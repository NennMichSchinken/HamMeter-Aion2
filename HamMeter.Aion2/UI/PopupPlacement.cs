using System.Numerics;
using System.Runtime.InteropServices;
using ImGuiNET;

namespace HamMeter.UI;

// Where a dropdown list opens: under its field, or above it when the monitor the field
// is on has no room left underneath (a settings window near the bottom edge). Measured
// against that monitor's work area, not the overlay, which spans every monitor.
internal static class PopupPlacement
{
    private const float Gap = 4f;

    // The list's top edge. Below if it fits; otherwise whichever side has more room.
    public static float ListTop(float fieldTop, float fieldBottom, float listHeight, float monitorTop, float monitorBottom)
    {
        float below = monitorBottom - (fieldBottom + Gap);
        float above = (fieldTop - Gap) - monitorTop;
        return below < listHeight && above > below
            ? fieldTop - Gap - listHeight
            : fieldBottom + Gap;
    }

    // Top and bottom of the work area (screen minus taskbar) of the monitor under a point,
    // in overlay coordinates (the overlay starts at the virtual screen's origin).
    public static (float Top, float Bottom) MonitorSpan(Vector2 overlayPoint)
    {
        int originX = GetSystemMetrics(76); // SM_XVIRTUALSCREEN
        int originY = GetSystemMetrics(77); // SM_YVIRTUALSCREEN
        var pt = new Point { X = (int)overlayPoint.X + originX, Y = (int)overlayPoint.Y + originY };
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        IntPtr monitor = MonitorFromPoint(pt, 2); // MONITOR_DEFAULTTONEAREST
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
        {
            return (0f, ImGui.GetIO().DisplaySize.Y);
        }

        return (info.Work.Top - originY, info.Work.Bottom - originY);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(Point pt, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
}
