using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Win11Notch.Services;

/// <summary>Represents a staged file in the Drop Shelf.</summary>
public sealed class ShelfItem
{
    public string      FilePath      { get; init; } = string.Empty;
    public string      FileName      { get; init; } = string.Empty;
    public string      FormattedSize { get; init; } = string.Empty;
    public ImageSource? Icon         { get; init; }
    public bool        IsDirectory   { get; init; }
    public string      TypeBadge     { get; init; } = "FILE";

    public static ShelfItem FromFile(string path)
    {
        bool isDir = Directory.Exists(path);
        string name = isDir ? new DirectoryInfo(path).Name : Path.GetFileName(path);
        string size = isDir ? "Folder" : (File.Exists(path) ? FormatBytes(new FileInfo(path).Length) : "0 B");
        string badge = isDir ? "DIR" : Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
        if (string.IsNullOrEmpty(badge)) badge = "FILE";

        ImageSource? icon = null;
        try
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (!isDir && ext is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp" or ".ico" && File.Exists(path))
            {
                // High quality image thumbnail
                var thumb = new BitmapImage();
                thumb.BeginInit();
                thumb.CacheOption = BitmapCacheOption.OnLoad;
                thumb.DecodePixelWidth = 64;
                thumb.UriSource = new Uri(path);
                thumb.EndInit();
                thumb.Freeze();
                icon = thumb;
            }
            else if (File.Exists(path))
            {
                using var sysIcon = System.Drawing.Icon.ExtractAssociatedIcon(path);
                if (sysIcon != null)
                {
                    using var bmp = sysIcon.ToBitmap();
                    using var ms = new MemoryStream();
                    bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                    ms.Position = 0;

                    var img = new BitmapImage();
                    img.BeginInit();
                    img.CacheOption = BitmapCacheOption.OnLoad;
                    img.StreamSource = ms;
                    img.EndInit();
                    img.Freeze();
                    icon = img;
                }
            }
        }
        catch { /* fallback */ }

        return new ShelfItem
        {
            FilePath      = path,
            FileName      = name,
            FormattedSize = size,
            Icon          = icon,
            IsDirectory   = isDir,
            TypeBadge     = badge
        };
    }

    private static string FormatBytes(long bytes)
    {
        string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
        int counter = 0;
        decimal number = bytes;
        while (Math.Round(number / 1024) >= 1)
        {
            number /= 1024;
            counter++;
        }
        return $"{number:n1} {suffixes[counter]}";
    }
}

/// <summary>
/// Temporary file staging ground.
/// Supports dropping files from Explorer, holding them, and dragging them out to other apps.
/// </summary>
public sealed class ShelfService
{
    private readonly string _stateFilePath;

    public ObservableCollection<ShelfItem> Items { get; } = new();

    public ShelfService()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Win11Notch");
        Directory.CreateDirectory(dir);
        _stateFilePath = Path.Combine(dir, "shelf.json");
        Load();
    }

    public void AddFiles(IEnumerable<string> paths)
    {
        bool added = false;
        foreach (var path in paths)
        {
            if (File.Exists(path) || Directory.Exists(path))
            {
                if (!Items.Any(i => string.Equals(i.FilePath, path, StringComparison.OrdinalIgnoreCase)))
                {
                    Items.Add(ShelfItem.FromFile(path));
                    added = true;
                }
            }
        }
        if (added) Save();
    }

    public void RemoveItem(ShelfItem item)
    {
        if (Items.Remove(item)) Save();
    }

    public void Clear()
    {
        if (Items.Count > 0)
        {
            Items.Clear();
            Save();
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_stateFilePath)) return;
            var paths = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(_stateFilePath));
            if (paths == null) return;
            foreach (var p in paths)
            {
                if (File.Exists(p) || Directory.Exists(p))
                {
                    Items.Add(ShelfItem.FromFile(p));
                }
            }
        }
        catch { /* best effort */ }
    }

    private void Save()
    {
        try
        {
            var paths = Items.Select(i => i.FilePath).ToList();
            File.WriteAllText(_stateFilePath, JsonSerializer.Serialize(paths));
        }
        catch { /* best effort */ }
    }

    public void CopyPathsToClipboard()
    {
        if (Items.Count == 0) return;
        var paths = Items.Select(i => i.FilePath);
        Clipboard.SetText(string.Join(Environment.NewLine, paths));
    }
}
