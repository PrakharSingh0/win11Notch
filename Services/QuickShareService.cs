using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using Microsoft.Win32;
using Win11Notch.Models;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace Win11Notch.Services;

public enum ShareLaunchOutcome
{
    QuickShareCom,
    WindowsShareFallback,
    Failed
}

/// <summary>
/// Integrates Google Quick Share for Windows (via its Packaged COM IExplorerCommand shell handler
/// {AD54D7CA-052D-4730-B0F2-2BA34D3438DC}) with automatic fallback to Windows 11's native Share UI,
/// clipboard-to-file staging, and transfer history persistence.
/// </summary>
public sealed class QuickShareService
{
    private const int MaxHistory = 20;
    private static readonly Guid ClsidQuickShareExplorerCommand = new("AD54D7CA-052D-4730-B0F2-2BA34D3438DC");
    private static readonly Guid IidExplorerCommand             = new("A08CE4D0-FA25-44AB-B57C-C7B1C323E0B9");
    private static readonly Guid IidDataTransferManager         = new("A5CAEE9B-8708-49D1-8D36-67D25A8DA00C");

    [ComImport]
    [Guid("A08CE4D0-FA25-44AB-B57C-C7B1C323E0B9")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IExplorerCommand
    {
        [PreserveSig] int GetTitle(IntPtr psiItemArray, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
        [PreserveSig] int GetIcon(IntPtr psiItemArray, [MarshalAs(UnmanagedType.LPWStr)] out string ppszIcon);
        [PreserveSig] int GetToolTip(IntPtr psiItemArray, [MarshalAs(UnmanagedType.LPWStr)] out string ppszInfotip);
        [PreserveSig] int GetCanonicalName(out Guid pguidCommandName);
        [PreserveSig] int GetState(IntPtr psiItemArray, [MarshalAs(UnmanagedType.Bool)] bool fOkToBeSlow, out uint pCmdState);
        [PreserveSig] int Invoke(IntPtr psiItemArray, IntPtr pbc);
        [PreserveSig] int GetFlags(out uint pFlags);
        [PreserveSig] int EnumSubCommands(out IntPtr ppEnum);
    }

    [ComImport]
    [Guid("3A3DCD6C-3EAB-43DC-BCDE-45671CE800C8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDataTransferManagerInterop
    {
        IntPtr GetForWindow([In] IntPtr appWindow, [In] ref Guid riid);
        void ShowShareUIForWindow([In] IntPtr appWindow);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(
        [MarshalAs(UnmanagedType.LPWStr)] string pszName,
        IntPtr pbc,
        out IntPtr ppidl,
        uint sfgaoIn,
        out uint psfgaoOut);

    [DllImport("shell32.dll")]
    private static extern int SHCreateShellItemArrayFromIDLists(
        uint cidl,
        [In] IntPtr[] rgpidl,
        out IntPtr ppsiItemArray);

    [DllImport("shell32.dll")]
    private static extern void ILFree(IntPtr pidl);

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(
        ref Guid rclsid,
        IntPtr pUnkOuter,
        uint dwClsContext,
        ref Guid riid,
        out IntPtr ppv);

    private readonly string _historyFilePath;
    private readonly string _tempShareDir;
    private IntPtr _windowHandle;
    private DataTransferManager? _dtm;
    private List<IStorageItem> _preparedStorageItems = new();

    public ObservableCollection<ShareQueueItem>    Queue   { get; } = new();
    public ObservableCollection<ShareHistoryEntry> History { get; } = new();

    public string QuickShareExePath { get; private set; } = string.Empty;
    public bool   IsQuickShareInstalled { get; private set; }
    public bool   IsQuickShareRunning   { get; private set; }

    public QuickShareService()
    {
        var appDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Win11Notch");
        Directory.CreateDirectory(appDir);
        _historyFilePath = Path.Combine(appDir, "share_history.json");

        _tempShareDir = Path.Combine(Path.GetTempPath(), "Win11NotchShare");
        Directory.CreateDirectory(_tempShareDir);
        CleanupStaleTempShares();

        LoadHistory();
        RefreshStatus();
    }

    private void CleanupStaleTempShares()
    {
        try
        {
            var cutoff = DateTime.UtcNow.AddHours(-24);
            foreach (var file in Directory.EnumerateFiles(_tempShareDir))
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff)
                {
                    try { File.Delete(file); } catch { }
                }
            }
        }
        catch { /* best effort */ }
    }

    public void AttachWindow(IntPtr hwnd) => _windowHandle = hwnd;

    public void RefreshStatus()
    {
        string? exe = FindQuickShareExecutable();
        QuickShareExePath = exe ?? string.Empty;

        bool comRegistered = IsPackagedComRegistered();
        IsQuickShareInstalled = comRegistered || !string.IsNullOrEmpty(exe);

        try
        {
            IsQuickShareRunning = Process.GetProcessesByName("nearby_share").Length > 0;
        }
        catch
        {
            IsQuickShareRunning = false;
        }
    }

    public void AddPaths(IEnumerable<string> paths)
    {
        foreach (var raw in paths)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            string p = raw.Trim();
            if (!File.Exists(p) && !Directory.Exists(p)) continue;

            if (!Queue.Any(x => string.Equals(x.FilePath, p, StringComparison.OrdinalIgnoreCase)))
            {
                Queue.Add(ShareQueueItem.FromPath(p));
            }
        }
    }

    public void RemoveQueueItem(ShareQueueItem item) => Queue.Remove(item);

    public void ClearQueue() => Queue.Clear();

    public bool StageClipboardItem(ClipboardItem? latestItem)
    {
        try
        {
            if (latestItem != null)
            {
                if (latestItem.IsImage && latestItem.ImagePngBytes is { Length: > 0 })
                {
                    string file = Path.Combine(_tempShareDir, $"clipboard_{DateTime.Now:yyyyMMdd_HHmmss}.png");
                    File.WriteAllBytes(file, latestItem.ImagePngBytes);
                    AddPaths(new[] { file });
                    return true;
                }

                if (!string.IsNullOrWhiteSpace(latestItem.Text))
                {
                    string prefix = latestItem.IsUrl ? "shared_link" : "shared_text";
                    string file = Path.Combine(_tempShareDir, $"{prefix}_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
                    File.WriteAllText(file, latestItem.Text, Encoding.UTF8);
                    AddPaths(new[] { file });
                    return true;
                }
            }

            if (System.Windows.Clipboard.ContainsFileDropList())
            {
                var list = System.Windows.Clipboard.GetFileDropList();
                var arr = new List<string>();
                foreach (var entry in list)
                {
                    if (!string.IsNullOrEmpty(entry)) arr.Add(entry);
                }
                if (arr.Count > 0)
                {
                    AddPaths(arr);
                    return true;
                }
            }

            if (System.Windows.Clipboard.ContainsText())
            {
                string text = System.Windows.Clipboard.GetText();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    string file = Path.Combine(_tempShareDir, $"shared_text_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
                    File.WriteAllText(file, text, Encoding.UTF8);
                    AddPaths(new[] { file });
                    return true;
                }
            }
        }
        catch { /* clipboard locked or empty */ }

        return false;
    }

    public async Task<ShareLaunchOutcome> SendQueuedAsync()
    {
        var paths = Queue
            .Select(q => q.FilePath)
            .Where(p => File.Exists(p) || Directory.Exists(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var outcome = await SendPathsAsync(paths);
        if (outcome != ShareLaunchOutcome.Failed)
        {
            ClearQueue();
        }
        return outcome;
    }

    /// <summary>
    /// Invokes Google Quick Share directly for the provided file/folder paths via its Packaged COM
    /// IExplorerCommand handler, falling back to Windows 11's native Share Sheet if needed.
    /// </summary>
    public async Task<ShareLaunchOutcome> SendPathsAsync(IEnumerable<string> rawPaths)
    {
        var paths = rawPaths
            .Where(p => !string.IsNullOrWhiteSpace(p) && (File.Exists(p) || Directory.Exists(p)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (paths.Count == 0) return ShareLaunchOutcome.Failed;

        RefreshStatus();

        // Packaged COM IExplorerCommand activation works cleanly on STA UI thread
        bool comInvoked = TryInvokeQuickShareExplorerCommand(paths);
        if (comInvoked)
        {
            RecordHistory(paths, "QUICK SHARE");
            RefreshStatus();
            return ShareLaunchOutcome.QuickShareCom;
        }

        // Fallback: Windows 11 native Share Contract UI
        bool winShareInvoked = await TryShowWindowsShareUiAsync(paths);
        if (winShareInvoked)
        {
            RecordHistory(paths, "WIN SHARE");
            return ShareLaunchOutcome.WindowsShareFallback;
        }

        return ShareLaunchOutcome.Failed;
    }

    public bool OpenQuickShareApp()
    {
        RefreshStatus();
        try
        {
            if (!string.IsNullOrEmpty(QuickShareExePath) && File.Exists(QuickShareExePath))
            {
                Process.Start(new ProcessStartInfo(QuickShareExePath) { UseShellExecute = true });
                return true;
            }

            Process.Start(new ProcessStartInfo("https://www.android.com/better-together/quick-share-app/")
            {
                UseShellExecute = true
            });
            return false;
        }
        catch
        {
            return false;
        }
    }

    public static void OpenDownloadsFolder()
    {
        try
        {
            string downloads = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Downloads");
            if (Directory.Exists(downloads))
            {
                Process.Start(new ProcessStartInfo(downloads) { UseShellExecute = true });
            }
        }
        catch { }
    }

    public void ClearHistory()
    {
        History.Clear();
        SaveHistory();
    }

    private bool TryInvokeQuickShareExplorerCommand(IReadOnlyList<string> paths)
    {
        var pidls = new List<IntPtr>(paths.Count);
        IntPtr itemArrayPtr = IntPtr.Zero;
        IntPtr cmdPtr = IntPtr.Zero;

        try
        {
            foreach (var p in paths)
            {
                int hrParse = SHParseDisplayName(p, IntPtr.Zero, out IntPtr pidl, 0, out _);
                if (hrParse == 0 && pidl != IntPtr.Zero)
                {
                    pidls.Add(pidl);
                }
            }

            if (pidls.Count == 0) return false;

            int hrArray = SHCreateShellItemArrayFromIDLists((uint)pidls.Count, pidls.ToArray(), out itemArrayPtr);
            if (hrArray != 0 || itemArrayPtr == IntPtr.Zero) return false;

            var clsid = ClsidQuickShareExplorerCommand;
            var iid = IidExplorerCommand;
            // CLSCTX_ALL (0x17) supports both in-proc and Packaged COM surrogate activation
            int hrCo = CoCreateInstance(ref clsid, IntPtr.Zero, 0x17, ref iid, out cmdPtr);
            if (hrCo != 0 || cmdPtr == IntPtr.Zero) return false;

            var cmd = (IExplorerCommand)Marshal.GetObjectForIUnknown(cmdPtr);
            try { cmd.GetTitle(itemArrayPtr, out _); } catch { }
            cmd.GetState(itemArrayPtr, true, out _);
            int hrInvoke = cmd.Invoke(itemArrayPtr, IntPtr.Zero);
            return hrInvoke == 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (cmdPtr != IntPtr.Zero) Marshal.Release(cmdPtr);
            if (itemArrayPtr != IntPtr.Zero) Marshal.Release(itemArrayPtr);
            foreach (var pidl in pidls) ILFree(pidl);
        }
    }

    private async Task<bool> TryShowWindowsShareUiAsync(IReadOnlyList<string> paths)
    {
        if (_windowHandle == IntPtr.Zero) return false;

        try
        {
            // Verify files asynchronously before launching Share UI
            var storageItems = new List<IStorageItem>();
            foreach (var p in paths)
            {
                try
                {
                    if (File.Exists(p))
                    {
                        storageItems.Add(await StorageFile.GetFileFromPathAsync(p));
                    }
                    else if (Directory.Exists(p))
                    {
                        storageItems.Add(await StorageFolder.GetFolderFromPathAsync(p));
                    }
                }
                catch { }
            }

            if (storageItems.Count == 0) return false;

            var interop = DataTransferManager.As<IDataTransferManagerInterop>();
            var iid = IidDataTransferManager;
            IntPtr dtmPtr = interop.GetForWindow(_windowHandle, ref iid);
            if (dtmPtr == IntPtr.Zero) return false;

            if (_dtm == null)
            {
                _dtm = WinRT.MarshalInspectable<DataTransferManager>.FromAbi(dtmPtr);
                _dtm.DataRequested += (_, args) =>
                {
                    var req = args.Request;
                    req.Data.Properties.Title = "Win11Notch File Share";
                    req.Data.Properties.Description = "Share files";
                    if (_preparedStorageItems.Count > 0)
                    {
                        req.Data.SetStorageItems(_preparedStorageItems);
                    }
                };
            }

            _preparedStorageItems = storageItems;
            interop.ShowShareUIForWindow(_windowHandle);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void RecordHistory(List<string> paths, string badge)
    {
        long totalBytes = paths.Where(File.Exists).Sum(p => new FileInfo(p).Length);
        string firstName = Directory.Exists(paths[0]) ? new DirectoryInfo(paths[0]).Name : Path.GetFileName(paths[0]);
        string title = paths.Count == 1 ? firstName : $"{firstName} +{paths.Count - 1} more";
        string countLabel = paths.Count == 1 ? "1 item" : $"{paths.Count} items";
        string sizeLabel = totalBytes > 0 ? $" • {ShareQueueItem.FormatBytes(totalBytes)}" : string.Empty;

        History.Insert(0, new ShareHistoryEntry
        {
            Title         = title,
            FilePaths     = paths,
            SummaryText   = $"{countLabel}{sizeLabel}",
            MethodBadge   = badge,
            Timestamp     = DateTime.Now,
            TimestampText = DateTime.Now.ToString("h:mm tt")
        });

        while (History.Count > MaxHistory)
        {
            History.RemoveAt(History.Count - 1);
        }

        SaveHistory();
    }

    private void LoadHistory()
    {
        try
        {
            if (!File.Exists(_historyFilePath)) return;
            var entries = JsonSerializer.Deserialize<List<ShareHistoryEntry>>(File.ReadAllText(_historyFilePath));
            if (entries == null) return;

            foreach (var entry in entries.Take(MaxHistory))
            {
                History.Add(entry);
            }
        }
        catch { }
    }

    private void SaveHistory()
    {
        try
        {
            var json = JsonSerializer.Serialize(History.ToList(), new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_historyFilePath, json);
        }
        catch { }
    }

    private static bool IsPackagedComRegistered()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Classes\PackagedCom\ClassIndex\{AD54D7CA-052D-4730-B0F2-2BA34D3438DC}");
            return key != null;
        }
        catch
        {
            return false;
        }
    }

    private static string? FindQuickShareExecutable()
    {
        string[] candidates =
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Google", "NearbyShare", "nearby_share.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Google", "Quick Share", "nearby_share.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Google", "NearbyShare", "nearby_share.exe")
        };

        return candidates.FirstOrDefault(File.Exists);
    }
}
