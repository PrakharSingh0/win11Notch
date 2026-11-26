using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Media;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
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
    private readonly HomeLayoutService _homeLayoutService;
    private readonly BrightnessService _brightnessService;
    private readonly ColorPickerService _colorPickerService;

    [DllImport("user32.dll")]
    private static extern bool LockWorkStation();

    public TimerService Timer => _timerService;
    public CalendarService Calendar => _calendarService;
    public HomeLayoutService HomeLayout => _homeLayoutService;

    public const int TabHome      = 0;
    public const int TabMedia     = 1;
    public const int TabTimers    = 2;
    public const int TabShelf     = 3;
    public const int TabCalendar  = 4;
    public const int TabResources = 5;
    public const int TabTools     = 6;
    public const int TabShare     = 7;

    [ObservableProperty] private NotchState _currentState = NotchState.Idle;
    [ObservableProperty] private string _clockText = string.Empty;
    [ObservableProperty] private string _dateText = string.Empty;
    [ObservableProperty] private int _selectedTabIndex = TabHome;
    [ObservableProperty] private int _shelfSubTabIndex = 0;
    [ObservableProperty] private int _clipboardFilterIndex = 0; // 0: All, 1: Text, 2: Images, 3: URLs

    // Home Dashboard overview properties
    [ObservableProperty] private string _homeDayNumberText = DateTime.Now.ToString("dd");
    [ObservableProperty] private string _homeDayOfWeekText = DateTime.Now.ToString("dddd");
    [ObservableProperty] private string _homeMonthYearText = DateTime.Now.ToString("MMMM yyyy");
    [ObservableProperty] private string _homeTodaySummaryText = "Clear schedule today";
    [ObservableProperty] private bool   _hasHomeUpcomingItems;
    [ObservableProperty] private string _homeShelfSummaryText = "Drop files to stage";
    [ObservableProperty] private string _homeClipboardCountText = "0 clips";
    [ObservableProperty] private string _homeTimerSummaryText = "25:00 ready";
    [ObservableProperty] private string _homeScratchpadPreviewText = "Quick Notes";
    public ObservableCollection<CalendarItem> HomeUpcomingItems { get; } = new();

    // Editable Home Bento Grid state
    [ObservableProperty] private bool   _isHomeEditMode;
    [ObservableProperty] private bool   _isAddHomeWidgetOpen;
    [ObservableProperty] private string _homeResizeStatusText = string.Empty;

    // Calendar quick composer & filter properties
    [ObservableProperty] private bool _isAddingCalendarEvent;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ComposerSaveButtonText))]
    private bool _isEditingCalendarItem;
    private CalendarItem? _editingCalendarItem;

    [ObservableProperty] private string _newCalendarTitle = string.Empty;
    [ObservableProperty] private string _newCalendarTime = "09:00";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NewCalendarHourText))]
    private int _newCalendarDisplayHour = 9;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NewCalendarMinuteText))]
    private int _newCalendarMinute = 0;

    [ObservableProperty] private string _newCalendarHourInput = "9";
    [ObservableProperty] private string _newCalendarMinuteInput = "00";
    private bool _isSyncingTimeInputs;

    partial void OnNewCalendarHourInputChanged(string value)
    {
        if (_isSyncingTimeInputs) return;
        string digits = new string((value ?? string.Empty).Where(char.IsDigit).ToArray());
        if (!int.TryParse(digits, out int h)) return;

        if (Use24HourClock)
        {
            NewCalendarDisplayHour = Math.Clamp(h, 0, 23);
        }
        else if (h >= 13 && h <= 23)
        {
            NewCalendarDisplayHour = h - 12;
            NewCalendarIsPm        = true;
        }
        else if (h == 0)
        {
            NewCalendarDisplayHour = 12;
            NewCalendarIsPm        = false;
        }
        else
        {
            NewCalendarDisplayHour = Math.Clamp(h, 1, 12);
        }

        RebuildCanonicalTimeFromPicker(syncInputs: false);
    }

    partial void OnNewCalendarMinuteInputChanged(string value)
    {
        if (_isSyncingTimeInputs) return;
        string digits = new string((value ?? string.Empty).Where(char.IsDigit).ToArray());
        if (!int.TryParse(digits, out int m)) return;

        NewCalendarMinute = Math.Clamp(m, 0, 59);
        RebuildCanonicalTimeFromPicker(syncInputs: false);
    }

    public void NormalizeTimeInputs()
    {
        _isSyncingTimeInputs = true;
        try
        {
            NewCalendarHourInput   = Use24HourClock ? NewCalendarDisplayHour.ToString("00") : NewCalendarDisplayHour.ToString();
            NewCalendarMinuteInput = NewCalendarMinute.ToString("00");
        }
        finally
        {
            _isSyncingTimeInputs = false;
        }
    }

    [ObservableProperty] private bool _newCalendarIsPm;
    [ObservableProperty] private bool _newCalendarIsAllDay;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NewCalendarIsEvent))]
    [NotifyPropertyChangedFor(nameof(ComposerModeHintText))]
    private bool _newCalendarIsReminder = true;

    [ObservableProperty] private string _newCalendarAccentHex = "#FF5E36";
    [ObservableProperty] private int _newCalendarDurationMinutes = 60;
    [ObservableProperty] private string _newCalendarLocation = string.Empty;
    [ObservableProperty] private CalendarRepeatRule _newCalendarRepeatRule = CalendarRepeatRule.Once;
    [ObservableProperty] private bool _newCalendarIsHighPriority;
    [ObservableProperty] private bool _newCalendarPlayChime = true;
    [ObservableProperty] private int _calendarAgendaFilterIndex = 0;

    public bool NewCalendarIsEvent => !NewCalendarIsReminder;
    public bool Use24HourClock => _settingsService.Current.Use24HourClock;
    public string NewCalendarHourText => Use24HourClock ? NewCalendarDisplayHour.ToString("00") : NewCalendarDisplayHour.ToString();
    public string NewCalendarMinuteText => NewCalendarMinute.ToString("00");
    public string ComposerSaveButtonText => IsEditingCalendarItem ? "Update" : "Save";
    public string ComposerModeHintText => NewCalendarIsReminder
        ? "Stays pinned on the Notch pill until Done, Snoozed, or Dismissed"
        : "Blocks agenda time with range & live countdown";

    partial void OnCalendarAgendaFilterIndexChanged(int value)
    {
        _calendarService.AgendaFilter = value switch
        {
            1 => CalendarAgendaFilter.Reminders,
            2 => CalendarAgendaFilter.Events,
            _ => CalendarAgendaFilter.All
        };
    }

    // Persistent Active Reminder Alert Queue (pinned on Notch Pill until dismissed)
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveReminderTitle))]
    [NotifyPropertyChangedFor(nameof(ActiveReminderDetailText))]
    [NotifyPropertyChangedFor(nameof(ActiveReminderAccentBrush))]
    private CalendarItem? _activeReminderAlert;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMultiplePendingAlerts))]
    [NotifyPropertyChangedFor(nameof(PendingAlertQueueBadge))]
    private int _pendingReminderAlertCount;

    [ObservableProperty] private bool _isReminderAlertActive;
    public event Action<bool>? ReminderAlertActiveChanged;
    partial void OnIsReminderAlertActiveChanged(bool value) => ReminderAlertActiveChanged?.Invoke(value);

    public bool HasMultiplePendingAlerts => PendingReminderAlertCount > 1;
    public string PendingAlertQueueBadge => PendingReminderAlertCount > 1 ? $"+{PendingReminderAlertCount - 1}" : string.Empty;
    public string ActiveReminderTitle => ActiveReminderAlert?.Title ?? string.Empty;
    public string ActiveReminderDetailText
    {
        get
        {
            if (ActiveReminderAlert == null) return string.Empty;
            if (ActiveReminderAlert.IsOverdue && !string.IsNullOrEmpty(ActiveReminderAlert.RelativeStatusText))
                return $"{ActiveReminderAlert.CompactTimeText} • {ActiveReminderAlert.RelativeStatusText}";
            return ActiveReminderAlert.CompactTimeText;
        }
    }
    public Brush ActiveReminderAccentBrush => CreateFrozenBrush(ActiveReminderAlert?.AccentHex ?? "#FF5E36");
    public string SnoozeButtonLabel => $"+{Math.Clamp(_settingsService.Current.DefaultReminderSnoozeMinutes, 5, 60)}m";

    // Drag-and-drop split action overlay
    [ObservableProperty] private bool _isFileDragOverlayVisible;
    [ObservableProperty] private bool _isDropDockHovered;
    [ObservableProperty] private bool _isDropShareHovered;
    [ObservableProperty] private bool _hasPendingDropChoice;
    [ObservableProperty] private string _pendingDropSummary = "Choose what to do with dropped files";

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

    public bool IsDualActivity => ShowFullLiveActivity && IsMediaPlaying && IsStopwatchRunning && _settingsService.Current.DualActivityEnabled;
    public bool IsMediaOnlyActivity => ShowFullLiveActivity && IsMediaPlaying && (!IsStopwatchRunning || !_settingsService.Current.DualActivityEnabled);
    public bool IsTimerOnlyActivity => ShowFullLiveActivity && !IsMediaPlaying && IsStopwatchRunning;

    [ObservableProperty] private bool _isOnDesktop = true;
    [ObservableProperty] private string _idleMediaDisplayText = "No Media Playing";
    private bool _showArtistInIdleTicker;
    private DispatcherTimer? _idleTickerTimer;
    private DispatcherTimer? _desktopClockTimer;

    public bool ShowFullLiveActivity =>
        (IsOnDesktop || _settingsService.Current.HandleVisibilityMode == "AlwaysLive")
        && (IsMediaPlaying || IsStopwatchRunning);

    public bool ShowDesktopAmbientPill =>
        IsOnDesktop
        && !IsMediaPlaying
        && !IsStopwatchRunning
        && _settingsService.Current.ShowDesktopAmbientGlance;

    public bool IsAmbientDesktopActivity => ShowDesktopAmbientPill;
    public bool IsMinimalRestingPill => !ShowFullLiveActivity && !ShowDesktopAmbientPill;
    public bool HasCompactAppPulse => IsMinimalRestingPill && (IsMediaPlaying || IsStopwatchRunning);

    partial void OnIsOnDesktopChanged(bool value) => NotifyIdlePresentationChanged();

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
        if (value != TabHome)
        {
            IsHomeEditMode      = false;
            IsAddHomeWidgetOpen = false;
            HomeResizeStatusText = string.Empty;
        }
        if (value == TabHome)
        {
            RefreshHomeDashboard();
        }
        else if (value == TabShare)
        {
            RefreshQuickShareState();
        }
    }

    [RelayCommand]
    private void NavigateToTab(object? tabParam)
    {
        if (IsHomeEditMode) return;
        if (int.TryParse(tabParam?.ToString(), out int idx))
        {
            SelectedTabIndex = Math.Clamp(idx, TabHome, TabShare);
        }
    }

    [RelayCommand]
    private void ToggleHomeEditMode()
    {
        IsHomeEditMode = !IsHomeEditMode;
        if (!IsHomeEditMode)
        {
            IsAddHomeWidgetOpen  = false;
            HomeResizeStatusText = string.Empty;
        }
    }

    [RelayCommand]
    private void EnterHomeEditMode()
    {
        SelectedTabIndex = TabHome;
        IsHomeEditMode   = true;
    }

    [RelayCommand]
    private void OpenAddHomeWidget()
    {
        IsHomeEditMode      = true;
        IsAddHomeWidgetOpen = true;
    }

    [RelayCommand]
    private void CloseAddHomeWidget() => IsAddHomeWidgetOpen = false;

    [RelayCommand]
    private void AddHomeSlot(object? param)
    {
        if (param is HomeWidgetCatalogItem catalogItem)
        {
            _homeLayoutService.AddSlot(catalogItem.Kind, catalogItem.DefaultColSpan, catalogItem.DefaultRowSpan);
            return;
        }

        if (param is HomeWidgetKind kind)
        {
            _homeLayoutService.AddSlot(kind);
        }
    }

    [RelayCommand]
    private void RemoveHomeSlot(HomeSlotItem? slot)
    {
        if (slot != null) _homeLayoutService.RemoveSlot(slot);
    }

    [RelayCommand]
    private void CycleHomeSlotSize(HomeSlotItem? slot)
    {
        if (slot != null) _homeLayoutService.CycleSlotSize(slot);
    }

    [RelayCommand]
    private void MoveHomeSlotLeft(HomeSlotItem? slot)
    {
        if (slot != null) _homeLayoutService.MoveSlot(slot, -1);
    }

    [RelayCommand]
    private void MoveHomeSlotRight(HomeSlotItem? slot)
    {
        if (slot != null) _homeLayoutService.MoveSlot(slot, +1);
    }

    [RelayCommand]
    private void ResetHomeLayout()
    {
        _homeLayoutService.ResetToDefault();
        HomeResizeStatusText = string.Empty;
    }

    public void ResizeHomeSlot(HomeSlotItem slot, int colSpan, int rowSpan)
    {
        _homeLayoutService.ResizeSlot(slot, colSpan, rowSpan);
    }

    public void SwapHomeSlots(HomeSlotItem source, HomeSlotItem target)
    {
        _homeLayoutService.SwapSlots(source, target);
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
        HomeLayoutService homeLayoutService,
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
        _homeLayoutService = homeLayoutService;
        _brightnessService = brightnessService;
        _colorPickerService = colorPickerService;

        _scratchpadText = _scratchpadService.Load();
        _calendarService.ApplyClockFormat(_settingsService.Current.Use24HourClock);
        _powerAwakeService.AwakeStateChanged += awake => IsStayAwakeEnabled = awake;
        _calendarService.ReminderDue += OnCalendarReminderDue;
        _calendarService.CalendarChanged += () =>
        {
            UpdateUpcomingReminder();
            SyncActiveReminderAlerts(playChimeForNewTriggers: false);
            ScheduleNextReminderCheck();
            RefreshHomeDashboard();
        };
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
        _brightnessService.ExternalBrightnessChanged += val =>
        {
            _isSyncingBrightness = true;
            try
            {
                DisplayBrightness = val;
                IsBrightnessSupported = _brightnessService.IsBrightnessSupported;
                BrightnessTooltip = _brightnessService.StatusTooltip;
            }
            finally { _isSyncingBrightness = false; }

            if (_stateMachine.State is NotchState.Idle or NotchState.CollapsedHover)
            {
                IsVolumeToastActive = false;
                IsPowerToastActive = false;
                IsBrightnessToastActive = true;
                bool collapseOnTimeout = _stateMachine.State == NotchState.Idle;
                if (collapseOnTimeout) _stateMachine.ShowToastBanner();
                RestartGestureHudTimer(collapseOnTimeout);
            }
        };
        _brightnessService.RefreshBrightnessAsync(_settingsService.Current.TargetMonitorIndex);
        UpdateGreetingState(DateTime.Now, notifyRollover: false);
        UpdateDateTimeFields(DateTime.Now);
        UpdateUpcomingReminder();
        SystemEvents.PowerModeChanged += OnSystemPowerModeChanged;
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            SyncActiveReminderAlerts(playChimeForNewTriggers: true);
            ScheduleNextReminderCheck();
        }, DispatcherPriority.Background);
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
            RefreshHomeTimerSummary();
            NotifyIdlePresentationChanged();
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
            RefreshHomeWorkspaceSummaries();
        };
        HasStagedFiles = _shelfService.Items.Count > 0;
        ShelfCountText = _shelfService.Items.Count > 0 ? $"{_shelfService.Items.Count}" : string.Empty;

        // Wire clipboard changes
        _clipboardService.History.CollectionChanged += (_, _) =>
        {
            RefreshFilteredClipboard();
            RefreshHomeWorkspaceSummaries();
        };
        RefreshFilteredClipboard();
        RefreshHomeDashboard();

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
                IsHomeEditMode = false;
                IsAddHomeWidgetOpen = false;
                HomeResizeStatusText = string.Empty;
                IsFileDragOverlayVisible = false;
                IsDropDockHovered = false;
                IsDropShareHovered = false;
                HasPendingDropChoice = false;
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
            NotifyIdlePresentationChanged();
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
        _showArtistInIdleTicker = false;
        NotifyIdlePresentationChanged();

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
        // Active only when waveform bars are visible (Hover/Peek, or Idle when Full Live Preview is active)
        bool shouldRun = IsMediaPlaying
                         && CurrentState != NotchState.Stealth
                         && (CurrentState != NotchState.Idle || ShowFullLiveActivity);
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

        if (CurrentState == NotchState.PeekExpanded && SelectedTabIndex == TabMedia)
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
        SelectedTabIndex = TabShare;

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
        if (IsAddingCalendarEvent)
        {
            IsAddingCalendarEvent = false;
            ResetComposerState();
            return;
        }

        ResetComposerState();
        var defaultTime = DateTime.Now.AddMinutes(30);
        SyncPickerFromDateTime(defaultTime);
        IsAddingCalendarEvent = true;
    }

    [RelayCommand]
    private void EditCalendarItem(CalendarItem? item)
    {
        if (item == null) return;

        _editingCalendarItem       = item;
        IsEditingCalendarItem      = true;
        NewCalendarTitle           = item.Title;
        NewCalendarIsAllDay        = item.IsAllDay;
        NewCalendarIsReminder      = item.IsReminder;
        NewCalendarAccentHex       = item.AccentHex;
        NewCalendarDurationMinutes = item.IsReminder ? 60 : Math.Max(15, item.DurationMinutes);
        NewCalendarLocation        = item.Location;
        NewCalendarRepeatRule      = item.RepeatRule;
        NewCalendarIsHighPriority  = item.IsHighPriority;
        NewCalendarPlayChime       = item.PlayChime;

        if (item.TryGetTimeOfDay(out var tod))
            SyncPickerFromDateTime(DateTime.Today.Add(tod));
        else
            SyncPickerFromDateTime(DateTime.Now.AddMinutes(30));

        IsAddingCalendarEvent = true;
    }

    [RelayCommand]
    private void SetCalendarType(object? isReminderParam)
    {
        if (bool.TryParse(isReminderParam?.ToString(), out bool isReminder))
        {
            NewCalendarIsReminder = isReminder;
            if (!IsEditingCalendarItem && NewCalendarAccentHex is "#FF5E36" or "#00E5FF")
            {
                NewCalendarAccentHex = isReminder ? "#FF5E36" : "#00E5FF";
            }
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
    private void StepCalendarHour(object? deltaParam)
    {
        if (!int.TryParse(deltaParam?.ToString(), out int delta)) return;
        NewCalendarIsAllDay = false;

        if (Use24HourClock)
        {
            NewCalendarDisplayHour = (NewCalendarDisplayHour + delta + 24) % 24;
        }
        else
        {
            int idx = (NewCalendarDisplayHour - 1 + delta + 12) % 12;
            NewCalendarDisplayHour = idx + 1;
        }
        RebuildCanonicalTimeFromPicker();
    }

    [RelayCommand]
    private void StepCalendarMinute(object? deltaParam)
    {
        if (!int.TryParse(deltaParam?.ToString(), out int delta)) return;
        NewCalendarIsAllDay = false;
        NewCalendarMinute = (NewCalendarMinute + delta + 60) % 60;
        RebuildCanonicalTimeFromPicker();
    }

    [RelayCommand]
    private void SetCalendarMeridiem(object? isPmParam)
    {
        if (bool.TryParse(isPmParam?.ToString(), out bool isPm))
        {
            NewCalendarIsAllDay = false;
            NewCalendarIsPm = isPm;
            RebuildCanonicalTimeFromPicker();
        }
    }

    [RelayCommand]
    private void ApplyTimePreset(string? preset)
    {
        NewCalendarIsAllDay = false;
        DateTime target = preset switch
        {
            "+15m"      => DateTime.Now.AddMinutes(15),
            "+1h"       => DateTime.Now.AddHours(1),
            "Evening"   => DateTime.Today.AddHours(17),
            "Tonight"   => DateTime.Today.AddHours(20),
            "Morning"   => DateTime.Today.AddHours(9),
            _           => DateTime.Now.AddMinutes(30)
        };
        SyncPickerFromDateTime(target);
    }

    [RelayCommand]
    private void SelectEventDuration(object? minutesParam)
    {
        if (int.TryParse(minutesParam?.ToString(), out int mins) && mins > 0)
        {
            NewCalendarDurationMinutes = mins;
        }
    }

    [RelayCommand]
    private void SetReminderRepeatRule(object? ruleParam)
    {
        if (Enum.TryParse<CalendarRepeatRule>(ruleParam?.ToString(), out var rule))
        {
            NewCalendarRepeatRule = rule;
        }
    }

    [RelayCommand]
    private void ToggleCalendarHighPriority() => NewCalendarIsHighPriority = !NewCalendarIsHighPriority;

    [RelayCommand]
    private void ToggleCalendarPlayChime() => NewCalendarPlayChime = !NewCalendarPlayChime;

    [RelayCommand]
    private void SetCalendarAgendaFilter(object? filterParam)
    {
        if (int.TryParse(filterParam?.ToString(), out int idx))
        {
            CalendarAgendaFilterIndex = Math.Clamp(idx, 0, 2);
        }
    }

    [RelayCommand]
    private void SaveNewCalendarItem()
    {
        if (string.IsNullOrWhiteSpace(NewCalendarTitle)) return;

        RebuildCanonicalTimeFromPicker();

        if (IsEditingCalendarItem && _editingCalendarItem != null)
        {
            _calendarService.UpdateItem(
                _editingCalendarItem,
                NewCalendarTitle,
                NewCalendarTime,
                NewCalendarIsAllDay,
                NewCalendarIsReminder,
                NewCalendarAccentHex,
                NewCalendarDurationMinutes,
                NewCalendarLocation,
                NewCalendarRepeatRule,
                NewCalendarIsHighPriority,
                NewCalendarPlayChime);
        }
        else
        {
            _calendarService.AddItem(
                NewCalendarTitle,
                NewCalendarTime,
                NewCalendarIsAllDay,
                NewCalendarIsReminder,
                NewCalendarAccentHex,
                NewCalendarDurationMinutes,
                NewCalendarLocation,
                NewCalendarRepeatRule,
                NewCalendarIsHighPriority,
                NewCalendarPlayChime);
        }

        IsAddingCalendarEvent = false;
        ResetComposerState();
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

    [RelayCommand]
    private void CompleteActiveReminder()
    {
        if (ActiveReminderAlert is not { } current) return;
        _calendarService.ToggleComplete(current);
    }

    [RelayCommand]
    private void SnoozeActiveReminder()
    {
        if (ActiveReminderAlert is not { } current) return;
        int snoozeMins = Math.Clamp(_settingsService.Current.DefaultReminderSnoozeMinutes, 5, 60);
        _calendarService.SnoozeReminder(current, TimeSpan.FromMinutes(snoozeMins));
    }

    [RelayCommand]
    private void DismissActiveReminder()
    {
        if (ActiveReminderAlert is not { } current) return;
        _calendarService.DismissReminderAlert(current);
    }

    [RelayCommand]
    private void SnoozeAllOverdue()
    {
        int snoozeMins = Math.Clamp(_settingsService.Current.DefaultReminderSnoozeMinutes, 5, 60);
        _calendarService.SnoozeAllOverdueToday(TimeSpan.FromMinutes(snoozeMins));
    }

    private void ResetComposerState()
    {
        _editingCalendarItem       = null;
        IsEditingCalendarItem      = false;
        NewCalendarTitle           = string.Empty;
        NewCalendarIsAllDay        = false;
        NewCalendarIsReminder      = true;
        NewCalendarAccentHex       = "#FF5E36";
        NewCalendarDurationMinutes = 60;
        NewCalendarLocation        = string.Empty;
        NewCalendarRepeatRule      = CalendarRepeatRule.Once;
        NewCalendarIsHighPriority  = false;
        NewCalendarPlayChime       = true;
    }

    private void SyncPickerFromDateTime(DateTime dt)
    {
        if (Use24HourClock)
        {
            NewCalendarDisplayHour = dt.Hour;
            NewCalendarIsPm        = dt.Hour >= 12;
        }
        else
        {
            NewCalendarIsPm        = dt.Hour >= 12;
            int h12                = dt.Hour % 12;
            NewCalendarDisplayHour = h12 == 0 ? 12 : h12;
        }

        NewCalendarMinute = dt.Minute;
        RebuildCanonicalTimeFromPicker();
    }

    private void RebuildCanonicalTimeFromPicker(bool syncInputs = true)
    {
        int hour24;
        if (Use24HourClock)
        {
            hour24 = Math.Clamp(NewCalendarDisplayHour, 0, 23);
        }
        else
        {
            int h12 = Math.Clamp(NewCalendarDisplayHour, 1, 12);
            hour24 = NewCalendarIsPm
                ? (h12 == 12 ? 12 : h12 + 12)
                : (h12 == 12 ? 0 : h12);
        }

        int minute = Math.Clamp(NewCalendarMinute, 0, 59);
        NewCalendarTime = $"{hour24:00}:{minute:00}";
        if (syncInputs) NormalizeTimeInputs();
    }

    private DispatcherTimer? _reminderToastTimer;
    private DispatcherTimer? _nextReminderTimer;
    private int              _lastStatusRefreshMinute = -1;
    private bool             _suppressChimeOnSync;

    private void OnSystemPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Resume) return;
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            SyncActiveReminderAlerts(playChimeForNewTriggers: true);
            ScheduleNextReminderCheck();
            UpdateUpcomingReminder();
        });
    }

    private void OnCalendarReminderDue(CalendarItem item)
    {
        if (_suppressChimeOnSync) return;
        if (_settingsService.Current.ReminderSoundChimesEnabled && item.PlayChime)
        {
            try { SystemSounds.Asterisk.Play(); } catch { }
        }
    }

    private void SyncActiveReminderAlerts(bool playChimeForNewTriggers)
    {
        var now = DateTime.Now;
        bool prevSuppress = _suppressChimeOnSync;
        _suppressChimeOnSync = !playChimeForNewTriggers;
        try
        {
            _calendarService.CheckDueReminders(now);
        }
        finally
        {
            _suppressChimeOnSync = prevSuppress;
        }

        var dueList = _calendarService.GetActiveDueReminders(now);
        PendingReminderAlertCount = dueList.Count;
        ActiveReminderAlert       = dueList.FirstOrDefault();
        IsReminderAlertActive     = ActiveReminderAlert != null;

        OnPropertyChanged(nameof(ActiveReminderTitle));
        OnPropertyChanged(nameof(ActiveReminderDetailText));
        OnPropertyChanged(nameof(ActiveReminderAccentBrush));
        OnPropertyChanged(nameof(PendingAlertQueueBadge));
        OnPropertyChanged(nameof(HasMultiplePendingAlerts));
        OnPropertyChanged(nameof(SnoozeButtonLabel));

        _stateMachine.SetPinnedReminderAlert(IsReminderAlertActive);
    }

    private void ScheduleNextReminderCheck()
    {
        _nextReminderTimer?.Stop();

        DateTime now = DateTime.Now;
        DateTime? nextTrigger = _calendarService.GetNextScheduledTrigger(now);
        if (!nextTrigger.HasValue) return;

        TimeSpan delay = nextTrigger.Value - now;
        if (delay <= TimeSpan.Zero)
        {
            delay = TimeSpan.FromMilliseconds(200);
        }
        else
        {
            delay = delay.Add(TimeSpan.FromMilliseconds(250));
            TimeSpan maxLeg = TimeSpan.FromHours(6);
            if (delay > maxLeg) delay = maxLeg;
        }

        _nextReminderTimer ??= new DispatcherTimer();
        _nextReminderTimer.Tick -= OnNextReminderTimerTick;
        _nextReminderTimer.Tick += OnNextReminderTimerTick;
        _nextReminderTimer.Interval = delay;
        _nextReminderTimer.Start();
    }

    private void OnNextReminderTimerTick(object? sender, EventArgs e)
    {
        _nextReminderTimer?.Stop();
        SyncActiveReminderAlerts(playChimeForNewTriggers: true);
        UpdateUpcomingReminder();
        ScheduleNextReminderCheck();
    }

    // ── Screen capture command ────────────────────────────────────────────────

    [RelayCommand]
    private void CaptureScreen()
    {
        _screenCaptureService.CaptureScreenToShelf();
        SelectedTabIndex = TabShelf; // Open Shelf tab (Dock & Clipboard)
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
        bool use24h = _settingsService.Current.Use24HourClock;
        _calendarService.ApplyClockFormat(use24h);
        OnPropertyChanged(nameof(Use24HourClock));
        OnPropertyChanged(nameof(SnoozeButtonLabel));
        SyncPickerFromDateTime(CalendarService.TryParseTime(NewCalendarTime, out var tod)
            ? DateTime.Today.Add(tod)
            : DateTime.Now.AddMinutes(30));

        UpdateDateTimeFields(DateTime.Now);
        UpdateUpcomingReminder();
        SyncActiveReminderAlerts(playChimeForNewTriggers: false);
        RefreshHomeDashboard();

        NotifyIdlePresentationChanged();
        OnPropertyChanged(nameof(IsVerticalDock));
        OnPropertyChanged(nameof(IsHorizontalDock));
        _brightnessService.RefreshBrightnessAsync(_settingsService.Current.TargetMonitorIndex);
    }

    public void NotifyIdlePresentationChanged()
    {
        OnPropertyChanged(nameof(ShowFullLiveActivity));
        OnPropertyChanged(nameof(ShowDesktopAmbientPill));
        OnPropertyChanged(nameof(IsDualActivity));
        OnPropertyChanged(nameof(IsMediaOnlyActivity));
        OnPropertyChanged(nameof(IsTimerOnlyActivity));
        OnPropertyChanged(nameof(IsAmbientDesktopActivity));
        OnPropertyChanged(nameof(IsMinimalRestingPill));
        OnPropertyChanged(nameof(HasCompactAppPulse));
        RefreshIdleMediaDisplayText();
        UpdateSpectrumState();
        UpdateDesktopIdleTimers();
    }

    private void RefreshIdleMediaDisplayText()
    {
        if (!IsMediaPlaying)
        {
            IdleMediaDisplayText = MediaTitle;
            return;
        }

        bool useArtist = _showArtistInIdleTicker && !string.IsNullOrWhiteSpace(MediaArtist);
        IdleMediaDisplayText = useArtist ? $"♪ {MediaArtist}" : MediaTitle;
    }

    private void UpdateDesktopIdleTimers()
    {
        bool shouldRunMediaTicker = CurrentState == NotchState.Idle
                                    && ShowFullLiveActivity
                                    && IsMediaPlaying
                                    && !string.IsNullOrWhiteSpace(MediaArtist);
        if (shouldRunMediaTicker)
        {
            if (_idleTickerTimer == null)
            {
                _idleTickerTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
                _idleTickerTimer.Tick += (_, _) =>
                {
                    _showArtistInIdleTicker = !_showArtistInIdleTicker;
                    RefreshIdleMediaDisplayText();
                };
            }
            if (!_idleTickerTimer.IsEnabled) _idleTickerTimer.Start();
        }
        else
        {
            _idleTickerTimer?.Stop();
            _showArtistInIdleTicker = false;
            RefreshIdleMediaDisplayText();
        }

        bool shouldRunAmbientClock = CurrentState == NotchState.Idle && ShowDesktopAmbientPill;
        if (shouldRunAmbientClock)
        {
            if (_desktopClockTimer == null)
            {
                _desktopClockTimer = new DispatcherTimer();
                _desktopClockTimer.Tick += (_, _) =>
                {
                    UpdateDateTime();
                    if (_desktopClockTimer != null)
                    {
                        _desktopClockTimer.Interval = TimeSpan.FromSeconds(Math.Max(1, 60 - DateTime.Now.Second));
                    }
                };
            }
            UpdateDateTime();
            _desktopClockTimer.Interval = TimeSpan.FromSeconds(Math.Max(1, 60 - DateTime.Now.Second));
            if (!_desktopClockTimer.IsEnabled) _desktopClockTimer.Start();
        }
        else
        {
            _desktopClockTimer?.Stop();
        }
    }

    partial void OnScratchpadTextChanged(string value)
    {
        RefreshHomeWorkspaceSummaries();
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
            UpcomingReminderText = next.IsAllDay ? next.Title : $"{next.CompactTimeText} • {next.Title}";
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

    private void UpdateDateTimeFields(DateTime now)
    {
        string fmt = _settingsService.Current.Use24HourClock ? "HH:mm" : "h:mm tt";
        ClockText = now.ToString(fmt, CultureInfo.InvariantCulture);
        DateText = now.ToString("ddd, MMM d");
        HomeDayNumberText = now.ToString("dd");
        HomeDayOfWeekText = now.ToString("dddd");
        HomeMonthYearText = now.ToString("MMMM yyyy");
    }

    public void RefreshHomeDashboard()
    {
        var now = DateTime.Now;
        UpdateDateTimeFields(now);

        var (evCount, rmCount) = _calendarService.GetTodayCounts(now);
        if (evCount == 0 && rmCount == 0)
        {
            HomeTodaySummaryText = "Clear schedule today";
        }
        else
        {
            var parts = new List<string>();
            if (evCount > 0) parts.Add(evCount == 1 ? "1 Event" : $"{evCount} Events");
            if (rmCount > 0) parts.Add(rmCount == 1 ? "1 Reminder" : $"{rmCount} Reminders");
            HomeTodaySummaryText = string.Join(" • ", parts);
        }

        HomeUpcomingItems.Clear();
        foreach (var item in _calendarService.GetTodayUpcomingItems(now, 2))
        {
            HomeUpcomingItems.Add(item);
        }
        HasHomeUpcomingItems = HomeUpcomingItems.Count > 0;

        RefreshHomeTimerSummary();
        RefreshHomeWorkspaceSummaries();
    }

    private void RefreshHomeTimerSummary()
    {
        HomeTimerSummaryText = _timerService.IsAnyTimerRunning
            ? _timerService.IslandTimerText
            : $"{_timerService.FocusMinutes}m ready";
    }

    private void RefreshHomeWorkspaceSummaries()
    {
        int shelfCount = _shelfService.Items.Count;
        HomeShelfSummaryText = shelfCount > 0
            ? (shelfCount == 1 ? "1 file staged" : $"{shelfCount} files staged")
            : "Shelf empty";

        int clipCount = _clipboardService.History.Count;
        HomeClipboardCountText = clipCount == 1 ? "1 clip" : $"{clipCount} clips";

        string firstLine = (ScratchpadText ?? string.Empty)
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault()?.Trim() ?? string.Empty;
        HomeScratchpadPreviewText = string.IsNullOrEmpty(firstLine) ? "Quick Notes" : firstLine;
    }

    private void UpdateDateTime()
    {
        var now = DateTime.Now;
        UpdateDateTimeFields(now);
        UpdateGreetingState(now, notifyRollover: true);

        int minuteStamp = now.Hour * 60 + now.Minute;
        if (minuteStamp != _lastStatusRefreshMinute)
        {
            _lastStatusRefreshMinute = minuteStamp;
            _calendarService.RefreshDynamicStatuses(now);
            SyncActiveReminderAlerts(playChimeForNewTriggers: true);
            ScheduleNextReminderCheck();
            UpdateUpcomingReminder();
            RefreshHomeDashboard();
        }
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
