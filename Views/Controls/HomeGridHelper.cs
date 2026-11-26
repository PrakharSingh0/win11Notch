using System.Windows;
using System.Windows.Controls;

namespace Win11Notch.Views.Controls;

/// <summary>
/// Dynamically synchronizes the 4-column × N-row unit Grid inside Home's ItemsPanelTemplate
/// so packed widgets never overflow or require hardcoded row definitions.
/// </summary>
public static class HomeGridHelper
{
    public const double UnitRowHeight = 148.0;

    public static readonly DependencyProperty RowCountProperty =
        DependencyProperty.RegisterAttached(
            "RowCount",
            typeof(int),
            typeof(HomeGridHelper),
            new PropertyMetadata(0, OnRowCountChanged));

    public static int GetRowCount(DependencyObject obj) => (int)obj.GetValue(RowCountProperty);
    public static void SetRowCount(DependencyObject obj, int value) => obj.SetValue(RowCountProperty, value);

    private static void OnRowCountChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Grid grid) return;
        SyncGrid(grid, (int)e.NewValue);
    }

    private static void SyncGrid(Grid grid, int rows)
    {
        int targetRows = Math.Max(2, rows);

        if (grid.ColumnDefinitions.Count != 4)
        {
            grid.ColumnDefinitions.Clear();
            for (int c = 0; c < 4; c++)
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            }
        }

        if (grid.RowDefinitions.Count != targetRows)
        {
            grid.RowDefinitions.Clear();
            for (int r = 0; r < targetRows; r++)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(UnitRowHeight, GridUnitType.Pixel) });
            }
        }
    }
}
