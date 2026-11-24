using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Win11Notch.Core.State;
using Win11Notch.Services;

namespace Win11Notch.ViewModels;

/// <summary>
/// Master ViewModel for the NotchWindow overlay.
/// Coordinates media controls, drop shelf, resource monitoring, and quick tools.
/// </summary>
public sealed partial class NotchViewModel : ObservableObject
{
    private readonly NotchStateMachine     _stateMachine;
    private readonly MediaService           _mediaService;
    private readonly ShelfService           _shelfService;
    private readonly AudioService           _audioService;
    private readonly ResourceMonitorService _resourceMonitor;

    [ObservableProperty] private NotchState   _currentState       = NotchState.Idle;
    [ObservableProperty] private string       _clockText          = string.Empty;
    [ObservableProperty] private string       _dateText           = string.Empty;
    [ObservableProperty] private int          _selectedTabIndex   = 0;

    // Media properties
    [ObservableProperty] private string       _mediaTitle         = "No Media Playing";
    [ObservableProperty] private string       _mediaArtist        = string.Empty;
    [ObservableProperty] private bool         _isMediaPlaying;
    [ObservableProperty] private bool         _hasMedia;
    [ObservableProperty] private ImageSource? _mediaThumbnail;

    // Resource properties
    [ObservableProperty] private int          _cpuPercent;
    [ObservableProperty] private int          _memoryPercent;
    [ObservableProperty] private int          _batteryPercent     = 100;
    [ObservableProperty] private bool         _isCharging;

    // Audio properties
    [ObservableProperty] private float        _masterVolume       = 50f;
    [ObservableProperty] private bool         _isMuted;

    // Shelf items collection
    public ObservableCollection<ShelfItem> ShelfItems => _shelfService.Items;

    private DispatcherTimer? _clockTimer;

    public NotchViewModel(
        NotchStateMachine     stateMachine,
        MediaService           mediaService,
        ShelfService           shelfService,
        AudioService           audioService,
        ResourceMonitorService resourceMonitor)
    {
        _stateMachine     = stateMachine;
        _mediaService     = mediaService;
        _shelfService     = shelfService;
        _audioService     = audioService;
        _resourceMonitor  = resourceMonitor;

        // Wire media updates
        _mediaService.MediaChanged += OnMediaChanged;

        // Wire resource stats
        _resourceMonitor.StatsUpdated += OnResourceStatsUpdated;

        // Sync initial volume
        _masterVolume = _audioService.GetMasterVolume() * 100f;
        _isMuted      = _audioService.GetMute();

        // State changes
        _stateMachine.StateChanged += (_, state) =>
        {
            CurrentState = state;

            if (state is NotchState.CollapsedHover or NotchState.PeekExpanded)
                StartClock();
            else
                StopClock();

            // Resource monitoring runs ONLY while Peek is expanded to keep idle CPU at 0%
            if (state == NotchState.PeekExpanded)
            {
                _resourceMonitor.StartMonitoring();
                _ = _mediaService.RefreshMediaInfoAsync();
            }
            else
            {
                _resourceMonitor.StopMonitoring();
            }
        };
    }

    private void OnMediaChanged()
    {
        MediaTitle      = _mediaService.Title;
        MediaArtist     = _mediaService.Artist;
        IsMediaPlaying  = _mediaService.IsPlaying;
        HasMedia        = _mediaService.HasMedia;
        MediaThumbnail  = _mediaService.Thumbnail;
    }

    private void OnResourceStatsUpdated()
    {
        CpuPercent     = _resourceMonitor.CpuPercent;
        MemoryPercent  = _resourceMonitor.MemoryPercent;
        BatteryPercent = _resourceMonitor.BatteryPercent;
        IsCharging     = _resourceMonitor.IsCharging;
    }

    // ── Media commands ────────────────────────────────────────────────────────

    [RelayCommand] private async Task PlayPause() => await _mediaService.TogglePlayPauseAsync();
    [RelayCommand] private async Task NextTrack() => await _mediaService.NextTrackAsync();
    [RelayCommand] private async Task PreviousTrack() => await _mediaService.PreviousTrackAsync();

    // ── Shelf commands ────────────────────────────────────────────────────────

    [RelayCommand] private void RemoveShelfItem(ShelfItem item) => _shelfService.RemoveItem(item);
    [RelayCommand] private void ClearShelf() => _shelfService.Clear();
    [RelayCommand] private void CopyShelfPaths() => _shelfService.CopyPathsToClipboard();

    // ── Audio commands ────────────────────────────────────────────────────────

    [RelayCommand]
    private void ToggleMute()
    {
        _audioService.ToggleMute();
        IsMuted = _audioService.GetMute();
    }

    partial void OnMasterVolumeChanged(float value)
    {
        _audioService.SetMasterVolume(value / 100f);
    }

    // ── Quick tool commands ───────────────────────────────────────────────────

    [RelayCommand]
    private static void OpenTaskManager() =>
        SafeLaunch("taskmgr.exe");

    [RelayCommand]
    private static void OpenSnippingTool() =>
        SafeLaunch("snippingtool.exe");

    [RelayCommand]
    private static void OpenNotepad() =>
        SafeLaunch("notepad.exe");

    [RelayCommand]
    private static void OpenCalculator() =>
        SafeLaunch("calc.exe");

    private static void SafeLaunch(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch { /* best effort */ }
    }

    // ── Clock ─────────────────────────────────────────────────────────────────

    private void StartClock()
    {
        if (_clockTimer is { IsEnabled: true }) return;

        UpdateDateTime();
        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, _) => UpdateDateTime();
        _clockTimer.Start();
    }

    private void UpdateDateTime()
    {
        var now   = DateTime.Now;
        ClockText = now.ToString("HH:mm");
        DateText  = now.ToString("ddd, MMM d");
    }

    private void StopClock()
    {
        _clockTimer?.Stop();
        _clockTimer = null;
    }
}
