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
    private DispatcherTimer? _dwellTimer;
    private DispatcherTimer? _graceTimer;

    /// <summary>Raised on the UI thread whenever the state transitions.</summary>
    public event EventHandler<NotchState>? StateChanged;

    public NotchState State => _state;

    public NotchStateMachine(SettingsService settings) => _settings = settings;

    // ── External triggers ────────────────────────────────────────────────────

    /// <summary>Call when the mouse enters the notch window.</summary>
    public void OnMouseEnter()
    {
        if (_state is NotchState.PeekExpanded or NotchState.Stealth) return;
        _graceTimer?.Stop();
        if (_state == NotchState.CollapsedHover) return;
        RestartDwellTimer();
    }

    /// <summary>Call when the mouse leaves the notch window.</summary>
    public void OnMouseLeave()
    {
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

    /// <summary>Collapses to Idle from any state (Esc / click-outside / tray action).</summary>
    public void Collapse()
    {
        _dwellTimer?.Stop();
        _graceTimer?.Stop();
        Transition(NotchState.Idle);
    }

    /// <summary>Toggle PeekExpanded ↔ Idle (hotkey or tray action).</summary>
    public void Toggle()
    {
        if (_state == NotchState.PeekExpanded) { Collapse(); return; }
        _dwellTimer?.Stop();
        _graceTimer?.Stop();
        Transition(NotchState.PeekExpanded);
    }

    public void EnterStealth() => Transition(NotchState.Stealth);
    public void ExitStealth()  => Transition(NotchState.Idle);

    // ── Internals ────────────────────────────────────────────────────────────

    private void RestartDwellTimer()
    {
        _dwellTimer?.Stop();
        _dwellTimer = new DispatcherTimer
            { Interval = TimeSpan.FromMilliseconds(_settings.Current.HoverDwellMs) };
        _dwellTimer.Tick += (_, _) =>
        {
            _dwellTimer!.Stop();
            if (_state == NotchState.Idle)
                Transition(NotchState.CollapsedHover);
        };
        _dwellTimer.Start();
    }

    private void RestartGraceTimer()
    {
        _graceTimer?.Stop();
        _graceTimer = new DispatcherTimer
            { Interval = TimeSpan.FromMilliseconds(_settings.Current.ExitGraceMs) };
        _graceTimer.Tick += (_, _) =>
        {
            _graceTimer!.Stop();
            if (_state == NotchState.CollapsedHover)
                Transition(NotchState.Idle);
        };
        _graceTimer.Start();
    }

    private void Transition(NotchState next)
    {
        if (_state == next) return;
        _state = next;
        StateChanged?.Invoke(this, next);
    }
}
