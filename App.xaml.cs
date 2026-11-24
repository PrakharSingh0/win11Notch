using System.IO;
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
    private static readonly string LogFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "debug_run.log");

    public static void Log(string message)
    {
#if DEBUG
        try
        {
            File.AppendAllText(LogFile, $"[{DateTime.Now:HH:mm:ss.fff}] {message}\n");
        }
        catch { }
#endif
    }

    private TrayService?               _trayService;
    private NotchWindow?               _notchWindow;
    private MediaService?              _mediaService;
    private AudioService?              _audioService;
    private AudioHudService?           _audioHud;
    private ResourceMonitorService?    _resourceMonitor;
    private IslandNotificationService? _islandNotifications;

    protected override async void OnStartup(StartupEventArgs e)
    {
        Log($"=== OnStartup: PID={Environment.ProcessId}, Name={System.Diagnostics.Process.GetCurrentProcess().ProcessName} ===");

        AppDomain.CurrentDomain.UnhandledException += (s, ev) =>
        {
            Log($"[CRASH] UnhandledException: {ev.ExceptionObject}");
            try { File.WriteAllText("crash.log", ev.ExceptionObject.ToString()); } catch { }
        };
        DispatcherUnhandledException += (s, ev) =>
        {
            Log($"[CRASH] DispatcherUnhandledException: {ev.Exception}");
            try { File.WriteAllText("crash.log", ev.Exception.ToString()); } catch { }
        };

        try
        {
            Log("Calling SingleInstanceGuard.TryAcquire()...");
            if (!SingleInstanceGuard.TryAcquire())
            {
                Log("SingleInstanceGuard returned FALSE! Another instance is running. Shutting down.");
                Shutdown();
                return;
            }
            Log("SingleInstanceGuard acquired successfully.");

            base.OnStartup(e);

            try
            {
                Wpf.Ui.Appearance.ApplicationThemeManager.Apply(
                    Wpf.Ui.Appearance.ApplicationTheme.Dark);
                Log("ThemeManager applied Dark theme.");
            }
            catch (Exception exTheme)
            {
                Log($"ThemeManager warning: {exTheme.Message}");
            }

            Log("Loading settings...");
            var settings = new SettingsService();
            await settings.LoadAsync();
            Log($"Settings loaded. DockEdge={settings.Current.DockEdge}, Hotkey={settings.Current.HotkeyPreset}");

            var startupService    = new StartupService();
            var monitor           = new MonitorService();
            var stateMachine      = new NotchStateMachine(settings);

            Log("Initializing MediaService...");
            _mediaService = new MediaService();
            await _mediaService.InitializeAsync();
            Log("MediaService initialized.");

            var shelfService      = new ShelfService();
            Log("Initializing AudioService...");
            _audioService         = new AudioService();
            Log("AudioService initialized.");

            _resourceMonitor      = new ResourceMonitorService();
            var clipboardService  = new ClipboardService();
            var screenCapture     = new ScreenCaptureService(shelfService);

            var hotkeyService     = new HotkeyService();
            var fullscreenService = new FullscreenDetectorService(stateMachine);

            var notchVm = new NotchViewModel(
                stateMachine,
                _mediaService,
                shelfService,
                _audioService,
                _resourceMonitor,
                clipboardService,
                screenCapture);

            _islandNotifications = new IslandNotificationService(stateMachine, _mediaService, settings, notchVm);
            _audioHud            = new AudioHudService(stateMachine, _audioService, notchVm);

            var trayVm     = new TrayViewModel(stateMachine, settings);
            var settingsVm = new SettingsViewModel(settings, startupService);

            Log("Constructing NotchWindow...");
            _notchWindow = new NotchWindow(
                notchVm,
                stateMachine,
                monitor,
                settings,
                hotkeyService,
                fullscreenService,
                shelfService,
                settingsVm,
                clipboardService);

            Log("Showing NotchWindow...");
            _notchWindow.Show();
            Log("NotchWindow shown.");

            Log("Initializing TrayService...");
            _trayService = new TrayService(trayVm, _notchWindow, stateMachine, settings, monitor);
            _trayService.Initialize();

            // Wire single-instance wakeup to immediately expand the notch
            SingleInstanceGuard.StartWakeupListener(() =>
            {
                Dispatcher.InvokeAsync(() =>
                {
                    Log("SingleInstanceGuard wakeup received! Expanding Notch.");
                    if (stateMachine.State != NotchState.PeekExpanded)
                    {
                        stateMachine.Toggle();
                    }
                    _notchWindow?.Activate();
                });
            });

            Log("TrayService initialized. Win11Notch startup fully complete!");
        }
        catch (Exception ex)
        {
            Log($"[FATAL EXCEPTION in OnStartup]: {ex}");
            try { File.WriteAllText("crash.log", ex.ToString()); } catch { }
            throw;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log($"=== OnExit called: Application exiting (ExitCode={e.ApplicationExitCode}) ===");
        SingleInstanceGuard.Release();
        _trayService?.Dispose();
        _audioHud?.Dispose();
        _audioService?.Dispose();
        _islandNotifications?.Dispose();
        _mediaService?.Dispose();
        _resourceMonitor?.Dispose();
        base.OnExit(e);
    }
}
