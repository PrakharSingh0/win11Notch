using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Win11Notch.Services;

public enum CalendarRepeatRule
{
    Once     = 0,
    Daily    = 1,
    Weekdays = 2,
    Weekly   = 3
}

public enum CalendarAgendaFilter
{
    All       = 0,
    Reminders = 1,
    Events    = 2
}

public sealed partial class CalendarItem : ObservableObject
{
    public string   Id   { get; init; } = Guid.NewGuid().ToString("N");
    [ObservableProperty] private string _title = string.Empty;
    public DateTime Date { get; set; }  = DateTime.Today;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayTime))]
    [NotifyPropertyChangedFor(nameof(CompactTimeText))]
    private string _timeText = "09:00";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayTime))]
    [NotifyPropertyChangedFor(nameof(CompactTimeText))]
    private bool _isAllDay;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEvent))]
    [NotifyPropertyChangedFor(nameof(DisplayTime))]
    [NotifyPropertyChangedFor(nameof(TypeTagText))]
    [NotifyPropertyChangedFor(nameof(HasLocation))]
    [NotifyPropertyChangedFor(nameof(HasRepeat))]
    private bool _isReminder;

    [ObservableProperty] private bool   _isCompleted;
    [ObservableProperty] private string _accentHex = "#FF5E36";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayTime))]
    [NotifyPropertyChangedFor(nameof(TypeTagText))]
    private int _durationMinutes = 60;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TypeTagText))]
    [NotifyPropertyChangedFor(nameof(HasLocation))]
    private string _location = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRepeat))]
    [NotifyPropertyChangedFor(nameof(RepeatLabel))]
    [NotifyPropertyChangedFor(nameof(TypeTagText))]
    private CalendarRepeatRule _repeatRule = CalendarRepeatRule.Once;

    [ObservableProperty] private bool _isHighPriority;
    [ObservableProperty] private bool _playChime = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayTime))]
    [NotifyPropertyChangedFor(nameof(CompactTimeText))]
    [NotifyPropertyChangedFor(nameof(IsSnoozed))]
    private DateTime? _snoozedUntil;

    [ObservableProperty] private bool _isAlertDismissed;
    public bool Alerted { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayTime))]
    [NotifyPropertyChangedFor(nameof(CompactTimeText))]
    private bool _use24HourFormat;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRelativeStatus))]
    private string _relativeStatusText = string.Empty;

    [ObservableProperty] private bool   _isOverdue;
    [ObservableProperty] private bool   _isHappeningNow;
    [ObservableProperty] private string _statusBadgeBrushHex = "#8E9098";

    public bool IsEvent           => !IsReminder;
    public bool IsSnoozed         => SnoozedUntil.HasValue && SnoozedUntil.Value > DateTime.Now;
    public bool HasLocation       => !IsReminder && !string.IsNullOrWhiteSpace(Location);
    public bool HasRepeat         => IsReminder && RepeatRule != CalendarRepeatRule.Once;
    public bool HasRelativeStatus => !string.IsNullOrEmpty(RelativeStatusText);

    public string RepeatLabel => RepeatRule switch
    {
        CalendarRepeatRule.Daily    => "Daily",
        CalendarRepeatRule.Weekdays => "Weekdays",
        CalendarRepeatRule.Weekly   => "Weekly",
        _                           => string.Empty
    };

    public string CompactTimeText
    {
        get
        {
            if (IsAllDay) return "All day";
            if (IsReminder && SnoozedUntil is { } snooze)
            {
                return CalendarService.FormatTimeOfDay(snooze.TimeOfDay, Use24HourFormat);
            }
            return CalendarService.TryParseTime(TimeText, out var tod)
                ? CalendarService.FormatTimeOfDay(tod, Use24HourFormat)
                : (string.IsNullOrWhiteSpace(TimeText) ? "All day" : TimeText);
        }
    }

    public string DisplayTime
    {
        get
        {
            if (IsAllDay) return "All day";
            if (!CalendarService.TryParseTime(TimeText, out var tod))
            {
                return string.IsNullOrWhiteSpace(TimeText) ? "All day" : TimeText;
            }

            string startStr = CalendarService.FormatTimeOfDay(tod, Use24HourFormat);
            if (!IsReminder && DurationMinutes > 0)
            {
                var endTod = Date.Date.Add(tod).AddMinutes(DurationMinutes).TimeOfDay;
                return $"{startStr} – {CalendarService.FormatTimeOfDay(endTod, Use24HourFormat)}";
            }

            if (IsReminder && SnoozedUntil is { } snooze && snooze > DateTime.Now)
            {
                return $"{startStr} (Snoozed → {CalendarService.FormatTimeOfDay(snooze.TimeOfDay, Use24HourFormat)})";
            }

            return startStr;
        }
    }

    public string TypeTagText
    {
        get
        {
            if (IsReminder)
            {
                return HasRepeat ? $"Reminder • ↻ {RepeatLabel}" : "Reminder";
            }
            if (!string.IsNullOrWhiteSpace(Location))
            {
                return $"Event • {Location.Trim()}";
            }
            return DurationMinutes > 0 ? $"Event • {FormatDuration(DurationMinutes)}" : "Event";
        }
    }

    public bool TryGetTimeOfDay(out TimeSpan tod) => CalendarService.TryParseTime(TimeText, out tod);

    public DateTime? GetEffectiveDueDateTime()
    {
        if (IsAllDay || !TryGetTimeOfDay(out var tod)) return null;
        if (SnoozedUntil.HasValue) return SnoozedUntil.Value;
        return Date.Date + tod;
    }

    internal static string FormatDuration(int minutes)
    {
        if (minutes < 60) return $"{minutes}m";
        int h = minutes / 60;
        int m = minutes % 60;
        return m == 0 ? $"{h}h" : $"{h}h {m}m";
    }
}

