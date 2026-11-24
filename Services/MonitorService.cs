using System.Windows;
using Win11Notch.Core.State;

namespace Win11Notch.Services;

/// <summary>
/// Provides screen-geometry helpers for notch window positioning across Top, Left, and Right docking edges.
/// Returns coordinates in WPF logical DIPs.
/// Supports expanded Dynamic Island notification pill sizes (320px for Music, 260px for Volume HUD).
/// </summary>
public sealed class MonitorService
{
    public (double Width, double Height) GetDimensions(
        string dockEdge,
        NotchState state,
        bool isMediaToast = false,
        bool isVolumeToast = false,
        bool isPowerToast = false)
    {
        bool isVerticalEdge = dockEdge is "Left" or "Right";
        double hoverWidth   = (isVolumeToast || isPowerToast) ? 260 : (isMediaToast ? 320 : 240);

        return state switch
        {
            NotchState.Idle => isVerticalEdge ? (8, 72) : (72, 8),
            NotchState.CollapsedHover => isVerticalEdge ? (34, hoverWidth) : (hoverWidth, 34),
            NotchState.PeekExpanded => isVerticalEdge ? (240, 540) : (540, 240),
            _ => isVerticalEdge ? (8, 72) : (72, 8)
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
