using System.Windows.Media;

namespace Win11Notch.Models;

/// <summary>
/// Per-GPU adapter telemetry snapshot & sparkline data (supports dual iGPU + dGPU systems).
/// </summary>
public sealed class GpuAdapterInfo
{
    public string Name          { get; set; } = string.Empty;
    public string LuidKey       { get; set; } = string.Empty;
    public bool   IsDiscrete    { get; set; }
    public bool   IsActive      { get; set; }
    public int    GpuPercent    { get; set; }
    public double VramUsedGb    { get; set; }
    public double VramTotalGb   { get; set; }
    public int    VramPercent   { get; set; }
    public string VramText      { get; set; } = "0.0 GB";
    public string RoleLabel     { get; set; } = "GPU";
    public string AccentHex     { get; set; } = "#69F0AE";
    public List<double> History { get; set; } = new();
    public PointCollection GraphPoints { get; set; } = new();
    public PointCollection AreaPoints  { get; set; } = new();
}
