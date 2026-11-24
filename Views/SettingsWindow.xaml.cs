using System.Windows;
using System.Windows.Input;
using Win11Notch.Core.Win32;
using Win11Notch.ViewModels;

namespace Win11Notch.Views;

public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _vm;

    public SettingsWindow(SettingsViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        InitializeComponent();

        Loaded += OnLoaded;
        SourceInitialized += OnSourceInitialized;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        AcrylicHelper.ApplyLegacyAcrylic(hwnd);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _vm.RefreshDiagnostics();

        // Sync radio buttons with current dock edge
        RadioTop.IsChecked   = _vm.DockEdge == "Top";
        RadioLeft.IsChecked  = _vm.DockEdge == "Left";
        RadioRight.IsChecked = _vm.DockEdge == "Right";
    }

    private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        Hide();
    }

    private void OnDockTopChecked(object sender, RoutedEventArgs e)   => _vm.DockEdge = "Top";
    private void OnDockLeftChecked(object sender, RoutedEventArgs e)  => _vm.DockEdge = "Left";
    private void OnDockRightChecked(object sender, RoutedEventArgs e) => _vm.DockEdge = "Right";
}
