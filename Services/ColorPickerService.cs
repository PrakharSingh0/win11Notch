using Win11Notch.Views;

namespace Win11Notch.Services;

/// <summary>
/// Launches a full-screen magnified pixel color sampler and copies the selected #RRGGBB hex
/// directly to the Windows clipboard (auto-captured by ClipboardService).
/// </summary>
public sealed class ColorPickerService
{
    public string? PickScreenColor()
    {
        try
        {
            var win = new ColorLoupeWindow();
            bool? result = win.ShowDialog();
            return result == true ? win.PickedHex : null;
        }
        catch
        {
            return null;
        }
    }
}
