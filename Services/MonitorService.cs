using System.Runtime.InteropServices;
using System.Windows;
using Win11Notch.Core.State;

namespace Win11Notch.Services;

public sealed class MonitorItem
{
    public int    Index      { get; init; }
    public string Name       { get; init; } = string.Empty;
    public bool   IsPrimary  { get; init; }
    public IntPtr Handle     { get; init; }
    public double DpiScale   { get; init; } = 1.0;

    // Logical DIP bounds for WPF Window positioning
    public double Left       { get; init; }
    public double Top        { get; init; }
    public double Width      { get; init; }
    public double Height     { get; init; }
    public double WorkLeft   { get; init; }
    public double WorkTop    { get; init; }
    public double WorkWidth  { get; init; }
    public double WorkHeight { get; init; }

    // Physical pixel bounds for GDI / BitBlt screen capture
    public int    PhysLeft   { get; init; }
    public int    PhysTop    { get; init; }
    public int    PhysWidth  { get; init; }
    public int    PhysHeight { get; init; }
}

/// <summary>
/// Provides screen-geometry helpers for notch window positioning across Top, Left, and Right docking edges.
/// Supports multi-monitor targeting, work-area offsets, and expanded Dynamic Island notification pill sizes.
/// </summary>
public sealed class MonitorService
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    private static double GetMonitorDpiScale(IntPtr hMonitor)
    {
        try
        {
            if (GetDpiForMonitor(hMonitor, 0 /* MDT_EFFECTIVE_DPI */, out uint dpiX, out _) == 0 && dpiX >= 96)
            {
                return dpiX / 96.0;
            }
        }
        catch { /* fallback for older OS builds */ }
        return 1.0;
    }

    public IReadOnlyList<MonitorItem> GetMonitors()
    {
        var list = new List<MonitorItem>();
        int index = 1;

        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMon, IntPtr hdc, ref RECT rc, IntPtr data) =>
        {
            var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfo(hMon, ref mi))
            {
                bool isPrimary = (mi.dwFlags & 1 /* MONITORINFOF_PRIMARY */) != 0;
                int pw  = mi.rcMonitor.Right - mi.rcMonitor.Left;
                int ph  = mi.rcMonitor.Bottom - mi.rcMonitor.Top;
                int pww = mi.rcWork.Right - mi.rcWork.Left;
                int pwh = mi.rcWork.Bottom - mi.rcWork.Top;

                double scale = GetMonitorDpiScale(hMon);

                string label = isPrimary ? $"Primary Display ({pw}×{ph})" : $"Display {index} ({pw}×{ph})";

                list.Add(new MonitorItem
                {
                    Index      = index,
                    Name       = label,
                    IsPrimary  = isPrimary,
                    Handle     = hMon,
                    DpiScale   = scale,
                    Left       = mi.rcMonitor.Left / scale,
                    Top        = mi.rcMonitor.Top  / scale,
                    Width      = pw  / scale,
                    Height     = ph  / scale,
                    WorkLeft   = mi.rcWork.Left / scale,
                    WorkTop    = mi.rcWork.Top  / scale,
                    WorkWidth  = pww / scale,
                    WorkHeight = pwh / scale,
                    PhysLeft   = mi.rcMonitor.Left,
                    PhysTop    = mi.rcMonitor.Top,
                    PhysWidth  = pw,
                    PhysHeight = ph
                });
                index++;
            }
            return true;
        }, IntPtr.Zero);

        return list;
    }

    public MonitorItem? GetTargetMonitor(int monitorIndex = 0)
    {
        var monitors = GetMonitors();
        return (monitorIndex > 0 && monitorIndex <= monitors.Count)
            ? monitors[monitorIndex - 1]
            : monitors.FirstOrDefault(m => m.IsPrimary) ?? (monitors.Count > 0 ? monitors[0] : null);
    }

    public (double Width, double Height) GetDimensions(
        string dockEdge,
        NotchState state,
        bool isMediaToast = false,
        bool isVolumeToast = false,
        bool isPowerToast = false,
        bool isMediaPlaying = false,
        bool isStopwatchRunning = false,
        int handleWidth = 72,
        int handleThickness = 8,
        bool dynamicSizing = true,
        bool dualActivityEnabled = true,
        bool hasBluetoothBattery = false,
        bool isBrightnessToast = false,
        bool hasUpcomingReminder = false,
        bool showFullLivePreview = true,
        bool showDesktopAmbient = false)
    {
        bool isVerticalEdge = dockEdge is "Left" or "Right";
        int hw = Math.Clamp(handleWidth, 40, 200);
        int ht = Math.Clamp(handleThickness, 4, 16);

        bool isDualActivity = dualActivityEnabled && isMediaPlaying && isStopwatchRunning;
        bool idleShowsMedia = showFullLivePreview && isMediaPlaying;
        bool idleShowsTimer = showFullLivePreview && isStopwatchRunning;
        bool idleDual       = dualActivityEnabled && idleShowsMedia && idleShowsTimer;
        bool idleHasLive    = idleShowsMedia || idleShowsTimer;

        double hoverWidth;
        if (!dynamicSizing)
        {
            hoverWidth = isPowerToast ? 355 : (isVolumeToast || isBrightnessToast) ? 290 : (isMediaToast ? 370 : 300);
        }
        else if (isPowerToast)
        {
            hoverWidth = 355;
        }
        else if (isVolumeToast || isBrightnessToast)
        {
            hoverWidth = 290;
        }
        else if (isMediaToast)
        {
            hoverWidth = 370;
        }
        else if (isDualActivity)
        {
            hoverWidth = 455;
        }
        else if (isMediaPlaying)
        {
            hoverWidth = 375;
        }
        else if (isStopwatchRunning)
        {
            hoverWidth = 315;
        }
        else
        {
            hoverWidth = 275; // minimal clock, date & network badge capsule
        }

        if (hasBluetoothBattery && !isVolumeToast && !isPowerToast && !isBrightnessToast)
        {
            hoverWidth += 54;
        }
        if (hasUpcomingReminder && !isMediaPlaying && !isStopwatchRunning && !isVolumeToast && !isPowerToast && !isBrightnessToast && !isMediaToast)
        {
            hoverWidth += 92;
        }

        int idleWidth = idleDual ? 230 : (idleHasLive ? 168 : 148);

        return state switch
        {
            NotchState.Idle => (idleHasLive || showDesktopAmbient)
                ? (isVerticalEdge ? (24, idleWidth) : (idleWidth, 24))
                : (isVerticalEdge ? (ht, hw) : (hw, ht)),
            NotchState.CollapsedHover => (hoverWidth, 44),
            NotchState.PeekExpanded   => (680, 412),
            _                         => isVerticalEdge ? (ht, hw) : (hw, ht)
        };
    }

    public (double Left, double Top) ComputePosition(string dockEdge, double windowWidth, double windowHeight, int monitorIndex = 0)
    {
        var target = GetTargetMonitor(monitorIndex);

        if (target != null)
        {
            return dockEdge switch
            {
                "Left"  => (target.WorkLeft, target.WorkTop + (target.WorkHeight - windowHeight) / 2.0),
                "Right" => (target.WorkLeft + target.WorkWidth - windowWidth, target.WorkTop + (target.WorkHeight - windowHeight) / 2.0),
                _       => (target.WorkLeft + (target.WorkWidth - windowWidth) / 2.0, target.WorkTop)
            };
        }

        double screenW  = SystemParameters.PrimaryScreenWidth;
        double workTop  = SystemParameters.WorkArea.Top;
        double workH    = SystemParameters.WorkArea.Height;

        return dockEdge switch
        {
            "Left"  => (0,                             workTop + (workH - windowHeight) / 2.0),
            "Right" => (screenW - windowWidth,         workTop + (workH - windowHeight) / 2.0),
            _       => ((screenW - windowWidth) / 2.0, 0)
        };
    }
}
