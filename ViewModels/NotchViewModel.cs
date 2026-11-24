using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Win11Notch.Core.State;
using Win11Notch.Models;
using Win11Notch.Services;

namespace Win11Notch.ViewModels;

/// <summary>
/// Master ViewModel for the NotchWindow overlay.
/// Coordinates media controls, drop shelf, clipboard history, screen capture,
/// hardware volume HUD, media timeline, resource monitoring, tools widgets, and settings.
/// </summary>
public sealed partial class NotchViewModel : ObservableObject
{
    private readonly NotchStateMachine      _stateMachine;
    private readonly MediaService            _mediaService;
    private readonly ShelfService            _shelfService;
    private readonly AudioService            _audioService;
    private readonly ResourceMonitorService  _resourceMonitor;
    private readonly ClipboardService        _clipboardService;
    private readonly ScreenCaptureService    _screenCaptureService;

    [ObservableProperty] private NotchState   _currentState       = NotchState.Idle;
    [ObservableProperty] private string       _clockText          = string.Empty;
    [ObservableProperty] private string       _dateText           = string.Empty;
    [ObservableProperty] private int          _selectedTabIndex   = 0;
    [ObservableProperty] private int          _shelfSubTabIndex   = 0; // 0: Files, 1: Clipboard

    // Media properties
    [ObservableProperty] private string       _mediaTitle             = "No Media Playing";
    [ObservableProperty] private string       _mediaArtist            = string.Empty;
    [ObservableProperty] private bool         _isMediaPlaying;
    [ObservableProperty] private bool         _hasMedia;
    [ObservableProperty] private ImageSource? _mediaThumbnail;
    [ObservableProperty] private bool         _isMediaToastActive;
    [ObservableProperty] private string       _mediaPositionText      = "00:00";
    [ObservableProperty] private string       _mediaDurationText      = "00:00";
    [ObservableProperty] private double       _mediaProgressFraction;
    [ObservableProperty] private double       _mediaProgressPercent;

    public event Action<bool>? MediaToastActiveChanged;
    partial void OnIsMediaToastActiveChanged(bool value) => MediaToastActiveChanged?.Invoke(value);

    // Audio properties & Volume HUD
    [ObservableProperty] private float        _masterVolume           = 50f;
    [ObservableProperty] private bool         _isMuted;
    [ObservableProperty] private bool         _isVolumeToastActive;
    [ObservableProperty] private string       _activeAudioDeviceName  = "Default Playback Device";
    [ObservableProperty] private bool         _isAudioDeviceMenuOpen;
    public ObservableCollection<AudioDeviceItem> AudioDevices { get; } = new();
    private bool                              _isSyncingVolume;

    public event Action<bool>? VolumeToastActiveChanged;
    partial void OnIsVolumeToastActiveChanged(bool value) => VolumeToastActiveChanged?.Invoke(value);

    // Power & Battery Toast properties
    [ObservableProperty] private bool         _isPowerToastActive;
    [ObservableProperty] private string       _powerToastIcon         = "⚡";
    [ObservableProperty] private string       _powerToastTitle        = "Charger Connected";
    [ObservableProperty] private string       _powerToastDetail       = "85%";
    public event Action<bool>? PowerToastActiveChanged;
    partial void OnIsPowerToastActiveChanged(bool value) => PowerToastActiveChanged?.Invoke(value);

    // Resource properties
    [ObservableProperty] private int             _cpuPercent;
    [ObservableProperty] private int             _memoryPercent;
    [ObservableProperty] private string          _ramText             = "0.0 / 0.0 GB";
    [ObservableProperty] private int             _gpuPercent;
    [ObservableProperty] private string          _vramText            = "0.00 GB";
    [ObservableProperty] private string          _networkName         = "WiFi";
    [ObservableProperty] private string          _networkDetail       = "Wi-Fi - Connected";
    [ObservableProperty] private string          _downloadSpeedText   = "0 B/s";
    [ObservableProperty] private string          _uploadSpeedText     = "0 B/s";
    [ObservableProperty] private int             _batteryPercent      = 100;
    [ObservableProperty] private bool            _isCharging;
    [ObservableProperty] private string          _batteryStatusText   = "Fully charged";
    [ObservableProperty] private string          _batteryHealthText   = "Health 100%";

