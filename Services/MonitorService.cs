using System.Windows;
using Win11Notch.Core.State;

namespace Win11Notch.Services;

/// <summary>
/// Provides screen-geometry helpers for notch window positioning across Top, Left, and Right docking edges.
/// Returns coordinates in WPF logical DIPs (DPI-unaware; WPF handles scaling via PerMonitorV2 manifest).
/// </summary>
public sealed class MonitorService
{
    public (double Width, double Height) GetDimensions(string dockEdge, NotchState state)
    {
        bool isVerticalEdge = dockEdge is "Left" or "Right";

        return state switch
        {
            NotchState.Idle => isVerticalEdge ? (5, 72) : (72, 5),
            NotchState.CollapsedHover => isVerticalEdge ? (34, 240) : (240, 34),
            NotchState.PeekExpanded => isVerticalEdge ? (240, 540) : (540, 240),
            _ => (72, 5)
        };
    }

    public (double Left, double Top) ComputePosition(string dockEdge, double windowWidth, double windowHeight)
    {
        double screenW  = SystemParameters.PrimaryScreenWidth;
        double screenH  = SystemParameters.PrimaryScreenHeight;
        double workLeft = SystemParameters.WorkArea.Left;
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
