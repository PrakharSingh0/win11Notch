using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Win11Notch.Services;

public sealed partial class ClipboardItem : ObservableObject
{
    public string   Text      { get; init; } = string.Empty;
    public string   Preview   { get; init; } = string.Empty;
    public DateTime Timestamp { get; init; } = DateTime.Now;
    [ObservableProperty] private bool _isPinned;
    [ObservableProperty] private bool _isCopied;
}

/// <summary>
/// Event-driven Windows clipboard history monitor.
/// Uses Win32 AddClipboardFormatListener on WM_CLIPBOARDUPDATE — ZERO polling.
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

    public ObservableCollection<ClipboardItem> History { get; } = new();

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

                // Format preview for display (single line trimmed)
                string preview = text.Replace("\r\n", " ").Replace("\n", " ").Trim();
                if (preview.Length > 60) preview = preview[..57] + "...";

                // Insert at front, keeping pinned items at the top
                int insertIndex = History.Count(i => i.IsPinned);
                History.Insert(insertIndex, new ClipboardItem
                {
                    Text      = text,
                    Preview   = preview,
                    Timestamp = DateTime.Now
                });

                // Limit history capacity to 25 items
                while (History.Count > 25)
                {
                    var lastUnpinned = History.LastOrDefault(i => !i.IsPinned);
                    if (lastUnpinned != null) History.Remove(lastUnpinned);
                    else break;
                }
            }
        }
        catch { /* clipboard busy or locked by another app */ }
    }

    public void CopyItem(ClipboardItem item)
    {
        try
        {
            _lastText = item.Text;
            Clipboard.SetText(item.Text);
        }
        catch { /* best effort */ }
    }

    public void TogglePin(ClipboardItem item)
    {
        item.IsPinned = !item.IsPinned;
        // Re-sort: pinned on top
        var items = History.OrderByDescending(i => i.IsPinned).ThenByDescending(i => i.Timestamp).ToList();
        History.Clear();
        foreach (var it in items) History.Add(it);
    }

    public void Remove(ClipboardItem item) => History.Remove(item);

    public void ClearUnpinned()
    {
        var unpinned = History.Where(i => !i.IsPinned).ToList();
        foreach (var it in unpinned) History.Remove(it);
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
