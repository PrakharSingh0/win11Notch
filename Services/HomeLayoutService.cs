using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Win11Notch.Services;

public enum HomeWidgetKind
{
    DateClock,
    MusicPlayer,
    UpcomingAgenda,
    PowerAndAudio,
    SystemResources,
    TimersAndWater,
    ShelfAndClipboard,
    ToolsAndBrightness,
    QuickActions
}

public sealed partial class HomeSlotItem : ObservableObject
{
    public string         Id         { get; init; } = Guid.NewGuid().ToString("N");
    public HomeWidgetKind WidgetKind { get; init; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCompactWidth))]
    [NotifyPropertyChangedFor(nameof(IsWideWidth))]
    [NotifyPropertyChangedFor(nameof(IsExtraWideWidth))]
    [NotifyPropertyChangedFor(nameof(SizeLabel))]
    private int _colSpan = 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTallHeight))]
    [NotifyPropertyChangedFor(nameof(SizeLabel))]
    private int _rowSpan = 1;

    [ObservableProperty] private int  _gridRow;
    [ObservableProperty] private int  _gridColumn;
    [ObservableProperty] private int  _order;
    [ObservableProperty] private bool _isPreviewResizing;

    public bool   IsCompactWidth   => ColSpan == 1;
    public bool   IsWideWidth      => ColSpan >= 2;
    public bool   IsExtraWideWidth => ColSpan >= 3;
    public bool   IsTallHeight     => RowSpan >= 2;
    public string SizeLabel        => $"{ColSpan}×{RowSpan}";

    public string DisplayName => WidgetKind switch
    {
        HomeWidgetKind.DateClock          => "Date & Clock",
        HomeWidgetKind.MusicPlayer        => "Now Playing",
        HomeWidgetKind.UpcomingAgenda     => "Up Next Agenda",
        HomeWidgetKind.PowerAndAudio      => "Battery & Audio",
        HomeWidgetKind.SystemResources    => "System Resources",
        HomeWidgetKind.TimersAndWater     => "Timers & Water",
        HomeWidgetKind.ShelfAndClipboard  => "Shelf & Clips",
        HomeWidgetKind.ToolsAndBrightness => "Brightness & Notes",
        HomeWidgetKind.QuickActions       => "Quick Actions",
        _                                 => WidgetKind.ToString()
    };
}

public sealed partial class HomeWidgetCatalogItem : ObservableObject
{
    public HomeWidgetKind Kind           { get; init; }
    public string         Title          { get; init; } = string.Empty;
    public string         Subtitle       { get; init; } = string.Empty;
    public string         IconGlyph      { get; init; } = "\uE80F";
    public string         AccentHex      { get; init; } = "#FF5E36";
    public int            DefaultColSpan { get; init; } = 1;
    public int            DefaultRowSpan { get; init; } = 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusBadgeText))]
    [NotifyPropertyChangedFor(nameof(ActionButtonText))]
    private int _activeCount;

    public bool   IsAdded          => ActiveCount > 0;
    public string StatusBadgeText  => ActiveCount switch
    {
        0 => $"{DefaultColSpan}×{DefaultRowSpan}",
        1 => "Active",
        _ => $"{ActiveCount}× Active"
    };
    public string ActionButtonText => ActiveCount > 0 ? "+ Another" : "+ Add";
}

/// <summary>
/// Manages editable, resizable Home tab Bento widget slots across a 4-column collision-free
/// auto-packed grid and persists custom layouts to %AppData%\Win11Notch\home-layout.json.
/// </summary>
public sealed partial class HomeLayoutService : ObservableObject
{
    public const int GridColumns = 4;
    public const int MaxRowSpan  = 2;

    private static readonly (int Cols, int Rows)[] SizeCycleOrder =
    {
        (1, 1),
        (2, 1),
        (1, 2),
        (2, 2),
        (4, 1)
    };

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _filePath;

    public ObservableCollection<HomeSlotItem>        Slots         { get; } = new();
    public ObservableCollection<HomeWidgetCatalogItem> WidgetCatalog { get; } = new();

    [ObservableProperty]
    private int _totalRows = 2;

