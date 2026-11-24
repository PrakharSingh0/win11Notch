using System.IO;

namespace Win11Notch.Services;

/// <summary>
/// Persists the lightweight Quick Scratchpad note to %AppData%\Win11Notch\scratchpad.txt.
/// </summary>
public sealed class ScratchpadService
{
    private readonly string _filePath;

    public ScratchpadService()
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Win11Notch");
            Directory.CreateDirectory(dir);
            _filePath = Path.Combine(dir, "scratchpad.txt");
        }
        catch
        {
            _filePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "scratchpad.txt");
        }
    }

    public string Load()
    {
        try
        {
            return File.Exists(_filePath) ? File.ReadAllText(_filePath) : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    public void Save(string content)
    {
        try
        {
            File.WriteAllText(_filePath, content ?? string.Empty);
        }
        catch { /* best effort */ }
    }
}