    // Storage partitions
    public ObservableCollection<DriveItem>       Drives               => _resourceMonitor.Drives;
    [ObservableProperty] private DriveItem?      _selectedDrive;
    [ObservableProperty] private bool            _isDriveMenuOpen;

    // Connected Audio Device & Bluetooth Battery
    [ObservableProperty] private string          _bluetoothBatteryText = string.Empty;
    [ObservableProperty] private bool            _hasBluetoothBattery;

    // Sparkline Graph & Area Point Collections
    [ObservableProperty] private PointCollection _cpuGraphPoints      = new();
    [ObservableProperty] private PointCollection _cpuAreaPoints       = new();
    [ObservableProperty] private PointCollection _ramGraphPoints      = new();
    [ObservableProperty] private PointCollection _ramAreaPoints       = new();
    [ObservableProperty] private PointCollection _gpuGraphPoints      = new();
    [ObservableProperty] private PointCollection _gpuAreaPoints       = new();
    [ObservableProperty] private int             _diskPercent;
    [ObservableProperty] private PointCollection _diskGraphPoints      = new();
    [ObservableProperty] private PointCollection _diskAreaPoints       = new();
    [ObservableProperty] private PointCollection _downloadGraphPoints = new();
    [ObservableProperty] private PointCollection _downloadAreaPoints  = new();
    [ObservableProperty] private PointCollection _uploadGraphPoints   = new();
    [ObservableProperty] private PointCollection _uploadAreaPoints    = new();
    [ObservableProperty] private bool            _isResourceVolumeMode;

    // Shelf badge
    [ObservableProperty] private bool         _hasStagedFiles;
    [ObservableProperty] private string       _shelfCountText         = string.Empty;

    // Stopwatch tool
    [ObservableProperty] private string       _stopwatchText          = "00:00.0";
    [ObservableProperty] private bool         _isStopwatchRunning;
    private readonly Stopwatch                _stopwatch              = new();
    private DispatcherTimer?                  _stopwatchTimer;

    // Events
    public event Action? OpenSettingsRequested;

    // Collections
    public ObservableCollection<ShelfItem>     ShelfItems               => _shelfService.Items;
    public ObservableCollection<ClipboardItem> ClipboardHistory         => _clipboardService.History;
    public ObservableCollection<ClipboardItem> FilteredClipboardHistory { get; } = new();

    [ObservableProperty] private string _clipboardSearchText = string.Empty;

    private DispatcherTimer? _clockTimer;
    private DispatcherTimer? _timelineTimer;

    public NotchViewModel(
        NotchStateMachine      stateMachine,
        MediaService            mediaService,
        ShelfService            shelfService,
        AudioService            audioService,
        ResourceMonitorService  resourceMonitor,
        ClipboardService        clipboardService,
        ScreenCaptureService    screenCaptureService)
    {
        _stateMachine          = stateMachine;
        _mediaService          = mediaService;
        _shelfService          = shelfService;
        _audioService          = audioService;
        _resourceMonitor       = resourceMonitor;
        _clipboardService      = clipboardService;
        _screenCaptureService  = screenCaptureService;

        // Wire media updates & timeline
        _mediaService.MediaChanged    += OnMediaChanged;
        _mediaService.TimelineChanged += OnTimelineChanged;

        // Wire resource stats
        _resourceMonitor.StatsUpdated += OnResourceStatsUpdated;

        // Wire shelf changes
        _shelfService.Items.CollectionChanged += (_, _) =>
        {
            HasStagedFiles = _shelfService.Items.Count > 0;
            ShelfCountText = _shelfService.Items.Count > 0 ? $"📦 {_shelfService.Items.Count}" : string.Empty;
        };

        // Wire clipboard changes
        _clipboardService.History.CollectionChanged += (_, _) => RefreshFilteredClipboard();
        RefreshFilteredClipboard();

        // Sync initial volume and audio device
        _isSyncingVolume = true;
        try
        {
            _masterVolume = (float)Math.Round(_audioService.GetMasterVolume() * 100f);
            _isMuted      = _audioService.GetMute();
        }
        finally
        {
            _isSyncingVolume = false;
        }

        _activeAudioDeviceName = _audioService.ActiveDeviceName;

        _audioService.ActiveDeviceChanged += name =>
        {
            ActiveAudioDeviceName = name;
            RefreshAudioDevices(updateDeviceInfo: false);
        };

        _audioService.VolumeChanged += (vol, muted, _) =>
        {
            UpdateVolumeFromDevice(vol * 100f, muted);
        };

        // Timeline ticking timer (only active while PeekExpanded is open and music is playing)
        _timelineTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _timelineTimer.Tick += (_, _) =>
        {
            if (CurrentState == NotchState.PeekExpanded && IsMediaPlaying)
            {
                _mediaService.UpdateTimeline();
            }
        };

        // State changes
        _stateMachine.StateChanged += (_, state) =>
        {
            CurrentState = state;

            if (state is NotchState.CollapsedHover or NotchState.PeekExpanded)
                StartClock();
            else
                StopClock();

            // Resource monitoring runs while Hover or Peek is active (stopped in Idle)
            if (state is NotchState.CollapsedHover or NotchState.PeekExpanded)
            {
                _resourceMonitor.StartMonitoring();
                _audioService.RefreshActiveDevice();
                ActiveAudioDeviceName = _audioService.ActiveDeviceName;
                _ = _mediaService.RefreshMediaInfoAsync();

                if (state == NotchState.PeekExpanded && IsMediaPlaying)
                {
                    _timelineTimer.Start();
                }
            }
            else
            {
                _resourceMonitor.StopMonitoring();
                _timelineTimer.Stop();
            }
        };
    }

