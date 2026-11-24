using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Win11Notch.Core.Settings;
using Win11Notch.Core.State;
using Win11Notch.ViewModels;
using Win11Notch.Views;
using Wpf.Ui.Tray.Controls;

namespace Win11Notch.Services;

/// <summary>
/// Manages the system tray icon and context menu using WPF-UI.Tray.
/// </summary>
public sealed class TrayService : IDisposable
{
    private readonly TrayViewModel     _vm;
    private readonly NotchWindow       _window;
    private readonly NotchStateMachine  _stateMachine;
    private readonly SettingsService   _settings;
    private readonly MonitorService    _monitor;

    private NotifyIcon? _notifyIcon;
    private MenuItem?   _dockTopItem;
    private MenuItem?   _dockLeftItem;
    private MenuItem?   _dockRightItem;

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
            TooltipText       = "Win11Notch",
            FocusOnLeftClick  = true,
            MenuOnRightClick  = true,
            ContextMenu       = menu,
            Icon              = CreateDefaultIcon()
        };

        // Wire dock-edge changes from ViewModel
        _vm.DockEdgeChanged += (_, edge) =>
        {
            UpdateDockChecks(edge);
            RepositionWindow();
        };

        _vm.ConfigReloaded += (_, _) =>
        {
            UpdateDockChecks(_settings.Current.DockEdge);
            RepositionWindow();
        };

        _notifyIcon.Register();
    }

    private ContextMenu BuildMenu()
    {
        var menu = new ContextMenu();

        var togglePeek = new MenuItem { Header = "Toggle Peek (Alt+Space)" };
        togglePeek.Click += (_, _) => _vm.TogglePeekCommand.Execute(null);
        menu.Items.Add(togglePeek);

        menu.Items.Add(new Separator());

        _dockTopItem = new MenuItem { Header = "Dock: Top", IsCheckable = true };
        _dockTopItem.Click += (_, _) => DockAndReposition("Top");
        menu.Items.Add(_dockTopItem);

        _dockLeftItem = new MenuItem { Header = "Dock: Left", IsCheckable = true };
        _dockLeftItem.Click += (_, _) => DockAndReposition("Left");
        menu.Items.Add(_dockLeftItem);

        _dockRightItem = new MenuItem { Header = "Dock: Right", IsCheckable = true };
        _dockRightItem.Click += (_, _) => DockAndReposition("Right");
        menu.Items.Add(_dockRightItem);

        UpdateDockChecks(_settings.Current.DockEdge);

        menu.Items.Add(new Separator());

        var reloadConfig = new MenuItem { Header = "Reload Config" };
        reloadConfig.Click += (_, _) => _vm.ReloadConfigCommand.Execute(null);
        menu.Items.Add(reloadConfig);

        menu.Items.Add(new Separator());

        var exitItem = new MenuItem { Header = "Exit" };
        exitItem.Click += (_, _) => _vm.ExitCommand.Execute(null);
        menu.Items.Add(exitItem);

        return menu;
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

    private void RepositionWindow()
    {
        _window.Dispatcher.Invoke(() =>
        {
            _window.SnapToState(_stateMachine.State);
        });
    }

    private static ImageSource CreateDefaultIcon()
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            // Dark slate pill with specular border (IPS aesthetic)
            var brush = new SolidColorBrush(Color.FromRgb(0x1A, 0x1C, 0x20));
            var pen   = new Pen(new SolidColorBrush(Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF)), 1.5);
            dc.DrawRoundedRectangle(brush, pen, new Rect(2, 6, 28, 20), 10, 10);

            // Active accent dot inside pill
            dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50)), null, new Point(16, 16), 3, 3);
        }

        var rtb = new RenderTargetBitmap(32, 32, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        return rtb;
    }

    public void Dispose()
    {
        _notifyIcon?.Unregister();
    }
}
