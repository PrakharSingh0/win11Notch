using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace Win11Notch.Services;

/// <summary>
/// Instant screen capture tool.
/// Snaps the primary monitor and immediately stages the image file in the Notch Shelf.
/// </summary>
public sealed class ScreenCaptureService
{
    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    private readonly ShelfService _shelfService;
    private readonly string _screenshotsFolder;

    public ScreenCaptureService(ShelfService shelfService)
    {
        _shelfService = shelfService;
        _screenshotsFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Win11Notch", "Screenshots");

        Directory.CreateDirectory(_screenshotsFolder);
    }

    public string CaptureScreenToShelf()
    {
        int width  = GetSystemMetrics(SM_CXSCREEN);
        int height = GetSystemMetrics(SM_CYSCREEN);

        string fileName = $"Snip_{DateTime.Now:yyyyMMdd_HHmmss}.png";
        string filePath = Path.Combine(_screenshotsFolder, fileName);

        using (var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb))
        {
            using (var g = Graphics.FromImage(bmp))
            {
                g.CopyFromScreen(0, 0, 0, 0, new System.Drawing.Size(width, height), CopyPixelOperation.SourceCopy);
            }
            bmp.Save(filePath, ImageFormat.Png);
        }

        // Add directly to the Notch Drop Shelf
        _shelfService.AddFiles(new[] { filePath });

        return filePath;
    }
}
