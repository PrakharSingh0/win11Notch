using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Win11Notch.Core.Settings;
using Win11Notch.Core.State;
using Win11Notch.Models;
using Win11Notch.Services;

namespace Win11Notch.ViewModels;

public sealed partial class WaveBarModel : ObservableObject
{
    [ObservableProperty] private double _height = 5.0;
}

/// <summary>
/// Master ViewModel for the NotchWindow overlay.
/// Coordinates media controls, drop shelf, clipboard history, screen capture,
/// hardware volume HUD, media timeline, resource monitoring, tools widgets, and settings.
/// </summary>
public sealed partial class NotchViewModel : ObservableObject
{
    private readonly NotchStateMachine _stateMachine;
    private readonly MediaService _mediaService;
    private readonly ShelfService _shelfService;
    private readonly AudioService _audioService;
    private readonly ResourceMonitorService _resourceMonitor;
    private readonly ClipboardService _clipboardService;
    private readonly ScreenCaptureService _screenCaptureService;
    private readonly TimerService _timerService;
    private readonly SettingsService _settingsService;
    private readonly PowerAwakeService _powerAwakeService;
    private readonly ScratchpadService _scratchpadService;
    private readonly CalendarService _calendarService;
    private readonly QuickShareService _quickShareService;
    private readonly BrightnessService _brightnessService;
    private readonly ColorPickerService _colorPickerService;

    [DllImport("user32.dll")]
    private static extern bool LockWorkStation();

    public TimerService Timer => _timerService;
    public CalendarService Calendar => _calendarService;

    [ObservableProperty] private NotchState _currentState = NotchState.Idle;
    [ObservableProperty] private string _clockText = string.Empty;
    [ObservableProperty] private string _dateText = string.Empty;
    [ObservableProperty] private int _selectedTabIndex = 0;
    [ObservableProperty] private int _shelfSubTabIndex = 0;
    [ObservableProperty] private int _clipboardFilterIndex = 0; // 0: All, 1: Text, 2: Images, 3: URLs

    // Calendar quick composer properties
    [ObservableProperty] private bool _isAddingCalendarEvent;
    [ObservableProperty] private string _newCalendarTitle = string.Empty;
    [ObservableProperty] private string _newCalendarTime = "09:00";
    [ObservableProperty] private bool _newCalendarIsAllDay;
    [ObservableProperty] private bool _newCalendarIsReminder = true;
    [ObservableProperty] private string _newCalendarAccentHex = "#FF5E36";

    public bool NewCalendarIsEvent => !NewCalendarIsReminder;
    partial void OnNewCalendarIsReminderChanged(bool value) => OnPropertyChanged(nameof(NewCalendarIsEvent));

    // Drag-and-drop split action overlay
    [ObservableProperty] private bool _isFileDragOverlayVisible;
    [ObservableProperty] private bool _isDropDockHovered;
    [ObservableProperty] private bool _isDropShareHovered;

    // Media properties
    [ObservableProperty] private string _mediaTitle = "No Media Playing";
    [ObservableProperty] private string _mediaArtist = string.Empty;
    [ObservableProperty] private string _activeMediaSourceName = "System Media";
    [ObservableProperty] private bool _isMediaSourceMenuOpen;
    [ObservableProperty] private bool _isMediaPlaying;
    [ObservableProperty] private bool _hasMedia;
    [ObservableProperty] private ImageSource? _mediaThumbnail;
    [ObservableProperty] private Brush _mediaAccentBrush = CreateFrozenBrush(Color.FromRgb(0xFF, 0x5E, 0x36));
    [ObservableProperty] private Brush _mediaAccentGlowBrush = CreateFrozenBrush(Color.FromArgb(0x40, 0xFF, 0x5E, 0x36));
    [ObservableProperty] private bool _isMediaToastActive;
    [ObservableProperty] private string _mediaPositionText = "00:00";
    [ObservableProperty] private string _mediaDurationText = "00:00";
    [ObservableProperty] private double _mediaProgressFraction;
    [ObservableProperty] private double _mediaProgressPercent;
    public ObservableCollection<MediaSessionItem> MediaSessions { get; } = new();
    public ObservableCollection<WaveBarModel> WaveBars { get; } = new();
    public ObservableCollection<WaveBarModel> CompactWaveBars { get; } = new();

    public event Action<bool>? MediaToastActiveChanged;
    partial void OnIsMediaToastActiveChanged(bool value) => MediaToastActiveChanged?.Invoke(value);

    // Audio properties & Volume HUD
    [ObservableProperty] private float _masterVolume = 50f;
    [ObservableProperty] private bool _isMuted;
    [ObservableProperty] private bool _isVolumeToastActive;
    [ObservableProperty] private string _activeAudioDeviceName = "Default Playback Device";
    [ObservableProperty] private bool _isAudioDeviceMenuOpen;
    public ObservableCollection<AudioDeviceItem> AudioDevices { get; } = new();
    private bool _isSyncingVolume;

    public event Action<bool>? VolumeToastActiveChanged;
    partial void OnIsVolumeToastActiveChanged(bool value) => VolumeToastActiveChanged?.Invoke(value);