    public void UpdateVolumeFromDevice(float volume, bool isMuted)
    {
        _isSyncingVolume = true;
        try
        {
            MasterVolume = (float)Math.Round(volume);
            IsMuted      = isMuted;
        }
        finally
        {
            _isSyncingVolume = false;
        }
    }

    private void OnMediaChanged()
    {
        MediaTitle      = _mediaService.Title;
        MediaArtist     = _mediaService.Artist;
        IsMediaPlaying  = _mediaService.IsPlaying;
        HasMedia        = _mediaService.HasMedia;
        MediaThumbnail  = _mediaService.Thumbnail;
        OnTimelineChanged();

        if (CurrentState == NotchState.PeekExpanded && IsMediaPlaying)
        {
            _timelineTimer?.Start();
        }
        else if (!IsMediaPlaying)
        {
            _timelineTimer?.Stop();
        }
    }

    private void OnTimelineChanged()
    {
        MediaPositionText     = _mediaService.PositionText;
        MediaDurationText     = _mediaService.DurationText;
        MediaProgressFraction = _mediaService.ProgressFraction;
        MediaProgressPercent  = Math.Clamp(_mediaService.ProgressFraction * 100.0, 0.0, 100.0);
    }

    private void OnResourceStatsUpdated()
    {
        CpuPercent         = _resourceMonitor.CpuPercent;
        MemoryPercent      = _resourceMonitor.MemoryPercent;
        RamText            = $"{_resourceMonitor.RamUsedGb:F1} / {_resourceMonitor.RamTotalGb:F1} GB";
        GpuPercent         = _resourceMonitor.GpuPercent;
        VramText           = _resourceMonitor.VramUsedGb > 0 ? $"{_resourceMonitor.VramUsedGb:F2} GB" : "Shared";
        DiskPercent        = _resourceMonitor.DiskPercent;
        NetworkName        = _resourceMonitor.NetworkName;
        NetworkDetail      = _resourceMonitor.NetworkDetail;
        DownloadSpeedText  = _resourceMonitor.DownloadSpeedText;
        UploadSpeedText    = _resourceMonitor.UploadSpeedText;
        BatteryPercent     = _resourceMonitor.BatteryPercent;
        IsCharging         = _resourceMonitor.IsCharging;
        BatteryStatusText  = _resourceMonitor.BatteryStatusText;
        BatteryHealthText  = _resourceMonitor.BatteryHealthText;

        // Sync selected drive & audio info
        SelectedDrive = _resourceMonitor.SelectedDrive;
        UpdateAudioDeviceInfo();

        // Generate Sparkline line & area point collections
        var (cpuL, cpuA) = GenerateGraphAndArea(_resourceMonitor.CpuHistory, 100, 20, 100);
        CpuGraphPoints = cpuL;
        CpuAreaPoints  = cpuA;

        var (ramL, ramA) = GenerateGraphAndArea(_resourceMonitor.RamHistory, 100, 20, 100);
        RamGraphPoints = ramL;
        RamAreaPoints  = ramA;

        var (gpuL, gpuA) = GenerateGraphAndArea(_resourceMonitor.GpuHistory, 100, 20, 100);
        GpuGraphPoints = gpuL;
        GpuAreaPoints  = gpuA;

        var (diskL, diskA) = GenerateGraphAndArea(_resourceMonitor.DiskHistory, 100, 20, 100, autoScale: true);
        DiskGraphPoints = diskL;
        DiskAreaPoints  = diskA;

        var (dnL, dnA)   = GenerateGraphAndArea(_resourceMonitor.DownloadHistory, 60, 16, 50, autoScale: true);
        DownloadGraphPoints = dnL;
        DownloadAreaPoints  = dnA;

        var (upL, upA)   = GenerateGraphAndArea(_resourceMonitor.UploadHistory, 60, 16, 50, autoScale: true);
        UploadGraphPoints = upL;
        UploadAreaPoints  = upA;
    }

