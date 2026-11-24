using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Win11Notch.Core.Settings;
using Win11Notch.Core.State;

namespace Win11Notch.ViewModels;

/// <summary>Relay commands wired to the system tray context menu.</summary>
public sealed partial class TrayViewModel : ObservableObject
{
    private readonly NotchStateMachine _stateMachine;
    private readonly SettingsService   _settingsService;

    /// <summary>Raised when the user changes the dock edge via the tray menu.</summary>
    public event EventHandler<string>? DockEdgeChanged;

    /// <summary>Raised after a successful config reload.</summary>
    public event EventHandler? ConfigReloaded;

    public TrayViewModel(NotchStateMachine stateMachine, SettingsService settingsService)
    {
        _stateMachine    = stateMachine;
        _settingsService = settingsService;
    }

    [RelayCommand] private void TogglePeek() => _stateMachine.Toggle();

    [RelayCommand] private async Task DockTop()   => await SetDockAsync("Top");
    [RelayCommand] private async Task DockLeft()  => await SetDockAsync("Left");
    [RelayCommand] private async Task DockRight() => await SetDockAsync("Right");

    [RelayCommand]
    private async Task ReloadConfig()
    {
        await _settingsService.LoadAsync();
        ConfigReloaded?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private static void Exit() => System.Windows.Application.Current.Shutdown();

    private async Task SetDockAsync(string edge)
    {
        _settingsService.Current.DockEdge = edge;
        await _settingsService.SaveAsync();
        DockEdgeChanged?.Invoke(this, edge);
    }
}
