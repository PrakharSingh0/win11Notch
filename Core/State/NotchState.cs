namespace Win11Notch.Core.State;

/// <summary>3-tier state machine states for the Win11 notch overlay.</summary>
public enum NotchState
{
    /// <summary>Minimal 72×5 px transparent pill. Zero background activity.</summary>
    Idle,

    /// <summary>240×34 px frosted Acrylic capsule showing time + status dot.</summary>
    CollapsedHover,

    /// <summary>540×240 px interactive hub. Only state that takes keyboard focus.</summary>
    PeekExpanded,

    /// <summary>Fully hidden when a fullscreen app is detected. Opacity=0, click-through.</summary>
    Stealth
}
