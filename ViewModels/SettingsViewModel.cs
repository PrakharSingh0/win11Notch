using System.Collections.ObjectModel;
using System.Diagnostics;
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
    private readonly MonitorService  _monitorService = new();
    private bool                      _isLoading;

    [ObservableProperty] private string _dockEdge                     = "Top";
    [ObservableProperty] private int    _idleOpacityPercent            = 20;
    [ObservableProperty] private int    _acrylicTintPercent            = 70;
    [ObservableProperty] private int    _hoverDwellMs                 = 100;
    [ObservableProperty] private int    _exitGraceMs                  = 300;
    [ObservableProperty] private bool   _launchAtStartup;
    [ObservableProperty] private bool   _autoExpandOnDrag             = true;
    [ObservableProperty] private bool   _showMediaIslandNotifications = true;
    [ObservableProperty] private string _hotkeyPreset                 = "Alt+Space";
    [ObservableProperty] private int    _targetMonitorIndex           = 0;
    [ObservableProperty] private int    _handleWidth                  = 72;
    [ObservableProperty] private int    _handleThickness              = 8;
    [ObservableProperty] private string _handleColor                  = "#E61A1C20";
    [ObservableProperty] private string _borderAnimationPreset        = "None";
    [ObservableProperty] private bool   _dynamicContentSizing         = true;
    [ObservableProperty] private string _currentMemoryText            = "35.0 MB";

    public ObservableCollection<MonitorItem> Monitors { get; } = new();

    public event Action? SettingsChanged;

    public SettingsViewModel(SettingsService settingsService, StartupService startupService)
    {
        _settingsService = settingsService;
        _startupService  = startupService;

        LoadFromSettings();
    }

    public void RefreshDiagnostics()
    {
        long bytes = Process.GetCurrentProcess().WorkingSet64;
        CurrentMemoryText = $"{bytes / (1024.0 * 1024.0):F1} MB";
    }

    private void LoadFromSettings()
    {
        _isLoading = true;
        try
        {
            var s = _settingsService.Current;
            DockEdge                     = s.DockEdge;
            IdleOpacityPercent           = (int)Math.Round(s.IdleOpacity * 100);
            AcrylicTintPercent           = (int)Math.Round(s.AcrylicTintOpacity * 100);
            HoverDwellMs                 = s.HoverDwellMs;
            ExitGraceMs                  = s.ExitGraceMs;
            AutoExpandOnDrag             = s.AutoExpandOnDrag;
            ShowMediaIslandNotifications = s.ShowMediaIslandNotifications;
            HotkeyPreset                 = s.HotkeyPreset;
            TargetMonitorIndex           = s.TargetMonitorIndex;
            HandleWidth                  = s.HandleWidth;
            HandleThickness              = s.HandleThickness;
            HandleColor                  = s.HandleColor;
            BorderAnimationPreset        = s.BorderAnimationPreset;
            DynamicContentSizing         = s.DynamicContentSizing;
            LaunchAtStartup              = _startupService.IsStartupEnabled();

            Monitors.Clear();
            foreach (var m in _monitorService.GetMonitors())
            {
                Monitors.Add(m);
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
    partial void OnHotkeyPresetChanged(string value)                 => ApplyAndSave();
    partial void OnTargetMonitorIndexChanged(int value)               => ApplyAndSave();
    partial void OnHandleWidthChanged(int value)                     => ApplyAndSave();
    partial void OnHandleThicknessChanged(int value)                 => ApplyAndSave();
    partial void OnHandleColorChanged(string value)                  => ApplyAndSave();
    partial void OnBorderAnimationPresetChanged(string value)        => ApplyAndSave();
    partial void OnDynamicContentSizingChanged(bool value)           => ApplyAndSave();

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
        s.IdleOpacity                  = IdleOpacityPercent / 100.0;
        s.AcrylicTintOpacity           = AcrylicTintPercent / 100.0;
        s.HoverDwellMs                 = HoverDwellMs;
        s.ExitGraceMs                  = ExitGraceMs;
        s.AutoExpandOnDrag             = AutoExpandOnDrag;
        s.ShowMediaIslandNotifications = ShowMediaIslandNotifications;
        s.HotkeyPreset                 = HotkeyPreset;
        s.TargetMonitorIndex           = TargetMonitorIndex;
        s.HandleWidth                  = HandleWidth;
        s.HandleThickness              = HandleThickness;
        s.HandleColor                  = HandleColor;
        s.BorderAnimationPreset        = BorderAnimationPreset;
        s.DynamicContentSizing         = DynamicContentSizing;

        _ = _settingsService.SaveAsync();
        SettingsChanged?.Invoke();
    }

    [RelayCommand]
    private void ResetDefaults()
    {
        DockEdge                     = "Top";
        IdleOpacityPercent           = 20;
        AcrylicTintPercent           = 70;
        HoverDwellMs                 = 100;
        ExitGraceMs                  = 300;
        AutoExpandOnDrag             = true;
        ShowMediaIslandNotifications = true;
        HotkeyPreset                 = "Alt+Space";
        TargetMonitorIndex           = 0;
        HandleWidth                  = 72;
        HandleThickness              = 8;
        HandleColor                  = "#E61A1C20";
        BorderAnimationPreset        = "None";
        DynamicContentSizing         = true;
        LaunchAtStartup              = false;
    }
}
