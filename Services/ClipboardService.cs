using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Win11Notch.Services;

public enum ClipboardItemKind
{
    Text,
    Url,
    Image
}

public sealed partial class ClipboardItem : ObservableObject
{
    public ClipboardItemKind Kind             { get; init; } = ClipboardItemKind.Text;
    public string            Text             { get; init; } = string.Empty;
    public string            Preview          { get; init; } = string.Empty;
    public string            BadgeText        { get; init; } = "TEXT";
    public ImageSource?      ImageThumbnail   { get; init; }
    public byte[]?           ImagePngBytes    { get; init; }
    public string            ImageHash        { get; init; } = string.Empty;
    public DateTime          Timestamp        { get; init; } = DateTime.Now;
    [ObservableProperty] private bool _isPinned;
    [ObservableProperty] private bool _isCopied;

    public bool IsImage => Kind == ClipboardItemKind.Image;
    public bool IsUrl   => Kind == ClipboardItemKind.Url;
    public bool IsText  => Kind == ClipboardItemKind.Text;
}

/// <summary>
/// Event-driven Windows clipboard history monitor supporting Text, URLs, and Images.
/// Uses Win32 AddClipboardFormatListener on WM_CLIPBOARDUPDATE — ZERO polling.
/// Persists pinned Text/URL snippets across restarts.
/// </summary>
public sealed class ClipboardService : IDisposable
{
    private const int WM_CLIPBOARDUPDATE = 0x031D;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    private IntPtr _hwnd;
    private HwndSource? _source;
    private bool _isListening;
    private string _lastText = string.Empty;
    private string _lastImageHash = string.Empty;
    private readonly string _pinnedFilePath;

    public ObservableCollection<ClipboardItem> History { get; } = new();

