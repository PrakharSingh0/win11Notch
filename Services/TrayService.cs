using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Win11Notch.Core.Settings;
using Win11Notch.Core.State;
using Win11Notch.ViewModels;
using Win11Notch.Views;
using Wpf.Ui.Tray.Controls;

namespace Win11Notch.Services;

/// <summary>
/// Manages the system tray icon and right-click context menu using WPF-UI.Tray.
/// </summary>
public sealed class TrayService : IDisposable
{
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    private readonly TrayViewModel     _vm;
    private readonly NotchWindow       _window;
    private readonly NotchStateMachine _stateMachine;
    private readonly SettingsService   _settings;
    private readonly MonitorService    _monitor;

    private NotifyIcon? _notifyIcon;
    private MenuItem?   _dockTopItem;
    private MenuItem?   _dockLeftItem;
    private MenuItem?   _dockRightItem;
    private MenuItem?   _stealthItem;
    private readonly List<(int Index, MenuItem Item)> _monitorMenuItems = new();

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
        var menu = BuildMenu();

        _notifyIcon = new NotifyIcon
        {
            TooltipText      = $"Win11Notch • {GreetingHelper.GetGreeting(DateTime.Now).DisplayTitle}",
            FocusOnLeftClick = false,
            MenuOnRightClick = true,
            Menu             = menu,
            Icon             = CreateDefaultIcon()
        };

#pragma warning disable CS8622
        _notifyIcon.LeftClick += (_, _) => _stateMachine.Toggle();
#pragma warning restore CS8622

        _vm.DockEdgeChanged += (_, edge) =>
        {
            UpdateDockChecks(edge);
            RepositionWindow();
        };

        _vm.ConfigReloaded += (_, _) =>
        {
            UpdateDockChecks(_settings.Current.DockEdge);
            UpdateMonitorChecks(_settings.Current.TargetMonitorIndex);
            RepositionWindow();
        };

        _stateMachine.ManualStealthChanged += (_, hidden) =>
        {
            if (_stealthItem != null) _stealthItem.IsChecked = hidden;
        };

        _notifyIcon.Register();
    }

    private ContextMenu BuildMenu()
    {
        var menu = new ContextMenu
        {
            MinWidth = 220
        };

        // Ensure clicking outside dismisses the WPF tray ContextMenu cleanly on Windows 11
        menu.Opened += (_, _) =>
        {
            if (_notifyIcon != null)
            {
                _notifyIcon.TooltipText = $"Win11Notch • {GreetingHelper.GetGreeting(DateTime.Now).DisplayTitle}";
            }
            if (PresentationSource.FromVisual(menu) is HwndSource src && src.Handle != IntPtr.Zero)
            {
                SetForegroundWindow(src.Handle);
            }
        };

        menu.Items.Add(MakeItem("Open / Toggle Hub (Alt+Space)", "\uE71D", () => _vm.TogglePeekCommand.Execute(null)));

        _stealthItem = MakeItem("Hide Notch Handle (Stealth)", "\uED1A", () =>
        {
            _stateMachine.SetManualStealth(!_stateMachine.IsManualStealth);
        }, checkable: true);
        _stealthItem.IsChecked = _stateMachine.IsManualStealth;
        menu.Items.Add(_stealthItem);

        menu.Items.Add(MakeItem("Show Greeting Banner", "\uE706", () => _window.TriggerGreetingBanner(durationMs: 3600, ignoreActiveCheck: true)));

        menu.Items.Add(new Separator());

        var dockMenu = MakeParentItem("Dock Edge", "\uE7F4");
        _dockTopItem = MakeItem("Top Edge", "\uE74A", () => DockAndReposition("Top"), checkable: true);
        _dockLeftItem = MakeItem("Left Edge", "\uE76B", () => DockAndReposition("Left"), checkable: true);
        _dockRightItem = MakeItem("Right Edge", "\uE76C", () => DockAndReposition("Right"), checkable: true);
        dockMenu.Items.Add(_dockTopItem);
        dockMenu.Items.Add(_dockLeftItem);
        dockMenu.Items.Add(_dockRightItem);
        UpdateDockChecks(_settings.Current.DockEdge);
        menu.Items.Add(dockMenu);

        _monitorMenuItems.Clear();
        var monitorMenu = MakeParentItem("Target Monitor", "\uE7F8");
        var primaryMonitorItem = MakeItem("Primary Monitor (Default)", "\uE7F4", () => SelectMonitor(0), checkable: true);
        monitorMenu.Items.Add(primaryMonitorItem);
        _monitorMenuItems.Add((0, primaryMonitorItem));

        foreach (var m in _monitor.GetMonitors())
        {
            int mIdx = m.Index;
            var mItem = MakeItem(m.Name, "\uE7F4", () => SelectMonitor(mIdx), checkable: true);
            monitorMenu.Items.Add(mItem);
            _monitorMenuItems.Add((mIdx, mItem));
        }
        UpdateMonitorChecks(_settings.Current.TargetMonitorIndex);
        menu.Items.Add(monitorMenu);

        menu.Items.Add(new Separator());

        menu.Items.Add(MakeItem("Settings...", "\uE713", () => _window.ShowSettingsWindow(0)));
        menu.Items.Add(MakeItem("About & License", "\uE946", () => _window.ShowSettingsWindow(4)));
        menu.Items.Add(MakeItem("Open Config & Logs", "\uE8B7", OpenAppDataFolder));
        menu.Items.Add(MakeItem("Reload Config", "\uE72C", () => _vm.ReloadConfigCommand.Execute(null)));

        menu.Items.Add(new Separator());

        menu.Items.Add(MakeItem("Quit Win11Notch", "\uE711", () => _vm.ExitCommand.Execute(null)));

        return menu;
    }

    private static MenuItem MakeParentItem(string header, string glyph)
    {
        return new MenuItem
        {
            Header = header,
            Icon   = MakeMenuIcon(glyph)
        };
    }

    private static MenuItem MakeItem(string header, string glyph, Action onClick, bool checkable = false)
    {
        var item = new MenuItem
        {
            Header      = header,
            IsCheckable = checkable,
            Icon        = checkable ? null : MakeMenuIcon(glyph)
        };
        item.Click += (_, _) => onClick();
        return item;
    }

    private static TextBlock MakeMenuIcon(string glyph) => new()
    {
        FontFamily          = new FontFamily("Segoe Fluent Icons"),
        Text                = glyph,
        FontSize            = 12,
        Foreground          = new SolidColorBrush(Color.FromRgb(0x22, 0xE5, 0x76)),
        VerticalAlignment   = VerticalAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center
    };

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

    private void UpdateDockChecks(string edge)
    {
        if (_dockTopItem != null)   _dockTopItem.IsChecked   = edge == "Top";
        if (_dockLeftItem != null)  _dockLeftItem.IsChecked  = edge == "Left";
        if (_dockRightItem != null) _dockRightItem.IsChecked = edge == "Right";
    }

    private void DockAndReposition(string edge)
    {
        _settings.Current.DockEdge = edge;
        _ = _settings.SaveAsync();
        UpdateDockChecks(edge);
        RepositionWindow();
    }

    private void UpdateMonitorChecks(int targetIndex)
    {
        foreach (var (idx, item) in _monitorMenuItems)
        {
            item.IsChecked = idx == targetIndex;
        }
    }

    private void SelectMonitor(int index)
    {
        _settings.Current.TargetMonitorIndex = index;
        _ = _settings.SaveAsync();
        UpdateMonitorChecks(index);
        RepositionWindow();
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
        _notifyIcon?.Unregister();
    }
}
