namespace Win11Notch.Core.Settings;

/// <summary>
/// Serialisable settings model persisted to %AppData%\Win11Notch\settings.json.
/// All defaults match the GEMINI.md specification.
/// </summary>
public sealed class AppSettings
{
    /// <summary>Window opacity while in Idle state (0–1). Default 0.20 = 20%.</summary>
    public double IdleOpacity { get; set; } = 0.20;

    /// <summary>Dock edge: "Top" | "Left" | "Right".</summary>
    public string DockEdge { get; set; } = "Top";

    /// <summary>Milliseconds of hover dwell before CollapsedHover activates.</summary>
    public int HoverDwellMs { get; set; } = 100;

    /// <summary>Milliseconds of grace period before CollapsedHover collapses after mouse-leave.</summary>
    public int ExitGraceMs { get; set; } = 300;

    /// <summary>Alpha for the WPF IPS tint overlay (0–1). 0.70 = 70% opaque dark-slate.</summary>
    public double AcrylicTintOpacity { get; set; } = 0.70;

    /// <summary>Whether to start Win11Notch automatically with Windows.</summary>
    public bool LaunchAtStartup { get; set; } = false;

    /// <summary>Whether dragging a file over the notch automatically expands it.</summary>
    public bool AutoExpandOnDrag { get; set; } = true;

    /// <summary>Whether to show Dynamic Island temporary toast on song change.</summary>
    public bool ShowMediaIslandNotifications { get; set; } = true;

    /// <summary>Global hotkey preset (e.g. "Alt+Space", "Win+Z", "Ctrl+Shift+Space", "Ctrl+~").</summary>
    public string HotkeyPreset { get; set; } = "Alt+Space";
}
