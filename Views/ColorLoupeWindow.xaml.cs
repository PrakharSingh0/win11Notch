using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Color = System.Windows.Media.Color;
using PixelFormat = System.Drawing.Imaging.PixelFormat;

namespace Win11Notch.Views;

public partial class ColorLoupeWindow : Window
{
    private const int ZoomPatchSize = 11; // 11x11 pixels -> 99x99 px grid

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X, Y;
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    private static readonly IntPtr HWND_TOPMOST = new(-1);

    private readonly int _physLeft;
    private readonly int _physTop;
    private readonly int _physWidth;
    private readonly int _physHeight;
    private readonly Bitmap _screenBitmap;
    private readonly BitmapSource _screenSource;

    public string? PickedHex { get; private set; }

    public ColorLoupeWindow()
    {
        InitializeComponent();

        _physLeft   = GetSystemMetrics(76 /* SM_XVIRTUALSCREEN */);
        _physTop    = GetSystemMetrics(77 /* SM_YVIRTUALSCREEN */);
        _physWidth  = Math.Max(1, GetSystemMetrics(78 /* SM_CXVIRTUALSCREEN */));
        _physHeight = Math.Max(1, GetSystemMetrics(79 /* SM_CYVIRTUALSCREEN */));

        _screenBitmap = new Bitmap(_physWidth, _physHeight, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(_screenBitmap))
        {
            g.CopyFromScreen(_physLeft, _physTop, 0, 0, new System.Drawing.Size(_physWidth, _physHeight), CopyPixelOperation.SourceCopy);
        }

        _screenSource = ConvertToBitmapSource(_screenBitmap);
        BackdropImage.Source = _screenSource;

        SourceInitialized += OnSourceInitialized;
        Loaded += (_, _) => UpdateLoupeAtCursor();
        MouseMove += (_, _) => UpdateLoupeAtCursor();
        MouseLeftButtonDown += OnPickClick;
        MouseRightButtonDown += (_, _) => CancelPick();
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) CancelPick();
        };
        Closed += (_, _) => _screenBitmap.Dispose();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        SetWindowPos(hwnd, HWND_TOPMOST, _physLeft, _physTop, _physWidth, _physHeight, 0x0040 /* SWP_SHOWWINDOW */);
        Activate();
        Focus();
    }

    private void UpdateLoupeAtCursor()
    {
        if (!GetCursorPos(out var pt)) return;

        int px = Math.Clamp(pt.X - _physLeft, 0, _physWidth - 1);
        int py = Math.Clamp(pt.Y - _physTop, 0, _physHeight - 1);

        var c = _screenBitmap.GetPixel(px, py);
        string hex = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        HexText.Text = hex;
        RgbText.Text = $"{c.R}, {c.G}, {c.B}";
        ColorSwatch.Background = new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B));

        // Crop 11x11 patch centered around cursor pixel safely inside bounds
        int half = ZoomPatchSize / 2;
        int cropX = Math.Clamp(px - half, 0, Math.Max(0, _physWidth - ZoomPatchSize));
        int cropY = Math.Clamp(py - half, 0, Math.Max(0, _physHeight - ZoomPatchSize));
        int cropW = Math.Min(ZoomPatchSize, _physWidth - cropX);
        int cropH = Math.Min(ZoomPatchSize, _physHeight - cropY);

        if (cropW > 0 && cropH > 0)
        {
            var crop = new CroppedBitmap(_screenSource, new Int32Rect(cropX, cropY, cropW, cropH));
            ZoomImage.Source = crop;
        }

        // Convert physical cursor coordinates to WPF DIPs relative to overlay window
        try
        {
            var dipPt = PointFromScreen(new System.Windows.Point(pt.X, pt.Y));
            double cardW = LoupeCard.ActualWidth > 0 ? LoupeCard.ActualWidth : 120;
            double cardH = LoupeCard.ActualHeight > 0 ? LoupeCard.ActualHeight : 165;

            double x = dipPt.X + 20;
            double y = dipPt.Y + 20;
            if (x + cardW > OverlayCanvas.ActualWidth - 8) x = dipPt.X - cardW - 18;
            if (y + cardH > OverlayCanvas.ActualHeight - 8) y = dipPt.Y - cardH - 18;

            Canvas.SetLeft(LoupeCard, Math.Max(6, x));
            Canvas.SetTop(LoupeCard, Math.Max(6, y));
        }
        catch { }
    }

    private void OnPickClick(object sender, MouseButtonEventArgs e)
    {
        if (GetCursorPos(out var pt))
        {
            int px = Math.Clamp(pt.X - _physLeft, 0, _physWidth - 1);
            int py = Math.Clamp(pt.Y - _physTop, 0, _physHeight - 1);
            var c = _screenBitmap.GetPixel(px, py);
            PickedHex = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        }
        else
        {
            PickedHex = HexText.Text;
        }

        try
        {
            if (!string.IsNullOrEmpty(PickedHex))
            {
                Clipboard.SetText(PickedHex);
            }
        }
        catch { }

        DialogResult = true;
        Close();
    }

    private void CancelPick()
    {
        PickedHex = null;
        DialogResult = false;
        Close();
    }

    private static BitmapSource ConvertToBitmapSource(Bitmap bitmap)
    {
        var hBitmap = bitmap.GetHbitmap();
        try
        {
            var src = Imaging.CreateBitmapSourceFromHBitmap(
                hBitmap,
                IntPtr.Zero,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        finally
        {
            DeleteObject(hBitmap);
        }
    }
}
