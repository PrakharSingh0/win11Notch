using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Win11Notch.Core.Settings;
using Win11Notch.Core.State;
using Win11Notch.ViewModels;
using Win11Notch.Views;
using Wpf.Ui.Tray.Controls;

namespace Win11Notch.Services;

/// <summary>
/// Manages the Windows system tray icon (WPF-UI.Tray) and a lightweight native Win32
/// dark context menu (TrackPopupMenuEx) anchored cleanly to the cursor at the taskbar/tray.
/// </summary>
public sealed class TrayService : IDisposable
{
    private const int  VK_RBUTTON      = 0x02;
    private const uint WM_NULL         = 0x0000;
    private const int  WS_POPUP        = unchecked((int)0x80000000);
    private const int  WS_EX_TOOLWINDOW = 0x00000080;

    private const uint MF_STRING       = 0x00000000;
    private const uint MF_CHECKED      = 0x00000008;
    private const uint MF_POPUP        = 0x00000010;
    private const uint MF_SEPARATOR    = 0x00000800;

    private const uint TPM_LEFTALIGN   = 0x0000;
    private const uint TPM_RIGHTBUTTON = 0x0002;
    private const uint TPM_RETURNCMD   = 0x0100;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AppendMenu(IntPtr hMenu, uint uFlags, UIntPtr uIDNewItem, string? lpNewItem);

    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenuEx(IntPtr hMenu, uint fuFlags, int x, int y, IntPtr hwnd, IntPtr lptpm);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    // Windows 11 dark-mode support for native Win32 popup menus (uxtheme undocumented ordinals)
    [DllImport("uxtheme.dll", EntryPoint = "#135")]
    private static extern int SetPreferredAppMode(int preferredAppMode);

    [DllImport("uxtheme.dll", EntryPoint = "#136")]
    private static extern void FlushMenuThemes();

    private readonly TrayViewModel     _vm;
    private readonly NotchWindow       _window;
    private readonly NotchStateMachine _stateMachine;
    private readonly SettingsService   _settings;
    private readonly MonitorService    _monitor;

    private NotifyIcon?      _notifyIcon;
    private HwndSource?      _menuOwnerHwnd;
    private DispatcherTimer? _rightClickReleaseTimer;
    private bool             _isMenuOpen;

    public TrayService(
        TrayViewModel     vm,
        NotchWindow       window,
        NotchStateMachine stateMachine,
        SettingsService   settings,
        MonitorService    monitor)
    {
        _vm           = vm;
        _window       = window;
        _stateMachine = stateMachine;
        _settings     = settings;
        _monitor      = monitor;
    }

    public void Initialize()
    {
        try
        {
            SetPreferredAppMode(2); // ForceDark for native Win32 tray menus
            FlushMenuThemes();
        }
        catch { /* fallback to system default menu theme */ }

        _menuOwnerHwnd = new HwndSource(new HwndSourceParameters("Win11NotchTrayMenuOwner")
        {
            Width               = 0,
            Height              = 0,
            WindowStyle         = WS_POPUP,
            ExtendedWindowStyle = WS_EX_TOOLWINDOW
        });

        _notifyIcon = new NotifyIcon
        {
            TooltipText      = "Win11Notch",
            FocusOnLeftClick = false,
            MenuOnRightClick = false,
            Icon             = CreateDefaultIcon()
        };

#pragma warning disable CS8622
        _notifyIcon.LeftClick  += (_, _) => _stateMachine.Toggle();
        _notifyIcon.RightClick += (_, _) => ScheduleNativeTrayMenu();
#pragma warning restore CS8622

        _vm.DockEdgeChanged += (_, _) => RepositionWindow();
        _vm.ConfigReloaded  += (_, _) => RepositionWindow();

        _notifyIcon.Register();
    }

