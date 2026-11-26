using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Win11Notch.Core.Settings;
using Win11Notch.Core.State;
using Win11Notch.Core.Win32;
using Win11Notch.Services;
using Win11Notch.ViewModels;

namespace Win11Notch.Views;

public partial class NotchWindow : Window
{
    private readonly NotchViewModel           _vm;
    private readonly NotchStateMachine        _sm;
    private readonly MonitorService           _monitor;
    private readonly SettingsService          _settings;
    private readonly HotkeyService            _hotkeyService;
    private readonly FullscreenDetectorService _fullscreenDetector;
    private readonly ShelfService             _shelfService;
    private readonly SettingsViewModel        _settingsVm;
    private readonly ClipboardService        _clipboardService;
    private readonly QuickShareService       _quickShareService;

    private SettingsWindow? _settingsWindow;
    private IntPtr          _hwnd;
    private Point           _dragStartPoint;

    // Animation constants — buttery Dynamic Island cubic-bezier spring curves
    private static readonly CubicEase       DropletExpandEase  = new() { EasingMode = EasingMode.EaseOut };
    private static readonly ExponentialEase DropletRetractEase = new() { Exponent = 3.2, EasingMode = EasingMode.EaseOut };
    private int _animGeneration;

    public NotchWindow(
        NotchViewModel           vm,
        NotchStateMachine        sm,
        MonitorService           monitor,
        SettingsService          settings,
        HotkeyService            hotkeyService,
        FullscreenDetectorService fullscreenDetector,
        ShelfService             shelfService,
        SettingsViewModel        settingsVm,
        ClipboardService         clipboardService,
        QuickShareService        quickShareService)
    {
        _vm                 = vm;
        _sm                 = sm;
        _monitor            = monitor;
        _settings           = settings;
        _hotkeyService      = hotkeyService;
        _fullscreenDetector = fullscreenDetector;
        _shelfService       = shelfService;
        _settingsVm         = settingsVm;
        _clipboardService   = clipboardService;
        _quickShareService  = quickShareService;

        DataContext = vm;
        InitializeComponent();

        SourceInitialized += OnSourceInitialized;
        Loaded            += OnLoaded;
        Deactivated       += OnDeactivated;
        PreviewKeyDown    += OnPreviewKeyDown;
        Closed            += OnClosed;

        _sm.StateChanged += OnStateChanged;

        _vm.OpenSettingsRequested   += () => ShowSettingsWindow(0);
        _vm.GreetingPeriodRolledOver += OnGreetingPeriodRolledOver;
        _settingsVm.SettingsChanged += OnSettingsUpdated;

        _vm.MediaToastActiveChanged      += OnMediaToastActiveChanged;
        _vm.VolumeToastActiveChanged     += OnVolumeToastActiveChanged;
        _vm.PowerToastActiveChanged      += OnPowerToastActiveChanged;
        _vm.BrightnessToastActiveChanged += OnBrightnessToastActiveChanged;
        _vm.ReminderAlertActiveChanged   += OnReminderAlertActiveChanged;

        _vm.PrepareForScreenPickAsync = async () =>
        {
            Opacity = 0;
            _sm.Collapse();
            await Task.Delay(75);
        };
        _vm.RestoreAfterScreenPick = () =>
        {
            Opacity = OpacityFor(_sm.State);
        };

        _vm.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName is nameof(_vm.IsMediaPlaying) or nameof(_vm.IsStopwatchRunning)
                or nameof(_vm.HasStagedFiles) or nameof(_vm.HasBluetoothBattery)
                or nameof(_vm.IsOnDesktop) or nameof(_vm.ShowFullLiveActivity)
                or nameof(_vm.ShowDesktopAmbientPill) or nameof(_vm.IsReminderAlertActive)
                or nameof(_vm.HasUpcomingReminder))
            {
                Dispatcher.InvokeAsync(() =>
                {
                    if (_sm.State == NotchState.Stealth) return;
                    UpdateHandleAppearance();
                    AnimateToCurrentState();
                });
            }
        };
    }

    private void AnimateToCurrentState()
    {
        if (_sm.State == NotchState.Stealth)
        {
            FinalizeVisualLayers(NotchState.Stealth);
            Opacity = 0.0;
            return;
        }
        App.Log($"[NotchWindow] AnimateToCurrentState enter (State={_sm.State})");
        var (tw, th) = _monitor.GetDimensions(
            _settings.Current.DockEdge,
            _sm.State,
            _vm.IsMediaToastActive,
            _vm.IsVolumeToastActive,
            _vm.IsPowerToastActive,
            _vm.IsMediaPlaying,
            _vm.IsStopwatchRunning,
            _settings.Current.HandleWidth,
            _settings.Current.HandleThickness,
            _settings.Current.DynamicContentSizing,
            _settings.Current.DualActivityEnabled,
            _vm.HasBluetoothBattery,
            _vm.IsBrightnessToastActive,
            _vm.HasUpcomingReminder,
            _vm.ShowFullLiveActivity,
            _vm.ShowDesktopAmbientPill,
            _vm.IsReminderAlertActive);

        var (tl, tt) = _monitor.ComputePosition(_settings.Current.DockEdge, tw, th, _settings.Current.TargetMonitorIndex);

        int gen = ++_animGeneration;
        double curW = ActualWidth  > 0 ? ActualWidth  : Width;
        double curH = ActualHeight > 0 ? ActualHeight : Height;
        double curL = !double.IsNaN(Left) ? Left : tl;
        double curT = !double.IsNaN(Top)  ? Top  : tt;

        bool isExpanding = th > curH || tw > curW;
        var dur = TimeSpan.FromMilliseconds(
            _sm.State == NotchState.PeekExpanded ? 240 :
            _sm.State == NotchState.CollapsedHover ? 210 : 185);
        IEasingFunction ease = isExpanding ? DropletExpandEase : DropletRetractEase;

        var animW = new DoubleAnimation(curW, tw, dur) { EasingFunction = ease, FillBehavior = FillBehavior.Stop };
        var animH = new DoubleAnimation(curH, th, dur) { EasingFunction = ease, FillBehavior = FillBehavior.Stop };
        var animL = new DoubleAnimation(curL, tl, dur) { EasingFunction = ease, FillBehavior = FillBehavior.Stop };
        var animT = new DoubleAnimation(curT, tt, dur) { EasingFunction = ease, FillBehavior = FillBehavior.Stop };

        double targetOp = OpacityFor(_sm.State);
        if (Math.Abs(Opacity - targetOp) > 0.02)
        {
            var animOp = new DoubleAnimation(Opacity, targetOp, dur) { EasingFunction = ease, FillBehavior = FillBehavior.Stop };
            animOp.Completed += (_, _) =>
            {
                if (gen != _animGeneration) return;
                BeginAnimation(OpacityProperty, null);
                Opacity = OpacityFor(_sm.State);
            };
            BeginAnimation(OpacityProperty, animOp, HandoffBehavior.SnapshotAndReplace);
        }

        animH.Completed += (_, _) =>
        {
            if (gen != _animGeneration) return;
            BeginAnimation(WidthProperty,  null);
            BeginAnimation(HeightProperty, null);
            BeginAnimation(LeftProperty,   null);
            BeginAnimation(TopProperty,    null);

            Width  = tw;
            Height = th;
            Left   = tl;
            Top    = tt;

            FinalizeVisualLayers(_sm.State);
            if (_sm.State != NotchState.Stealth && _hwnd != IntPtr.Zero)
            {
                WindowStyleHelper.EnsureTopmost(_hwnd);
            }
        };

        BeginAnimation(LeftProperty,   animL, HandoffBehavior.SnapshotAndReplace);
        BeginAnimation(TopProperty,    animT, HandoffBehavior.SnapshotAndReplace);
        BeginAnimation(WidthProperty,  animW, HandoffBehavior.SnapshotAndReplace);
        BeginAnimation(HeightProperty, animH, HandoffBehavior.SnapshotAndReplace);
    }

    private void OnPowerToastActiveChanged(bool isPowerToastActive)
    {
        if (_sm.State == NotchState.CollapsedHover) AnimateToCurrentState();
    }

    private void OnBrightnessToastActiveChanged(bool isBrightnessToastActive)
    {
        if (_sm.State == NotchState.CollapsedHover) AnimateToCurrentState();
    }

    private void OnVolumeToastActiveChanged(bool isVolumeToastActive)
    {
        if (_sm.State == NotchState.CollapsedHover) AnimateToCurrentState();
    }

    private void OnMediaToastActiveChanged(bool isToastActive)
    {
        if (_sm.State == NotchState.CollapsedHover) AnimateToCurrentState();
    }

    private void OnReminderAlertActiveChanged(bool isReminderAlertActive)
    {
        if (_sm.State == NotchState.CollapsedHover) AnimateToCurrentState();
    }

    // ── Win32 setup ──────────────────────────────────────────────────────────

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;

        // Hide from Alt+Tab; prevent focus steal on hover/click; enforce HWND_TOPMOST
        WindowStyleHelper.ApplyOverlayStyles(_hwnd);

        // Intercept WM_MOUSEACTIVATE to suppress activation on click
        HwndSource.FromHwnd(_hwnd)!.AddHook(WndProc);

        // Global hotkey registration using saved preset
        _hotkeyService.Register(_hwnd, _settings.Current.HotkeyPreset);
        _hotkeyService.HotkeyPressed += () => _sm.Toggle();

        // Event-driven fullscreen & desktop context detection + topmost Z-order re-assertion (ZERO idle polling timers)
        _fullscreenDetector.ForegroundWindowChanged += OnForegroundWindowChanged;
        _fullscreenDetector.DesktopContextChanged += OnDesktopContextChanged;
        _fullscreenDetector.Start(_hwnd);
        SyncDesktopContext(_fullscreenDetector.IsOnDesktop);

        // Event-driven clipboard listener (zero idle polling)
        _clipboardService.Start(_hwnd);

        // Bind HWND for Windows Share Sheet fallback
        _quickShareService.AttachWindow(_hwnd);
    }

    private void OnForegroundWindowChanged()
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (_sm.State != NotchState.Stealth && _hwnd != IntPtr.Zero)
            {
                WindowStyleHelper.EnsureTopmost(_hwnd);
            }
        });
    }

    private void OnDesktopContextChanged(bool isOnDesktop)
    {
        Dispatcher.InvokeAsync(() => SyncDesktopContext(isOnDesktop));
    }

    private void SyncDesktopContext(bool isOnDesktop)
    {
        _vm.IsOnDesktop = isOnDesktop;
        bool hideOverApps = _settings.Current.HandleVisibilityMode == "DesktopOnly" && !isOnDesktop;
        _sm.SetContextHidden(hideOverApps);
        UpdateHandleAppearance();
        AnimateToCurrentState();
    }

    public void ApplyContextModeFromTray()
    {
        Dispatcher.InvokeAsync(() =>
        {
            _fullscreenDetector.EvaluateCurrentContext();
            SyncDesktopContext(_fullscreenDetector.IsOnDesktop);
            _sm.SyncRestingState();
            _vm.NotifyIdlePresentationChanged();
        });
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // Single-instance wakeup message from secondary launch
        if (SingleInstanceGuard.ActivationMessage != 0 && (uint)msg == SingleInstanceGuard.ActivationMessage)
        {
            if (_sm.State != NotchState.PeekExpanded)
            {
                _sm.Toggle();
            }
            WindowStyleHelper.SetNoActivate(_hwnd, false);
            Activate();
            WindowStyleHelper.EnsureTopmost(_hwnd);
            handled = true;
            return IntPtr.Zero;
        }

        if (WindowStyleHelper.IsMouseActivate(msg))
        {
            // In PeekExpanded, allow normal mouse activation so hub controls receive clicks & focus
            if (_sm.State == NotchState.PeekExpanded)
            {
                return IntPtr.Zero;
            }

            handled = true;
            return WindowStyleHelper.MaNoActivate;
        }

        // Power status change event (AC plug/unplug, low battery)
        if (msg == 0x0218 /* WM_POWERBROADCAST */)
        {
            CheckPowerStatus();
        }
        return IntPtr.Zero;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS lpSystemPowerStatus);

    private int _lastAcStatus = -1;
    private int _lastBatteryPercent = -1;
    private DispatcherTimer? _powerToastTimer;

    private void CheckPowerStatus()
    {
        if (GetSystemPowerStatus(out var status))
        {
            int ac = status.ACLineStatus; // 0 = Offline, 1 = Online, 255 = Unknown
            int battery = status.BatteryLifePercent; // 0-100 or 255 if unknown

            if (_lastAcStatus != -1 && ac != 255)
            {
                if (ac == 1 && _lastAcStatus == 0)
                {
                    ShowPowerToast("\uE945", "Charger Connected", battery <= 100 ? $"{battery}%" : "Charging");
                }
                else if (ac == 0 && _lastAcStatus == 1)
                {
                    ShowPowerToast("\uEBA0", "On Battery Power", battery <= 100 ? $"{battery}% Remaining" : "Discharging");
                }
            }

            if (battery <= 20 && _lastBatteryPercent > 20 && ac == 0)
            {
                ShowPowerToast("\uEBB5", "Battery Low", $"{battery}% Remaining");
            }

            _lastAcStatus = ac;
            _lastBatteryPercent = battery;
        }
    }

    private void ShowPowerToast(string icon, string title, string detail, int durationMs = 2200, string accentHex = "#FF5E36")
    {
        if (_sm.State == NotchState.PeekExpanded || _sm.State == NotchState.Stealth) return;

        _vm.PowerToastIconBrush = NotchViewModel.CreateFrozenBrush(accentHex);
        _vm.PowerToastIcon = icon;
        _vm.PowerToastTitle = title;
        _vm.PowerToastDetail = detail;
        _vm.IsPowerToastActive = true;

        if (_sm.State == NotchState.Idle)
        {
            _sm.ShowToastBanner();
        }

        _powerToastTimer?.Stop();
        _powerToastTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(durationMs) };
        _powerToastTimer.Tick += (_, _) =>
        {
            _powerToastTimer.Stop();
            _vm.IsPowerToastActive = false;
            if (_sm.State == NotchState.CollapsedHover
                && !_vm.IsMediaToastActive
                && !_vm.IsVolumeToastActive
                && !_vm.IsBrightnessToastActive)
            {
                _sm.Collapse();
            }
        };
        _powerToastTimer.Start();
    }

    // ── Startup & Time-Based Greeting ────────────────────────────────────────

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        UpdateHandleAppearance();
        ApplyBorderAnimationPreset();
        _sm.SyncRestingState();
        SnapToState(_sm.State);
        Opacity = OpacityFor(_sm.State);

        if (_settings.Current.ShowStartupGreeting)
        {
            _ = TriggerStartupGreetingAsync();
        }

        // Trim initial startup allocations once the idle handle settles
        WindowStyleHelper.TrimProcessWorkingSet();
    }

    private async Task TriggerStartupGreetingAsync()
    {
        await Task.Delay(500);
        if (!_settings.Current.ShowStartupGreeting) return;
        TriggerGreetingBanner(durationMs: 3600, ignoreActiveCheck: false);
    }

    private void OnGreetingPeriodRolledOver(TimeGreetingInfo info)
    {
        if (!_settings.Current.ShowStartupGreeting) return;
        TriggerGreetingBanner(durationMs: 3000, ignoreActiveCheck: false);
    }

    public void TriggerGreetingBanner(int durationMs = 3600, bool ignoreActiveCheck = true)
    {
        if (!ignoreActiveCheck)
        {
            if (_sm.State is not (NotchState.Idle or NotchState.CollapsedHover)) return;
            if (_vm.IsMediaToastActive || _vm.IsVolumeToastActive || _vm.IsBrightnessToastActive || _vm.IsPowerToastActive) return;
        }
        else if (_sm.State == NotchState.PeekExpanded)
        {
            _sm.Collapse();
        }
        else if (_sm.State == NotchState.Stealth)
        {
            return;
        }

        var now = DateTime.Now;
        var info = GreetingHelper.GetGreeting(now);
        string detail = $"{info.Tagline} • {now:h:mm tt}";
        ShowPowerToast(info.IconGlyph, info.DisplayTitle, detail, durationMs, info.AccentHex);
    }

    // ── Settings update listener ──────────────────────────────────────────────

    private void OnSettingsUpdated()
    {
        _hotkeyService.ChangePreset(_settings.Current.HotkeyPreset);
        _vm.Timer.ApplySettings(_settings.Current);
        _fullscreenDetector.EvaluateCurrentContext();
        _vm.IsOnDesktop = _fullscreenDetector.IsOnDesktop;
        _sm.SetContextHidden(_settings.Current.HandleVisibilityMode == "DesktopOnly" && !_fullscreenDetector.IsOnDesktop);
        _sm.SyncRestingState();
        _vm.RefreshSettingsBindings();
        UpdateHandleAppearance();
        ApplyBorderAnimationPreset();
        SnapToState(_sm.State);
    }

    public void ShowSettingsWindow(int categoryIndex = 0)
    {
        if (_settingsWindow == null || !_settingsWindow.IsLoaded)
        {
            _settingsWindow = new SettingsWindow(_settingsVm);
        }

        _settingsVm.SelectedCategoryIndex = Math.Clamp(categoryIndex, 0, 4);
        _settingsVm.RefreshDiagnostics();
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    // ── State machine handler ─────────────────────────────────────────────────

    private DateTime _lastExpandedTime = DateTime.MinValue;
    private NotchState _prevState = NotchState.Idle;

    private void OnStateChanged(object? sender, NotchState newState)
    {
        App.Log($"[NotchWindow] OnStateChanged: {_prevState} -> {newState}");
        bool collapsedToIdle = (_prevState == NotchState.PeekExpanded || _prevState == NotchState.CollapsedHover)
                               && newState == NotchState.Idle;
        _prevState = newState;

        // ── Focus management ──────────────────────────────────────────────
        if (newState == NotchState.PeekExpanded)
        {
            _lastExpandedTime = DateTime.UtcNow;
            WindowStyleHelper.SetNoActivate(_hwnd, false);
            Activate();
        }
        else
        {
            WindowStyleHelper.SetNoActivate(_hwnd, true);
        }

        // ── Click-through for Stealth ─────────────────────────────────────
        WindowStyleHelper.SetClickThrough(_hwnd, newState == NotchState.Stealth);

        // ── Switch visual layers smoothly with droplet cross-fade ──────────
        PrepareVisualLayersForState(newState);

        // ── Animate window size, position & opacity ───────────────────────
        AnimateToCurrentState();

        if (newState != NotchState.Stealth && _hwnd != IntPtr.Zero)
        {
            WindowStyleHelper.EnsureTopmost(_hwnd);
        }

        var targetOpacity = OpacityFor(newState);
        var curOpacity = Opacity;
        var opacityEase = newState == NotchState.Idle ? DropletRetractEase : (IEasingFunction)DropletExpandEase;
        var animOp = new DoubleAnimation(curOpacity, targetOpacity, TimeSpan.FromMilliseconds(200))
        {
            EasingFunction = opacityEase,
            FillBehavior   = FillBehavior.Stop
        };
        animOp.Completed += (_, _) =>
        {
            BeginAnimation(OpacityProperty, null);
            Opacity = targetOpacity;
            if (collapsedToIdle)
            {
                WindowStyleHelper.TrimProcessWorkingSet();
            }
        };
        BeginAnimation(OpacityProperty, animOp, HandoffBehavior.SnapshotAndReplace);
    }

    private void PrepareVisualLayersForState(NotchState state)
    {
        var fadeInDur  = TimeSpan.FromMilliseconds(180);
        var fadeOutDur = TimeSpan.FromMilliseconds(130);

        static void MorphIn(Border layer, ScaleTransform scale, TimeSpan dur, IEasingFunction ease)
        {
            layer.Visibility = Visibility.Visible;
            double curOp = double.IsNaN(layer.Opacity) ? 0.0 : layer.Opacity;
            var op = new DoubleAnimation(curOp, 1.0, dur) { EasingFunction = ease };
            layer.BeginAnimation(OpacityProperty, op, HandoffBehavior.SnapshotAndReplace);

            double curSx = scale.ScaleX <= 0.1 ? 0.90 : scale.ScaleX;
            double curSy = scale.ScaleY <= 0.1 ? 0.86 : scale.ScaleY;
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(curSx, 1.0, dur) { EasingFunction = ease }, HandoffBehavior.SnapshotAndReplace);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(curSy, 1.0, dur) { EasingFunction = ease }, HandoffBehavior.SnapshotAndReplace);
        }

        static void MorphOut(Border layer, ScaleTransform scale, TimeSpan dur, IEasingFunction ease)
        {
            if (layer.Visibility != Visibility.Visible) return;
            double curOp = double.IsNaN(layer.Opacity) ? 1.0 : layer.Opacity;
            var op = new DoubleAnimation(curOp, 0.0, dur) { EasingFunction = ease };
            layer.BeginAnimation(OpacityProperty, op, HandoffBehavior.SnapshotAndReplace);
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(scale.ScaleX, 0.93, dur) { EasingFunction = ease }, HandoffBehavior.SnapshotAndReplace);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(scale.ScaleY, 0.90, dur) { EasingFunction = ease }, HandoffBehavior.SnapshotAndReplace);
        }

        switch (state)
        {
            case NotchState.Idle:
                MorphIn(IdlePill, IdleScale, fadeInDur, DropletExpandEase);
                MorphOut(HoverCapsule, HoverScale, fadeOutDur, DropletRetractEase);
                MorphOut(ExpandedHub, HubScale, fadeOutDur, DropletRetractEase);
                break;

            case NotchState.CollapsedHover:
                MorphIn(HoverCapsule, HoverScale, fadeInDur, DropletExpandEase);
                MorphOut(IdlePill, IdleScale, fadeOutDur, DropletRetractEase);
                MorphOut(ExpandedHub, HubScale, fadeOutDur, DropletRetractEase);
                break;

            case NotchState.PeekExpanded:
                MorphIn(ExpandedHub, HubScale, fadeInDur, DropletExpandEase);
                MorphOut(IdlePill, IdleScale, fadeOutDur, DropletRetractEase);
                MorphOut(HoverCapsule, HoverScale, fadeOutDur, DropletRetractEase);
                break;

            case NotchState.Stealth:
                MorphOut(IdlePill, IdleScale, fadeOutDur, DropletRetractEase);
                MorphOut(HoverCapsule, HoverScale, fadeOutDur, DropletRetractEase);
                MorphOut(ExpandedHub, HubScale, fadeOutDur, DropletRetractEase);
                break;
        }
    }

    private void FinalizeVisualLayers(NotchState state)
    {
        IdlePill.BeginAnimation(OpacityProperty, null);
        HoverCapsule.BeginAnimation(OpacityProperty, null);
        ExpandedHub.BeginAnimation(OpacityProperty, null);

        IdleScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        IdleScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        HoverScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        HoverScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        HubScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        HubScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);

        IdleScale.ScaleX  = IdleScale.ScaleY  = 1.0;
        HoverScale.ScaleX = HoverScale.ScaleY = 1.0;
        HubScale.ScaleX   = HubScale.ScaleY   = 1.0;

        IdlePill.Visibility     = state == NotchState.Idle           ? Visibility.Visible : Visibility.Collapsed;
        HoverCapsule.Visibility = state == NotchState.CollapsedHover ? Visibility.Visible : Visibility.Collapsed;
        ExpandedHub.Visibility  = state == NotchState.PeekExpanded   ? Visibility.Visible : Visibility.Collapsed;

        IdlePill.Opacity     = state == NotchState.Idle           ? 1.0 : 0.0;
        HoverCapsule.Opacity = state == NotchState.CollapsedHover ? 1.0 : 0.0;
        ExpandedHub.Opacity  = state == NotchState.PeekExpanded   ? 1.0 : 0.0;
    }

    // ── Dismiss handlers ─────────────────────────────────────────────────────

    private void OnDeactivated(object? sender, EventArgs e)
    {
        App.Log($"[NotchWindow] OnDeactivated fired! State={_sm.State}");
        // Do not collapse if the user clicked into Settings or has an open file picker dialog
        if ((_settingsWindow != null && _settingsWindow.IsActive) || _vm.IsModalPickerOpen) return;

        // Debounce: ignore deactivations during initial expand animation or focus handoff
        if ((DateTime.UtcNow - _lastExpandedTime).TotalMilliseconds < 600)
        {
            App.Log("[NotchWindow] OnDeactivated suppressed by expansion debounce window.");
            return;
        }

        if (_sm.State == NotchState.PeekExpanded)
        {
            App.Log("[NotchWindow] Light-dismissing PeekExpanded to Idle.");
            _sm.Collapse();
        }
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (_sm.State != NotchState.PeekExpanded) return;

        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            _vm.CloseAllMenus();
            _sm.Collapse();
            return;
        }

        if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            int tabIdx = e.Key switch
            {
                Key.D1 => NotchViewModel.TabHome,
                Key.D2 => NotchViewModel.TabMedia,
                Key.D3 => NotchViewModel.TabTimers,
                Key.D4 => NotchViewModel.TabShelf,
                Key.D5 => NotchViewModel.TabCalendar,
                Key.D6 => NotchViewModel.TabResources,
                Key.D7 => NotchViewModel.TabTools,
                Key.D8 => NotchViewModel.TabShare,
                _      => -1
            };
            if (tabIdx >= 0)
            {
                _vm.SelectedTabIndex = tabIdx;
                e.Handled = true;
                return;
            }
        }

        if (e.Key == Key.Space && Keyboard.FocusedElement is not TextBox)
        {
            _vm.PlayPauseCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnWindowPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Allow in-place interactive controls (e.g. Play/Pause, Pause/Resume Timer) without forcing hub expansion
        if (e.OriginalSource is DependencyObject d && FindVisualParent<System.Windows.Controls.Primitives.ButtonBase>(d) != null)
        {
            return;
        }

        if (_sm.State == NotchState.PeekExpanded)
        {
            _vm.CloseAllMenus();
            return;
        }

        if (_sm.State is NotchState.Idle or NotchState.CollapsedHover)
        {
            App.Log($"[NotchWindow] OnWindowPreviewMouseLeftButtonDown in state {_sm.State} -> Expanding to PeekExpanded!");
            _sm.OnClick();
            e.Handled = true;
        }
    }

    private static T? FindVisualParent<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child != null)
        {
            if (child is T parent) return parent;
            child = VisualTreeHelper.GetParent(child);
        }
        return null;
    }

    private void OnTimelineProgressBarClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ProgressBar pb && pb.ActualWidth > 0)
        {
            var clickX = e.GetPosition(pb).X;
            var fraction = Math.Clamp(clickX / pb.ActualWidth, 0.0, 1.0);
            _vm.SeekMediaFractionCommand.Execute(fraction);
        }
    }

    private void OnMediaSliderPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is Slider slider)
        {
            double fraction = Math.Clamp(slider.Value / 100.0, 0.0, 1.0);
            _vm.SeekMediaFractionCommand.Execute(fraction);
        }
    }

    private void OnCalendarTimeBoxGotFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox tb) tb.SelectAll();
    }

    private void OnCalendarTimeBoxMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is TextBox tb && !tb.IsKeyboardFocusWithin)
        {
            tb.Focus();
            tb.SelectAll();
            e.Handled = true;
        }
    }

    private void OnCalendarTimeBoxLostFocus(object sender, RoutedEventArgs e)
    {
        _vm.NormalizeTimeInputs();
    }

    private void OnCalendarHourMouseWheel(object sender, MouseWheelEventArgs e)
    {
        _vm.StepCalendarHourCommand.Execute(e.Delta > 0 ? 1 : -1);
        e.Handled = true;
    }

    private void OnCalendarMinuteMouseWheel(object sender, MouseWheelEventArgs e)
    {
        _vm.StepCalendarMinuteCommand.Execute(e.Delta > 0 ? 1 : -1);
        e.Handled = true;
    }

    // ── Mouse event bridge ───────────────────────────────────────────────────

    private string[]? _pendingDropFiles;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct WIN32_POINT { public int X, Y; }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetCursorPos(out WIN32_POINT lpPoint);

    private bool IsCursorInsideWindowBounds()
    {
        try
        {
            if (!GetCursorPos(out var pt)) return false;
            var source = PresentationSource.FromVisual(this);
            double dpiX = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            double dpiY = source?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;

            double pxLeft   = Left * dpiX - 6;
            double pxTop    = Top * dpiY - 6;
            double pxRight  = (Left + ActualWidth) * dpiX + 6;
            double pxBottom = (Top + ActualHeight) * dpiY + 6;
            return pt.X >= pxLeft && pt.X <= pxRight && pt.Y >= pxTop && pt.Y <= pxBottom;
        }
        catch { return false; }
    }

    protected override void OnMouseEnter(MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        App.Log($"[NotchWindow] OnMouseEnter (State={_sm.State})");
        if (!_vm.HasPendingDropChoice)
        {
            _vm.IsFileDragOverlayVisible = false;
            _vm.IsDropDockHovered = false;
            _vm.IsDropShareHovered = false;
        }
        _sm.OnMouseEnter();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        App.Log($"[NotchWindow] OnMouseLeave (State={_sm.State})");
        _sm.OnMouseLeave();
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.ChangedButton == MouseButton.Middle && _sm.State is NotchState.Idle or NotchState.CollapsedHover)
        {
            _vm.PlayPauseCommand.Execute(null);
            e.Handled = true;
            return;
        }
        App.Log($"[NotchWindow] OnMouseDown (Button={e.ChangedButton}, State={_sm.State})");
        _sm.OnClick();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_sm.State is NotchState.Idle or NotchState.CollapsedHover)
        {
            int dir = e.Delta > 0 ? 1 : -1;
            if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
            {
                _vm.NudgeBrightnessFromGesture(dir);
            }
            else
            {
                _vm.NudgeVolumeFromGesture(dir);
            }
            e.Handled = true;
        }
    }

    // ── Drag and drop file staging / Quick Share choice ─────────────────────

    protected override void OnDragEnter(DragEventArgs e)
    {
        base.OnDragEnter(e);
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;

        // If already on the Share tab, allow direct drop onto the queue without overlay
        if (_sm.State != NotchState.PeekExpanded || _vm.SelectedTabIndex != NotchViewModel.TabShare)
        {
            _pendingDropFiles = null;
            _vm.HasPendingDropChoice = false;
            _vm.PendingDropSummary = "Drop left to Stage in Dock • Drop right to Quick Share";
            _vm.IsFileDragOverlayVisible = true;
            _vm.IsDropDockHovered = false;
            _vm.IsDropShareHovered = false;
        }

        if (_settings.Current.AutoExpandOnDrag && _sm.State != NotchState.PeekExpanded)
        {
            _sm.OnDragEnter();
        }

        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
    }

    protected override void OnDragOver(DragEventArgs e)
    {
        base.OnDragOver(e);
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            if ((_sm.State != NotchState.PeekExpanded || _vm.SelectedTabIndex != NotchViewModel.TabShare) && !_vm.IsFileDragOverlayVisible)
            {
                _vm.HasPendingDropChoice = false;
                _vm.IsFileDragOverlayVisible = true;
            }
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }
    }

    protected override void OnDragLeave(DragEventArgs e)
    {
        base.OnDragLeave(e);
        if (_vm.HasPendingDropChoice) return;

        // Defer slightly so edge movement or Idle->Expanded transition does not kill the overlay
        Dispatcher.InvokeAsync(async () =>
        {
            await Task.Delay(70);
            if (_vm.HasPendingDropChoice) return;
            if (!IsCursorInsideWindowBounds())
            {
                _vm.IsFileDragOverlayVisible = false;
                _vm.IsDropDockHovered = false;
                _vm.IsDropShareHovered = false;
            }
        });
    }

    protected override void OnDrop(DragEventArgs e)
    {
        base.OnDrop(e);
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0) return;

        if (_sm.State == NotchState.PeekExpanded && _vm.SelectedTabIndex == NotchViewModel.TabShare)
        {
            ClearDropOverlayState();
            _quickShareService.AddPaths(files);
            _vm.RefreshQuickShareState();
            e.Handled = true;
            return;
        }

        // Dropped on the notch edge/frame before selecting left/right half: keep overlay open and ask!
        _pendingDropFiles = files;
        string firstName = System.IO.Path.GetFileName(files[0]);
        if (string.IsNullOrEmpty(firstName)) firstName = files[0];
        _vm.PendingDropSummary = files.Length == 1
            ? $"\"{firstName}\" — choose action:"
            : $"{files.Length} items ({firstName}...) — choose action:";
        _vm.HasPendingDropChoice = true;
        _vm.IsFileDragOverlayVisible = true;
        _vm.IsDropDockHovered = false;
        _vm.IsDropShareHovered = false;

        if (_sm.State != NotchState.PeekExpanded)
        {
            _sm.OnDragEnter();
        }
        WindowStyleHelper.SetNoActivate(_hwnd, false);
        Activate();
        e.Handled = true;
    }

    private void ClearDropOverlayState()
    {
        _pendingDropFiles = null;
        _vm.HasPendingDropChoice = false;
        _vm.IsFileDragOverlayVisible = false;
        _vm.IsDropDockHovered = false;
        _vm.IsDropShareHovered = false;
    }

    private void OnDockDropTargetDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            _vm.IsDropDockHovered = true;
            _vm.IsDropShareHovered = false;
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }
    }

    private void OnDockDropTargetDragLeave(object sender, DragEventArgs e) => _vm.IsDropDockHovered = false;

    private void OnDockDropTargetDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            ClearDropOverlayState();
            _shelfService.AddFiles(files);
            _vm.SelectedTabIndex = NotchViewModel.TabShelf;
            e.Handled = true;
        }
    }

    private void OnShareDropTargetDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            _vm.IsDropShareHovered = true;
            _vm.IsDropDockHovered = false;
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }
    }

    private void OnShareDropTargetDragLeave(object sender, DragEventArgs e) => _vm.IsDropShareHovered = false;

    private async void OnShareDropTargetDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            ClearDropOverlayState();
            e.Handled = true;
            await _vm.SendDroppedFilesViaQuickShareAsync(files);
        }
    }

    private void OnDockChoiceClick(object sender, MouseButtonEventArgs e)
    {
        if (!_vm.HasPendingDropChoice || _pendingDropFiles == null) return;
        var files = _pendingDropFiles;
        ClearDropOverlayState();
        _shelfService.AddFiles(files);
        _vm.SelectedTabIndex = NotchViewModel.TabShelf;
        e.Handled = true;
    }

    private async void OnShareChoiceClick(object sender, MouseButtonEventArgs e)
    {
        if (!_vm.HasPendingDropChoice || _pendingDropFiles == null) return;
        var files = _pendingDropFiles;
        ClearDropOverlayState();
        e.Handled = true;
        await _vm.SendDroppedFilesViaQuickShareAsync(files);
    }

    private void OnCancelDropChoiceClick(object sender, RoutedEventArgs e)
    {
        ClearDropOverlayState();
        _sm.Collapse();
    }

    // ── Drag staged file OUT of the Dock (removes staged entry on drop) ───────

    private void OnShelfListPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStartPoint = e.GetPosition(null);
    }

    private void OnShelfListPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;

        var currentPoint = e.GetPosition(null);
        var diff = _dragStartPoint - currentPoint;

        if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance ||
            Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
        {
            if (sender is ListBox listBox && listBox.SelectedItem is ShelfItem item)
            {
                var data = new DataObject(DataFormats.FileDrop, new[] { item.FilePath });
                var result = DragDrop.DoDragDrop(listBox, data, DragDropEffects.Copy | DragDropEffects.Move);
                if (result != DragDropEffects.None)
                {
                    _shelfService.RemoveItem(item);
                }
            }
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    public void SnapToState(NotchState state)
    {
        _animGeneration++;
        _vm.RefreshSettingsBindings();
        BeginAnimation(LeftProperty,    null);
        BeginAnimation(TopProperty,     null);
        BeginAnimation(WidthProperty,   null);
        BeginAnimation(HeightProperty,  null);
        BeginAnimation(OpacityProperty, null);

        var (w, h) = _monitor.GetDimensions(
            _settings.Current.DockEdge,
            state,
            _vm.IsMediaToastActive,
            _vm.IsVolumeToastActive,
            _vm.IsPowerToastActive,
            _vm.IsMediaPlaying,
            _vm.IsStopwatchRunning,
            _settings.Current.HandleWidth,
            _settings.Current.HandleThickness,
            _settings.Current.DynamicContentSizing,
            _settings.Current.DualActivityEnabled,
            _vm.HasBluetoothBattery,
            _vm.IsBrightnessToastActive,
            _vm.HasUpcomingReminder,
            _vm.ShowFullLiveActivity,
            _vm.ShowDesktopAmbientPill,
            _vm.IsReminderAlertActive);

        var (l, t) = _monitor.ComputePosition(_settings.Current.DockEdge, w, h, _settings.Current.TargetMonitorIndex);

        Width   = w;
        Height  = h;
        Left    = l;
        Top     = t;
        Opacity = OpacityFor(state);

        FinalizeVisualLayers(state);

        if (state != NotchState.Stealth && _hwnd != IntPtr.Zero)
        {
            WindowStyleHelper.EnsureTopmost(_hwnd);
        }
    }

    public void UpdateHandleAppearance()
    {
        bool hasExpandedIdle = _vm.ShowFullLiveActivity || _vm.ShowDesktopAmbientPill;

        // Live IPS dark-slate acrylic tint (#1A1C20) for HoverCapsule and ExpandedHub
        byte hoverAlpha = (byte)Math.Round(Math.Clamp(_settings.Current.AcrylicTintOpacity + 0.18, 0.65, 0.96) * 255);
        byte hubAlpha   = (byte)Math.Round(Math.Clamp(_settings.Current.AcrylicTintOpacity + 0.22, 0.70, 0.97) * 255);
        HoverCapsule.Background = new SolidColorBrush(Color.FromArgb(hoverAlpha, 0x1A, 0x1C, 0x20));
        ExpandedHub.Background  = new SolidColorBrush(Color.FromArgb(hubAlpha, 0x1A, 0x1C, 0x20));

        try
        {
            var converter = new BrushConverter();
            if (converter.ConvertFromString(_settings.Current.HandleColor) is Brush brush)
            {
                IdlePill.Background = brush;
            }
        }
        catch
        {
            IdlePill.Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x1A, 0x1C, 0x20));
        }

        if (hasExpandedIdle)
        {
            IdlePill.CornerRadius    = new CornerRadius(12);
            IdlePill.BorderBrush     = (_vm.ShowFullLiveActivity && _vm.IsMediaPlaying)
                ? _vm.MediaAccentGlowBrush
                : new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
            IdlePill.BorderThickness = new Thickness(1);
        }
        else
        {
            double radius = Math.Max(2.0, _settings.Current.HandleThickness / 2.0);
            IdlePill.CornerRadius    = new CornerRadius(radius);
            IdlePill.BorderBrush     = new SolidColorBrush(Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF));
            IdlePill.BorderThickness = new Thickness(1);
        }
    }

    public void ApplyBorderAnimationPreset()
    {
        string preset = _settings.Current.BorderAnimationPreset;

        HoverBorderGlow.BeginAnimation(OpacityProperty, null);
        HubBorderGlow.BeginAnimation(OpacityProperty, null);
        HoverBorderGlow.BorderBrush = null;
        HubBorderGlow.BorderBrush   = null;
        HoverBorderGlow.Opacity     = 1.0;
        HubBorderGlow.Opacity       = 1.0;
        HoverBorderGlow.BorderThickness = new Thickness(1.5);
        HubBorderGlow.BorderThickness   = new Thickness(1.5);

        switch (preset)
        {
            case "MeteorSweep":
            {
                var txHover = new TranslateTransform(-0.52, 0);
                var txHub   = new TranslateTransform(-0.52, 0);

                HoverBorderGlow.BorderBrush     = CreateMeteorSweepBrush(txHover);
                HubBorderGlow.BorderBrush       = CreateMeteorSweepBrush(txHub);
                HoverBorderGlow.BorderThickness = new Thickness(2.0);
                HubBorderGlow.BorderThickness   = new Thickness(2.0);
                HoverBorderGlow.Visibility      = Visibility.Visible;
                HubBorderGlow.Visibility        = Visibility.Visible;

                var anim = new DoubleAnimation(-0.52, 0.52, new Duration(TimeSpan.FromSeconds(1.35)))
                {
                    AutoReverse    = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
                };
                txHover.BeginAnimation(TranslateTransform.XProperty, anim);
                txHub.BeginAnimation(TranslateTransform.XProperty, anim);
                break;
            }

            case "MacAppleGlow":
            {
                var rotateHover = new RotateTransform(0, 0.5, 0.5);
                var rotateHub   = new RotateTransform(0, 0.5, 0.5);

                HoverBorderGlow.BorderBrush     = CreateMacAppleBrush(rotateHover);
                HubBorderGlow.BorderBrush       = CreateMacAppleBrush(rotateHub);
                HoverBorderGlow.BorderThickness = new Thickness(2.0);
                HubBorderGlow.BorderThickness   = new Thickness(2.0);
                HoverBorderGlow.Visibility      = Visibility.Visible;
                HubBorderGlow.Visibility        = Visibility.Visible;

                var spin = new DoubleAnimation(0, 360, new Duration(TimeSpan.FromSeconds(2.6)))
                {
                    RepeatBehavior = RepeatBehavior.Forever
                };
                var pulse = new DoubleAnimation(0.72, 1.0, new Duration(TimeSpan.FromSeconds(1.6)))
                {
                    AutoReverse    = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
                };
                rotateHover.BeginAnimation(RotateTransform.AngleProperty, spin);
                rotateHub.BeginAnimation(RotateTransform.AngleProperty, spin);
                HoverBorderGlow.BeginAnimation(OpacityProperty, pulse);
                HubBorderGlow.BeginAnimation(OpacityProperty, pulse);
                break;
            }

            case "RainbowWave":
            {
                var rotateHover = new RotateTransform(0, 0.5, 0.5);
                var rotateHub   = new RotateTransform(0, 0.5, 0.5);

                var rainbowHover = CreateRainbowBrush(rotateHover);
                var rainbowHub   = CreateRainbowBrush(rotateHub);

                HoverBorderGlow.BorderBrush = rainbowHover;
                HubBorderGlow.BorderBrush   = rainbowHub;
                HoverBorderGlow.Visibility  = Visibility.Visible;
                HubBorderGlow.Visibility    = Visibility.Visible;

                var anim = new DoubleAnimation(0, 360, new Duration(TimeSpan.FromSeconds(3.5)))
                {
                    RepeatBehavior = RepeatBehavior.Forever
                };
                rotateHover.BeginAnimation(RotateTransform.AngleProperty, anim);
                rotateHub.BeginAnimation(RotateTransform.AngleProperty, anim);
                break;
            }

            case "BreathingGlow":
            {
                var brushHover = new SolidColorBrush(Color.FromArgb(0xC0, 0x80, 0xD8, 0xFF));
                var brushHub   = new SolidColorBrush(Color.FromArgb(0xC0, 0x80, 0xD8, 0xFF));

                HoverBorderGlow.BorderBrush = brushHover;
                HubBorderGlow.BorderBrush   = brushHub;
                HoverBorderGlow.Visibility  = Visibility.Visible;
                HubBorderGlow.Visibility    = Visibility.Visible;

                var anim = new DoubleAnimation(0.25, 0.95, new Duration(TimeSpan.FromSeconds(1.8)))
                {
                    AutoReverse    = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
                };
                HoverBorderGlow.BeginAnimation(OpacityProperty, anim);
                HubBorderGlow.BeginAnimation(OpacityProperty, anim);
                break;
            }

            case "NeonGlow":
            {
                var neonHover = new LinearGradientBrush(
                    Color.FromArgb(0xFF, 0x00, 0xE5, 0xFF),
                    Color.FromArgb(0xFF, 0xFF, 0x00, 0x7F),
                    new Point(0, 0), new Point(1, 1));
                var neonHub = new LinearGradientBrush(
                    Color.FromArgb(0xFF, 0x00, 0xE5, 0xFF),
                    Color.FromArgb(0xFF, 0xFF, 0x00, 0x7F),
                    new Point(0, 0), new Point(1, 1));

                HoverBorderGlow.BorderBrush = neonHover;
                HubBorderGlow.BorderBrush   = neonHub;
                HoverBorderGlow.Visibility  = Visibility.Visible;
                HubBorderGlow.Visibility    = Visibility.Visible;

                var anim = new DoubleAnimation(0.4, 1.0, new Duration(TimeSpan.FromSeconds(1.2)))
                {
                    AutoReverse    = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
                };
                HoverBorderGlow.BeginAnimation(OpacityProperty, anim);
                HubBorderGlow.BeginAnimation(OpacityProperty, anim);
                break;
            }

            default:
                HoverBorderGlow.Visibility = Visibility.Collapsed;
                HubBorderGlow.Visibility   = Visibility.Collapsed;
                break;
        }
    }

    private static LinearGradientBrush CreateMeteorSweepBrush(TranslateTransform translate)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint        = new Point(0, 0),
            EndPoint          = new Point(1, 0.12),
            RelativeTransform = translate
        };
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 0.00));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF), 0.30));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0xB8, 0xFF, 0xFF, 0xFF), 0.44));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF), 0.50));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0xB8, 0xFF, 0xFF, 0xFF), 0.56));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF), 0.70));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 1.00));
        return brush;
    }

    private static LinearGradientBrush CreateMacAppleBrush(RotateTransform rotate)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint        = new Point(0, 0),
            EndPoint          = new Point(1, 1),
            RelativeTransform = rotate
        };
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(0xFF, 0x6D, 0x3F), 0.00));
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(0xFF, 0x37, 0x5F), 0.17));
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(0xBF, 0x5A, 0xF2), 0.34));
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(0x0A, 0x84, 0xFF), 0.51));
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(0x5A, 0xC8, 0xFA), 0.68));
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(0x30, 0xD1, 0x58), 0.85));
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(0xFF, 0x6D, 0x3F), 1.00));
        return brush;
    }

    private static LinearGradientBrush CreateRainbowBrush(RotateTransform rotate)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint        = new Point(0, 0),
            EndPoint          = new Point(1, 1),
            RelativeTransform = rotate
        };
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(0xFF, 0x3D, 0x00), 0.00));
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(0xFF, 0x91, 0x00), 0.16));
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(0xFF, 0xEA, 0x00), 0.33));
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(0x00, 0xE6, 0x76), 0.50));
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(0x00, 0xE5, 0xFF), 0.66));
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(0x29, 0x79, 0xFF), 0.83));
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(0xD5, 0x00, 0xF9), 1.00));
        return brush;
    }

    private double OpacityFor(NotchState s) => s switch
    {
        NotchState.Stealth => 0.0,
        NotchState.Idle    => (_vm.ShowFullLiveActivity || _vm.ShowDesktopAmbientPill)
            ? Math.Max(_settings.Current.IdleOpacity, 0.90)
            : _settings.Current.IdleOpacity,
        _                  => 1.0
    };

    private void OnClosed(object? sender, EventArgs e)
    {
        _fullscreenDetector.ForegroundWindowChanged -= OnForegroundWindowChanged;
        _fullscreenDetector.DesktopContextChanged -= OnDesktopContextChanged;
        _hotkeyService.Dispose();
        _fullscreenDetector.Dispose();
        _clipboardService.Dispose();
        _settingsWindow?.Close();
    }
}