    public event Action? LayoutChanged;

    public HomeLayoutService()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Win11Notch");
        Directory.CreateDirectory(dir);
        _filePath = Path.Combine(dir, "home-layout.json");

        InitializeCatalog();
        Load();
        if (Slots.Count == 0)
        {
            ApplyDefaultSlots();
        }
        RepackLayout(save: false);
    }

    public HomeSlotItem AddSlot(HomeWidgetKind kind, int? colSpan = null, int? rowSpan = null)
    {
        var def = WidgetCatalog.FirstOrDefault(w => w.Kind == kind);
        int cols = Math.Clamp(colSpan ?? def?.DefaultColSpan ?? 1, 1, GridColumns);
        int rows = Math.Clamp(rowSpan ?? def?.DefaultRowSpan ?? 1, 1, MaxRowSpan);

        var slot = new HomeSlotItem
        {
            WidgetKind = kind,
            ColSpan    = cols,
            RowSpan    = rows,
            Order      = Slots.Count
        };
        Slots.Add(slot);
        RepackLayout(save: true);
        return slot;
    }

    public void RemoveSlot(HomeSlotItem slot)
    {
        if (Slots.Remove(slot))
        {
            RepackLayout(save: true);
        }
    }

    public void ResizeSlot(HomeSlotItem slot, int colSpan, int rowSpan)
    {
        int cols = Math.Clamp(colSpan, 1, GridColumns);
        int rows = Math.Clamp(rowSpan, 1, MaxRowSpan);
        if (slot.ColSpan == cols && slot.RowSpan == rows) return;

        slot.ColSpan = cols;
        slot.RowSpan = rows;
        RepackLayout(save: true);
    }

    public void CycleSlotSize(HomeSlotItem slot)
    {
        int currentIdx = -1;
        for (int i = 0; i < SizeCycleOrder.Length; i++)
        {
            if (SizeCycleOrder[i].Cols == slot.ColSpan && SizeCycleOrder[i].Rows == slot.RowSpan)
            {
                currentIdx = i;
                break;
            }
        }

        var next = SizeCycleOrder[(currentIdx + 1) % SizeCycleOrder.Length];
        ResizeSlot(slot, next.Cols, next.Rows);
    }

    public void MoveSlot(HomeSlotItem slot, int delta)
    {
        int oldIdx = Slots.IndexOf(slot);
        if (oldIdx < 0) return;

        int newIdx = Math.Clamp(oldIdx + delta, 0, Slots.Count - 1);
        if (newIdx == oldIdx) return;

        Slots.Move(oldIdx, newIdx);
        RepackLayout(save: true);
    }

    public void SwapSlots(HomeSlotItem source, HomeSlotItem target)
    {
        if (ReferenceEquals(source, target)) return;
        int idxA = Slots.IndexOf(source);
        int idxB = Slots.IndexOf(target);
        if (idxA < 0 || idxB < 0) return;

        if (idxA < idxB)
        {
            Slots.Move(idxB, idxA);
            Slots.Move(idxA + 1, idxB);
        }
        else
        {
            Slots.Move(idxA, idxB);
            Slots.Move(idxB + 1, idxA);
        }

        RepackLayout(save: true);
    }

    public void ResetToDefault()
    {
        Slots.Clear();
        ApplyDefaultSlots();
        RepackLayout(save: true);
    }

    private void InitializeCatalog()
    {
        WidgetCatalog.Clear();
        WidgetCatalog.Add(new HomeWidgetCatalogItem
        {
            Kind = HomeWidgetKind.DateClock, Title = "Date & Clock",
            Subtitle = "Day, Month, Year, Greeting & 12h/24h Clock",
            IconGlyph = "\uE787", AccentHex = "#FF5E36", DefaultColSpan = 1, DefaultRowSpan = 1
        });
        WidgetCatalog.Add(new HomeWidgetCatalogItem
        {
            Kind = HomeWidgetKind.MusicPlayer, Title = "Now Playing Music",
            Subtitle = "Album art, track info, inline controls & seek progress",
            IconGlyph = "\uEC4F", AccentHex = "#FF5E36", DefaultColSpan = 2, DefaultRowSpan = 1
        });
        WidgetCatalog.Add(new HomeWidgetCatalogItem
        {
            Kind = HomeWidgetKind.UpcomingAgenda, Title = "Up Next Schedule",
            Subtitle = "Today's upcoming Events, Reminders & overdue alerts",
            IconGlyph = "\uEA8F", AccentHex = "#00E5FF", DefaultColSpan = 1, DefaultRowSpan = 1
        });
        WidgetCatalog.Add(new HomeWidgetCatalogItem
        {
            Kind = HomeWidgetKind.PowerAndAudio, Title = "Battery & Audio",
            Subtitle = "System battery %, charging & Bluetooth device battery",
            IconGlyph = "\uEBA0", AccentHex = "#69F0AE", DefaultColSpan = 1, DefaultRowSpan = 1
        });
        WidgetCatalog.Add(new HomeWidgetCatalogItem
        {
            Kind = HomeWidgetKind.SystemResources, Title = "Device Resources",
            Subtitle = "CPU %, GPU %, RAM usage & live network speeds",
            IconGlyph = "\uE9D9", AccentHex = "#00E5FF", DefaultColSpan = 1, DefaultRowSpan = 1
        });
        WidgetCatalog.Add(new HomeWidgetCatalogItem
        {
            Kind = HomeWidgetKind.TimersAndWater, Title = "Timers & Hydration",
            Subtitle = "Pomodoro focus timer & daily water cup counter",
            IconGlyph = "\uE916", AccentHex = "#40C4FF", DefaultColSpan = 1, DefaultRowSpan = 1
        });
        WidgetCatalog.Add(new HomeWidgetCatalogItem
        {
            Kind = HomeWidgetKind.ShelfAndClipboard, Title = "Shelf & Clipboard",
            Subtitle = "Staged files counter & recent clipboard history",
            IconGlyph = "\uE8B7", AccentHex = "#40C4FF", DefaultColSpan = 1, DefaultRowSpan = 1
        });
        WidgetCatalog.Add(new HomeWidgetCatalogItem
        {
            Kind = HomeWidgetKind.ToolsAndBrightness, Title = "Brightness & Notes",
            Subtitle = "Display brightness %, scratchpad snippet & Quick Share",
            IconGlyph = "\uE706", AccentHex = "#FFD54F", DefaultColSpan = 1, DefaultRowSpan = 1
        });
        WidgetCatalog.Add(new HomeWidgetCatalogItem
        {
            Kind = HomeWidgetKind.QuickActions, Title = "Quick Actions",
            Subtitle = "One-tap Stay Awake, Screen Snip, Color Picker & Settings",
            IconGlyph = "\uE713", AccentHex = "#B388FF", DefaultColSpan = 2, DefaultRowSpan = 1
        });
    }

    private void ApplyDefaultSlots()
    {
        Slots.Add(new HomeSlotItem { WidgetKind = HomeWidgetKind.DateClock,          ColSpan = 1, RowSpan = 1 });
        Slots.Add(new HomeSlotItem { WidgetKind = HomeWidgetKind.MusicPlayer,        ColSpan = 2, RowSpan = 1 });
        Slots.Add(new HomeSlotItem { WidgetKind = HomeWidgetKind.UpcomingAgenda,     ColSpan = 1, RowSpan = 1 });
        Slots.Add(new HomeSlotItem { WidgetKind = HomeWidgetKind.PowerAndAudio,      ColSpan = 1, RowSpan = 1 });
        Slots.Add(new HomeSlotItem { WidgetKind = HomeWidgetKind.SystemResources,    ColSpan = 1, RowSpan = 1 });
        Slots.Add(new HomeSlotItem { WidgetKind = HomeWidgetKind.TimersAndWater,     ColSpan = 1, RowSpan = 1 });
        Slots.Add(new HomeSlotItem { WidgetKind = HomeWidgetKind.ShelfAndClipboard,  ColSpan = 1, RowSpan = 1 });
    }

    /// <summary>
    /// Collision-free first-fit 2D occupancy packer across GridColumns columns.
    /// Guarantees zero overlapping widgets regardless of resize, reorder, or deletion order.
    /// </summary>
    public void RepackLayout(bool save)
    {
        var occupied = new List<bool[]>();
        int maxRowEnd = 1;

        for (int i = 0; i < Slots.Count; i++)
        {
            var slot = Slots[i];
            slot.Order   = i;
            slot.ColSpan = Math.Clamp(slot.ColSpan, 1, GridColumns);
            slot.RowSpan = Math.Clamp(slot.RowSpan, 1, MaxRowSpan);

            var (row, col) = FindFirstFreeCell(occupied, slot.ColSpan, slot.RowSpan);
            MarkOccupied(occupied, row, col, slot.ColSpan, slot.RowSpan);

            slot.GridRow    = row;
            slot.GridColumn = col;
            maxRowEnd = Math.Max(maxRowEnd, row + slot.RowSpan);
        }

        TotalRows = Math.Max(2, maxRowEnd);
        RefreshCatalogCounts();

        if (save) Save();
        LayoutChanged?.Invoke();
    }

    private static (int Row, int Col) FindFirstFreeCell(List<bool[]> occupied, int colSpan, int rowSpan)
    {
        for (int r = 0; ; r++)
        {
            EnsureRows(occupied, r + rowSpan);
            for (int c = 0; c <= GridColumns - colSpan; c++)
            {
                bool fits = true;
                for (int dr = 0; dr < rowSpan && fits; dr++)
                {
                    for (int dc = 0; dc < colSpan; dc++)
                    {
                        if (occupied[r + dr][c + dc])
                        {
                            fits = false;
                            break;
                        }
                    }
                }
                if (fits) return (r, c);
            }
        }
    }

    private static void MarkOccupied(List<bool[]> occupied, int row, int col, int colSpan, int rowSpan)
    {
        EnsureRows(occupied, row + rowSpan);
        for (int dr = 0; dr < rowSpan; dr++)
        {
            for (int dc = 0; dc < colSpan; dc++)
            {
                occupied[row + dr][col + dc] = true;
            }
        }
    }

    private static void EnsureRows(List<bool[]> occupied, int neededRows)
    {
        while (occupied.Count < neededRows)
        {
            occupied.Add(new bool[GridColumns]);
        }
    }

    private void RefreshCatalogCounts()
    {
        foreach (var cat in WidgetCatalog)
        {
            cat.ActiveCount = Slots.Count(s => s.WidgetKind == cat.Kind);
        }
    }

    private sealed record HomeLayoutDto(List<HomeSlotDto> Slots);
    private sealed record HomeSlotDto(string Id, HomeWidgetKind WidgetKind, int ColSpan, int RowSpan);

    private void Load()
    {
        try
        {
            if (!File.Exists(_filePath)) return;
            var dto = JsonSerializer.Deserialize<HomeLayoutDto>(File.ReadAllText(_filePath), JsonOpts);
            if (dto?.Slots == null || dto.Slots.Count == 0) return;

            Slots.Clear();
            foreach (var item in dto.Slots)
            {
                if (!Enum.IsDefined(typeof(HomeWidgetKind), item.WidgetKind)) continue;
                Slots.Add(new HomeSlotItem
                {
                    Id         = string.IsNullOrWhiteSpace(item.Id) ? Guid.NewGuid().ToString("N") : item.Id,
                    WidgetKind = item.WidgetKind,
                    ColSpan    = Math.Clamp(item.ColSpan, 1, GridColumns),
                    RowSpan    = Math.Clamp(item.RowSpan, 1, MaxRowSpan)
                });
            }
        }
        catch { /* corrupt file falls back cleanly to default */ }
    }

    private void Save()
    {
        try
        {
            var dto = new HomeLayoutDto(
                Slots.Select(s => new HomeSlotDto(s.Id, s.WidgetKind, s.ColSpan, s.RowSpan)).ToList());
            File.WriteAllText(_filePath, JsonSerializer.Serialize(dto, JsonOpts));
        }
        catch { /* best effort */ }
    }
}
