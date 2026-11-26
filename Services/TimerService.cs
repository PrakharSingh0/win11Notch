using System.Collections.ObjectModel;
using System.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Win11Notch.Core.Settings;

namespace Win11Notch.Services;

public enum TimerMode
{
    Pomodoro,
    Countdown,
    Stopwatch,
    Water
}

public enum PomodoroPhase
{
    Focus,
    ShortBreak,
    LongBreak
}

/// <summary>
/// Comprehensive productivity timer engine managing Pomodoro cycles, Countdowns,
/// Stopwatch sprint precision, and daily hydration tracking.
/// </summary>
public sealed partial class TimerService : ObservableObject
{
    private readonly DispatcherTimer _ticker;

    // ── Active Mode ──────────────────────────────────────────────────────────
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPomodoroMode))]
    [NotifyPropertyChangedFor(nameof(IsCountdownMode))]
    [NotifyPropertyChangedFor(nameof(IsStopwatchMode))]
    [NotifyPropertyChangedFor(nameof(IsWaterMode))]
    [NotifyPropertyChangedFor(nameof(DialTimeText))]
    [NotifyPropertyChangedFor(nameof(DialPhaseLabel))]
    [NotifyPropertyChangedFor(nameof(DialProgressFraction))]
    private TimerMode _activeMode = TimerMode.Pomodoro;

    public bool IsPomodoroMode  => ActiveMode == TimerMode.Pomodoro;
    public bool IsCountdownMode => ActiveMode == TimerMode.Countdown;
    public bool IsStopwatchMode => ActiveMode == TimerMode.Stopwatch;
    public bool IsWaterMode     => ActiveMode == TimerMode.Water;

    // ── Pomodoro Mode State ──────────────────────────────────────────────────
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PomodoroTimeText))]
    [NotifyPropertyChangedFor(nameof(PomodoroProgress))]
    [NotifyPropertyChangedFor(nameof(DialTimeText))]
    [NotifyPropertyChangedFor(nameof(DialProgressFraction))]
    [NotifyPropertyChangedFor(nameof(IslandTimerText))]
    private TimeSpan _pomodoroRemaining = TimeSpan.FromMinutes(25);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAnyTimerRunning))]
    [NotifyPropertyChangedFor(nameof(IslandTimerText))]
    private bool _isPomodoroRunning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DialPhaseLabel))]
    [NotifyPropertyChangedFor(nameof(PomodoroStatusText))]
    [NotifyPropertyChangedFor(nameof(PomodoroNextPhaseText))]
    private PomodoroPhase _pomodoroPhase = PomodoroPhase.Focus;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PomodoroStatusText))]
    [NotifyPropertyChangedFor(nameof(IsCycle1Active))]
    [NotifyPropertyChangedFor(nameof(IsCycle2Active))]
    [NotifyPropertyChangedFor(nameof(IsCycle3Active))]
    [NotifyPropertyChangedFor(nameof(IsCycle4Active))]
    private int _currentCycle = 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PomodoroStatusText))]
    private int _targetCycles = 4;

    [ObservableProperty]
    private int _focusMinutes = 25;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PomodoroNextPhaseText))]
    private int _shortBreakMinutes = 5;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PomodoroNextPhaseText))]
    private int _longBreakMinutes = 15;

    [ObservableProperty]
    private bool _autoStartNext = true;

    [ObservableProperty]
    private int _completedTodayCount = 0;

    [ObservableProperty]
    private bool _soundChimesEnabled = true;

    public bool IsCycle1Active => CurrentCycle >= 1;
    public bool IsCycle2Active => CurrentCycle >= 2;
    public bool IsCycle3Active => CurrentCycle >= 3;
    public bool IsCycle4Active => CurrentCycle >= 4;

    public string PomodoroStatusText => $"{PomodoroPhaseLabel} {CurrentCycle} of {TargetCycles}";
    public string PomodoroPhaseLabel => PomodoroPhase switch
    {
        PomodoroPhase.Focus      => "Focus",
        PomodoroPhase.ShortBreak => "Short Break",
        PomodoroPhase.LongBreak  => "Long Break",
        _                        => "Focus"
    };

    public string PomodoroNextPhaseText => PomodoroPhase == PomodoroPhase.Focus
        ? (CurrentCycle >= TargetCycles ? $"Long Break {LongBreakMinutes:D2}:00 next" : $"Break {ShortBreakMinutes:D2}:00 next")
        : $"Focus {FocusMinutes:D2}:00 next";

    public string PomodoroTimeText => $"{Math.Floor(PomodoroRemaining.TotalMinutes):00}:{PomodoroRemaining.Seconds:00}";

    public double PomodoroProgress
    {
        get
        {
            double totalSec = (PomodoroPhase switch
            {
                PomodoroPhase.Focus      => FocusMinutes,
                PomodoroPhase.ShortBreak => ShortBreakMinutes,
                PomodoroPhase.LongBreak  => LongBreakMinutes,
                _                        => FocusMinutes
            }) * 60.0;
            if (totalSec <= 0) return 0.0;
            double elapsed = totalSec - PomodoroRemaining.TotalSeconds;
            return Math.Clamp(elapsed / totalSec, 0.0, 1.0);
        }
    }

    // ── Countdown Mode State ─────────────────────────────────────────────────
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CountdownTimeText))]
    [NotifyPropertyChangedFor(nameof(CountdownProgress))]
    [NotifyPropertyChangedFor(nameof(DialTimeText))]
    [NotifyPropertyChangedFor(nameof(DialProgressFraction))]
    [NotifyPropertyChangedFor(nameof(IslandTimerText))]
    private TimeSpan _countdownRemaining = TimeSpan.FromMinutes(15);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CountdownProgress))]
    private int _countdownMinutes = 15;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAnyTimerRunning))]
    [NotifyPropertyChangedFor(nameof(IslandTimerText))]
    private bool _isCountdownRunning;

    public string CountdownTimeText => $"{Math.Floor(CountdownRemaining.TotalMinutes):00}:{CountdownRemaining.Seconds:00}";

    public double CountdownProgress
    {
        get
        {
            double totalSec = CountdownMinutes * 60.0;
            if (totalSec <= 0) return 0.0;
            double elapsed = totalSec - CountdownRemaining.TotalSeconds;
            return Math.Clamp(elapsed / totalSec, 0.0, 1.0);
        }
    }

    // ── Stopwatch Mode State ─────────────────────────────────────────────────
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StopwatchTimeText))]
    [NotifyPropertyChangedFor(nameof(StopwatchProgress))]
    [NotifyPropertyChangedFor(nameof(DialTimeText))]
    [NotifyPropertyChangedFor(nameof(DialProgressFraction))]
    [NotifyPropertyChangedFor(nameof(IslandTimerText))]
    private TimeSpan _stopwatchElapsed = TimeSpan.Zero;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAnyTimerRunning))]
    [NotifyPropertyChangedFor(nameof(IslandTimerText))]
    private bool _isStopwatchRunning;

    public ObservableCollection<string> Laps { get; } = new();

    public string StopwatchTimeText =>
        $"{(int)Math.Floor(StopwatchElapsed.TotalMinutes):D2}:{StopwatchElapsed.Seconds:D2}.{StopwatchElapsed.Milliseconds / 10:D2}";

    public double StopwatchProgress =>
        (StopwatchElapsed.Seconds + StopwatchElapsed.Milliseconds / 1000.0) / 60.0;

    // ── Water Mode State ─────────────────────────────────────────────────────
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WaterText))]
    [NotifyPropertyChangedFor(nameof(WaterVolumeText))]
    [NotifyPropertyChangedFor(nameof(WaterProgress))]
    [NotifyPropertyChangedFor(nameof(DialTimeText))]
    [NotifyPropertyChangedFor(nameof(DialPhaseLabel))]
    [NotifyPropertyChangedFor(nameof(DialProgressFraction))]
    private int _waterCupsCurrent = 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WaterText))]
    [NotifyPropertyChangedFor(nameof(WaterVolumeText))]
    [NotifyPropertyChangedFor(nameof(WaterProgress))]
    [NotifyPropertyChangedFor(nameof(DialTimeText))]
    [NotifyPropertyChangedFor(nameof(DialPhaseLabel))]
    [NotifyPropertyChangedFor(nameof(DialProgressFraction))]
    private int _waterCupsTarget = 8;

    public string WaterText => $"{WaterCupsCurrent} / {WaterCupsTarget}";
    public string WaterVolumeText => $"{WaterCupsCurrent * 250} ml / {WaterCupsTarget * 250} ml";
    public double WaterProgress => Math.Clamp((double)WaterCupsCurrent / Math.Max(1, WaterCupsTarget), 0.0, 1.0);

    // ── Center Dial Aggregates ───────────────────────────────────────────────
    public string DialTimeText => ActiveMode switch
    {
        TimerMode.Pomodoro  => PomodoroTimeText,
        TimerMode.Countdown => CountdownTimeText,
        TimerMode.Stopwatch => StopwatchTimeText,
        TimerMode.Water     => WaterText,
        _                   => PomodoroTimeText
    };

    public string DialPhaseLabel => ActiveMode switch
    {
        TimerMode.Pomodoro  => PomodoroPhaseLabel,
        TimerMode.Countdown => "Countdown",
        TimerMode.Stopwatch => "Elapsed",
        TimerMode.Water     => $"{WaterCupsCurrent * 250} ml",
        _                   => "Focus"
    };

    public double DialProgressFraction => ActiveMode switch
    {
        TimerMode.Pomodoro  => PomodoroProgress,
        TimerMode.Countdown => CountdownProgress,
        TimerMode.Stopwatch => StopwatchProgress,
        TimerMode.Water     => WaterProgress,
        _                   => 0.0
    };

    // ── Dynamic Island Aggregates ────────────────────────────────────────────
    public bool IsAnyTimerRunning => IsPomodoroRunning || IsCountdownRunning || IsStopwatchRunning;

    public string IslandTimerText
    {
        get
        {
            if (IsPomodoroRunning)  return PomodoroTimeText;
            if (IsCountdownRunning) return CountdownTimeText;
            if (IsStopwatchRunning) return $"{(int)Math.Floor(StopwatchElapsed.TotalMinutes):D2}:{StopwatchElapsed.Seconds:D2}";
            return string.Empty;
        }
    }

    public event Action? TimerActivityChanged;
    private DateTime _lastTickUtc = DateTime.UtcNow;

    public TimerService()
    {
        _ticker = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _ticker.Tick += OnTick;
    }

    private void EnsureTickerRunning()
    {
        if (!IsAnyTimerRunning)
        {
            _ticker.Stop();
            return;
        }

        _ticker.Interval = IsStopwatchRunning
            ? TimeSpan.FromMilliseconds(100)
            : TimeSpan.FromMilliseconds(500);

        if (!_ticker.IsEnabled)
        {
            _lastTickUtc = DateTime.UtcNow;
            _ticker.Start();
        }
    }

    public void Stop() => _ticker.Stop();

    private void OnTick(object? sender, EventArgs e)
    {
        DateTime now = DateTime.UtcNow;
        TimeSpan delta = now - _lastTickUtc;
        _lastTickUtc = now;

        if (delta <= TimeSpan.Zero) delta = TimeSpan.FromMilliseconds(100);
        else if (delta > TimeSpan.FromSeconds(5)) delta = TimeSpan.FromSeconds(5);

        bool changed = false;

        // 1. Pomodoro Tick
        if (IsPomodoroRunning)
        {
            if (PomodoroRemaining > delta)
            {
                PomodoroRemaining -= delta;
            }
            else
            {
                PomodoroRemaining = TimeSpan.Zero;
                HandlePomodoroPhaseComplete();
            }
            changed = true;
        }

        // 2. Countdown Tick
        if (IsCountdownRunning)
        {
            if (CountdownRemaining > delta)
            {
                CountdownRemaining -= delta;
            }
            else
            {
                CountdownRemaining = TimeSpan.Zero;
                IsCountdownRunning = false;
                PlayChime();
            }
            changed = true;
        }

        // 3. Stopwatch Tick
        if (IsStopwatchRunning)
        {
            StopwatchElapsed += delta;
            changed = true;
        }

        if (changed)
        {
            TimerActivityChanged?.Invoke();
        }

        EnsureTickerRunning();
    }

    public void ApplySettings(AppSettings settings)
    {
        int prevFocus = FocusMinutes;
        int prevBreak = ShortBreakMinutes;

        FocusMinutes        = settings.DefaultFocusMinutes;
        ShortBreakMinutes   = settings.DefaultBreakMinutes;
        WaterCupsTarget     = settings.DailyWaterTargetCups;
        SoundChimesEnabled  = settings.TimerSoundChimesEnabled;

        if (PomodoroPhase == PomodoroPhase.Focus && !IsPomodoroRunning &&
            PomodoroRemaining == TimeSpan.FromMinutes(prevFocus))
        {
            PomodoroRemaining = TimeSpan.FromMinutes(FocusMinutes);
        }
        else if (PomodoroPhase == PomodoroPhase.ShortBreak && !IsPomodoroRunning &&
                 PomodoroRemaining == TimeSpan.FromMinutes(prevBreak))
        {
            PomodoroRemaining = TimeSpan.FromMinutes(ShortBreakMinutes);
        }
    }

    [RelayCommand]
    public void ToggleActiveTimer()
    {
        if (IsPomodoroRunning)
        {
            TogglePomodoro();
        }
        else if (IsCountdownRunning)
        {
            ToggleCountdown();
        }
        else if (IsStopwatchRunning)
        {
            ToggleStopwatch();
        }
        else
        {
            // Resume/start current mode
            switch (ActiveMode)
            {
                case TimerMode.Pomodoro:  TogglePomodoro();  break;
                case TimerMode.Countdown: ToggleCountdown(); break;
                case TimerMode.Stopwatch: ToggleStopwatch(); break;
                default:                  TogglePomodoro();  break;
            }
        }
    }

    private void PlayChime()
    {
        if (!SoundChimesEnabled) return;
        try { SystemSounds.Asterisk.Play(); } catch { }
    }

    private void HandlePomodoroPhaseComplete()
    {
        PlayChime();

        if (PomodoroPhase == PomodoroPhase.Focus)
        {
            CompletedTodayCount++;
            if (CurrentCycle >= TargetCycles)
            {
                PomodoroPhase = PomodoroPhase.LongBreak;
                PomodoroRemaining = TimeSpan.FromMinutes(LongBreakMinutes);
            }
            else
            {
                PomodoroPhase = PomodoroPhase.ShortBreak;
                PomodoroRemaining = TimeSpan.FromMinutes(ShortBreakMinutes);
            }
        }
        else // Short or Long Break complete
        {
            if (PomodoroPhase == PomodoroPhase.LongBreak)
            {
                CurrentCycle = 1;
            }
            else
            {
                CurrentCycle = Math.Min(TargetCycles, CurrentCycle + 1);
            }
            PomodoroPhase = PomodoroPhase.Focus;
            PomodoroRemaining = TimeSpan.FromMinutes(FocusMinutes);
        }

        IsPomodoroRunning = AutoStartNext;
        OnPropertyChanged(nameof(PomodoroStatusText));
        OnPropertyChanged(nameof(PomodoroNextPhaseText));
        OnPropertyChanged(nameof(DialPhaseLabel));
    }

    // ── Mode Switch Command ──────────────────────────────────────────────────
    [RelayCommand]
    public void SelectMode(object? mode)
    {
        if (mode is TimerMode tm)
        {
            ActiveMode = tm;
        }
        else if (mode is string s && Enum.TryParse<TimerMode>(s, out var parsed))
        {
            ActiveMode = parsed;
        }
    }

    // ── Pomodoro Commands ────────────────────────────────────────────────────
    [RelayCommand]
    public void TogglePomodoro()
    {
        IsPomodoroRunning = !IsPomodoroRunning;
        EnsureTickerRunning();
        TimerActivityChanged?.Invoke();
    }

    [RelayCommand]
    public void ResetPomodoro()
    {
        IsPomodoroRunning = false;
        PomodoroPhase     = PomodoroPhase.Focus;
        CurrentCycle      = 1;
        PomodoroRemaining = TimeSpan.FromMinutes(FocusMinutes);
        EnsureTickerRunning();
        TimerActivityChanged?.Invoke();
    }

    [RelayCommand]
    public void SkipPomodoro()
    {
        HandlePomodoroPhaseComplete();
        EnsureTickerRunning();
        TimerActivityChanged?.Invoke();
    }

    [RelayCommand]
    public void AdjustFocus(object? delta)
    {
        int d = ParseInt(delta, 5);
        FocusMinutes = Math.Clamp(FocusMinutes + d, 5, 90);
        if (PomodoroPhase == PomodoroPhase.Focus && !IsPomodoroRunning)
        {
            PomodoroRemaining = TimeSpan.FromMinutes(FocusMinutes);
        }
    }

    [RelayCommand]
    public void AdjustShortBreak(object? delta)
    {
        int d = ParseInt(delta, 1);
        ShortBreakMinutes = Math.Clamp(ShortBreakMinutes + d, 1, 30);
        if (PomodoroPhase == PomodoroPhase.ShortBreak && !IsPomodoroRunning)
        {
            PomodoroRemaining = TimeSpan.FromMinutes(ShortBreakMinutes);
        }
    }

    [RelayCommand]
    public void AdjustLongBreak(object? delta)
    {
        int d = ParseInt(delta, 5);
        LongBreakMinutes = Math.Clamp(LongBreakMinutes + d, 5, 45);
        if (PomodoroPhase == PomodoroPhase.LongBreak && !IsPomodoroRunning)
        {
            PomodoroRemaining = TimeSpan.FromMinutes(LongBreakMinutes);
        }
    }

    [RelayCommand]
    public void AdjustTargetCycles(object? delta)
    {
        int d = ParseInt(delta, 1);
        TargetCycles = Math.Clamp(TargetCycles + d, 1, 12);
        CurrentCycle = Math.Min(CurrentCycle, TargetCycles);
    }

    // ── Countdown Commands ───────────────────────────────────────────────────
    [RelayCommand]
    public void ToggleCountdown()
    {
        IsCountdownRunning = !IsCountdownRunning;
        EnsureTickerRunning();
        TimerActivityChanged?.Invoke();
    }

    [RelayCommand]
    public void ResetCountdown()
    {
        IsCountdownRunning = false;
        CountdownRemaining = TimeSpan.FromMinutes(CountdownMinutes);
        EnsureTickerRunning();
        TimerActivityChanged?.Invoke();
    }

    [RelayCommand]
    public void SetCountdownDuration(object? minutes)
    {
        int m = ParseInt(minutes, 15);
        CountdownMinutes   = Math.Clamp(m, 1, 180);
        CountdownRemaining = TimeSpan.FromMinutes(CountdownMinutes);
        IsCountdownRunning = false;
        EnsureTickerRunning();
        TimerActivityChanged?.Invoke();
    }

    [RelayCommand]
    public void AddOneMinuteToCountdown()
    {
        CountdownRemaining += TimeSpan.FromMinutes(1);
    }

    // ── Stopwatch Commands ───────────────────────────────────────────────────
    [RelayCommand]
    public void ToggleStopwatch()
    {
        IsStopwatchRunning = !IsStopwatchRunning;
        EnsureTickerRunning();
        TimerActivityChanged?.Invoke();
    }

    [RelayCommand]
    public void ResetStopwatch()
    {
        IsStopwatchRunning = false;
        StopwatchElapsed   = TimeSpan.Zero;
        Laps.Clear();
        EnsureTickerRunning();
        TimerActivityChanged?.Invoke();
    }

    [RelayCommand]
    public void RecordLap()
    {
        if (StopwatchElapsed > TimeSpan.Zero)
        {
            Laps.Insert(0, $"Lap {Laps.Count + 1}: {StopwatchTimeText}");
        }
    }

    // ── Water Commands ───────────────────────────────────────────────────────
    [RelayCommand]
    public void AddWaterCup()
    {
        WaterCupsCurrent = Math.Clamp(WaterCupsCurrent + 1, 0, 24);
    }

    [RelayCommand]
    public void RemoveWaterCup()
    {
        WaterCupsCurrent = Math.Clamp(WaterCupsCurrent - 1, 0, 24);
    }

    [RelayCommand]
    public void AdjustWaterTarget(object? delta)
    {
        int d = ParseInt(delta, 1);
        WaterCupsTarget = Math.Clamp(WaterCupsTarget + d, 4, 24);
    }

    [RelayCommand]
    public void ResetWater()
    {
        WaterCupsCurrent = 0;
    }

    private static int ParseInt(object? obj, int fallback)
    {
        if (obj is int i) return i;
        if (obj is string s && int.TryParse(s, out int parsed)) return parsed;
        return fallback;
    }
}
