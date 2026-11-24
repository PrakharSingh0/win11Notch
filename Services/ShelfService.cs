using System.Collections.ObjectModel;
using System.IO;
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

    public static ShelfItem FromFile(string path)
    {
        var fi = new FileInfo(path);
        string size = fi.Exists ? FormatBytes(fi.Length) : "0 B";

        ImageSource? icon = null;
        try
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
        catch { /* fallback icon */ }

        return new ShelfItem
        {
            FilePath      = path,
            FileName      = fi.Name,
            FormattedSize = size,
            Icon          = icon
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
    public ObservableCollection<ShelfItem> Items { get; } = new();

    public void AddFiles(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            if (File.Exists(path) || Directory.Exists(path))
            {
                if (!Items.Any(i => string.Equals(i.FilePath, path, StringComparison.OrdinalIgnoreCase)))
                {
                    Items.Add(ShelfItem.FromFile(path));
                }
            }
        }
    }

    public void RemoveItem(ShelfItem item) => Items.Remove(item);

    public void Clear() => Items.Clear();

    public void CopyPathsToClipboard()
    {
        if (Items.Count == 0) return;
        var paths = Items.Select(i => i.FilePath);
        Clipboard.SetText(string.Join(Environment.NewLine, paths));
    }
}