    public ClipboardService()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Win11Notch");
        Directory.CreateDirectory(dir);
        _pinnedFilePath = Path.Combine(dir, "pinned_clipboard.json");
        LoadPinned();
    }

    public void Start(IntPtr hwnd)
    {
        _hwnd = hwnd;
        _source = HwndSource.FromHwnd(hwnd);
        _source?.AddHook(WndProc);

        _isListening = AddClipboardFormatListener(_hwnd);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_CLIPBOARDUPDATE)
        {
            OnClipboardUpdated();
        }
        return IntPtr.Zero;
    }

    private void OnClipboardUpdated()
    {
        try
        {
            if (Clipboard.ContainsText())
            {
                string text = Clipboard.GetText();
                if (string.IsNullOrWhiteSpace(text) || text == _lastText) return;

                _lastText = text;
                string trimmed = text.Trim();
                bool isUrl = Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) &&
                             (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

                string preview = trimmed.Replace("\r\n", " ").Replace("\n", " ");
                if (preview.Length > 64) preview = preview[..61] + "...";

                InsertNewItem(new ClipboardItem
                {
                    Kind      = isUrl ? ClipboardItemKind.Url : ClipboardItemKind.Text,
                    Text      = text,
                    Preview   = preview,
                    BadgeText = isUrl ? uri!.Host : "TEXT",
                    Timestamp = DateTime.Now
                });
                return;
            }

            if (Clipboard.ContainsImage())
            {
                var img = Clipboard.GetImage();
                if (img == null) return;

                byte[] pngBytes;
                using (var ms = new MemoryStream())
                {
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(img));
                    encoder.Save(ms);
                    pngBytes = ms.ToArray();
                }

                string hash = Convert.ToHexString(SHA256.HashData(pngBytes));
                if (hash == _lastImageHash) return;
                _lastImageHash = hash;

                var thumb = CreateFrozenThumbnail(pngBytes);
                InsertNewItem(new ClipboardItem
                {
                    Kind           = ClipboardItemKind.Image,
                    Text           = $"Image ({img.PixelWidth}×{img.PixelHeight})",
                    Preview        = $"{img.PixelWidth} × {img.PixelHeight} px",
                    BadgeText      = "IMG",
                    ImageThumbnail = thumb,
                    ImagePngBytes  = pngBytes,
                    ImageHash      = hash,
                    Timestamp      = DateTime.Now
                });
            }
        }
        catch { /* clipboard busy or locked by another app */ }
    }

    private void InsertNewItem(ClipboardItem item)
    {
        int insertIndex = History.Count(i => i.IsPinned);
        History.Insert(insertIndex, item);

        while (History.Count > 30)
        {
            var lastUnpinned = History.LastOrDefault(i => !i.IsPinned);
            if (lastUnpinned != null) History.Remove(lastUnpinned);
            else break;
        }

        // Keep at most 8 cached image buffers to avoid RAM bloat
        var imgs = History.Where(i => i.IsImage && !i.IsPinned).Skip(8).ToList();
        foreach (var old in imgs) History.Remove(old);
    }

    private static ImageSource? CreateFrozenThumbnail(byte[] pngBytes)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 72;
            bmp.StreamSource = new MemoryStream(pngBytes);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    public void CopyItem(ClipboardItem item)
    {
        try
        {
            if (item.IsImage && item.ImagePngBytes != null)
            {
                _lastImageHash = item.ImageHash;
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = new MemoryStream(item.ImagePngBytes);
                bmp.EndInit();
                bmp.Freeze();
                Clipboard.SetImage(bmp);
            }
            else if (!string.IsNullOrEmpty(item.Text))
            {
                _lastText = item.Text;
                Clipboard.SetText(item.Text);
            }
        }
        catch { /* best effort */ }
    }

    public void TogglePin(ClipboardItem item)
    {
        item.IsPinned = !item.IsPinned;
        var items = History.OrderByDescending(i => i.IsPinned).ThenByDescending(i => i.Timestamp).ToList();
        History.Clear();
        foreach (var it in items) History.Add(it);
        SavePinned();
    }

    public void Remove(ClipboardItem item)
    {
        bool wasPinned = item.IsPinned;
        History.Remove(item);
        if (wasPinned) SavePinned();
    }

    public void ClearUnpinned()
    {
        var unpinned = History.Where(i => !i.IsPinned).ToList();
        foreach (var it in unpinned) History.Remove(it);
    }

    private sealed record PinnedClipboardDto(ClipboardItemKind Kind, string Text, string Preview, string BadgeText, DateTime Timestamp);

    private void LoadPinned()
    {
        try
        {
            if (!File.Exists(_pinnedFilePath)) return;
            var items = JsonSerializer.Deserialize<List<PinnedClipboardDto>>(File.ReadAllText(_pinnedFilePath));
            if (items == null) return;
            foreach (var it in items)
            {
                if (!string.IsNullOrWhiteSpace(it.Text) && it.Kind != ClipboardItemKind.Image)
                {
                    History.Add(new ClipboardItem
                    {
                        Kind      = it.Kind,
                        Text      = it.Text,
                        Preview   = it.Preview,
                        BadgeText = string.IsNullOrWhiteSpace(it.BadgeText) ? "TEXT" : it.BadgeText,
                        Timestamp = it.Timestamp,
                        IsPinned  = true
                    });
                }
            }
        }
        catch { /* best effort */ }
    }

    private void SavePinned()
    {
        try
        {
            var pinned = History
                .Where(i => i.IsPinned && !i.IsImage)
                .Select(i => new PinnedClipboardDto(i.Kind, i.Text, i.Preview, i.BadgeText, i.Timestamp))
                .ToList();
            File.WriteAllText(_pinnedFilePath, JsonSerializer.Serialize(pinned));
        }
        catch { /* best effort */ }
    }

    public void Dispose()
    {
        if (_isListening && _hwnd != IntPtr.Zero)
        {
            RemoveClipboardFormatListener(_hwnd);
            _isListening = false;
        }
        _source?.RemoveHook(WndProc);
    }
}
