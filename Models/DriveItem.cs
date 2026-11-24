namespace Win11Notch.Models;

/// <summary>
/// Represents a storage drive partition with capacity and usage statistics.
/// Full read-write properties prevent WPF TwoWay binding crashes.
/// </summary>
public sealed class DriveItem
{
    public string Name        { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string VolumeLabel { get; set; } = string.Empty;
    public double TotalGb     { get; set; }
    public double FreeGb      { get; set; }
    public double UsedGb      { get; set; }
    public int    UsedPercent { get; set; }
    public string SummaryText { get; set; } = string.Empty;
}