    // Power, Brightness, Eyedropper & Upcoming Reminder state
    [ObservableProperty] private bool _isPowerToastActive;
    [ObservableProperty] private string _powerToastIcon = "⚡";
    [ObservableProperty] private string _powerToastTitle = "Charger Connected";
    [ObservableProperty] private string _powerToastDetail = "85%";
    [ObservableProperty] private Brush _powerToastIconBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x5E, 0x36));
    public event Action<bool>? PowerToastActiveChanged;
    partial void OnIsPowerToastActiveChanged(bool value) => PowerToastActiveChanged?.Invoke(value);

    [ObservableProperty] private int _displayBrightness = 70;
    [ObservableProperty] private bool _isBrightnessSupported = true;
    [ObservableProperty] private string _brightnessTooltip = "Adjust display brightness";
    [ObservableProperty] private bool _isBrightnessToastActive;
    private bool _isSyncingBrightness;
    public event Action<bool>? BrightnessToastActiveChanged;
    partial void OnIsBrightnessToastActiveChanged(bool value) => BrightnessToastActiveChanged?.Invoke(value);

    // Live time-aware greeting state
    [ObservableProperty] private string _greetingTitle = string.Empty;
    [ObservableProperty] private string _greetingTagline = string.Empty;
    [ObservableProperty] private string _greetingIcon = "\uE706";
    [ObservableProperty] private Brush _greetingAccentBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xD5, 0x4F));
    private string _lastGreetingPeriodKey = string.Empty;
    public event Action<TimeGreetingInfo>? GreetingPeriodRolledOver;

    [ObservableProperty] private bool _hasUpcomingReminder;
    [ObservableProperty] private string _upcomingReminderText = string.Empty;

    public Func<Task>? PrepareForScreenPickAsync { get; set; }
    public Action? RestoreAfterScreenPick { get; set; }

    private DispatcherTimer? _gestureHudTimer;

    // Resource properties
    [ObservableProperty] private int _cpuPercent;
    [ObservableProperty] private int _memoryPercent;
    [ObservableProperty] private string _ramText = "0.0 / 0.0 GB";
    [ObservableProperty] private int _gpuPercent;
    [ObservableProperty] private string _vramText = "0.00 GB";
    [ObservableProperty] private string _activeGpuName = "Graphics (GPU)";
    [ObservableProperty] private bool _hasMultipleGpus;
    [ObservableProperty] private bool _hasSingleGpu = true;
    [ObservableProperty] private GpuAdapterInfo? _primaryGpu;
    public ObservableCollection<GpuAdapterInfo> GpuAdapters { get; } = new();
    [ObservableProperty] private string _networkName = "WiFi";
    [ObservableProperty] private string _networkDetail = "Wi-Fi - Connected";
    [ObservableProperty] private string _downloadSpeedText = "0 B/s";
    [ObservableProperty] private string _uploadSpeedText = "0 B/s";
    [ObservableProperty] private int _batteryPercent = 100;
    [ObservableProperty] private bool _isCharging;
    [ObservableProperty] private bool _isBatteryLow;
    [ObservableProperty] private string _batteryStatusText = "Fully charged";
    [ObservableProperty] private string _batteryHealthText = "Health 100%";
    [ObservableProperty] private string _batteryTooltip = "Battery • 100%";

    // Storage partitions
    public ObservableCollection<DriveItem> Drives => _resourceMonitor.Drives;
    [ObservableProperty] private DriveItem? _selectedDrive;
    [ObservableProperty] private bool _isDriveMenuOpen;

    // Connected Audio Device & Bluetooth Battery
    [ObservableProperty] private string _bluetoothBatteryText = string.Empty;
    [ObservableProperty] private bool _hasBluetoothBattery;
    [ObservableProperty] private bool _isBottomAudioMenuOpen;

    public bool IsDualActivity => IsMediaPlaying && IsStopwatchRunning && _settingsService.Current.DualActivityEnabled;
    public bool IsMediaOnlyActivity => IsMediaPlaying && (!IsStopwatchRunning || !_settingsService.Current.DualActivityEnabled);
    public bool IsTimerOnlyActivity => !IsMediaPlaying && IsStopwatchRunning;

    public bool IsVerticalDock => _settingsService.Current.DockEdge is "Left" or "Right";
    public bool IsHorizontalDock => !IsVerticalDock;

    // Power Stay-Awake & Quick Scratchpad
    [ObservableProperty] private bool _isStayAwakeEnabled;
    [ObservableProperty] private string _scratchpadText = string.Empty;
    [ObservableProperty] private bool _scratchpadCopied;
    private DispatcherTimer? _scratchpadSaveTimer;

    // Sparkline Graph & Area Point Collections
    [ObservableProperty] private PointCollection _cpuGraphPoints = new();
    [ObservableProperty] private PointCollection _cpuAreaPoints = new();
    [ObservableProperty] private PointCollection _ramGraphPoints = new();
    [ObservableProperty] private PointCollection _ramAreaPoints = new();
    [ObservableProperty] private PointCollection _gpuGraphPoints = new();
    [ObservableProperty] private PointCollection _gpuAreaPoints = new();
    [ObservableProperty] private int _diskPercent;
    [ObservableProperty] private PointCollection _diskGraphPoints = new();
    [ObservableProperty] private PointCollection _diskAreaPoints = new();
    [ObservableProperty] private PointCollection _downloadGraphPoints = new();
    [ObservableProperty] private PointCollection _downloadAreaPoints = new();
    [ObservableProperty] private PointCollection _uploadGraphPoints = new();
    [ObservableProperty] private PointCollection _uploadAreaPoints = new();
    [ObservableProperty] private bool _isResourceVolumeMode;

    // Shelf badge
    [ObservableProperty] private bool _hasStagedFiles;
    [ObservableProperty] private string _shelfCountText = string.Empty;

    // Stopwatch tool
    [ObservableProperty] private string _stopwatchText = "00:00.0";
    [ObservableProperty] private bool _isStopwatchRunning;
    private readonly Stopwatch _stopwatch = new();
    private DispatcherTimer? _stopwatchTimer;

    // Events
    public event Action? OpenSettingsRequested;

    // Shelf & Quick Share collections
    public ObservableCollection<ShelfItem> ShelfItems => _shelfService.Items;
    public ObservableCollection<ClipboardItem> ClipboardHistory => _clipboardService.History;
    public ObservableCollection<ClipboardItem> FilteredClipboardHistory { get; } = new();
    public ObservableCollection<ShareQueueItem> ShareQueue => _quickShareService.Queue;
    public ObservableCollection<ShareHistoryEntry> ShareHistory => _quickShareService.History;

    [ObservableProperty] private string _clipboardSearchText = string.Empty;
    [ObservableProperty] private bool _isQuickShareInstalled;
    [ObservableProperty] private bool _isQuickShareRunning;
    [ObservableProperty] private string _quickShareStatusText = "Checking...";
    [ObservableProperty] private string _shareActionFeedback = string.Empty;
    [ObservableProperty] private bool _hasShareQueueItems;
    [ObservableProperty] private string _shareQueueSummaryText = "Send with Quick Share";
    [ObservableProperty] private bool _hasShareHistory;
    public bool IsModalPickerOpen { get; private set; }

    private DispatcherTimer? _clockTimer;
    private DispatcherTimer? _timelineTimer;
    private DispatcherTimer? _spectrumTimer;
    private double _wavePhase;

    partial void OnSelectedTabIndexChanged(int value)
    {
        CloseAllMenus();
        UpdateSpectrumState();
        if (value == 6)
        {
            RefreshQuickShareState();
        }
    }

    public NotchViewModel(
        NotchStateMachine stateMachine,
        MediaService mediaService,
        ShelfService shelfService,
        AudioService audioService,
        ResourceMonitorService resourceMonitor,
        ClipboardService clipboardService,
        ScreenCaptureService screenCaptureService,
        TimerService timerService,
        SettingsService settingsService,
        PowerAwakeService powerAwakeService,
        ScratchpadService scratchpadService,
        CalendarService calendarService,
        QuickShareService quickShareService,
        BrightnessService brightnessService,
        ColorPickerService colorPickerService)
    {
        _stateMachine = stateMachine;
        _mediaService = mediaService;
        _shelfService = shelfService;
        _audioService = audioService;
        _resourceMonitor = resourceMonitor;
        _clipboardService = clipboardService;
        _screenCaptureService = screenCaptureService;
        _timerService = timerService;
        _settingsService = settingsService;
        _powerAwakeService = powerAwakeService;
        _scratchpadService = scratchpadService;
        _calendarService = calendarService;
        _quickShareService = quickShareService;
        _brightnessService = brightnessService;
        _colorPickerService = colorPickerService;

        _scratchpadText = _scratchpadService.Load();
        _powerAwakeService.AwakeStateChanged += awake => IsStayAwakeEnabled = awake;
        _calendarService.ReminderDue += OnCalendarReminderDue;
        _calendarService.CalendarChanged += UpdateUpcomingReminder;
        _brightnessService.BrightnessChanged += val =>
        {
            _isSyncingBrightness = true;
            try
            {
                DisplayBrightness = val;
                IsBrightnessSupported = _brightnessService.IsBrightnessSupported;
                BrightnessTooltip = _brightnessService.StatusTooltip;
            }
            finally { _isSyncingBrightness = false; }
        };
        _brightnessService.RefreshBrightnessAsync(_settingsService.Current.TargetMonitorIndex);
        UpdateGreetingState(DateTime.Now, notifyRollover: false);
        UpdateUpcomingReminder();
        _quickShareService.Queue.CollectionChanged += (_, _) => RefreshQuickShareState();
        _quickShareService.History.CollectionChanged += (_, _) => RefreshQuickShareState();
        RefreshQuickShareState();

        // Wire timer updates to Dynamic Island handle & capsule
        _timerService.TimerActivityChanged += () =>
        {
            IsStopwatchRunning = _timerService.IsAnyTimerRunning;
            StopwatchText = _timerService.IslandTimerText;
            OnPropertyChanged(nameof(IsStopwatchRunning));
            OnPropertyChanged(nameof(StopwatchText));
            OnPropertyChanged(nameof(IsDualActivity));
            OnPropertyChanged(nameof(IsMediaOnlyActivity));
            OnPropertyChanged(nameof(IsTimerOnlyActivity));
        };

        // Wire media updates & timeline
        _mediaService.MediaChanged += OnMediaChanged;
        _mediaService.TimelineChanged += OnTimelineChanged;

        // Wire resource stats
        _resourceMonitor.StatsUpdated += OnResourceStatsUpdated;

        // Initialize 16-bar Media Tab waveform + 4-bar compact handle/hover waveform
        for (int i = 0; i < 16; i++) WaveBars.Add(new WaveBarModel());
        for (int i = 0; i < 4; i++) CompactWaveBars.Add(new WaveBarModel { Height = 3.0 });

        // Wire shelf changes & load initial persisted count
        _shelfService.Items.CollectionChanged += (_, _) =>
        {
            HasStagedFiles = _shelfService.Items.Count > 0;
            ShelfCountText = _shelfService.Items.Count > 0 ? $"{_shelfService.Items.Count}" : string.Empty;
        };
        HasStagedFiles = _shelfService.Items.Count > 0;
        ShelfCountText = _shelfService.Items.Count > 0 ? $"{_shelfService.Items.Count}" : string.Empty;

        // Wire clipboard changes
        _clipboardService.History.CollectionChanged += (_, _) => RefreshFilteredClipboard();
        RefreshFilteredClipboard();

        // Sync initial volume and audio device
        _isSyncingVolume = true;
        try
        {
            _masterVolume = (float)Math.Round(_audioService.GetMasterVolume() * 100f);
            _isMuted = _audioService.GetMute();
        }
        finally
        {
            _isSyncingVolume = false;
        }

        _activeAudioDeviceName = _audioService.ActiveDeviceName;

        _audioService.ActiveDeviceChanged += name =>
        {
            ActiveAudioDeviceName = name;
            RefreshAudioDevices(updateDeviceInfo: true);
        };

        _audioService.BluetoothBatteryChanged += _ =>
        {
            Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                UpdateAudioDeviceInfo();
                if (IsAudioDeviceMenuOpen || IsBottomAudioMenuOpen)
                {
                    RefreshAudioDevices(updateDeviceInfo: false);
                }
            });
        };

        UpdateAudioDeviceInfo();

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

        // Spectrum visualizer timer (25 FPS, active ONLY while Media Tab is open & playing; 0 Hz in Idle)
        _spectrumTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        _spectrumTimer.Tick += (_, _) => OnSpectrumTick();

        // State changes
        _stateMachine.StateChanged += (_, state) =>
        {
            CurrentState = state;
            if (state != NotchState.PeekExpanded)
            {
                CloseAllMenus();
                IsFileDragOverlayVisible = false;
                IsDropDockHovered = false;
                IsDropShareHovered = false;
            }

            if (state is NotchState.CollapsedHover or NotchState.PeekExpanded)
                StartClock();
            else
                StopClock();

            // Resource monitoring runs while Hover or Peek is active (stopped in Idle)
            if (state is NotchState.CollapsedHover or NotchState.PeekExpanded)
            {
                _resourceMonitor.StartMonitoring();
                _audioService.RefreshActiveDevice();
                UpdateAudioDeviceInfo();
                _brightnessService.RefreshBrightnessAsync(_settingsService.Current.TargetMonitorIndex);
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

            UpdateSpectrumState();
        };
    }

    public void UpdateVolumeFromDevice(float volume, bool isMuted)
    {
        _isSyncingVolume = true;
        try
        {
            MasterVolume = (float)Math.Round(volume);
            IsMuted = isMuted;
        }
        finally
        {
            _isSyncingVolume = false;
        }
    }

    private void OnMediaChanged()
    {
        MediaTitle = _mediaService.Title;
        MediaArtist = _mediaService.Artist;
        ActiveMediaSourceName = _mediaService.ActiveSourceName;
        IsMediaPlaying = _mediaService.IsPlaying;
        HasMedia = _mediaService.HasMedia;
        MediaThumbnail = _mediaService.Thumbnail;

        var accent = _mediaService.AccentColor;
        MediaAccentBrush = CreateFrozenBrush(accent);
        MediaAccentGlowBrush = CreateFrozenBrush(Color.FromArgb(0x42, accent.R, accent.G, accent.B));

        OnTimelineChanged();
        OnPropertyChanged(nameof(IsDualActivity));
        OnPropertyChanged(nameof(IsMediaOnlyActivity));
        OnPropertyChanged(nameof(IsTimerOnlyActivity));

        if (CurrentState == NotchState.PeekExpanded && IsMediaPlaying)
        {
            _timelineTimer?.Start();
        }
        else if (!IsMediaPlaying)
        {
            _timelineTimer?.Stop();
        }

        UpdateSpectrumState();
    }

    private void UpdateSpectrumState()
    {
        // Active while media is playing (and not in Stealth); stops completely at 0 Hz when paused/stopped
        bool shouldRun = IsMediaPlaying && CurrentState != NotchState.Stealth;
        if (shouldRun)
        {
            if (_spectrumTimer is { IsEnabled: false }) _spectrumTimer.Start();
        }
        else
        {
            _spectrumTimer?.Stop();
            foreach (var bar in WaveBars) bar.Height = 5.0;
            foreach (var bar in CompactWaveBars) bar.Height = 3.0;
        }
    }

    private void OnSpectrumTick()
    {
        if (!IsMediaPlaying || CurrentState == NotchState.Stealth)
        {
            UpdateSpectrumState();
            return;
        }

        var (left, right, peak) = _audioService.GetAudioPeakLevels();
        // Gentle minimum floor when SMTC reports playing even during quiet passages
        double baseAmp = Math.Max(peak, 0.08);
        _wavePhase += 0.28;

        if (CurrentState == NotchState.PeekExpanded && SelectedTabIndex == 0)
        {
            int n = WaveBars.Count;
            for (int i = 0; i < n; i++)
            {
                double pan = (double)i / Math.Max(1, n - 1);
                double channelAmp = (1.0 - pan) * Math.Max(left, baseAmp) + pan * Math.Max(right, baseAmp);
                double centerShape = 0.65 + 0.35 * Math.Sin(pan * Math.PI);
                double harmonic = 0.55 + 0.45 * Math.Abs(Math.Sin(_wavePhase + i * 0.72) * Math.Cos(_wavePhase * 0.53 + i * 0.41));

                double target = Math.Clamp(5.0 + channelAmp * centerShape * harmonic * 36.0, 5.0, 38.0);
                WaveBars[i].Height = WaveBars[i].Height * 0.42 + target * 0.58;
            }
        }
        else
        {
            double maxCompact = CurrentState == NotchState.CollapsedHover ? 13.0 : 11.0;
            const double minCompact = 2.5;
            int cn = CompactWaveBars.Count;
            for (int i = 0; i < cn; i++)
            {
                double pan = (double)i / Math.Max(1, cn - 1);
                double channelAmp = (1.0 - pan) * Math.Max(left, baseAmp) + pan * Math.Max(right, baseAmp);
                double centerShape = 0.72 + 0.28 * Math.Sin(pan * Math.PI);
                double harmonic = 0.50 + 0.50 * Math.Abs(Math.Sin(_wavePhase * 1.15 + i * 1.05) * Math.Cos(_wavePhase * 0.6 + i * 0.5));

                double target = Math.Clamp(minCompact + channelAmp * centerShape * harmonic * (maxCompact - minCompact), minCompact, maxCompact);
                CompactWaveBars[i].Height = CompactWaveBars[i].Height * 0.38 + target * 0.62;
            }
        }
    }

    private static Brush CreateFrozenBrush(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private void OnTimelineChanged()
    {
        MediaPositionText = _mediaService.PositionText;
        MediaDurationText = _mediaService.DurationText;
        MediaProgressFraction = _mediaService.ProgressFraction;
        MediaProgressPercent = Math.Clamp(_mediaService.ProgressFraction * 100.0, 0.0, 100.0);
    }

    private void OnResourceStatsUpdated()
    {
        CpuPercent = _resourceMonitor.CpuPercent;
        MemoryPercent = _resourceMonitor.MemoryPercent;
        RamText = $"{_resourceMonitor.RamUsedGb:F1} / {_resourceMonitor.RamTotalGb:F1} GB";
        GpuPercent = _resourceMonitor.GpuPercent;
        VramText = _resourceMonitor.VramTotalGb > 0
            ? $"VRAM {_resourceMonitor.VramUsedGb:F1} / {_resourceMonitor.VramTotalGb:F0} GB"
            : (_resourceMonitor.VramUsedGb > 0 ? $"VRAM {_resourceMonitor.VramUsedGb:F1} GB" : "VRAM Shared");
        DiskPercent = _resourceMonitor.DiskPercent;
        NetworkName = _resourceMonitor.NetworkName;
        NetworkDetail = _resourceMonitor.NetworkDetail;
        DownloadSpeedText = _resourceMonitor.DownloadSpeedText;
        UploadSpeedText = _resourceMonitor.UploadSpeedText;
        BatteryPercent = _resourceMonitor.BatteryPercent;
        IsCharging = _resourceMonitor.IsCharging;
        IsBatteryLow = BatteryPercent <= 20 && !IsCharging;
        BatteryStatusText = _resourceMonitor.BatteryStatusText;
        BatteryHealthText = _resourceMonitor.BatteryHealthText;
        BatteryTooltip = $"{BatteryStatusText} • {BatteryHealthText}";

        // Sync multi-GPU adapters with per-adapter sparklines
        var sourceGpus = _resourceMonitor.GpuAdapters;
        var builtGpus = new List<GpuAdapterInfo>(sourceGpus.Count);
        foreach (var g in sourceGpus)
        {
            var (gl, ga) = GenerateGraphAndArea(g.History, 80, 18, 100);
            builtGpus.Add(new GpuAdapterInfo
            {
                Name = g.Name,
                LuidKey = g.LuidKey,
                IsDiscrete = g.IsDiscrete,
                IsActive = g.IsActive,
                GpuPercent = g.GpuPercent,
                VramUsedGb = g.VramUsedGb,
                VramTotalGb = g.VramTotalGb,
                VramPercent = g.VramPercent,
                VramText = g.VramText,
                RoleLabel = g.RoleLabel,
                AccentHex = g.AccentHex,
                History = g.History,
                GraphPoints = gl,
                AreaPoints = ga
            });
        }

        if (GpuAdapters.Count != builtGpus.Count)
        {
            GpuAdapters.Clear();
            foreach (var g in builtGpus) GpuAdapters.Add(g);
        }
        else
        {
            for (int i = 0; i < builtGpus.Count; i++)
            {
                GpuAdapters[i] = builtGpus[i];
            }
        }

        HasMultipleGpus = GpuAdapters.Count > 1;
        HasSingleGpu = !HasMultipleGpus;
        PrimaryGpu = GpuAdapters.FirstOrDefault(g => g.IsActive) ?? GpuAdapters.FirstOrDefault();
        ActiveGpuName = PrimaryGpu?.Name ?? "Graphics (GPU)";

        // Sync selected drive & audio info
        SelectedDrive = _resourceMonitor.SelectedDrive;
        UpdateAudioDeviceInfo();

        // Generate Sparkline line & area point collections
        var (cpuL, cpuA) = GenerateGraphAndArea(_resourceMonitor.CpuHistory, 100, 20, 100);
        CpuGraphPoints = cpuL;
        CpuAreaPoints = cpuA;

        var (ramL, ramA) = GenerateGraphAndArea(_resourceMonitor.RamHistory, 100, 20, 100);
        RamGraphPoints = ramL;
        RamAreaPoints = ramA;

        var (gpuL, gpuA) = GenerateGraphAndArea(_resourceMonitor.GpuHistory, 100, 20, 100);
        GpuGraphPoints = gpuL;
        GpuAreaPoints = gpuA;

        var (diskL, diskA) = GenerateGraphAndArea(_resourceMonitor.DiskHistory, 100, 20, 100, autoScale: true);
        DiskGraphPoints = diskL;
        DiskAreaPoints = diskA;

        var (dnL, dnA) = GenerateGraphAndArea(_resourceMonitor.DownloadHistory, 60, 16, 50, autoScale: true);
        DownloadGraphPoints = dnL;
        DownloadAreaPoints = dnA;

        var (upL, upA) = GenerateGraphAndArea(_resourceMonitor.UploadHistory, 60, 16, 50, autoScale: true);
        UploadGraphPoints = upL;
        UploadAreaPoints = upA;
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

    [RelayCommand]
    private void ToggleMediaSourceMenu()
    {
        bool next = !IsMediaSourceMenuOpen;
        CloseAllMenus();
        IsMediaSourceMenuOpen = next;
        if (next)
        {
            MediaSessions.Clear();
            foreach (var s in _mediaService.GetAvailableSessions())
            {
                MediaSessions.Add(s);
            }
        }
    }

    [RelayCommand]
    private void SelectMediaSession(MediaSessionItem? session)
    {
        if (session != null)
        {
            _mediaService.SelectSessionByAppId(session.AppId);
        }
        IsMediaSourceMenuOpen = false;
    }

    public void CloseAllMenus()
    {
        IsAudioDeviceMenuOpen = false;
        IsBottomAudioMenuOpen = false;
        IsDriveMenuOpen = false;
        IsMediaSourceMenuOpen = false;
    }

    // ── Shelf commands ────────────────────────────────────────────────────────

    [RelayCommand] private void RemoveShelfItem(ShelfItem item) => _shelfService.RemoveItem(item);
    [RelayCommand] private void ClearShelf() => _shelfService.Clear();
    [RelayCommand] private void CopyShelfPaths() => _shelfService.CopyPathsToClipboard();

    // ── Quick Share File Transfer commands ───────────────────────────────────

    public void RefreshQuickShareState()
    {
        _quickShareService.RefreshStatus();
        IsQuickShareInstalled = _quickShareService.IsQuickShareInstalled;
        IsQuickShareRunning = _quickShareService.IsQuickShareRunning;

        QuickShareStatusText = IsQuickShareInstalled
            ? (IsQuickShareRunning ? "Quick Share Active • Ready to Send/Receive" : "Quick Share Installed • Ready to Send")
            : "Quick Share Not Found • Uses Windows Share";

        int count = _quickShareService.Queue.Count;
        HasShareQueueItems = count > 0;
        ShareQueueSummaryText = count > 0
            ? $"Send with Quick Share ({count})"
            : "Send with Quick Share";
        HasShareHistory = _quickShareService.History.Count > 0;
    }

    [RelayCommand]
    private void BrowseShareFiles()
    {
        try
        {
            IsModalPickerOpen = true;
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Select Files for Quick Share",
                Multiselect = true,
                CheckFileExists = true
            };
            if (dlg.ShowDialog() == true && dlg.FileNames.Length > 0)
            {
                _quickShareService.AddPaths(dlg.FileNames);
                ShareActionFeedback = string.Empty;
            }
        }
        finally
        {
            IsModalPickerOpen = false;
        }
    }

    [RelayCommand]
    private void PullDockToShare()
    {
        var dockPaths = _shelfService.Items.Select(i => i.FilePath).ToList();
        if (dockPaths.Count == 0)
        {
            ShareActionFeedback = "Dock is empty — drop files in Dock or browse files.";
            return;
        }
        _quickShareService.AddPaths(dockPaths);
        ShareActionFeedback = $"Pulled {dockPaths.Count} item(s) from Dock.";
    }

    [RelayCommand]
    private void AddClipboardToShare()
    {
        var latest = _clipboardService.History.FirstOrDefault();
        if (_quickShareService.StageClipboardItem(latest))
        {
            ShareActionFeedback = "Staged latest clipboard content as file.";
        }
        else
        {
            ShareActionFeedback = "No shareable clipboard content found.";
        }
    }

    [RelayCommand]
    private void RemoveShareItem(ShareQueueItem? item)
    {
        if (item != null) _quickShareService.RemoveQueueItem(item);
    }

    [RelayCommand]
    private void ClearShareQueue()
    {
        _quickShareService.ClearQueue();
        ShareActionFeedback = string.Empty;
    }

    [RelayCommand]
    private async Task SendWithQuickShare()
    {
        if (_quickShareService.Queue.Count == 0) return;

        var outcome = await _quickShareService.SendQueuedAsync();
        if (outcome == ShareLaunchOutcome.QuickShareCom || outcome == ShareLaunchOutcome.WindowsShareFallback)
        {
            ShareActionFeedback = outcome == ShareLaunchOutcome.QuickShareCom
                ? "Handed off to Google Quick Share."
                : "Opened Windows Share Sheet.";
            _stateMachine.Collapse();
        }
        else
        {
            ShareActionFeedback = "Could not start Quick Share for selected item(s).";
        }
    }

    public async Task SendDroppedFilesViaQuickShareAsync(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return;

        _quickShareService.AddPaths(paths);
        SelectedTabIndex = 6;

        var outcome = await _quickShareService.SendQueuedAsync();
        if (outcome != ShareLaunchOutcome.Failed)
        {
            ShareActionFeedback = outcome == ShareLaunchOutcome.QuickShareCom
                ? "Handed off to Google Quick Share."
                : "Opened Windows Share Sheet.";
            _stateMachine.Collapse();
        }
    }

    [RelayCommand]
    private async Task ShareDockDirectly()
    {
        var paths = _shelfService.Items.Select(i => i.FilePath).ToList();
        if (paths.Count == 0) return;

        var outcome = await _quickShareService.SendPathsAsync(paths);
        if (outcome != ShareLaunchOutcome.Failed)
        {
            _stateMachine.Collapse();
        }
    }

    [RelayCommand]
    private async Task ResendHistoryEntry(ShareHistoryEntry? entry)
    {
        if (entry == null) return;
        var validPaths = entry.FilePaths.Where(p => System.IO.File.Exists(p) || System.IO.Directory.Exists(p)).ToList();
        if (validPaths.Count == 0)
        {
            ShareActionFeedback = "Original file(s) no longer exist on disk.";
            return;
        }
        var outcome = await _quickShareService.SendPathsAsync(validPaths);
        if (outcome != ShareLaunchOutcome.Failed)
        {
            _stateMachine.Collapse();
        }
    }

    [RelayCommand]
    private void OpenQuickShareApp()
    {
        _quickShareService.OpenQuickShareApp();
        _stateMachine.Collapse();
    }

    [RelayCommand]
    private static void OpenDownloadsFolder() => QuickShareService.OpenDownloadsFolder();

    [RelayCommand]
    private void ClearShareHistory() => _quickShareService.ClearHistory();

    // ── Clipboard commands ────────────────────────────────────────────────────

    partial void OnClipboardSearchTextChanged(string value) => RefreshFilteredClipboard();
    partial void OnClipboardFilterIndexChanged(int value) => RefreshFilteredClipboard();

    public void RefreshFilteredClipboard()
    {
        var q = ClipboardSearchText?.Trim();
        IEnumerable<ClipboardItem> source = _clipboardService.History;

        source = ClipboardFilterIndex switch
        {
            1 => source.Where(i => i.Kind == ClipboardItemKind.Text),
            2 => source.Where(i => i.Kind == ClipboardItemKind.Image),
            3 => source.Where(i => i.Kind == ClipboardItemKind.Url),
            _ => source
        };

        if (!string.IsNullOrEmpty(q))
        {
            source = source.Where(i =>
                i.Text.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                i.BadgeText.Contains(q, StringComparison.OrdinalIgnoreCase));
        }

        var ordered = source
            .OrderByDescending(i => i.IsPinned)
            .ThenByDescending(i => i.Timestamp);

        FilteredClipboardHistory.Clear();
        foreach (var item in ordered)
        {
            FilteredClipboardHistory.Add(item);
        }
    }

    [RelayCommand]
    private void SetClipboardFilter(object? param)
    {
        if (int.TryParse(param?.ToString(), out int idx))
        {
            ClipboardFilterIndex = idx;
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
    private void OpenClipboardUrl(ClipboardItem? item)
    {
        if (item is not { Kind: ClipboardItemKind.Url } || string.IsNullOrWhiteSpace(item.Text))
        {
            return;
        }

        // Only launch explicit HTTP/HTTPS web URLs captured in clipboard history
        if (Uri.TryCreate(item.Text.Trim(), UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            SafeLaunch(uri.AbsoluteUri);
        }
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

    // ── Calendar commands ─────────────────────────────────────────────────────

    [RelayCommand] private void CalendarPreviousMonth() => _calendarService.PreviousMonth();
    [RelayCommand] private void CalendarNextMonth() => _calendarService.NextMonth();
    [RelayCommand] private void CalendarToday() => _calendarService.GoToToday();

    [RelayCommand]
    private void CalendarSelectDate(CalendarDayCell? cell)
    {
        if (cell != null)
        {
            _calendarService.SelectDate(cell.Date);
        }
    }

    [RelayCommand]
    private void ToggleAddCalendarEvent()
    {
        IsAddingCalendarEvent = !IsAddingCalendarEvent;
        if (IsAddingCalendarEvent && string.IsNullOrWhiteSpace(NewCalendarTime))
        {
            NewCalendarTime = DateTime.Now.AddMinutes(30).ToString("HH:mm");
        }
    }

    [RelayCommand]
    private void SetCalendarType(object? isReminderParam)
    {
        if (bool.TryParse(isReminderParam?.ToString(), out bool isReminder))
        {
            NewCalendarIsReminder = isReminder;
        }
    }

    [RelayCommand]
    private void ToggleCalendarAllDay()
    {
        NewCalendarIsAllDay = !NewCalendarIsAllDay;
    }

    [RelayCommand]
    private void SelectCalendarAccent(string? hex)
    {
        if (!string.IsNullOrWhiteSpace(hex))
        {
            NewCalendarAccentHex = hex;
        }
    }

    [RelayCommand]
    private void SaveNewCalendarItem()
    {
        if (string.IsNullOrWhiteSpace(NewCalendarTitle)) return;

        _calendarService.AddItem(
            NewCalendarTitle,
            NewCalendarTime,
            NewCalendarIsAllDay,
            NewCalendarIsReminder,
            NewCalendarAccentHex);

        NewCalendarTitle = string.Empty;
        IsAddingCalendarEvent = false;
    }

    [RelayCommand]
    private void ToggleCalendarItemComplete(CalendarItem? item)
    {
        if (item != null)
        {
            _calendarService.ToggleComplete(item);
        }
    }

    [RelayCommand]
    private void RemoveCalendarItem(CalendarItem? item)
    {
        if (item != null)
        {
            _calendarService.RemoveItem(item);
        }
    }

    private DispatcherTimer? _reminderToastTimer;

    private void OnCalendarReminderDue(CalendarItem item)
    {
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            PowerToastIconBrush = CreateFrozenBrush("#FF5E36");
            PowerToastIcon = "\uE787";
            PowerToastTitle = item.Title;
            PowerToastDetail = item.DisplayTime;
            IsPowerToastActive = true;

            _reminderToastTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _reminderToastTimer.Stop();
            _reminderToastTimer.Tick -= OnReminderToastTick;
            _reminderToastTimer.Tick += OnReminderToastTick;
            _reminderToastTimer.Start();
        });
    }

    private void OnReminderToastTick(object? sender, EventArgs e)
    {
        _reminderToastTimer?.Stop();
        IsPowerToastActive = false;
    }

    // ── Screen capture command ────────────────────────────────────────────────

    [RelayCommand]
    private void CaptureScreen()
    {
        _screenCaptureService.CaptureScreenToShelf();
        SelectedTabIndex = 2; // Open Shelf tab (Dock & Clipboard)
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
        bool next = !IsDriveMenuOpen;
        CloseAllMenus();
        IsDriveMenuOpen = next;
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
        bool next = !IsAudioDeviceMenuOpen;
        CloseAllMenus();
        IsAudioDeviceMenuOpen = next;
        if (next)
        {
            RefreshAudioDevices();
        }
    }

    [RelayCommand]
    private void ToggleBottomAudioMenu()
    {
        bool next = !IsBottomAudioMenuOpen;
        CloseAllMenus();
        IsBottomAudioMenuOpen = next;
        if (next)
        {
            RefreshAudioDevices();
        }
    }

    [RelayCommand]
    private void ToggleActiveTimer()
    {
        _timerService.ToggleActiveTimer();
    }

    [RelayCommand]
    private void SelectAudioDevice(AudioDeviceItem? item)
    {
        if (item != null)
        {
            _audioService.SetDefaultPlaybackDevice(item.Id);
            IsAudioDeviceMenuOpen = false;
            IsBottomAudioMenuOpen = false;
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
            BluetoothBatteryText = $"{btBattery}%";
            HasBluetoothBattery = true;
        }
        else
        {
            BluetoothBatteryText = string.Empty;
            HasBluetoothBattery = false;
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

    public void RefreshSettingsBindings()
    {
        OnPropertyChanged(nameof(IsDualActivity));
        OnPropertyChanged(nameof(IsMediaOnlyActivity));
        OnPropertyChanged(nameof(IsTimerOnlyActivity));
        OnPropertyChanged(nameof(IsVerticalDock));
        OnPropertyChanged(nameof(IsHorizontalDock));
        _brightnessService.RefreshBrightnessAsync(_settingsService.Current.TargetMonitorIndex);
    }

    partial void OnScratchpadTextChanged(string value)
    {
        _scratchpadSaveTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        _scratchpadSaveTimer.Stop();
        _scratchpadSaveTimer.Tick -= OnScratchpadSaveTick;
        _scratchpadSaveTimer.Tick += OnScratchpadSaveTick;
        _scratchpadSaveTimer.Start();
    }

    private void OnScratchpadSaveTick(object? sender, EventArgs e)
    {
        _scratchpadSaveTimer?.Stop();
        _scratchpadService.Save(ScratchpadText);
    }

    [RelayCommand]
    private void ToggleStayAwake() => _powerAwakeService.Toggle();

    [RelayCommand]
    private void OpenScreenshotsFolder() => _screenCaptureService.OpenScreenshotsFolder();

    [RelayCommand]
    private async Task CopyScratchpad()
    {
        if (string.IsNullOrEmpty(ScratchpadText)) return;
        try
        {
            Clipboard.SetText(ScratchpadText);
            ScratchpadCopied = true;
            await Task.Delay(1200);
            ScratchpadCopied = false;
        }
        catch { /* clipboard busy */ }
    }

    [RelayCommand]
    private void ClearScratchpad()
    {
        ScratchpadText = string.Empty;
        _scratchpadService.Save(string.Empty);
    }

    [RelayCommand] private void OpenSettings() => OpenSettingsRequested?.Invoke();

    partial void OnDisplayBrightnessChanged(int value)
    {
        if (!_isSyncingBrightness)
        {
            _brightnessService.SetBrightness(value, _settingsService.Current.TargetMonitorIndex);
        }
    }

    public void NudgeVolumeFromGesture(int direction)
    {
        float cur = _audioService.GetMasterVolume();
        float next = Math.Clamp(cur + direction * 0.04f, 0f, 1f);
        _audioService.SetMasterVolume(next);
        UpdateVolumeFromDevice(next * 100f, _audioService.GetMute());

        bool collapseOnTimeout = _stateMachine.State == NotchState.Idle;
        IsBrightnessToastActive = false;
        IsVolumeToastActive = true;
        if (collapseOnTimeout) _stateMachine.ShowToastBanner();

        RestartGestureHudTimer(collapseOnTimeout);
    }

    public void NudgeBrightnessFromGesture(int direction)
    {
        int next = Math.Clamp(DisplayBrightness + direction * 5, 0, 100);
        DisplayBrightness = next;

        bool collapseOnTimeout = _stateMachine.State == NotchState.Idle;
        IsVolumeToastActive = false;
        IsBrightnessToastActive = true;
        if (collapseOnTimeout) _stateMachine.ShowToastBanner();

        RestartGestureHudTimer(collapseOnTimeout);
    }

    private void RestartGestureHudTimer(bool collapseOnTimeout)
    {
        _gestureHudTimer?.Stop();
        _gestureHudTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1400) };
        _gestureHudTimer.Tick += (_, _) =>
        {
            _gestureHudTimer?.Stop();
            IsVolumeToastActive = false;
            IsBrightnessToastActive = false;
            if (collapseOnTimeout && _stateMachine.State == NotchState.CollapsedHover)
            {
                _stateMachine.Collapse();
            }
        };
        _gestureHudTimer.Start();
    }

    [RelayCommand]
    private async Task PickScreenColor()
    {
        string? hex = null;
        try
        {
            IsModalPickerOpen = true;
            if (PrepareForScreenPickAsync != null)
            {
                await PrepareForScreenPickAsync();
            }
            hex = _colorPickerService.PickScreenColor();
        }
        finally
        {
            RestoreAfterScreenPick?.Invoke();
            IsModalPickerOpen = false;
        }

        if (!string.IsNullOrEmpty(hex))
        {
            PowerToastIconBrush = CreateFrozenBrush("#FF5E36");
            PowerToastIcon = "\uEF3C";
            PowerToastTitle = "Copied Color";
            PowerToastDetail = hex;
            IsPowerToastActive = true;
            if (_stateMachine.State == NotchState.Idle) _stateMachine.ShowToastBanner();

            _reminderToastTimer?.Stop();
            _reminderToastTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2200) };
            _reminderToastTimer.Tick += (_, _) =>
            {
                _reminderToastTimer?.Stop();
                IsPowerToastActive = false;
                if (_stateMachine.State == NotchState.CollapsedHover && !IsMediaToastActive && !IsVolumeToastActive && !IsBrightnessToastActive)
                {
                    _stateMachine.Collapse();
                }
            };
            _reminderToastTimer.Start();
        }
    }

    [RelayCommand]
    private static void LockWorkstation()
    {
        try { LockWorkStation(); } catch { }
    }

    [RelayCommand]
    private static void OpenTerminal() => SafeLaunch("wt.exe");

    [RelayCommand]
    private static void OpenBluetoothSettings() => SafeLaunch("ms-settings:bluetooth");

    [RelayCommand]
    private static void OpenTaskManager() => SafeLaunch("taskmgr.exe");

    [RelayCommand]
    private static void OpenSnippingTool() => SafeLaunch("snippingtool.exe");

    [RelayCommand]
    private static void OpenNotepad() => SafeLaunch("notepad.exe");

    [RelayCommand]
    private static void OpenCalculator() => SafeLaunch("calc.exe");

    private static void SafeLaunch(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch { /* best effort */ }
    }

    // ── Clock & Upcoming Reminder Glance ──────────────────────────────────────

    private void UpdateUpcomingReminder()
    {
        var next = _calendarService.GetNextTodayReminder(DateTime.Now);
        if (next != null)
        {
            HasUpcomingReminder = true;
            UpcomingReminderText = next.IsAllDay ? next.Title : $"{next.TimeText} • {next.Title}";
        }
        else
        {
            HasUpcomingReminder = false;
            UpcomingReminderText = string.Empty;
        }
    }

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
        var now = DateTime.Now;
        ClockText = now.ToString("HH:mm");
        DateText = now.ToString("ddd, MMM d");
        UpdateGreetingState(now, notifyRollover: true);
        _calendarService.CheckDueReminders(now);
        UpdateUpcomingReminder();
    }

    private void UpdateGreetingState(DateTime now, bool notifyRollover)
    {
        var info = GreetingHelper.GetGreeting(now);
        GreetingTitle = info.DisplayTitle;
        GreetingTagline = info.Tagline;
        GreetingIcon = info.IconGlyph;
        GreetingAccentBrush = CreateFrozenBrush(info.AccentHex);

        if (string.IsNullOrEmpty(_lastGreetingPeriodKey))
        {
            _lastGreetingPeriodKey = info.PeriodKey;
            return;
        }

        if (_lastGreetingPeriodKey != info.PeriodKey)
        {
            _lastGreetingPeriodKey = info.PeriodKey;
            if (notifyRollover)
            {
                GreetingPeriodRolledOver?.Invoke(info);
            }
        }
    }

    public static Brush CreateFrozenBrush(string hex)
    {
        try
        {
            var color = (Color)ColorConverter.ConvertFromString(hex);
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
        catch
        {
            var fallback = new SolidColorBrush(Color.FromRgb(0xFF, 0x5E, 0x36));
            fallback.Freeze();
            return fallback;
        }
    }

    private void StopClock()
    {
        _clockTimer?.Stop();
        _clockTimer = null;
    }
}