public sealed partial class CalendarDayCell : ObservableObject
{
    public DateTime Date           { get; init; }
    public int      DayNumber      => Date.Day;
    public bool     IsCurrentMonth { get; init; }
    public bool     IsToday        => Date.Date == DateTime.Today;
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _hasOverdueReminder;

    public ObservableCollection<string> DotColors         { get; } = new();
    public ObservableCollection<string> EventDotColors    { get; } = new();
    public ObservableCollection<string> ReminderDotColors { get; } = new();

    public bool HasEventDots    => EventDotColors.Count > 0;
    public bool HasReminderDots => ReminderDotColors.Count > 0;
}

/// <summary>
/// Manages whole-month calendar navigation, distinct Events &amp; Reminders,
/// exact single-shot due scheduling, snooze/dismiss persistence, and recurring rules.
/// Persists entries cleanly to %AppData%\Win11Notch\calendar.json.
/// </summary>
public sealed partial class CalendarService : ObservableObject
{
    private static readonly string[] TimeParseFormats =
    {
        "h:mm tt", "h:mmtt", "hh:mm tt", "hh:mmtt",
        "H:mm", "HH:mm", "H:m", "HHmm",
        "h tt", "htt", "H"
    };

    private readonly string _filePath;
    private readonly List<CalendarItem> _allItems = new();
    private bool _use24HourFormat;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MonthYearLabel))]
    private DateTime _displayMonth = new(DateTime.Today.Year, DateTime.Today.Month, 1);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedDateHeader))]
    private DateTime _selectedDate = DateTime.Today;

    [ObservableProperty]
    private CalendarAgendaFilter _agendaFilter = CalendarAgendaFilter.All;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOverdueToday))]
    [NotifyPropertyChangedFor(nameof(OverdueBannerText))]
    private int _overdueTodayCount;

    [ObservableProperty] private string _agendaSummaryText = "No items scheduled";

    public bool   HasOverdueToday   => OverdueTodayCount > 0;
    public string OverdueBannerText => OverdueTodayCount == 1
        ? "1 overdue reminder today"
        : $"{OverdueTodayCount} overdue reminders today";

    public string MonthYearLabel     => DisplayMonth.ToString("MMMM yyyy");
    public string SelectedDateHeader => SelectedDate.Date == DateTime.Today
        ? $"Today, {SelectedDate:MMM d}"
        : SelectedDate.ToString("ddd, MMM d");

    public ObservableCollection<CalendarDayCell> MonthDays        { get; } = new();
    public ObservableCollection<CalendarItem>    SelectedDayItems { get; } = new();

    public event Action<CalendarItem>? ReminderDue;
    public event Action?               CalendarChanged;

    public CalendarService(bool use24HourFormat = false)
    {
        _use24HourFormat = use24HourFormat;
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Win11Notch");
        Directory.CreateDirectory(dir);
        _filePath = Path.Combine(dir, "calendar.json");

        Load();
        RefreshDynamicStatuses(DateTime.Now);
        RebuildMonthGrid();
        RefreshSelectedDayItems();
    }

    partial void OnAgendaFilterChanged(CalendarAgendaFilter value) => RefreshSelectedDayItems();

    public void ApplyClockFormat(bool use24HourFormat)
    {
        if (_use24HourFormat == use24HourFormat) return;
        _use24HourFormat = use24HourFormat;
        foreach (var item in _allItems)
        {
            item.Use24HourFormat = use24HourFormat;
        }
        CalendarChanged?.Invoke();
    }

    public static bool TryParseTime(string? input, out TimeSpan result)
    {
        result = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(input)) return false;

        string trimmed = input.Trim();
        if (DateTime.TryParseExact(trimmed, TimeParseFormats, CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces, out var dt))
        {
            result = dt.TimeOfDay;
            return true;
        }

        if (DateTime.TryParse(trimmed, CultureInfo.CurrentCulture, DateTimeStyles.NoCurrentDateDefault, out dt))
        {
            result = dt.TimeOfDay;
            return true;
        }

        return TimeSpan.TryParse(trimmed, CultureInfo.InvariantCulture, out result);
    }

    public static string NormalizeTimeText(string? input)
    {
        return TryParseTime(input, out var tod)
            ? DateTime.Today.Add(tod).ToString("HH:mm", CultureInfo.InvariantCulture)
            : "09:00";
    }

    public static string FormatTimeOfDay(TimeSpan tod, bool use24Hour)
    {
        var dt = DateTime.Today.Add(tod);
        return dt.ToString(use24Hour ? "HH:mm" : "h:mm tt", CultureInfo.InvariantCulture);
    }

    public void PreviousMonth()
    {
        DisplayMonth = DisplayMonth.AddMonths(-1);
        RebuildMonthGrid();
    }

    public void NextMonth()
    {
        DisplayMonth = DisplayMonth.AddMonths(1);
        RebuildMonthGrid();
    }

    public void GoToToday()
    {
        DisplayMonth = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        SelectedDate = DateTime.Today;
        RebuildMonthGrid();
        RefreshSelectedDayItems();
    }

    public void SelectDate(DateTime date)
    {
        SelectedDate = date.Date;
        if (date.Year != DisplayMonth.Year || date.Month != DisplayMonth.Month)
        {
            DisplayMonth = new DateTime(date.Year, date.Month, 1);
            RebuildMonthGrid();
        }
        else
        {
            foreach (var cell in MonthDays)
            {
                cell.IsSelected = cell.Date.Date == SelectedDate;
            }
        }
        RefreshSelectedDayItems();
    }

    public CalendarItem? AddItem(
        string title,
        string timeText,
        bool isAllDay,
        bool isReminder,
        string accentHex,
        int durationMinutes = 60,
        string location = "",
        CalendarRepeatRule repeatRule = CalendarRepeatRule.Once,
        bool isHighPriority = false,
        bool playChime = true,
        DateTime? targetDate = null)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;

        DateTime date = (targetDate ?? SelectedDate).Date;
        string canonicalTime = isAllDay ? "All day" : NormalizeTimeText(timeText);

        var created = new CalendarItem
        {
            Title           = title.Trim(),
            Date            = date,
            TimeText        = canonicalTime,
            IsAllDay        = isAllDay,
            IsReminder      = isReminder,
            AccentHex       = string.IsNullOrWhiteSpace(accentHex) ? (isReminder ? "#FF5E36" : "#00E5FF") : accentHex,
            DurationMinutes = isReminder ? 0 : Math.Max(15, durationMinutes),
            Location        = isReminder ? string.Empty : location.Trim(),
            RepeatRule      = isReminder ? repeatRule : CalendarRepeatRule.Once,
            IsHighPriority  = isReminder && isHighPriority,
            PlayChime       = !isReminder || playChime,
            Use24HourFormat = _use24HourFormat
        };

        _allItems.Add(created);
        SaveAndNotify(DateTime.Now);
        return created;
    }

    public void UpdateItem(
        CalendarItem item,
        string title,
        string timeText,
        bool isAllDay,
        bool isReminder,
        string accentHex,
        int durationMinutes,
        string location,
        CalendarRepeatRule repeatRule,
        bool isHighPriority,
        bool playChime)
    {
        if (string.IsNullOrWhiteSpace(title)) return;

        string newCanonicalTime = isAllDay ? "All day" : NormalizeTimeText(timeText);
        bool timeChanged = !string.Equals(item.TimeText, newCanonicalTime, StringComparison.Ordinal) || item.IsAllDay != isAllDay;

        item.Title           = title.Trim();
        item.TimeText        = newCanonicalTime;
        item.IsAllDay        = isAllDay;
        item.IsReminder      = isReminder;
        item.AccentHex       = string.IsNullOrWhiteSpace(accentHex) ? "#FF5E36" : accentHex;
        item.DurationMinutes = isReminder ? 0 : Math.Max(15, durationMinutes);
        item.Location        = isReminder ? string.Empty : location.Trim();
        item.RepeatRule      = isReminder ? repeatRule : CalendarRepeatRule.Once;
        item.IsHighPriority  = isReminder && isHighPriority;
        item.PlayChime       = !isReminder || playChime;

        if (timeChanged)
        {
            item.SnoozedUntil     = null;
            item.IsAlertDismissed = false;
            item.Alerted          = false;
        }

        SaveAndNotify(DateTime.Now);
    }

    public void ToggleComplete(CalendarItem item)
    {
        bool becomingComplete = !item.IsCompleted;
        item.IsCompleted = becomingComplete;

        if (becomingComplete)
        {
            item.IsAlertDismissed = true;
            item.SnoozedUntil     = null;
            MaybeSpawnNextRecurrence(item);
        }
        else
        {
            item.IsAlertDismissed = false;
            item.Alerted          = false;
        }

        SaveAndNotify(DateTime.Now);
    }

    public void SnoozeReminder(CalendarItem item, TimeSpan duration)
    {
        DateTime target = DateTime.Now + duration;
        item.SnoozedUntil     = target;
        item.IsAlertDismissed = false;
        item.Alerted          = false;
        SaveAndNotify(DateTime.Now);
    }

    public void SnoozeAllOverdueToday(TimeSpan duration)
    {
        DateTime now = DateTime.Now;
        DateTime target = now + duration;
        bool changed = false;

        foreach (var item in _allItems)
        {
            if (IsDueTodayReminder(item, now))
            {
                item.SnoozedUntil     = target;
                item.IsAlertDismissed = false;
                item.Alerted          = false;
                changed = true;
            }
        }

        if (changed) SaveAndNotify(now);
    }

    public void DismissReminderAlert(CalendarItem item)
    {
        item.IsAlertDismissed = true;
        item.Alerted          = true;
        item.SnoozedUntil     = null;
        SaveAndNotify(DateTime.Now);
    }

    public void RemoveItem(CalendarItem item)
    {
        if (_allItems.Remove(item))
        {
            SaveAndNotify(DateTime.Now);
        }
    }

    public IReadOnlyList<CalendarItem> CheckDueReminders(DateTime now)
    {
        var newlyTriggered = new List<CalendarItem>();
        foreach (var item in _allItems)
        {
            if (!item.IsReminder || item.IsCompleted || item.IsAllDay || item.IsAlertDismissed || item.Alerted)
                continue;

            var dueAt = item.GetEffectiveDueDateTime();
            if (!dueAt.HasValue) continue;

            // Fire if due timestamp falls within today (including slightly missed frames / sleep recovery today)
            if (dueAt.Value <= now && dueAt.Value.Date == now.Date)
            {
                item.Alerted = true;
                newlyTriggered.Add(item);
                ReminderDue?.Invoke(item);
            }
        }

        if (newlyTriggered.Count > 0)
        {
            RefreshDynamicStatuses(now);
            RebuildMonthGrid();
            RefreshSelectedDayItems();
        }

        return newlyTriggered;
    }

    /// <summary>
    /// Returns all currently active (due/overdue today, uncompleted, undismissed) reminders
    /// to display persistently on the Notch Pill until acted upon.
    /// </summary>
    public List<CalendarItem> GetActiveDueReminders(DateTime now)
    {
        return _allItems
            .Where(i => IsDueTodayReminder(i, now))
            .OrderByDescending(i => i.IsHighPriority)
            .ThenBy(i => i.GetEffectiveDueDateTime() ?? DateTime.MaxValue)
            .ToList();
    }

    /// <summary>
    /// Computes the next exact future trigger timestamp across all pending reminders so the
    /// ViewModel can arm one zero-polling single-shot DispatcherTimer.
    /// </summary>
    public DateTime? GetNextScheduledTrigger(DateTime now)
    {
        DateTime? earliest = null;
        foreach (var item in _allItems)
        {
            if (!item.IsReminder || item.IsCompleted || item.IsAllDay || item.IsAlertDismissed)
                continue;

            var dueAt = item.GetEffectiveDueDateTime();
            if (!dueAt.HasValue) continue;

            // Skip past reminders from previous days unless explicitly snoozed into the future
            if (dueAt.Value <= now) continue;

            if (!earliest.HasValue || dueAt.Value < earliest.Value)
            {
                earliest = dueAt.Value;
            }
        }
        return earliest;
    }

    /// <summary>
    /// Returns the next uncompleted reminder or timed event due later today (or happening now) for glance display.
    /// </summary>
    public CalendarItem? GetNextTodayReminder(DateTime now)
    {
        return _allItems
            .Where(i => !i.IsCompleted && i.Date.Date == now.Date &&
                        (i.IsAllDay || IsUpcomingOrCurrentToday(i, now)))
            .OrderBy(i => i.IsAllDay ? 1 : 0)
            .ThenBy(i => i.GetEffectiveDueDateTime() ?? DateTime.MaxValue)
            .FirstOrDefault();
    }

    /// <summary>
    /// Returns today's active/upcoming items (including overdue reminders first, then live/upcoming
    /// items and all-day entries) capped for the compact Home dashboard preview.
    /// </summary>
    public List<CalendarItem> GetTodayUpcomingItems(DateTime now, int maxCount = 2)
    {
        return _allItems
            .Where(i => !i.IsCompleted && i.Date.Date == now.Date &&
                        (IsDueTodayReminder(i, now) || i.IsAllDay || IsUpcomingOrCurrentToday(i, now)))
            .OrderByDescending(i => i.IsOverdue)
            .ThenByDescending(i => i.IsHappeningNow)
            .ThenBy(i => i.IsAllDay ? 1 : 0)
            .ThenBy(i => i.GetEffectiveDueDateTime() ?? DateTime.MaxValue)
            .Take(Math.Max(1, maxCount))
            .ToList();
    }

    public (int EventsCount, int RemindersCount) GetTodayCounts(DateTime now)
    {
        int ev = _allItems.Count(i => !i.IsCompleted && i.Date.Date == now.Date && !i.IsReminder);
        int rm = _allItems.Count(i => !i.IsCompleted && i.Date.Date == now.Date && i.IsReminder);
        return (ev, rm);
    }

    public void RefreshDynamicStatuses(DateTime now)
    {
        int overdueCount = 0;

        foreach (var item in _allItems)
        {
            item.IsOverdue           = false;
            item.IsHappeningNow      = false;
            item.RelativeStatusText  = string.Empty;
            item.StatusBadgeBrushHex = "#8E9098";

            if (item.IsCompleted || item.IsAllDay || !item.TryGetTimeOfDay(out var tod))
                continue;

            var start = item.SnoozedUntil ?? (item.Date.Date + tod);

            if (item.IsReminder)
            {
                if (start <= now && start.Date == now.Date)
                {
                    overdueCount++;
                    item.IsOverdue = true;
                    var late = now - start;
                    item.RelativeStatusText  = late.TotalMinutes >= 1 ? $"Overdue {FormatShortSpan(late)}" : "Due now";
                    item.StatusBadgeBrushHex = "#FF6B57";
                }
                else if (start > now && start - now <= TimeSpan.FromHours(12))
                {
                    item.RelativeStatusText  = $"In {FormatShortSpan(start - now)}";
                    item.StatusBadgeBrushHex = item.SnoozedUntil.HasValue ? "#FFD54F" : "#9AA0AA";
                }
            }
            else
            {
                var end = start.AddMinutes(Math.Max(15, item.DurationMinutes));
                if (now >= start && now < end)
                {
                    item.IsHappeningNow      = true;
                    item.RelativeStatusText  = $"Now • {FormatShortSpan(end - now)} left";
                    item.StatusBadgeBrushHex = "#69F0AE";
                }
                else if (start > now && start - now <= TimeSpan.FromHours(12))
                {
                    item.RelativeStatusText  = $"In {FormatShortSpan(start - now)}";
                    item.StatusBadgeBrushHex = "#00E5FF";
                }
            }
        }

        OverdueTodayCount = overdueCount;
    }

    private void SaveAndNotify(DateTime now)
    {
        RefreshDynamicStatuses(now);
        Save();
        RebuildMonthGrid();
        RefreshSelectedDayItems();
        CalendarChanged?.Invoke();
    }

    private void MaybeSpawnNextRecurrence(CalendarItem completedItem)
    {
        if (!completedItem.IsReminder || completedItem.RepeatRule == CalendarRepeatRule.Once)
            return;

        DateTime nextDate = completedItem.RepeatRule switch
        {
            CalendarRepeatRule.Daily    => completedItem.Date.Date.AddDays(1),
            CalendarRepeatRule.Weekly   => completedItem.Date.Date.AddDays(7),
            CalendarRepeatRule.Weekdays => NextWeekday(completedItem.Date.Date),
            _                           => completedItem.Date.Date.AddDays(1)
        };

        bool alreadyExists = _allItems.Any(x =>
            x.Date.Date == nextDate &&
            x.IsReminder &&
            string.Equals(x.Title, completedItem.Title, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.TimeText, completedItem.TimeText, StringComparison.Ordinal));

        if (alreadyExists) return;

        _allItems.Add(new CalendarItem
        {
            Title           = completedItem.Title,
            Date            = nextDate,
            TimeText        = completedItem.TimeText,
            IsAllDay        = completedItem.IsAllDay,
            IsReminder      = true,
            AccentHex       = completedItem.AccentHex,
            RepeatRule      = completedItem.RepeatRule,
            IsHighPriority  = completedItem.IsHighPriority,
            PlayChime       = completedItem.PlayChime,
            Use24HourFormat = _use24HourFormat
        });
    }

    private static DateTime NextWeekday(DateTime date)
    {
        DateTime next = date.AddDays(1);
        while (next.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
        {
            next = next.AddDays(1);
        }
        return next;
    }

    private static bool IsDueTodayReminder(CalendarItem item, DateTime now)
    {
        if (!item.IsReminder || item.IsCompleted || item.IsAllDay || item.IsAlertDismissed)
            return false;

        var dueAt = item.GetEffectiveDueDateTime();
        return dueAt.HasValue && dueAt.Value <= now && dueAt.Value.Date == now.Date;
    }

    private static bool IsUpcomingOrCurrentToday(CalendarItem item, DateTime now)
    {
        if (!item.TryGetTimeOfDay(out var tod)) return false;
        var start = item.SnoozedUntil ?? (item.Date.Date + tod);
        if (item.IsReminder) return start >= now;
        var end = start.AddMinutes(Math.Max(15, item.DurationMinutes));
        return end > now;
    }

    private static string FormatShortSpan(TimeSpan span)
    {
        int totalMin = Math.Max(1, (int)Math.Ceiling(span.TotalMinutes));
        if (totalMin < 60) return $"{totalMin}m";
        int h = totalMin / 60;
        int m = totalMin % 60;
        return m == 0 ? $"{h}h" : $"{h}h {m}m";
    }

    private void RebuildMonthGrid()
    {
        MonthDays.Clear();
        var firstOfMonth = new DateTime(DisplayMonth.Year, DisplayMonth.Month, 1);
        int offset = (int)firstOfMonth.DayOfWeek; // Sunday = 0
        var startDate = firstOfMonth.AddDays(-offset);
        DateTime now = DateTime.Now;

        for (int i = 0; i < 42; i++)
        {
            var d = startDate.AddDays(i);
            var dayItems = _allItems.Where(x => x.Date.Date == d.Date).ToList();

            var cell = new CalendarDayCell
            {
                Date               = d,
                IsCurrentMonth     = d.Month == DisplayMonth.Month && d.Year == DisplayMonth.Year,
                IsSelected         = d.Date == SelectedDate.Date,
                HasOverdueReminder = dayItems.Any(x => IsDueTodayReminder(x, now))
            };

            foreach (var ev in dayItems.Where(x => !x.IsReminder).Take(3))
            {
                cell.EventDotColors.Add(ev.AccentHex);
                cell.DotColors.Add(ev.AccentHex);
            }
            foreach (var rem in dayItems.Where(x => x.IsReminder && !x.IsCompleted).Take(3))
            {
                cell.ReminderDotColors.Add(rem.AccentHex);
                if (cell.DotColors.Count < 4) cell.DotColors.Add(rem.AccentHex);
            }

            MonthDays.Add(cell);
        }
    }

    private void RefreshSelectedDayItems()
    {
        SelectedDayItems.Clear();
        var dayItems = _allItems.Where(x => x.Date.Date == SelectedDate.Date).ToList();

        int evCount  = dayItems.Count(x => !x.IsReminder);
        int remCount = dayItems.Count(x => x.IsReminder && !x.IsCompleted);
        if (dayItems.Count == 0)
        {
            AgendaSummaryText = "No events or reminders";
        }
        else
        {
            var parts = new List<string>();
            if (evCount > 0) parts.Add(evCount == 1 ? "1 event" : $"{evCount} events");
            if (remCount > 0) parts.Add(remCount == 1 ? "1 active reminder" : $"{remCount} reminders");
            AgendaSummaryText = parts.Count > 0 ? string.Join(" • ", parts) : $"{dayItems.Count} completed";
        }

        IEnumerable<CalendarItem> filtered = AgendaFilter switch
        {
            CalendarAgendaFilter.Reminders => dayItems.Where(x => x.IsReminder),
            CalendarAgendaFilter.Events    => dayItems.Where(x => !x.IsReminder),
            _                              => dayItems
        };

        var ordered = filtered
            .OrderBy(x => x.IsCompleted)
            .ThenByDescending(x => x.IsOverdue)
            .ThenByDescending(x => x.IsHappeningNow)
            .ThenByDescending(x => x.IsAllDay)
            .ThenBy(x => x.TimeText, StringComparer.Ordinal);

        foreach (var it in ordered)
        {
            SelectedDayItems.Add(it);
        }
    }

    private sealed record CalendarDto(
        string Id,
        string Title,
        DateTime Date,
        string TimeText,
        bool IsAllDay,
        bool IsReminder,
        bool IsCompleted,
        string AccentHex,
        int DurationMinutes = 60,
        string? Location = null,
        CalendarRepeatRule RepeatRule = CalendarRepeatRule.Once,
        bool IsHighPriority = false,
        bool PlayChime = true,
        DateTime? SnoozedUntil = null,
        bool IsAlertDismissed = false);

    private void Load()
    {
        try
        {
            if (!File.Exists(_filePath)) return;
            var list = JsonSerializer.Deserialize<List<CalendarDto>>(File.ReadAllText(_filePath));
            if (list == null) return;
            _allItems.Clear();
            foreach (var dto in list)
            {
                bool allDay = dto.IsAllDay || string.Equals(dto.TimeText, "All day", StringComparison.OrdinalIgnoreCase);
                string canonical = allDay ? "All day" : NormalizeTimeText(dto.TimeText);
                _allItems.Add(new CalendarItem
                {
                    Id               = string.IsNullOrWhiteSpace(dto.Id) ? Guid.NewGuid().ToString("N") : dto.Id,
                    Title            = dto.Title ?? string.Empty,
                    Date             = dto.Date.Date,
                    TimeText         = canonical,
                    IsAllDay         = allDay,
                    IsReminder       = dto.IsReminder,
                    IsCompleted      = dto.IsCompleted,
                    AccentHex        = string.IsNullOrWhiteSpace(dto.AccentHex) ? (dto.IsReminder ? "#FF5E36" : "#00E5FF") : dto.AccentHex,
                    DurationMinutes  = dto.IsReminder ? 0 : (dto.DurationMinutes > 0 ? dto.DurationMinutes : 60),
                    Location         = dto.Location ?? string.Empty,
                    RepeatRule       = dto.RepeatRule,
                    IsHighPriority   = dto.IsHighPriority,
                    PlayChime        = dto.PlayChime,
                    SnoozedUntil     = dto.SnoozedUntil,
                    IsAlertDismissed = dto.IsAlertDismissed,
                    Use24HourFormat  = _use24HourFormat
                });
            }
        }
        catch { /* best effort */ }
    }

    private void Save()
    {
        try
        {
            var list = _allItems.Select(x => new CalendarDto(
                x.Id,
                x.Title,
                x.Date,
                x.TimeText,
                x.IsAllDay,
                x.IsReminder,
                x.IsCompleted,
                x.AccentHex,
                x.DurationMinutes,
                x.Location,
                x.RepeatRule,
                x.IsHighPriority,
                x.PlayChime,
                x.SnoozedUntil,
                x.IsAlertDismissed)).ToList();
            File.WriteAllText(_filePath, JsonSerializer.Serialize(list));
        }
        catch { /* best effort */ }
    }
}
