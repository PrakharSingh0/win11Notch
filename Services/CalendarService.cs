using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Win11Notch.Services;

public sealed partial class CalendarItem : ObservableObject
{
    public string   Id       { get; init; } = Guid.NewGuid().ToString("N");
    [ObservableProperty] private string _title       = string.Empty;
    public DateTime Date     { get; set; }  = DateTime.Today;
    [ObservableProperty] private string _timeText    = "09:00";
    [ObservableProperty] private bool   _isAllDay;
    [ObservableProperty] private bool   _isReminder;
    [ObservableProperty] private bool   _isCompleted;
    [ObservableProperty] private string _accentHex   = "#FF5E36";
    public bool Alerted { get; set; }

    public string DisplayTime => IsAllDay ? "All day" : (string.IsNullOrWhiteSpace(TimeText) ? "All day" : TimeText);
}

public sealed partial class CalendarDayCell : ObservableObject
{
    public DateTime Date          { get; init; }
    public int      DayNumber     => Date.Day;
    public bool     IsCurrentMonth { get; init; }
    public bool     IsToday       => Date.Date == DateTime.Today;
    [ObservableProperty] private bool _isSelected;
    public ObservableCollection<string> DotColors { get; } = new();
}

/// <summary>
/// Manages whole-month calendar navigation, events, and Samsung-style checkable reminders.
/// Persists entries cleanly to %AppData%\Win11Notch\calendar.json.
/// </summary>
public sealed partial class CalendarService : ObservableObject
{
    private readonly string _filePath;
    private readonly List<CalendarItem> _allItems = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MonthYearLabel))]
    private DateTime _displayMonth = new(DateTime.Today.Year, DateTime.Today.Month, 1);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedDateHeader))]
    private DateTime _selectedDate = DateTime.Today;

    public string MonthYearLabel     => DisplayMonth.ToString("MMMM yyyy");
    public string SelectedDateHeader => SelectedDate.Date == DateTime.Today
        ? $"Today, {SelectedDate:MMM d}"
        : SelectedDate.ToString("ddd, MMM d");

    public ObservableCollection<CalendarDayCell> MonthDays        { get; } = new();
    public ObservableCollection<CalendarItem>    SelectedDayItems { get; } = new();

    public event Action<CalendarItem>? ReminderDue;

    public CalendarService()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Win11Notch");
        Directory.CreateDirectory(dir);
        _filePath = Path.Combine(dir, "calendar.json");

        Load();
        RebuildMonthGrid();
        RefreshSelectedDayItems();
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

    public void AddItem(string title, string timeText, bool isAllDay, bool isReminder, string accentHex)
    {
        if (string.IsNullOrWhiteSpace(title)) return;

        _allItems.Add(new CalendarItem
        {
            Title      = title.Trim(),
            Date       = SelectedDate.Date,
            TimeText   = isAllDay ? "All day" : (string.IsNullOrWhiteSpace(timeText) ? "09:00" : timeText.Trim()),
            IsAllDay   = isAllDay,
            IsReminder = isReminder,
            AccentHex  = string.IsNullOrWhiteSpace(accentHex) ? "#FF5E36" : accentHex
        });

        Save();
        RebuildMonthGrid();
        RefreshSelectedDayItems();
    }

    public void ToggleComplete(CalendarItem item)
    {
        item.IsCompleted = !item.IsCompleted;
        Save();
        RefreshSelectedDayItems();
    }

    public void RemoveItem(CalendarItem item)
    {
        if (_allItems.Remove(item))
        {
            Save();
            RebuildMonthGrid();
            RefreshSelectedDayItems();
        }
    }

    public void CheckDueReminders(DateTime now)
    {
        string hhmm = now.ToString("HH:mm");
        foreach (var item in _allItems)
        {
            if (item.IsReminder && !item.IsCompleted && !item.Alerted && !item.IsAllDay &&
                item.Date.Date == now.Date && string.Equals(item.TimeText, hhmm, StringComparison.Ordinal))
            {
                item.Alerted = true;
                ReminderDue?.Invoke(item);
            }
        }
    }

    private void RebuildMonthGrid()
    {
        MonthDays.Clear();
        var firstOfMonth = new DateTime(DisplayMonth.Year, DisplayMonth.Month, 1);
        int offset = (int)firstOfMonth.DayOfWeek; // Sunday = 0
        var startDate = firstOfMonth.AddDays(-offset);

        for (int i = 0; i < 42; i++)
        {
            var d = startDate.AddDays(i);
            var cell = new CalendarDayCell
            {
                Date           = d,
                IsCurrentMonth = d.Month == DisplayMonth.Month && d.Year == DisplayMonth.Year,
                IsSelected     = d.Date == SelectedDate.Date
            };

            foreach (var ev in _allItems.Where(x => x.Date.Date == d.Date).Take(3))
            {
                cell.DotColors.Add(ev.AccentHex);
            }

            MonthDays.Add(cell);
        }
    }

    private void RefreshSelectedDayItems()
    {
        SelectedDayItems.Clear();
        var items = _allItems
            .Where(x => x.Date.Date == SelectedDate.Date)
            .OrderBy(x => x.IsCompleted)
            .ThenByDescending(x => x.IsAllDay)
            .ThenBy(x => x.TimeText);

        foreach (var it in items)
        {
            SelectedDayItems.Add(it);
        }
    }

    private sealed record CalendarDto(
        string Id, string Title, DateTime Date, string TimeText,
        bool IsAllDay, bool IsReminder, bool IsCompleted, string AccentHex);

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
                _allItems.Add(new CalendarItem
                {
                    Id          = dto.Id,
                    Title       = dto.Title,
                    Date        = dto.Date.Date,
                    TimeText    = dto.TimeText,
                    IsAllDay    = dto.IsAllDay,
                    IsReminder  = dto.IsReminder,
                    IsCompleted = dto.IsCompleted,
                    AccentHex   = string.IsNullOrWhiteSpace(dto.AccentHex) ? "#FF5E36" : dto.AccentHex
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
                x.Id, x.Title, x.Date, x.TimeText, x.IsAllDay, x.IsReminder, x.IsCompleted, x.AccentHex)).ToList();
            File.WriteAllText(_filePath, JsonSerializer.Serialize(list));
        }
        catch { /* best effort */ }
    }
}
