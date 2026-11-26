using System.Windows.Threading;
using Win11Notch.Core.Settings;

namespace Win11Notch.Core.State;

/// <summary>
/// UI-thread 3-tier state machine for the notch overlay.
/// All timers are <see cref="DispatcherTimer"/> — zero overhead when stopped.
/// </summary>
public sealed class NotchStateMachine
{
    private readonly SettingsService _settings;

    private NotchState       _state = NotchState.Idle;
    private bool             _isManualStealth;
    private bool             _isFullscreenStealth;
    private bool             _isContextHidden;
    private bool             _isPinnedReminderAlertActive;
    private bool             _isPointerInside;
    private DispatcherTimer? _dwellTimer;
    private DispatcherTimer? _graceTimer;

    /// <summary>Raised on the UI thread whenever the state transitions.</summary>
    public event EventHandler<NotchState>? StateChanged;

    /// <summary>Raised when the user toggles manual Stealth / Hide Handle via tray.</summary>
    public event EventHandler<bool>? ManualStealthChanged;

    public NotchState State => _state;
    public bool IsManualStealth => _isManualStealth;
    public bool ShouldBeStealth => _isManualStealth || _isFullscreenStealth || _isContextHidden;
    private NotchState RestingState => ShouldBeStealth
        ? NotchState.Stealth
        : (_isPinnedReminderAlertActive || _settings.Current.AlwaysShowHoverCapsule
            ? NotchState.CollapsedHover
            : NotchState.Idle);

    public NotchStateMachine(SettingsService settings) => _settings = settings;

    // ── External triggers ────────────────────────────────────────────────────

    /// <summary>Call when the mouse enters the notch window.</summary>
    public void OnMouseEnter()
    {
        _isPointerInside = true;
        if (_state is NotchState.PeekExpanded or NotchState.Stealth) return;
        _graceTimer?.Stop();
        if (_state == NotchState.CollapsedHover) return;
        RestartDwellTimer();
    }

    /// <summary>Call when the mouse leaves the notch window.</summary>
    public void OnMouseLeave()
    {
        _isPointerInside = false;
        _dwellTimer?.Stop();
        if (_state != NotchState.CollapsedHover) return;
        RestartGraceTimer();
    }

    /// <summary>Call on mouse-click to open PeekExpanded from Idle or CollapsedHover.</summary>
    public void OnClick()
    {
        if (_state is NotchState.Idle or NotchState.CollapsedHover)
        {
            _dwellTimer?.Stop();
            _graceTimer?.Stop();
            Transition(NotchState.PeekExpanded);
        }
    }

    /// <summary>Triggered when a file drag reaches the notch edge.</summary>
    public void OnDragEnter()
    {
        if (_state == NotchState.Stealth) return;
        _dwellTimer?.Stop();
        _graceTimer?.Stop();
        Transition(NotchState.PeekExpanded);
    }

    /// <summary>Expands from Idle to CollapsedHover as a Dynamic Island temporary toast.</summary>
    public void ShowToastBanner()
    {
        if (_state == NotchState.Idle)
        {
            _dwellTimer?.Stop();
            _graceTimer?.Stop();
            Transition(NotchState.CollapsedHover);
        }
    }

    /// <summary>Collapses to resting state (Idle, Always-On HoverCapsule, or Stealth).</summary>
    public void Collapse()
    {
        _dwellTimer?.Stop();
        _graceTimer?.Stop();
        Transition(RestingState);
    }

    /// <summary>Returns a transient toast banner back to RestingState unless the pointer is currently hovering.</summary>
    public void DismissToastBanner()
    {
        if (_state == NotchState.CollapsedHover && !_isPointerInside)
        {
            _dwellTimer?.Stop();
            _graceTimer?.Stop();
            Transition(RestingState);
        }
    }

    public void StopTimers()
    {
        _dwellTimer?.Stop();
        _graceTimer?.Stop();
    }

    /// <summary>Synchronizes the current non-Peek state when settings like AlwaysShowHoverCapsule change.</summary>
    public void SyncRestingState()
    {
        if (_state == NotchState.PeekExpanded) return;
        _dwellTimer?.Stop();
        _graceTimer?.Stop();
        Transition(RestingState);
    }

    /// <summary>
    /// Pins the overlay in CollapsedHover while an un-dismissed due reminder is active,
    /// returning to the normal resting state once dismissed/completed/snoozed.
    /// </summary>
    public void SetPinnedReminderAlert(bool active)
    {
        if (_isPinnedReminderAlertActive == active) return;
        _isPinnedReminderAlertActive = active;
        if (_state == NotchState.PeekExpanded) return;
        _dwellTimer?.Stop();
        _graceTimer?.Stop();
        Transition(RestingState);
    }

    /// <summary>Toggle PeekExpanded ↔ resting state (hotkey or tray action).</summary>
    public void Toggle()
    {
        if (_state == NotchState.PeekExpanded)
        {
            Collapse();
            return;
        }
        if (_isManualStealth)
        {
            _isManualStealth = false;
            ManualStealthChanged?.Invoke(this, false);
        }
        _dwellTimer?.Stop();
        _graceTimer?.Stop();
        Transition(NotchState.PeekExpanded);
    }

    public void SetManualStealth(bool hidden)
    {
        if (_isManualStealth == hidden) return;
        _isManualStealth = hidden;
        _dwellTimer?.Stop();
        _graceTimer?.Stop();
        ManualStealthChanged?.Invoke(this, hidden);
        SyncStealthState(allowExitFromPeek: true);
    }

    public void SetContextHidden(bool hidden)
    {
        if (_isContextHidden == hidden) return;
        _isContextHidden = hidden;
        SyncStealthState(allowExitFromPeek: false);
    }

    public void EnterStealth()
    {
        if (_isFullscreenStealth) return;
        _isFullscreenStealth = true;
        SyncStealthState(allowExitFromPeek: true);
    }

    public void ExitStealth()
    {
        if (!_isFullscreenStealth) return;
        _isFullscreenStealth = false;
        SyncStealthState(allowExitFromPeek: false);
    }

    private void SyncStealthState(bool allowExitFromPeek)
    {
        if (ShouldBeStealth)
        {
            if (_state == NotchState.PeekExpanded && !allowExitFromPeek) return;
            _dwellTimer?.Stop();
            _graceTimer?.Stop();
            Transition(NotchState.Stealth);
        }
        else if (_state == NotchState.Stealth)
        {
            Transition(RestingState);
        }
    }

    // ── Internals ────────────────────────────────────────────────────────────

    private void RestartDwellTimer()
    {
        if (_dwellTimer == null)
        {
            _dwellTimer = new DispatcherTimer();
            _dwellTimer.Tick += (_, _) =>
            {
                _dwellTimer!.Stop();
                if (_state == NotchState.Idle)
                    Transition(NotchState.CollapsedHover);
            };
        }
        _dwellTimer.Stop();
        _dwellTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(20, _settings.Current.HoverDwellMs));
        _dwellTimer.Start();
    }

    private void RestartGraceTimer()
    {
        if (_graceTimer == null)
        {
            _graceTimer = new DispatcherTimer();
            _graceTimer.Tick += (_, _) =>
            {
                _graceTimer!.Stop();
                if (_state == NotchState.CollapsedHover)
                    Transition(RestingState);
            };
        }
        _graceTimer.Stop();
        _graceTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(50, _settings.Current.ExitGraceMs));
        _graceTimer.Start();
    }

    private void Transition(NotchState next)
    {
        if (_state == next) return;
        _state = next;
        StateChanged?.Invoke(this, next);
    }
}
