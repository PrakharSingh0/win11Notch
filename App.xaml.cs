using System.Windows;
using Win11Notch.Core.Settings;
using Win11Notch.Core.State;
using Win11Notch.Core.Win32;
using Win11Notch.Services;
using Win11Notch.ViewModels;
using Win11Notch.Views;

namespace Win11Notch;

public partial class App : Application
{
    private TrayService?            _trayService;
    private NotchWindow?            _notchWindow;
    private MediaService?           _mediaService;
    private ResourceMonitorService? _resourceMonitor;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Apply WPF-UI dark Fluent theme
        try
        {
            Wpf.Ui.Appearance.ApplicationThemeManager.Apply(
                Wpf.Ui.Appearance.ApplicationTheme.Dark);
        }
        catch { /* non-fatal if theme manager unavailable */ }

        // Bootstrap Core Settings & Services
        var settings          = new SettingsService();
        await settings.LoadAsync();

        var monitor           = new MonitorService();
        var stateMachine      = new NotchStateMachine(settings);

        _mediaService         = new MediaService();
        await _mediaService.InitializeAsync();

        var shelfService      = new ShelfService();
        var audioService      = new AudioService();
        _resourceMonitor      = new ResourceMonitorService();

        var hotkeyService     = new HotkeyService();
        var fullscreenService = new FullscreenDetectorService(stateMachine);

        // Bootstrap ViewModels
        var notchVm = new NotchViewModel(
            stateMachine,
            _mediaService,
            shelfService,
            audioService,
            _resourceMonitor);

        var trayVm  = new TrayViewModel(stateMachine, settings);

        // Bootstrap Overlay Window
        _notchWindow = new NotchWindow(
            notchVm,
            stateMachine,
            monitor,
            settings,
            hotkeyService,
            fullscreenService,
            shelfService);

        _notchWindow.Show();

        // Bootstrap System Tray
        _trayService = new TrayService(trayVm, _notchWindow, stateMachine, settings, monitor);
        _trayService.Initialize();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayService?.Dispose();
        _mediaService?.Dispose();
        _resourceMonitor?.Dispose();
        base.OnExit(e);
    }
}
