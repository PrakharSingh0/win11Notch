using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
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

    // Animation constants
    private static readonly Duration  AnimDur = new(TimeSpan.FromMilliseconds(150));
    private static readonly CubicEase Ease    = new() { EasingMode = EasingMode.EaseOut };

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
    }

    private void OnPowerToastActiveChanged(bool isPowerToastActive)
    {
        if (_sm.State == NotchState.CollapsedHover)
        {
            var (tw, th) = _monitor.GetDimensions(_settings.Current.DockEdge, NotchState.CollapsedHover, _vm.IsMediaToastActive, _vm.IsVolumeToastActive, isPowerToastActive);
            var (tl, tt) = _monitor.ComputePosition(_settings.Current.DockEdge, tw, th);

            BeginAnimation(LeftProperty,   MakeAnim(tl));
            BeginAnimation(TopProperty,    MakeAnim(tt));
            BeginAnimation(WidthProperty,  MakeAnim(tw));
            BeginAnimation(HeightProperty, MakeAnim(th));
        }
    }

    private void OnVolumeToastActiveChanged(bool isVolumeToastActive)
    {
        if (_sm.State == NotchState.CollapsedHover)
        {
            var (tw, th) = _monitor.GetDimensions(_settings.Current.DockEdge, NotchState.CollapsedHover, _vm.IsMediaToastActive, isVolumeToastActive);
            var (tl, tt) = _monitor.ComputePosition(_settings.Current.DockEdge, tw, th);

            BeginAnimation(LeftProperty,   MakeAnim(tl));
            BeginAnimation(TopProperty,    MakeAnim(tt));
            BeginAnimation(WidthProperty,  MakeAnim(tw));
            BeginAnimation(HeightProperty, MakeAnim(th));
        }
    }

    private void OnMediaToastActiveChanged(bool isToastActive)
    {
        if (_sm.State == NotchState.CollapsedHover)
        {
            var (tw, th) = _monitor.GetDimensions(_settings.Current.DockEdge, NotchState.CollapsedHover, isToastActive, _vm.IsVolumeToastActive);
            var (tl, tt) = _monitor.ComputePosition(_settings.Current.DockEdge, tw, th);

            BeginAnimation(LeftProperty,   MakeAnim(tl));
            BeginAnimation(TopProperty,    MakeAnim(tt));
            BeginAnimation(WidthProperty,  MakeAnim(tw));
            BeginAnimation(HeightProperty, MakeAnim(th));
        }
    }

    // ── Win32 setup ──────────────────────────────────────────────────────────

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;

        // Hide from Alt+Tab; prevent focus steal on hover/click
        WindowStyleHelper.ApplyOverlayStyles(_hwnd);

        // Intercept WM_MOUSEACTIVATE to suppress activation on click
        HwndSource.FromHwnd(_hwnd)!.AddHook(WndProc);

        // Global hotkey (Alt+Space) registration
        _hotkeyService.Register(_hwnd);
        _hotkeyService.HotkeyPressed += () => _sm.Toggle();

        // Event-driven fullscreen detection (zero idle polling)
        _fullscreenDetector.Start(_hwnd);

        // Event-driven clipboard listener (zero idle polling)
        _clipboardService.Start(_hwnd);
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
                    ShowPowerToast("⚡", "Charger Connected", battery <= 100 ? $"{battery}%" : "Charging");
                }
                else if (ac == 0 && _lastAcStatus == 1)
                {
                    ShowPowerToast("🔋", "On Battery Power", battery <= 100 ? $"{battery}% Remaining" : "Discharging");
                }
            }

            if (battery <= 20 && _lastBatteryPercent > 20 && ac == 0)
            {
                ShowPowerToast("🪫", "Battery Low", $"{battery}% Remaining");
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
        SnapToState(NotchState.Idle);
        Opacity = OpacityFor(NotchState.Idle);
    }

    // ── Settings update listener ──────────────────────────────────────────────

    private void OnSettingsUpdated()
    {
        _hotkeyService.ChangePreset(_settings.Current.HotkeyPreset);
        SnapToState(_sm.State);
    }

    public void ShowSettingsWindow()
    {
        if (_settingsWindow == null || !_settingsWindow.IsLoaded)
        {
            _settingsWindow = new SettingsWindow(_settingsVm);
        }

        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    // ── Sub-tab navigation ────────────────────────────────────────────────────

    private void OnShelfSubTabFilesChecked(object sender, RoutedEventArgs e)
    {
        if (PanelStagedFiles != null && PanelClipboard != null)
        {
            PanelStagedFiles.Visibility = Visibility.Visible;
            PanelClipboard.Visibility   = Visibility.Collapsed;
            _vm.ShelfSubTabIndex        = 0;
        }
    }

    private void OnShelfSubTabClipboardChecked(object sender, RoutedEventArgs e)
    {
        if (PanelStagedFiles != null && PanelClipboard != null)
        {
            PanelStagedFiles.Visibility = Visibility.Collapsed;
            PanelClipboard.Visibility   = Visibility.Visible;
            _vm.ShelfSubTabIndex        = 1;
        }
    }

    // ── State machine handler ─────────────────────────────────────────────────

    private DateTime _lastExpandedTime = DateTime.MinValue;

    private void OnStateChanged(object? sender, NotchState newState)
    {
        App.Log($"[NotchWindow] OnStateChanged -> {newState}");

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

        // ── Switch visual layers ──────────────────────────────────────────
        IdlePill.Visibility     = newState == NotchState.Idle          ? Visibility.Visible : Visibility.Collapsed;
        HoverCapsule.Visibility = newState == NotchState.CollapsedHover ? Visibility.Visible : Visibility.Collapsed;
        ExpandedHub.Visibility  = newState == NotchState.PeekExpanded   ? Visibility.Visible : Visibility.Collapsed;

        // ── Animate window size, position & opacity ───────────────────────
        var (tw, th) = _monitor.GetDimensions(_settings.Current.DockEdge, newState, _vm.IsMediaToastActive, _vm.IsVolumeToastActive, _vm.IsPowerToastActive);
        var (tl, tt) = _monitor.ComputePosition(_settings.Current.DockEdge, tw, th);

        BeginAnimation(LeftProperty,    MakeAnim(tl));
        BeginAnimation(TopProperty,     MakeAnim(tt));
        BeginAnimation(WidthProperty,   MakeAnim(tw));
        BeginAnimation(HeightProperty,  MakeAnim(th));
        BeginAnimation(OpacityProperty, MakeAnim(OpacityFor(newState)));
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
        if (e.Key == Key.Escape && _sm.State == NotchState.PeekExpanded)
        {
            e.Handled = true;
            _sm.Collapse();
        }
    }

    private void OnWindowPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_sm.State is NotchState.Idle or NotchState.CollapsedHover)
        {
            App.Log($"[NotchWindow] OnWindowPreviewMouseLeftButtonDown in state {_sm.State} -> Expanding to PeekExpanded!");
            _sm.OnClick();
            e.Handled = true;
        }
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
            _vm.SelectedTabIndex = 1; // Switch to Shelf tab
            _vm.ShelfSubTabIndex = 0; // Show Files
            RadioSubTabFiles.IsChecked = true;
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
                _vm.SelectedTabIndex = 1;
                _vm.ShelfSubTabIndex = 0;
                RadioSubTabFiles.IsChecked = true;
            }
            e.Handled = true;
        }
    }

    // ── Drag staged file OUT of the Shelf ─────────────────────────────────────

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
                DragDrop.DoDragDrop(listBox, data, DragDropEffects.Copy);
            }
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    public void SnapToState(NotchState state)
    {
        BeginAnimation(LeftProperty,   null);
        BeginAnimation(TopProperty,    null);
        BeginAnimation(WidthProperty,  null);
        BeginAnimation(HeightProperty, null);
        BeginAnimation(OpacityProperty, null);

        var (w, h) = _monitor.GetDimensions(_settings.Current.DockEdge, state);
        var (l, t) = _monitor.ComputePosition(_settings.Current.DockEdge, w, h);

        Width   = w;
        Height  = h;
        Left    = l;
        Top     = t;
        Opacity = OpacityFor(state);
    }

    private double OpacityFor(NotchState s) => s switch
    {
        NotchState.Stealth => 0.0,
        NotchState.Idle    => _settings.Current.IdleOpacity,
        _                  => 1.0
    };

    private static DoubleAnimation MakeAnim(double to) =>
        new(to, AnimDur) { EasingFunction = Ease };

    private void OnClosed(object? sender, EventArgs e)
    {
        _hotkeyService.Dispose();
        _fullscreenDetector.Dispose();
        _clipboardService.Dispose();
        _settingsWindow?.Close();
    }
}
