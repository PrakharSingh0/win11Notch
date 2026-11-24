using System.Runtime.InteropServices;

namespace Win11Notch.Services;

/// <summary>
/// Manages system Stay-Awake (Caffeine mode) via Win32 SetThreadExecutionState.
/// Prevents display dimming and system sleep while toggled on, and safely resets on dispose.
/// </summary>
public sealed class PowerAwakeService : IDisposable
{
    [Flags]
    private enum EXECUTION_STATE : uint
    {
        ES_SYSTEM_REQUIRED  = 0x00000001,
        ES_DISPLAY_REQUIRED = 0x00000002,
        ES_CONTINUOUS       = 0x80000000
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern EXECUTION_STATE SetThreadExecutionState(EXECUTION_STATE esFlags);

    public bool IsAwake { get; private set; }

    public event Action<bool>? AwakeStateChanged;

    public bool Toggle()
    {
        SetAwake(!IsAwake);
        return IsAwake;
    }

    public void SetAwake(bool enabled)
    {
        if (IsAwake == enabled) return;

        if (enabled)
        {
            SetThreadExecutionState(
                EXECUTION_STATE.ES_CONTINUOUS |
                EXECUTION_STATE.ES_DISPLAY_REQUIRED |
                EXECUTION_STATE.ES_SYSTEM_REQUIRED);
        }
        else
        {
            SetThreadExecutionState(EXECUTION_STATE.ES_CONTINUOUS);
        }

        IsAwake = enabled;
        AwakeStateChanged?.Invoke(IsAwake);
    }

    public void Dispose()
    {
        if (IsAwake)
        {
            SetAwake(false);
        }
    }
}