    private void ScheduleNativeTrayMenu()
    {
        if (_isMenuOpen) return;
        _rightClickReleaseTimer?.Stop();

        if ((GetAsyncKeyState(VK_RBUTTON) & 0x8000) == 0)
        {
            Application.Current?.Dispatcher.InvokeAsync(ShowNativeTrayMenu, DispatcherPriority.Input);
            return;
        }

        int attempts = 0;
        _rightClickReleaseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(15) };
        _rightClickReleaseTimer.Tick += (_, _) =>
        {
            attempts++;
            if ((GetAsyncKeyState(VK_RBUTTON) & 0x8000) == 0 || attempts >= 35)
            {
                _rightClickReleaseTimer.Stop();
                ShowNativeTrayMenu();
            }
        };
        _rightClickReleaseTimer.Start();
    }

    private void ShowNativeTrayMenu()
    {
        if (_isMenuOpen || _menuOwnerHwnd == null || _menuOwnerHwnd.Handle == IntPtr.Zero) return;

        _isMenuOpen = true;
        IntPtr hMenu = CreatePopupMenu();
        if (hMenu == IntPtr.Zero)
        {
            _isMenuOpen = false;
            return;
        }

        try
        {
            uint nextId = 1000;
            var actions = new Dictionary<uint, Action>();

            void AddCommand(IntPtr targetMenu, string label, Action action, bool isChecked = false)
            {
                uint id = nextId++;
                actions[id] = action;
                uint flags = MF_STRING | (isChecked ? MF_CHECKED : 0u);
                AppendMenu(targetMenu, flags, (UIntPtr)id, label);
            }

            // Main actions
            AddCommand(hMenu, "Open / Toggle Hub (Alt+Space)", () => _vm.TogglePeekCommand.Execute(null));
            AddCommand(hMenu, "Always Show Hover Capsule", ToggleAlwaysHover, _settings.Current.AlwaysShowHoverCapsule);
            AddCommand(hMenu, "Hide Notch Handle (Stealth)", () => _stateMachine.SetManualStealth(!_stateMachine.IsManualStealth), _stateMachine.IsManualStealth);
            AddCommand(hMenu, "Show Greeting Banner", () => _window.TriggerGreetingBanner(durationMs: 3600, ignoreActiveCheck: true));

            AppendMenu(hMenu, MF_SEPARATOR, UIntPtr.Zero, null);

            // Handle Context submenu
            IntPtr hContext = CreatePopupMenu();
            string curMode = _settings.Current.HandleVisibilityMode;
            AddCommand(hContext, "Smart Desktop (Pill in Apps)", () => SelectContextMode("SmartDesktop"), curMode == "SmartDesktop");
            AddCommand(hContext, "Desktop Only (Hide in Apps)", () => SelectContextMode("DesktopOnly"), curMode == "DesktopOnly");
            AddCommand(hContext, "Always Show Live Preview", () => SelectContextMode("AlwaysLive"), curMode == "AlwaysLive");
            AppendMenu(hMenu, MF_POPUP, (UIntPtr)(ulong)hContext.ToInt64(), "Handle Context");

            // Dock Edge submenu
            IntPtr hDock = CreatePopupMenu();
            string curEdge = _settings.Current.DockEdge;
            AddCommand(hDock, "Top Edge", () => DockAndReposition("Top"), curEdge == "Top");
            AddCommand(hDock, "Left Edge", () => DockAndReposition("Left"), curEdge == "Left");
            AddCommand(hDock, "Right Edge", () => DockAndReposition("Right"), curEdge == "Right");
            AppendMenu(hMenu, MF_POPUP, (UIntPtr)(ulong)hDock.ToInt64(), "Dock Edge");

            // Target Monitor submenu
            IntPtr hMon = CreatePopupMenu();
            int curMon = _settings.Current.TargetMonitorIndex;
            AddCommand(hMon, "Primary Monitor (Default)", () => SelectMonitor(0), curMon == 0);
            foreach (var m in _monitor.GetMonitors())
            {
                int idx = m.Index;
                AddCommand(hMon, m.Name, () => SelectMonitor(idx), curMon == idx);
            }
            AppendMenu(hMenu, MF_POPUP, (UIntPtr)(ulong)hMon.ToInt64(), "Target Monitor");

            AppendMenu(hMenu, MF_SEPARATOR, UIntPtr.Zero, null);

            AddCommand(hMenu, "Settings...", () => _window.ShowSettingsWindow(0));
            AddCommand(hMenu, "About & License", () => _window.ShowSettingsWindow(4));
            AddCommand(hMenu, "Open Config & Logs", OpenAppDataFolder);
            AddCommand(hMenu, "Reload Config", () => _vm.ReloadConfigCommand.Execute(null));

            AppendMenu(hMenu, MF_SEPARATOR, UIntPtr.Zero, null);

            AddCommand(hMenu, "Quit Win11Notch", () => _vm.ExitCommand.Execute(null));

            GetCursorPos(out var pt);
            IntPtr hwnd = _menuOwnerHwnd.Handle;
            SetForegroundWindow(hwnd);
            uint selectedId = TrackPopupMenuEx(
                hMenu,
                TPM_LEFTALIGN | TPM_RIGHTBUTTON | TPM_RETURNCMD,
                pt.X, pt.Y,
                hwnd,
                IntPtr.Zero);
            PostMessage(hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);

            if (selectedId != 0 && actions.TryGetValue(selectedId, out var selectedAction))
            {
                selectedAction();
            }
        }
        finally
        {
            DestroyMenu(hMenu);
            _isMenuOpen = false;
        }
    }

    private void ToggleAlwaysHover()
    {
        _settings.Current.AlwaysShowHoverCapsule = !_settings.Current.AlwaysShowHoverCapsule;
        _ = _settings.SaveAsync();
        _window.ApplyContextModeFromTray();
    }

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

    private void DockAndReposition(string edge)
    {
        _settings.Current.DockEdge = edge;
        _ = _settings.SaveAsync();
        RepositionWindow();
    }

    private void SelectMonitor(int index)
    {
        _settings.Current.TargetMonitorIndex = index;
        _ = _settings.SaveAsync();
        RepositionWindow();
    }

    private void SelectContextMode(string mode)
    {
        _settings.Current.HandleVisibilityMode = mode;
        _ = _settings.SaveAsync();
        _window.ApplyContextModeFromTray();
    }

    private void RepositionWindow()
    {
        _window.Dispatcher.Invoke(() =>
        {
            _window.SnapToState(_stateMachine.State);
        });
    }

    private static ImageSource CreateDefaultIcon()
    {
        try
        {
            var uri = new Uri("pack://application:,,,/Assets/app.ico", UriKind.Absolute);
            var decoder = BitmapDecoder.Create(uri, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames
                .OrderBy(f => Math.Abs(f.PixelWidth - 32))
                .FirstOrDefault() ?? decoder.Frames[0];
            frame.Freeze();
            return frame;
        }
        catch
        {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                var brush = new SolidColorBrush(Color.FromRgb(0x14, 0x16, 0x1B));
                var pen   = new Pen(new SolidColorBrush(Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF)), 1.5);
                dc.DrawRoundedRectangle(brush, pen, new Rect(2, 2, 28, 28), 8, 8);
                dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(0x55, 0x22, 0xE5, 0x76)), null, new Point(13, 16), 7, 7);
                dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0x22, 0xE5, 0x76)), null, new Point(13, 16), 4.5, 4.5);
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0xDC, 0xE2, 0xEC)), null, new Rect(20, 14, 6, 4), 2, 2);
            }

            var rtb = new RenderTargetBitmap(32, 32, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(visual);
            rtb.Freeze();
            return rtb;
        }
    }

    public void Dispose()
    {
        _rightClickReleaseTimer?.Stop();
        _notifyIcon?.Unregister();
        _menuOwnerHwnd?.Dispose();
        _menuOwnerHwnd = null;
    }
}
