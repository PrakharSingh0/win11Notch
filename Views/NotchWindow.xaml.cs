using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
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

    private IntPtr _hwnd;
    private Point  _dragStartPoint;

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
        ShelfService             shelfService)
    {
        _vm                 = vm;
        _sm                 = sm;
        _monitor            = monitor;
        _settings           = settings;
        _hotkeyService      = hotkeyService;
        _fullscreenDetector = fullscreenDetector;
        _shelfService       = shelfService;

        DataContext = vm;
        InitializeComponent();

        SourceInitialized += OnSourceInitialized;
        Loaded            += OnLoaded;
        Deactivated       += OnDeactivated;
        PreviewKeyDown    += OnPreviewKeyDown;
        Closed            += OnClosed;

        _sm.StateChanged += OnStateChanged;
    }

    // ── Win32 setup ──────────────────────────────────────────────────────────

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;

        // Hide from Alt+Tab; prevent focus steal on hover/click
        WindowStyleHelper.ApplyOverlayStyles(_hwnd);

        // Apply DWM Desktop Acrylic (or legacy ACCENT blur fallback)
        AcrylicHelper.Apply(_hwnd);

        // Intercept WM_MOUSEACTIVATE to suppress activation on click
        HwndSource.FromHwnd(_hwnd)!.AddHook(WndProc);

        // Global hotkey (Alt+Space) registration
        _hotkeyService.Register(_hwnd);
        _hotkeyService.HotkeyPressed += () => _sm.Toggle();

        // Event-driven fullscreen detection (zero idle polling)
        _fullscreenDetector.Start(_hwnd);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (WindowStyleHelper.IsMouseActivate(msg))
        {
            handled = true;
            return WindowStyleHelper.MaNoActivate;
        }
        return IntPtr.Zero;
    }

    // ── Startup ──────────────────────────────────────────────────────────────

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        SnapToState(NotchState.Idle);
        Opacity = OpacityFor(NotchState.Idle);
    }

    // ── State machine handler ─────────────────────────────────────────────────

    private void OnStateChanged(object? sender, NotchState newState)
    {
        // ── Focus management ──────────────────────────────────────────────
        if (newState == NotchState.PeekExpanded)
        {
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
        var (tw, th) = _monitor.GetDimensions(_settings.Current.DockEdge, newState);
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
        if (_sm.State == NotchState.PeekExpanded)
            _sm.Collapse();
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _sm.State == NotchState.PeekExpanded)
        {
            e.Handled = true;
            _sm.Collapse();
        }
    }

    // ── Mouse event bridge ───────────────────────────────────────────────────

    protected override void OnMouseEnter(MouseEventArgs e) { base.OnMouseEnter(e); _sm.OnMouseEnter(); }
    protected override void OnMouseLeave(MouseEventArgs e) { base.OnMouseLeave(e); _sm.OnMouseLeave(); }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        _sm.OnClick();
    }

    // ── Drag and drop file staging ───────────────────────────────────────────

    protected override void OnDragEnter(DragEventArgs e)
    {
        base.OnDragEnter(e);
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            _vm.SelectedTabIndex = 1; // Switch to Shelf tab
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

        var (w, h) = _monitor.GetDimensions(_settings.Current.DockEdge, state);
        var (l, t) = _monitor.ComputePosition(_settings.Current.DockEdge, w, h);

        Width = w; Height = h; Left = l; Top = t;
    }

    private static double OpacityFor(NotchState s) => s switch
    {
        NotchState.Stealth => 0.0,
        NotchState.Idle    => 0.20,
        _                  => 1.0
    };

    private static DoubleAnimation MakeAnim(double to) =>
        new(to, AnimDur) { EasingFunction = Ease };

    private void OnClosed(object? sender, EventArgs e)
    {
        _hotkeyService.Dispose();
        _fullscreenDetector.Dispose();
    }
}