    private static (PointCollection line, PointCollection area) GenerateGraphAndArea(
        IReadOnlyList<double> history, double width, double height, double maxVal, bool autoScale = false)
    {
        var line = new PointCollection();
        var area = new PointCollection();

        if (history == null || history.Count == 0)
        {
            line.Add(new Point(0, height - 2));
            line.Add(new Point(width, height - 2));

            area.Add(new Point(0, height));
            area.Add(new Point(0, height - 2));
            area.Add(new Point(width, height - 2));
            area.Add(new Point(width, height));
            return (line, area);
        }

        double effectiveMax = maxVal;
        if (autoScale)
        {
            double peak = history.Max();
            if (peak > 0)
            {
                effectiveMax = Math.Max(peak * 1.2, 10.0);
            }
        }

        double dx = history.Count > 1 ? width / (history.Count - 1) : width;
        area.Add(new Point(0, height)); // start at bottom-left

        for (int i = 0; i < history.Count; i++)
        {
            double norm = Math.Clamp(history[i] / (effectiveMax > 0 ? effectiveMax : 100.0), 0.0, 1.0);
            double y = height - (norm * (height - 4.0)) - 2.0;
            var pt = new Point(i * dx, y);
            line.Add(pt);
            area.Add(pt);
        }

        area.Add(new Point(width, height)); // close at bottom-right
        return (line, area);
    }

    [RelayCommand]
    private void ToggleResourceCard4Mode()
    {
        IsResourceVolumeMode = !IsResourceVolumeMode;
    }

    // ── Media commands ────────────────────────────────────────────────────────

    [RelayCommand] private async Task PlayPause() => await _mediaService.TogglePlayPauseAsync();
    [RelayCommand] private async Task NextTrack() => await _mediaService.NextTrackAsync();
    [RelayCommand] private async Task PreviousTrack() => await _mediaService.PreviousTrackAsync();
    [RelayCommand] private async Task SeekMediaFraction(double fraction) => await _mediaService.SeekToFractionAsync(fraction);

    // ── Shelf commands ────────────────────────────────────────────────────────

    [RelayCommand] private void RemoveShelfItem(ShelfItem item) => _shelfService.RemoveItem(item);
    [RelayCommand] private void ClearShelf() => _shelfService.Clear();
    [RelayCommand] private void CopyShelfPaths() => _shelfService.CopyPathsToClipboard();

    // ── Clipboard commands ────────────────────────────────────────────────────

    partial void OnClipboardSearchTextChanged(string value) => RefreshFilteredClipboard();

