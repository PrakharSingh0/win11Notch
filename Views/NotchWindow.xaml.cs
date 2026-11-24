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
        ClipboardService         clipboardService)
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

        DataContext = vm;
        InitializeComponent();

        SourceInitialized += OnSourceInitialized;
        Loaded            += OnLoaded;
        Deactivated       += OnDeactivated;
        PreviewKeyDown    += OnPreviewKeyDown;
        Closed            += OnClosed;

        _sm.StateChanged += OnStateChanged;

        _vm.OpenSettingsRequested   += ShowSettingsWindow;
        _settingsVm.SettingsChanged += OnSettingsUpdated;

        _vm.MediaToastActiveChanged  += OnMediaToastActiveChanged;
        _vm.VolumeToastActiveChanged += OnVolumeToastActiveChanged;
        _vm.PowerToastActiveChanged  += OnPowerToastActiveChanged;

        _vm.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName is nameof(_vm.IsMediaPlaying) or nameof(_vm.IsStopwatchRunning)
                or nameof(_vm.HasStagedFiles) or nameof(_vm.HasBluetoothBattery))
            {
                Dispatcher.InvokeAsync(() =>
                {
                    UpdateHandleAppearance();
                    AnimateToCurrentState();
                });
            }
        };
    }

    private void AnimateToCurrentState()
    {
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
            _vm.HasBluetoothBattery);

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

    private void OnVolumeToastActiveChanged(bool isVolumeToastActive)
    {
        if (_sm.State == NotchState.CollapsedHover) AnimateToCurrentState();
    }

    private void OnMediaToastActiveChanged(bool isToastActive)
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

        // Event-driven fullscreen detection & topmost Z-order re-assertion (ZERO idle polling timers)
        _fullscreenDetector.ForegroundWindowChanged += OnForegroundWindowChanged;
        _fullscreenDetector.Start(_hwnd);

        // Event-driven clipboard listener (zero idle polling)
        _clipboardService.Start(_hwnd);
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

    private void ShowPowerToast(string icon, string title, string detail)
    {
        if (_sm.State == NotchState.PeekExpanded || _sm.State == NotchState.Stealth) return;

        _vm.PowerToastIcon = icon;
        _vm.PowerToastTitle = title;
        _vm.PowerToastDetail = detail;
        _vm.IsPowerToastActive = true;

        if (_sm.State == NotchState.Idle)
        {
            _sm.ShowToastBanner();
        }

        _powerToastTimer?.Stop();
        _powerToastTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2200) };
        _powerToastTimer.Tick += (_, _) =>
        {
            _powerToastTimer.Stop();
            _vm.IsPowerToastActive = false;
            if (_sm.State == NotchState.CollapsedHover && !_vm.IsMediaToastActive && !_vm.IsVolumeToastActive)
            {
                _sm.Collapse();
            }
        };
        _powerToastTimer.Start();
    }

    // ── Startup ──────────────────────────────────────────────────────────────

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        UpdateHandleAppearance();
        ApplyBorderAnimationPreset();
        SnapToState(NotchState.Idle);
        Opacity = OpacityFor(NotchState.Idle);

        // Trim initial startup allocations once the idle handle settles
        WindowStyleHelper.TrimProcessWorkingSet();
    }

    // ── Settings update listener ──────────────────────────────────────────────

    private void OnSettingsUpdated()
    {
        _hotkeyService.ChangePreset(_settings.Current.HotkeyPreset);
        _vm.Timer.ApplySettings(_settings.Current);
        _vm.RefreshSettingsBindings();
        UpdateHandleAppearance();
        ApplyBorderAnimationPreset();
        SnapToState(_sm.State);
    }

    public void ShowSettingsWindow()
    {
        if (_settingsWindow == null || !_settingsWindow.IsLoaded)
        {
            _settingsWindow = new SettingsWindow(_settingsVm);
        }

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
        bool collapsedFromPeek = _prevState == NotchState.PeekExpanded && newState == NotchState.Idle;
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
            if (collapsedFromPeek)
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
        // Do not collapse if the user clicked into the Settings window
        if (_settingsWindow != null && _settingsWindow.IsActive) return;

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
                Key.D1 => 0,
                Key.D2 => 1,
                Key.D3 => 2,
                Key.D4 => 3,
                Key.D5 => 4,
                Key.D6 => 5,
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

    // ── Mouse event bridge ───────────────────────────────────────────────────

    protected override void OnMouseEnter(MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        App.Log($"[NotchWindow] OnMouseEnter (State={_sm.State})");
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
        App.Log($"[NotchWindow] OnMouseDown (Button={e.ChangedButton}, State={_sm.State})");
        _sm.OnClick();
    }

    // ── Drag and drop file staging ───────────────────────────────────────────

    protected override void OnDragEnter(DragEventArgs e)
    {
        base.OnDragEnter(e);
        if (!_settings.Current.AutoExpandOnDrag) return;

        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            _vm.SelectedTabIndex = 2; // Switch to Shelf tab (Dock)
            _sm.OnDragEnter();
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }
    }

    protected override void OnDragOver(DragEventArgs e)
    {
        base.OnDragOver(e);
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }
    }

    protected override void OnDrop(DragEventArgs e)
    {
        base.OnDrop(e);
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
            {
                _shelfService.AddFiles(files);
                _vm.SelectedTabIndex = 2; // Switch to Shelf tab (Dock)
            }
            e.Handled = true;
        }
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
            _vm.HasBluetoothBattery);

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
        bool hasActivity = _vm.IsMediaPlaying || _vm.IsStopwatchRunning;

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

        if (hasActivity)
        {
            IdlePill.CornerRadius    = new CornerRadius(12);
            IdlePill.BorderBrush     = new SolidColorBrush(Color.FromArgb(0x36, 0xFF, 0xFF, 0xFF));
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

        switch (preset)
        {
            case "RainbowWave":
            {
                var rotateHover = new RotateTransform(0);
                var rotateHub   = new RotateTransform(0);

                var rainbowHover = CreateRainbowBrush(rotateHover);
                var rainbowHub   = CreateRainbowBrush(rotateHub);

                HoverBorderGlow.BorderBrush = rainbowHover;
                HubBorderGlow.BorderBrush   = rainbowHub;
                HoverBorderGlow.Visibility  = Visibility.Visible;
                HubBorderGlow.Visibility    = Visibility.Visible;
                HoverBorderGlow.Opacity     = 1.0;
                HubBorderGlow.Opacity       = 1.0;

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
        NotchState.Idle    => _settings.Current.IdleOpacity,
        _                  => 1.0
    };

    private void OnClosed(object? sender, EventArgs e)
    {
        _fullscreenDetector.ForegroundWindowChanged -= OnForegroundWindowChanged;
        _hotkeyService.Dispose();
        _fullscreenDetector.Dispose();
        _clipboardService.Dispose();
        _settingsWindow?.Close();
    }
}
