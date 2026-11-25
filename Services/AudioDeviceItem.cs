namespace Win11Notch.Services;

/// <summary>
/// Represents a connected Windows audio playback device endpoint.
/// </summary>
public sealed class AudioDeviceItem
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public Guid ContainerId { get; init; }
    public bool IsDefault { get; set; }

    /// <summary>
    /// Bluetooth battery percentage (0-100) if this endpoint corresponds to a connected Bluetooth device, or -1.
    /// </summary>
    public int BluetoothBatteryPercent { get; set; } = -1;

    public bool HasBluetoothBattery => BluetoothBatteryPercent > 0;
    public string BluetoothBatteryText => BluetoothBatteryPercent > 0 ? $"{BluetoothBatteryPercent}%" : string.Empty;
}