    public void RefreshFilteredClipboard()
    {
        var q = ClipboardSearchText?.Trim();
        var source = _clipboardService.History;
        var filtered = string.IsNullOrEmpty(q)
            ? source.OrderByDescending(i => i.IsPinned).ThenByDescending(i => i.Timestamp)
            : source.Where(i => i.Text.Contains(q, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(i => i.IsPinned).ThenByDescending(i => i.Timestamp);

        FilteredClipboardHistory.Clear();
        foreach (var item in filtered)
        {
            FilteredClipboardHistory.Add(item);
        }
    }

    [RelayCommand]
    private async Task CopyClipboardItem(ClipboardItem item)
    {
        _clipboardService.CopyItem(item);
        item.IsCopied = true;
        await Task.Delay(1200);
        item.IsCopied = false;
    }

    [RelayCommand]
    private void TogglePinClipboardItem(ClipboardItem item)
    {
        _clipboardService.TogglePin(item);
        RefreshFilteredClipboard();
    }

    [RelayCommand]
    private void RemoveClipboardItem(ClipboardItem item)
    {
        _clipboardService.Remove(item);
        RefreshFilteredClipboard();
    }

    [RelayCommand]
    private void ClearClipboardHistory()
    {
        _clipboardService.ClearUnpinned();
        RefreshFilteredClipboard();
    }

    // ── Screen capture command ────────────────────────────────────────────────

    [RelayCommand]
    private void CaptureScreen()
    {
        _screenCaptureService.CaptureScreenToShelf();
        SelectedTabIndex = 1; // Open Shelf tab
        ShelfSubTabIndex = 0; // Show Staged Files
    }

    // ── Audio commands ────────────────────────────────────────────────────────

    [RelayCommand]
    private void ToggleMute()
    {
        _audioService.ToggleMute();
        IsMuted = _audioService.GetMute();
    }

    partial void OnMasterVolumeChanged(float value)
    {
        if (!_isSyncingVolume)
        {
            _audioService.SetMasterVolume(value / 100f);
        }
    }

    [RelayCommand]
    private void ToggleDriveMenu()
    {
        IsDriveMenuOpen = !IsDriveMenuOpen;
    }

    [RelayCommand]
    private void SelectDrive(DriveItem? drive)
    {
        if (drive != null)
        {
            SelectedDrive = drive;
            _resourceMonitor.SelectedDrive = drive;
            IsDriveMenuOpen = false;
        }
    }

    [RelayCommand]
    private void ToggleAudioDeviceMenu()
    {
        IsAudioDeviceMenuOpen = !IsAudioDeviceMenuOpen;
        if (IsAudioDeviceMenuOpen)
        {
            RefreshAudioDevices();
        }
    }

    [RelayCommand]
    private void SelectAudioDevice(AudioDeviceItem? item)
    {
        if (item != null)
        {
            _audioService.SetDefaultPlaybackDevice(item.Id);
            IsAudioDeviceMenuOpen = false;
            UpdateAudioDeviceInfo();
        }
    }

    public void RefreshAudioDevices(bool updateDeviceInfo = true)
    {
        AudioDevices.Clear();
        var devices = _audioService.GetActivePlaybackDevices();
        foreach (var d in devices)
        {
            AudioDevices.Add(d);
        }
        if (updateDeviceInfo)
        {
            UpdateAudioDeviceInfo();
        }
    }

    public void UpdateAudioDeviceInfo()
    {
        ActiveAudioDeviceName = _audioService.ActiveDeviceName;
        int btBattery = _audioService.GetActiveBluetoothBatteryPercent();
        if (btBattery > 0)
        {
            BluetoothBatteryText = $"🔋 {btBattery}%";
            HasBluetoothBattery  = true;
        }
        else
        {
            BluetoothBatteryText = string.Empty;
            HasBluetoothBattery  = false;
        }
    }

    // ── Stopwatch widget ──────────────────────────────────────────────────────

    [RelayCommand]
    private void ToggleStopwatch()
    {
        if (_stopwatch.IsRunning)
        {
            _stopwatch.Stop();
            _stopwatchTimer?.Stop();
            IsStopwatchRunning = false;
        }
        else
        {
            _stopwatch.Start();
            IsStopwatchRunning = true;
            _stopwatchTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            _stopwatchTimer.Tick += (_, _) =>
            {
                var ts = _stopwatch.Elapsed;
                StopwatchText = $"{ts.Minutes:D2}:{ts.Seconds:D2}.{ts.Milliseconds / 100}";
            };
            _stopwatchTimer.Start();
        }
    }

    [RelayCommand]
    private void ResetStopwatch()
    {
        _stopwatch.Reset();
        _stopwatchTimer?.Stop();
        IsStopwatchRunning = false;
        StopwatchText = "00:00.0";
    }

    // ── Quick tool commands ───────────────────────────────────────────────────

    [RelayCommand] private void OpenSettings() => OpenSettingsRequested?.Invoke();

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
