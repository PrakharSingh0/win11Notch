using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using Win11Notch.Core.Settings;

namespace Win11Notch.Services;

/// <summary>
/// Instant screen capture tool.
/// Snaps the active target monitor and immediately stages the image file in the Notch Shelf.
/// </summary>
public sealed class ScreenCaptureService
{
    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    private readonly ShelfService    _shelfService;
    private readonly MonitorService  _monitorService;
    private readonly SettingsService _settingsService;
    private readonly string          _screenshotsFolder;

    public ScreenCaptureService(ShelfService shelfService, MonitorService monitorService, SettingsService settingsService)
    {
        _shelfService    = shelfService;
        _monitorService  = monitorService;
        _settingsService = settingsService;
        try
        {
            _screenshotsFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Win11Notch", "Screenshots");
            Directory.CreateDirectory(_screenshotsFolder);
        }
        catch
        {
            try
            {
                _screenshotsFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Screenshots");
                Directory.CreateDirectory(_screenshotsFolder);
            }
            catch
            {
                _screenshotsFolder = Path.GetTempPath();
            }
        }
    }

    public string CaptureScreenToShelf()
    {
        try
        {
            var target = _monitorService.GetTargetMonitor(_settingsService.Current.TargetMonitorIndex);
            int srcX   = target?.PhysLeft ?? 0;
            int srcY   = target?.PhysTop  ?? 0;
            int width  = target is { PhysWidth:  > 0 } ? target.PhysWidth  : GetSystemMetrics(SM_CXSCREEN);
            int height = target is { PhysHeight: > 0 } ? target.PhysHeight : GetSystemMetrics(SM_CYSCREEN);

            string fileName = $"Snip_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png";
            string filePath = Path.Combine(_screenshotsFolder, fileName);

            using (var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.CopyFromScreen(srcX, srcY, 0, 0, new System.Drawing.Size(width, height), CopyPixelOperation.SourceCopy);
                }
                bmp.Save(filePath, ImageFormat.Png);
            }

            // Add directly to the Notch Drop Shelf
            _shelfService.AddFiles(new[] { filePath });
            return filePath;
        }
        catch
        {
            return string.Empty;
        }
    }

    public void OpenScreenshotsFolder()
    {
        try
        {
            Directory.CreateDirectory(_screenshotsFolder);
            Process.Start(new ProcessStartInfo(_screenshotsFolder) { UseShellExecute = true });
        }
        catch { /* best effort */ }
    }
}
