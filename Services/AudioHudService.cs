using System.Windows.Threading;
using Win11Notch.Core.State;
using Win11Notch.ViewModels;

namespace Win11Notch.Services;

/// <summary>
/// Coordinates the Dynamic Island Hardware Volume HUD.
/// When hardware volume keys or volume changes occur, morphs the notch
/// into an interactive volume HUD for 1.5 seconds.
/// </summary>
public sealed class AudioHudService : IDisposable
{
    private readonly NotchStateMachine _stateMachine;
    private readonly AudioService      _audioService;
    private readonly NotchViewModel    _notchViewModel;

    private DispatcherTimer? _hudTimer;
    private bool             _collapseOnTimeout;

    public AudioHudService(
        NotchStateMachine stateMachine,
        AudioService      audioService,
        NotchViewModel    notchViewModel)
    {
        _stateMachine   = stateMachine;
        _audioService   = audioService;
        _notchViewModel = notchViewModel;

        _audioService.VolumeChanged += OnVolumeChanged;
    }

    private void OnVolumeChanged(float volume, bool isMuted, bool isSelfTriggered)
    {
        // Update ViewModel volume properties safely (without echo loop)
        _notchViewModel.UpdateVolumeFromDevice(volume * 100f, isMuted);

        // If change was initiated by the notch app slider itself, do NOT pop up HUD toast
        if (isSelfTriggered) return;

        // Pop up Dynamic Island Volume HUD cleanly without leaving hover stuck after rapid presses
        if (_stateMachine.State == NotchState.Idle)
        {
            _collapseOnTimeout = true;
            _notchViewModel.IsVolumeToastActive = true;
            _stateMachine.ShowToastBanner();
        }
        else if (_stateMachine.State == NotchState.CollapsedHover)
        {
            _notchViewModel.IsVolumeToastActive = true;
        }
        else
        {
            return;
        }

        if (_hudTimer == null)
        {
            _hudTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
            _hudTimer.Tick += (_, _) =>
            {
                _hudTimer!.Stop();
                _notchViewModel.IsVolumeToastActive = false;
                if (_collapseOnTimeout)
                {
                    _collapseOnTimeout = false;
                    _stateMachine.DismissToastBanner();
                }
            };
        }

        _hudTimer.Stop();
        _hudTimer.Start();
    }

    public void Dispose()
    {
        _hudTimer?.Stop();
        _audioService.VolumeChanged -= OnVolumeChanged;
    }
}
