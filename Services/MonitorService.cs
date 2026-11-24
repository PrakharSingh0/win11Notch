using System.Runtime.InteropServices;
using System.Windows;
using Win11Notch.Core.State;

namespace Win11Notch.Services;

public sealed class MonitorItem
{
    public int    Index     { get; init; }
    public string Name      { get; init; } = string.Empty;
    public bool   IsPrimary { get; init; }
    public double Left      { get; init; }
    public double Top       { get; init; }
    public double Width     { get; init; }
    public double Height    { get; init; }
    public double WorkLeft  { get; init; }
    public double WorkTop   { get; init; }
    public double WorkWidth { get; init; }
    public double WorkHeight{ get; init; }
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
                double w = mi.rcMonitor.Right - mi.rcMonitor.Left;
                double h = mi.rcMonitor.Bottom - mi.rcMonitor.Top;
                double ww = mi.rcWork.Right - mi.rcWork.Left;
                double wh = mi.rcWork.Bottom - mi.rcWork.Top;

                string label = isPrimary ? $"Primary Display ({w:F0}×{h:F0})" : $"Display {index} ({w:F0}×{h:F0})";

                list.Add(new MonitorItem
                {
                    Index      = index,
                    Name       = label,
                    IsPrimary  = isPrimary,
                    Left       = mi.rcMonitor.Left,
                    Top        = mi.rcMonitor.Top,
                    Width      = w,
                    Height     = h,
                    WorkLeft   = mi.rcWork.Left,
                    WorkTop    = mi.rcWork.Top,
                    WorkWidth  = ww,
                    WorkHeight = wh
                });
                index++;
            }
            return true;
        }, IntPtr.Zero);

        return list;
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
        bool dynamicSizing = true)
    {
        bool isVerticalEdge = dockEdge is "Left" or "Right";
        int hw = Math.Clamp(handleWidth, 40, 200);
        int ht = Math.Clamp(handleThickness, 4, 16);

        double hoverWidth;
        if (!dynamicSizing)
        {
            hoverWidth = (isVolumeToast || isPowerToast) ? 290 : (isMediaToast ? 370 : 280);
        }
        else if (isVolumeToast || isPowerToast)
        {
            hoverWidth = 290;
        }
        else if (isMediaToast)
        {
            hoverWidth = 370;
        }
        else if (isMediaPlaying && isStopwatchRunning)
        {
            hoverWidth = 450;
        }
        else if (isMediaPlaying)
        {
            hoverWidth = 370;
        }
        else if (isStopwatchRunning)
        {
            hoverWidth = 310;
        }
        else
        {
            hoverWidth = 270; // minimal clock, date & network badge capsule
        }

        return state switch
        {
            NotchState.Idle => (isMediaPlaying || isStopwatchRunning)
                ? (isVerticalEdge ? (22, 160) : (160, 22))
                : (isVerticalEdge ? (ht, hw) : (hw, ht)),
            NotchState.CollapsedHover => isVerticalEdge ? (42, hoverWidth) : (hoverWidth, 42),
            NotchState.PeekExpanded => isVerticalEdge ? (390, 650) : (650, 390),
            _ => isVerticalEdge ? (ht, hw) : (hw, ht)
        };
    }

    public (double Left, double Top) ComputePosition(string dockEdge, double windowWidth, double windowHeight, int monitorIndex = 0)
    {
        var monitors = GetMonitors();
        var target = (monitorIndex > 0 && monitorIndex <= monitors.Count)
            ? monitors[monitorIndex - 1]
            : monitors.FirstOrDefault(m => m.IsPrimary) ?? (monitors.Count > 0 ? monitors[0] : null);

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
