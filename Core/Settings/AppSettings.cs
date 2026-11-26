namespace Win11Notch.Core.Settings;

/// <summary>
/// Serialisable settings model persisted to %AppData%\Win11Notch\settings.json.
/// All defaults match the GEMINI.md specification.
/// </summary>
public sealed class AppSettings
{
    /// <summary>Window opacity while in Idle state (0.10–1.00). Default 0.20 = 20%.</summary>
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

    /// <summary>Whether to display a time-aware greeting toast banner when Win11Notch starts.</summary>
    public bool ShowStartupGreeting { get; set; } = true;

    /// <summary>Global hotkey preset (e.g. "Alt+Space", "Win+Z", "Ctrl+Shift+Space", "Ctrl+~").</summary>
    public string HotkeyPreset { get; set; } = "Alt+Space";

    /// <summary>Target monitor index (0 = Primary, 1 = Display 1, 2 = Display 2...).</summary>
    public int TargetMonitorIndex { get; set; } = 0;

    /// <summary>Idle pill width in pixels (40–180). Default 72.</summary>
    public int HandleWidth { get; set; } = 72;

    /// <summary>Idle pill thickness/height in pixels (4–16). Default 8.</summary>
    public int HandleThickness { get; set; } = 8;

    /// <summary>Idle pill background tint hex color. Default "#E61A1C20".</summary>
    public string HandleColor { get; set; } = "#E61A1C20";

    /// <summary>Border animation effect preset: "None" | "BreathingGlow" | "RainbowWave" | "NeonGlow". Default "None".</summary>
    public string BorderAnimationPreset { get; set; } = "None";

    /// <summary>Whether CollapsedHover dynamically sizes to fit active content.</summary>
    public bool DynamicContentSizing { get; set; } = true;

    /// <summary>Default focus duration in minutes for Pomodoro.</summary>
    public int DefaultFocusMinutes { get; set; } = 25;

    /// <summary>Default break duration in minutes for Pomodoro.</summary>
    public int DefaultBreakMinutes { get; set; } = 5;

    /// <summary>Daily water intake target in cups (250ml each).</summary>
    public int DailyWaterTargetCups { get; set; } = 8;

    /// <summary>Whether sound chimes play on timer/pomodoro completion.</summary>
    public bool TimerSoundChimesEnabled { get; set; } = true;

    /// <summary>Whether to show dual-activity split presentation when music and timers run concurrently.</summary>
    public bool DualActivityEnabled { get; set; } = true;

    /// <summary>
    /// Idle handle context behavior:
    /// "SmartDesktop" (Live preview on Desktop, minimal pill over apps),
    /// "DesktopOnly" (Live preview on Desktop only, hidden over apps),
    /// "AlwaysLive" (Always show live preview everywhere).
    /// </summary>
    public string HandleVisibilityMode { get; set; } = "SmartDesktop";

    /// <summary>Whether to show an ambient clock &amp; status glance pill when resting on the Desktop.</summary>
    public bool ShowDesktopAmbientGlance { get; set; } = true;

    /// <summary>Whether to keep the compact HoverCapsule visible at all times instead of shrinking to IdlePill.</summary>
    public bool AlwaysShowHoverCapsule { get; set; } = false;

    /// <summary>Whether to format clocks and calendar schedules in 24-hour format (false = 12-hour AM/PM).</summary>
    public bool Use24HourClock { get; set; } = false;

    /// <summary>Whether notification chimes play when calendar reminders become due.</summary>
    public bool ReminderSoundChimesEnabled { get; set; } = true;

    /// <summary>Default snooze duration in minutes for active reminder pills.</summary>
    public int DefaultReminderSnoozeMinutes { get; set; } = 10;
}
