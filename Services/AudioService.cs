using System.Runtime.InteropServices;

namespace Win11Notch.Services;

/// <summary>
/// Controls system master audio volume and mute status via native CoreAudio COM interfaces.
/// Zero external NuGet dependencies.
/// </summary>
public sealed class AudioService
{
    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291433E")]
    private class MMDeviceEnumeratorComObject { }

    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig]
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
    }

    [Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig]
        int Activate(ref Guid id, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object interfacePointer);
    }

    [Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        int RegisterControlChangeNotify(IntPtr client);
        int UnregisterControlChangeNotify(IntPtr client);
        int GetChannelCount(out int channelCount);
        int SetMasterVolumeLevel(float level, ref Guid eventContext);
        [PreserveSig]
        int SetMasterVolumeLevelScalar(float level, ref Guid eventContext);
        int GetMasterVolumeLevel(out float level);
        [PreserveSig]
        int GetMasterVolumeLevelScalar(out float level);
        int SetChannelVolumeLevel(int channelNumber, float level, ref Guid eventContext);
        int SetChannelVolumeLevelScalar(int channelNumber, float level, ref Guid eventContext);
        int GetChannelVolumeLevel(int channelNumber, out float level);
        int GetChannelVolumeLevelScalar(int channelNumber, out float level);
        [PreserveSig]
        int SetMute([MarshalAs(UnmanagedType.Bool)] bool isMuted, ref Guid eventContext);
        [PreserveSig]
        int GetMute([MarshalAs(UnmanagedType.Bool)] out bool isMuted);
    }

    private static IAudioEndpointVolume? GetEndpointVolume()
    {
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            if (enumerator.GetDefaultAudioEndpoint(0 /* eRender */, 1 /* eMultimedia */, out var device) == 0)
            {
                var iid = typeof(IAudioEndpointVolume).GUID;
                if (device.Activate(ref iid, 1 /* CLSCTX_INPROC_SERVER */, IntPtr.Zero, out var epvObj) == 0)
                {
                    return (IAudioEndpointVolume)epvObj;
                }
            }
        }
        catch { /* audio device might be absent/disabled */ }
        return null;
    }

    public float GetMasterVolume()
    {
        var epv = GetEndpointVolume();
        if (epv != null && epv.GetMasterVolumeLevelScalar(out float level) == 0)
        {
            return level;
        }
        return 0.5f;
    }

    public void SetMasterVolume(float level)
    {
        var epv = GetEndpointVolume();
        if (epv != null)
        {
            var guid = Guid.Empty;
            epv.SetMasterVolumeLevelScalar(Math.Clamp(level, 0.0f, 1.0f), ref guid);
        }
    }

    public bool GetMute()
    {
        var epv = GetEndpointVolume();
        if (epv != null && epv.GetMute(out bool muted) == 0)
        {
            return muted;
        }
        return false;
    }

    public void ToggleMute()
    {
        var epv = GetEndpointVolume();
        if (epv != null)
        {
            bool current = GetMute();
            var guid = Guid.Empty;
            epv.SetMute(!current, ref guid);
        }
    }
}
