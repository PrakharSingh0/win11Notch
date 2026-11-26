using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Win11Notch.Core.Settings;
using Win11Notch.Services;

namespace Win11Notch.ViewModels;

/// <summary>
/// ViewModel for the Fluent Settings dialog.
/// Supports real-time live synchronization with the overlay window.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsService _settingsService;
    private readonly StartupService  _startupService;
    private readonly MonitorService  _monitorService;
    private bool                      _isLoading;

    [ObservableProperty] private int    _selectedCategoryIndex        = 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDockTop))]
    [NotifyPropertyChangedFor(nameof(IsDockLeft))]
    [NotifyPropertyChangedFor(nameof(IsDockRight))]
    private string _dockEdge = "Top";

    public bool IsDockTop   => DockEdge == "Top";
    public bool IsDockLeft  => DockEdge == "Left";
    public bool IsDockRight => DockEdge == "Right";
    [ObservableProperty] private int    _idleOpacityPercent            = 20;
    [ObservableProperty] private int    _acrylicTintPercent            = 70;
    [ObservableProperty] private int    _hoverDwellMs                 = 100;
    [ObservableProperty] private int    _exitGraceMs                  = 300;
    [ObservableProperty] private bool   _launchAtStartup;
    [ObservableProperty] private bool   _autoExpandOnDrag             = true;
    [ObservableProperty] private bool   _showMediaIslandNotifications = true;
    [ObservableProperty] private bool   _showStartupGreeting          = true;
    [ObservableProperty] private string _hotkeyPreset                 = "Alt+Space";
    [ObservableProperty] private int    _targetMonitorIndex           = 0;
    [ObservableProperty] private int    _handleWidth                  = 72;
    [ObservableProperty] private int    _handleThickness              = 8;
    [ObservableProperty] private string _handleColor                  = "#E61A1C20";
    [ObservableProperty] private string _borderAnimationPreset        = "None";
    [ObservableProperty] private bool   _dynamicContentSizing         = true;
    [ObservableProperty] private string _handleVisibilityMode         = "SmartDesktop";
    [ObservableProperty] private bool   _showDesktopAmbientGlance     = true;
    [ObservableProperty] private bool   _alwaysShowHoverCapsule       = false;
    [ObservableProperty] private string _currentMemoryText            = "35.0 MB";
    [ObservableProperty] private string _sidebarGreetingText          = "Control Center";

    // Productivity & Timer Settings
    [ObservableProperty] private int    _defaultFocusMinutes          = 25;
    [ObservableProperty] private int    _defaultBreakMinutes          = 5;
    [ObservableProperty] private int    _dailyWaterTargetCups         = 8;
    [ObservableProperty] private bool   _timerSoundChimesEnabled      = true;
    [ObservableProperty] private bool   _dualActivityEnabled          = true;
    [ObservableProperty] private bool   _use24HourClock               = false;
    [ObservableProperty] private bool   _reminderSoundChimesEnabled   = true;
    [ObservableProperty] private int    _defaultReminderSnoozeMinutes = 10;

    public string AppVersionText    { get; }
    public string AuthorName        => "Prakhar Singh";
    public string GitHubRepoDisplay => "PrakharSingh0/win11Notch";
    public string GitHubRepoUrl     => "https://github.com/PrakharSingh0/win11Notch";
    public string LicenseTitle      => "MIT License";
    public string RuntimeInfoText   => $".NET {Environment.Version.Major} WPF • Per-Monitor V2 DPI • Windows 11";

    public ObservableCollection<MonitorItem> Monitors { get; } = new();

    public event Action? SettingsChanged;

    public SettingsViewModel(SettingsService settingsService, StartupService startupService, MonitorService? monitorService = null)
    {
        _settingsService = settingsService;
        _startupService  = startupService;
        _monitorService  = monitorService ?? new MonitorService();

        var ver = Assembly.GetExecutingAssembly().GetName().Version;
        AppVersionText = ver != null ? $"v{ver.Major}.{ver.Minor}.{ver.Build}" : "v1.0.0";

        LoadFromSettings();
    }

    public void RefreshDiagnostics()
    {
        long bytes = Process.GetCurrentProcess().WorkingSet64;
        CurrentMemoryText = $"{bytes / (1024.0 * 1024.0):F1} MB";
        SidebarGreetingText = GreetingHelper.GetGreeting(DateTime.Now).DisplayTitle;
    }

    public void LoadFromSettings()
    {
        _isLoading = true;
        try
        {
            var s = _settingsService.Current;
            DockEdge                     = s.DockEdge;
            IdleOpacityPercent           = Math.Clamp((int)Math.Round(s.IdleOpacity * 100), 10, 100);
            AcrylicTintPercent           = (int)Math.Round(s.AcrylicTintOpacity * 100);
            HoverDwellMs                 = s.HoverDwellMs;
            ExitGraceMs                  = s.ExitGraceMs;
            AutoExpandOnDrag             = s.AutoExpandOnDrag;
            ShowMediaIslandNotifications = s.ShowMediaIslandNotifications;
            ShowStartupGreeting          = s.ShowStartupGreeting;
            HotkeyPreset                 = s.HotkeyPreset;
            TargetMonitorIndex           = s.TargetMonitorIndex;
            HandleWidth                  = s.HandleWidth;
            HandleThickness              = s.HandleThickness;
            HandleColor                  = s.HandleColor;
            BorderAnimationPreset        = s.BorderAnimationPreset;
            DynamicContentSizing         = s.DynamicContentSizing;
            HandleVisibilityMode         = string.IsNullOrWhiteSpace(s.HandleVisibilityMode) ? "SmartDesktop" : s.HandleVisibilityMode;
            ShowDesktopAmbientGlance     = s.ShowDesktopAmbientGlance;
            AlwaysShowHoverCapsule       = s.AlwaysShowHoverCapsule;
            DefaultFocusMinutes          = s.DefaultFocusMinutes;
            DefaultBreakMinutes          = s.DefaultBreakMinutes;
            DailyWaterTargetCups         = s.DailyWaterTargetCups;
            TimerSoundChimesEnabled      = s.TimerSoundChimesEnabled;
            DualActivityEnabled          = s.DualActivityEnabled;
            Use24HourClock               = s.Use24HourClock;
            ReminderSoundChimesEnabled   = s.ReminderSoundChimesEnabled;
            DefaultReminderSnoozeMinutes = Math.Clamp(s.DefaultReminderSnoozeMinutes, 5, 60);
            LaunchAtStartup              = _startupService.IsStartupEnabled();

            Monitors.Clear();
            foreach (var m in _monitorService.GetMonitors())
            {
                Monitors.Add(m);
            }

            // Ensure TargetMonitorIndex maps to a real monitor Index (MonitorItem.Index is 1-based)
            if (TargetMonitorIndex <= 0 || !Monitors.Any(m => m.Index == TargetMonitorIndex))
            {
                TargetMonitorIndex = Monitors.FirstOrDefault(m => m.IsPrimary)?.Index
                                     ?? Monitors.FirstOrDefault()?.Index
                                     ?? 1;
            }
        }
        finally
        {
            _isLoading = false;
        }

        RefreshDiagnostics();
    }

    partial void OnDockEdgeChanged(string value)                     => ApplyAndSave();
    partial void OnIdleOpacityPercentChanged(int value)               => ApplyAndSave();
    partial void OnAcrylicTintPercentChanged(int value)               => ApplyAndSave();
    partial void OnHoverDwellMsChanged(int value)                     => ApplyAndSave();
    partial void OnExitGraceMsChanged(int value)                      => ApplyAndSave();
    partial void OnAutoExpandOnDragChanged(bool value)                => ApplyAndSave();
    partial void OnShowMediaIslandNotificationsChanged(bool value)        => ApplyAndSave();
    partial void OnShowStartupGreetingChanged(bool value)                 => ApplyAndSave();
    partial void OnHotkeyPresetChanged(string value)                 => ApplyAndSave();
    partial void OnTargetMonitorIndexChanged(int value)               => ApplyAndSave();
    partial void OnHandleWidthChanged(int value)                     => ApplyAndSave();
    partial void OnHandleThicknessChanged(int value)                 => ApplyAndSave();
    partial void OnHandleColorChanged(string value)                  => ApplyAndSave();
    partial void OnBorderAnimationPresetChanged(string value)        => ApplyAndSave();
    partial void OnDynamicContentSizingChanged(bool value)           => ApplyAndSave();
    partial void OnHandleVisibilityModeChanged(string value)         => ApplyAndSave();
    partial void OnShowDesktopAmbientGlanceChanged(bool value)       => ApplyAndSave();
    partial void OnAlwaysShowHoverCapsuleChanged(bool value)         => ApplyAndSave();
    partial void OnDefaultFocusMinutesChanged(int value)             => ApplyAndSave();
    partial void OnDefaultBreakMinutesChanged(int value)             => ApplyAndSave();
    partial void OnDailyWaterTargetCupsChanged(int value)            => ApplyAndSave();
    partial void OnTimerSoundChimesEnabledChanged(bool value)        => ApplyAndSave();
    partial void OnDualActivityEnabledChanged(bool value)            => ApplyAndSave();
    partial void OnUse24HourClockChanged(bool value)                 => ApplyAndSave();
    partial void OnReminderSoundChimesEnabledChanged(bool value)     => ApplyAndSave();
    partial void OnDefaultReminderSnoozeMinutesChanged(int value)    => ApplyAndSave();

    [RelayCommand] private void SelectHandleColor(string hex) => HandleColor = hex;

    partial void OnLaunchAtStartupChanged(bool value)
    {
        if (_isLoading) return;
        _startupService.SetStartupEnabled(value);
        _settingsService.Current.LaunchAtStartup = value;
        _ = _settingsService.SaveAsync();
    }

    private void ApplyAndSave()
    {
        if (_isLoading) return;

        var s = _settingsService.Current;
        s.DockEdge                     = DockEdge;
        s.IdleOpacity                  = Math.Clamp(IdleOpacityPercent, 10, 100) / 100.0;
        s.AcrylicTintOpacity           = AcrylicTintPercent / 100.0;
        s.HoverDwellMs                 = HoverDwellMs;
        s.ExitGraceMs                  = ExitGraceMs;
        s.AutoExpandOnDrag             = AutoExpandOnDrag;
        s.ShowMediaIslandNotifications = ShowMediaIslandNotifications;
        s.ShowStartupGreeting          = ShowStartupGreeting;
        s.HotkeyPreset                 = HotkeyPreset;
        s.TargetMonitorIndex           = TargetMonitorIndex;
        s.HandleWidth                  = HandleWidth;
        s.HandleThickness              = HandleThickness;
        s.HandleColor                  = HandleColor;
        s.BorderAnimationPreset        = BorderAnimationPreset;
        s.DynamicContentSizing         = DynamicContentSizing;
        s.HandleVisibilityMode         = HandleVisibilityMode;
        s.ShowDesktopAmbientGlance     = ShowDesktopAmbientGlance;
        s.AlwaysShowHoverCapsule       = AlwaysShowHoverCapsule;
        s.DefaultFocusMinutes          = DefaultFocusMinutes;
        s.DefaultBreakMinutes          = DefaultBreakMinutes;
        s.DailyWaterTargetCups         = DailyWaterTargetCups;
        s.TimerSoundChimesEnabled      = TimerSoundChimesEnabled;
        s.DualActivityEnabled          = DualActivityEnabled;
        s.Use24HourClock               = Use24HourClock;
        s.ReminderSoundChimesEnabled   = ReminderSoundChimesEnabled;
        s.DefaultReminderSnoozeMinutes = DefaultReminderSnoozeMinutes;

        _ = _settingsService.SaveAsync();
        SettingsChanged?.Invoke();
    }

    [RelayCommand]
    private void SelectDockEdge(string? edge)
    {
        if (edge is "Top" or "Left" or "Right")
        {
            DockEdge = edge;
        }
    }

    [RelayCommand]
    private void ResetDefaults()
    {
        _isLoading = true;
        try
        {
            DockEdge                     = "Top";
            IdleOpacityPercent           = 20;
            AcrylicTintPercent           = 70;
            HoverDwellMs                 = 100;
            ExitGraceMs                  = 300;
            AutoExpandOnDrag             = true;
            ShowMediaIslandNotifications = true;
            ShowStartupGreeting          = true;
            HotkeyPreset                 = "Alt+Space";
            TargetMonitorIndex           = Monitors.FirstOrDefault(m => m.IsPrimary)?.Index
                                           ?? Monitors.FirstOrDefault()?.Index
                                           ?? 1;
            HandleWidth                  = 72;
            HandleThickness              = 8;
            HandleColor                  = "#E61A1C20";
            BorderAnimationPreset        = "None";
            DynamicContentSizing         = true;
            HandleVisibilityMode         = "SmartDesktop";
            ShowDesktopAmbientGlance     = true;
            AlwaysShowHoverCapsule       = false;
            DefaultFocusMinutes          = 25;
            DefaultBreakMinutes          = 5;
            DailyWaterTargetCups         = 8;
            TimerSoundChimesEnabled      = true;
            DualActivityEnabled          = true;
            Use24HourClock               = false;
            ReminderSoundChimesEnabled   = true;
            DefaultReminderSnoozeMinutes = 10;
            LaunchAtStartup              = false;
            _startupService.SetStartupEnabled(false);
        }
        finally
        {
            _isLoading = false;
        }
        ApplyAndSave();
    }

    [RelayCommand]
    private void OpenGitHub()
    {
        if (Uri.TryCreate(GitHubRepoUrl, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
        {
            try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); } catch { }
        }
    }

    [RelayCommand]
    private static void OpenAppDataFolder()
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Win11Notch");
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
        }
        catch { }
    }
}
