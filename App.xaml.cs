using System.IO;
using System.Windows;
using System.Windows.Shell;
using Win11Notch.Core.Settings;
using Win11Notch.Core.State;
using Win11Notch.Core.Win32;
using Win11Notch.Services;
using Win11Notch.ViewModels;
using Win11Notch.Views;

namespace Win11Notch;

public partial class App : Application
{
    private static readonly string LogDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Win11Notch",
        "Logs");
    private static readonly string LogFile      = Path.Combine(LogDir, "debug_run.log");
    private static readonly string CrashLogFile = Path.Combine(LogDir, "crash.log");

    static App()
    {
        try
        {
            Directory.CreateDirectory(LogDir);
            if (File.Exists(LogFile) && new FileInfo(LogFile).Length > 512 * 1024)
            {
                File.WriteAllText(LogFile, string.Empty);
            }
        }
        catch { }
    }

    public static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(LogDir);
            File.AppendAllText(LogFile, $"[{DateTime.Now:HH:mm:ss.fff}] {message}\n");
        }
        catch { }
    }

    private TrayService?               _trayService;
    private NotchWindow?               _notchWindow;
    private NotchViewModel?            _notchVm;
    private NotchStateMachine?         _stateMachine;
    private TimerService?              _timerService;
    private QuickShareService?         _quickShareService;
    private MediaService?              _mediaService;
    private AudioService?              _audioService;
    private AudioHudService?           _audioHud;
    private ResourceMonitorService?    _resourceMonitor;
    private IslandNotificationService? _islandNotifications;
    private PowerAwakeService?         _powerAwakeService;
    private BrightnessService?         _brightnessService;

    protected override async void OnStartup(StartupEventArgs e)
    {
        Log($"=== OnStartup: PID={Environment.ProcessId}, Name={System.Diagnostics.Process.GetCurrentProcess().ProcessName} ===");

        AppDomain.CurrentDomain.UnhandledException += (s, ev) =>
        {
            Log($"[CRASH] UnhandledException: {ev.ExceptionObject}");
            try
            {
                Directory.CreateDirectory(LogDir);
                File.WriteAllText(CrashLogFile, ev.ExceptionObject.ToString());
            }
            catch { }
        };
        DispatcherUnhandledException += (s, ev) =>
        {
            Log($"[CRASH] DispatcherUnhandledException: {ev.Exception}");
            try
            {
                Directory.CreateDirectory(LogDir);
                File.WriteAllText(CrashLogFile, ev.Exception.ToString());
            }
            catch { }
            ev.Handled = true; // Prevent process abort on dispatcher exceptions
        };

        try
        {
            Log("Calling SingleInstanceGuard.TryAcquire()...");
            if (!SingleInstanceGuard.TryAcquire(e.Args))
            {
                Log("SingleInstanceGuard returned FALSE! Another instance is running. Terminating secondary process.");
                Environment.Exit(0);
                return;
            }
            Log("SingleInstanceGuard acquired successfully.");

            base.OnStartup(e);
            RegisterTaskbarJumpList();

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
            var screenCapture     = new ScreenCaptureService(shelfService, monitor, settings);

            var hotkeyService     = new HotkeyService();
            var fullscreenService = new FullscreenDetectorService(stateMachine);
            _stateMachine         = stateMachine;
            _timerService         = new TimerService();
            _timerService.ApplySettings(settings.Current);
            _powerAwakeService    = new PowerAwakeService();
            var scratchpadService = new ScratchpadService();
            var calendarService   = new CalendarService();
            _quickShareService    = new QuickShareService();
            var homeLayoutService = new HomeLayoutService();
            _brightnessService    = new BrightnessService(monitor);
            var colorPickerService = new ColorPickerService();

            _notchVm = new NotchViewModel(
                stateMachine,
                _mediaService,
                shelfService,
                _audioService,
                _resourceMonitor,
                clipboardService,
                screenCapture,
                _timerService,
                settings,
                _powerAwakeService,
                scratchpadService,
                calendarService,
                _quickShareService,
                homeLayoutService,
                _brightnessService,
                colorPickerService);

            _islandNotifications = new IslandNotificationService(stateMachine, _mediaService, settings, _notchVm);
            _audioHud            = new AudioHudService(stateMachine, _audioService, _notchVm);

            var trayVm     = new TrayViewModel(stateMachine, settings);
            var settingsVm = new SettingsViewModel(settings, startupService, monitor);

            Log("Constructing NotchWindow...");
            _notchWindow = new NotchWindow(
                _notchVm,
                stateMachine,
                monitor,
                settings,
                hotkeyService,
                fullscreenService,
                shelfService,
                settingsVm,
                clipboardService,
                _quickShareService);

            Log("Showing NotchWindow...");
            _notchWindow.Show();
            Log("NotchWindow shown.");

            Log("Initializing TrayService...");
            _trayService = new TrayService(trayVm, _notchWindow, stateMachine, settings, monitor, settingsVm);
            _trayService.Initialize();

            // Wire single-instance & JumpList actions (--toggle, --settings, --about)
            void DispatchLaunchAction(string action)
            {
                if (action == "settings")
                {
                    _notchWindow?.ShowSettingsWindow(0);
                }
                else if (action == "about")
                {
                    _notchWindow?.ShowSettingsWindow(4);
                }
                else
                {
                    if (stateMachine.State != NotchState.PeekExpanded)
                    {
                        stateMachine.Toggle();
                    }
                    _notchWindow?.Show();
                    _notchWindow?.Activate();
                    _notchWindow?.Focus();
                }
            }

            SingleInstanceGuard.StartWakeupListener(action =>
            {
                Dispatcher.InvokeAsync(() =>
                {
                    Log($"SingleInstanceGuard wakeup received (action={action}).");
                    DispatchLaunchAction(action);
                });
            });

            if (e.Args.Length > 0)
            {
                var initialAction = SingleInstanceGuard.ParseActionFromArgs(e.Args);
                if (initialAction is "settings" or "about" || e.Args.Any(a => a.Equals("--toggle", StringComparison.OrdinalIgnoreCase)))
                {
                    _ = Dispatcher.InvokeAsync(() => DispatchLaunchAction(initialAction));
                }
            }

            Log("TrayService initialized. Win11Notch startup fully complete!");
        }
        catch (Exception ex)
        {
            Log($"[FATAL EXCEPTION in OnStartup]: {ex}");
            try
            {
                Directory.CreateDirectory(LogDir);
                File.WriteAllText(CrashLogFile, ex.ToString());
            }
            catch { }
            throw;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log($"=== OnExit called: Application exiting (ExitCode={e.ApplicationExitCode}) ===");
        SingleInstanceGuard.Release();
        _stateMachine?.StopTimers();
        _timerService?.Stop();
        _notchVm?.Dispose();
        _quickShareService?.Dispose();
        _trayService?.Dispose();
        _brightnessService?.Dispose();
        _audioHud?.Dispose();
        _audioService?.Dispose();
        _islandNotifications?.Dispose();
        _mediaService?.Dispose();
        _resourceMonitor?.Dispose();
        _powerAwakeService?.Dispose();
        base.OnExit(e);
    }

    private void RegisterTaskbarJumpList()
    {
        try
        {
            string? exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return;

            var jumpList = new JumpList();
            jumpList.JumpItems.Add(new JumpTask
            {
                Title           = "Open / Toggle Notch Hub",
                Description     = "Expand or collapse the Win11Notch hub",
                ApplicationPath = exe,
                Arguments       = "--toggle",
                IconResourcePath = exe,
                IconResourceIndex = 0
            });
            jumpList.JumpItems.Add(new JumpTask
            {
                Title           = "Settings",
                Description     = "Open Win11Notch Settings",
                ApplicationPath = exe,
                Arguments       = "--settings",
                IconResourcePath = exe,
                IconResourceIndex = 0
            });
            jumpList.JumpItems.Add(new JumpTask
            {
                Title           = "About & License",
                Description     = "View Win11Notch author, GitHub repo, and MIT license",
                ApplicationPath = exe,
                Arguments       = "--about",
                IconResourcePath = exe,
                IconResourceIndex = 0
            });
            JumpList.SetJumpList(this, jumpList);
            jumpList.Apply();
        }
        catch (Exception ex)
        {
            Log($"JumpList warning: {ex.Message}");
        }
    }
}
