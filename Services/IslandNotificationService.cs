using System.Windows;
using System.Windows.Threading;
using Win11Notch.Core.Settings;
using Win11Notch.Core.State;
using Win11Notch.ViewModels;

namespace Win11Notch.Services;

/// <summary>
/// Coordinates Dynamic Island notifications.
/// Automatically expands the notch from Idle to show track changes or alerts for 3 seconds.
/// </summary>
public sealed class IslandNotificationService : IDisposable
{
    private readonly NotchStateMachine _stateMachine;
    private readonly MediaService      _mediaService;
    private readonly SettingsService   _settings;
    private readonly NotchViewModel    _notchViewModel;

    private DispatcherTimer? _toastTimer;
    private string           _lastTrackTitle = string.Empty;

    public IslandNotificationService(
        NotchStateMachine stateMachine,
        MediaService      mediaService,
        SettingsService   settings,
        NotchViewModel    notchViewModel)
    {
        _stateMachine   = stateMachine;
        _mediaService   = mediaService;
        _settings       = settings;
        _notchViewModel = notchViewModel;

        _mediaService.MediaChanged += OnMediaChanged;
    }

    private void OnMediaChanged()
    {
        if (!_settings.Current.ShowMediaIslandNotifications) return;
        if (!_mediaService.HasMedia) return;

        // Trigger when the track title changes
        if (string.Equals(_mediaService.Title, _lastTrackTitle, StringComparison.OrdinalIgnoreCase)) return;
        _lastTrackTitle = _mediaService.Title;

        TriggerToast(3000);
    }

    public void TriggerToast(int durationMs)
    {
        if (Application.Current?.Dispatcher != null)
        {
            Application.Current.Dispatcher.Invoke(() => ExecuteToast(durationMs));
        }
        else
        {
            ExecuteToast(durationMs);
        }
    }

    private void ExecuteToast(int durationMs)
    {
        if (_stateMachine.State is not (NotchState.Idle or NotchState.CollapsedHover)) return;

        _notchViewModel.IsMediaToastActive = true;
        if (_stateMachine.State == NotchState.Idle)
        {
            _stateMachine.ShowToastBanner();
        }

        if (_toastTimer == null)
        {
            _toastTimer = new DispatcherTimer();
            _toastTimer.Tick += (_, _) =>
            {
                _toastTimer!.Stop();
                _notchViewModel.IsMediaToastActive = false;
                _stateMachine.DismissToastBanner();
            };
        }

        _toastTimer.Stop();
        _toastTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(500, durationMs));
        _toastTimer.Start();
    }

    public void Dispose()
    {
        _toastTimer?.Stop();
        _mediaService.MediaChanged -= OnMediaChanged;
    }
}
