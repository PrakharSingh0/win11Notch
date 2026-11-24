namespace Win11Notch.Services;

/// <summary>
/// Represents a connected Windows audio playback device endpoint.
/// </summary>
public sealed class AudioDeviceItem
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public bool IsDefault { get; set; }
}
