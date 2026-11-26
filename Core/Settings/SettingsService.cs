using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Win11Notch.Core.Settings;

/// <summary>Loads and persists <see cref="AppSettings"/> to %AppData%\Win11Notch\settings.json.</summary>
public sealed class SettingsService
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Win11Notch", "settings.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented           = true,
        DefaultIgnoreCondition  = JsonIgnoreCondition.Never
    };

    private readonly SemaphoreSlim _saveLock = new(1, 1);

    public AppSettings Current { get; private set; } = new();

    public async Task LoadAsync()
    {
        try
        {
            if (!File.Exists(SettingsPath)) { await SaveAsync(); return; }
            await using var fs = File.OpenRead(SettingsPath);
            Current = await JsonSerializer.DeserializeAsync<AppSettings>(fs, JsonOpts) ?? new();
        }
        catch { Current = new(); }
    }

    public async Task SaveAsync()
    {
        await _saveLock.WaitAsync().ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            await using var fs = File.Create(SettingsPath);
            await JsonSerializer.SerializeAsync(fs, Current, JsonOpts).ConfigureAwait(false);
        }
        catch { /* best-effort */ }
        finally
        {
            _saveLock.Release();
        }
    }
}
