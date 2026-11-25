using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Win11Notch.Models;

/// <summary>
/// Represents a file or folder queued in the File Share tab for sending via Quick Share.
/// </summary>
public sealed class ShareQueueItem
{
    public string       FilePath      { get; init; } = string.Empty;
    public string       FileName      { get; init; } = string.Empty;
    public string       FormattedSize { get; init; } = string.Empty;
    public long         ByteLength    { get; init; }
    public bool         IsDirectory   { get; init; }
    public string       TypeBadge     { get; init; } = "FILE";
    public ImageSource? Icon          { get; init; }

    public static ShareQueueItem FromPath(string path)
    {
        bool isDir = Directory.Exists(path);
        long len = !isDir && File.Exists(path) ? new FileInfo(path).Length : 0;
        string name = isDir ? new DirectoryInfo(path).Name : Path.GetFileName(path);
        string size = isDir ? "Folder" : FormatBytes(len);
        string badge = isDir ? "DIR" : Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
        if (string.IsNullOrEmpty(badge)) badge = "FILE";

        ImageSource? icon = null;
        try
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (!isDir && ext is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp" or ".ico" && File.Exists(path))
            {
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
        catch { /* best effort icon preview */ }

        return new ShareQueueItem
        {
            FilePath      = path,
            FileName      = name,
            FormattedSize = size,
            ByteLength    = len,
            IsDirectory   = isDir,
            TypeBadge     = badge,
            Icon          = icon
        };
    }

    public static string FormatBytes(long bytes)
    {
        string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
        int counter = 0;
        decimal number = Math.Max(0, bytes);
        while (Math.Round(number / 1024) >= 1 && counter < suffixes.Length - 1)
        {
            number /= 1024;
            counter++;
        }
        return $"{number:n1} {suffixes[counter]}";
    }
}

/// <summary>
/// Persisted record of a past file transfer invocation from Win11Notch.
/// </summary>
public sealed class ShareHistoryEntry
{
    public string       Title         { get; set; } = string.Empty;
    public List<string> FilePaths     { get; set; } = new();
    public string       SummaryText   { get; set; } = string.Empty;
    public string       MethodBadge   { get; set; } = "QUICK SHARE";
    public string       TimestampText { get; set; } = string.Empty;
    public DateTime     Timestamp     { get; set; } = DateTime.Now;
}
