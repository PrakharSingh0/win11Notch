using System.Runtime.InteropServices;
using System.Windows;

namespace Win11Notch.Services;

/// <summary>
/// Controls system master audio volume, mute status, and actively listens
/// to hardware volume events via IAudioEndpointVolumeCallback.
/// Listens to default audio device changes via IMMNotificationClient.
/// Reads active default playback device friendly name.
/// </summary>
public sealed class AudioService : IDisposable
{
    public static readonly Guid Win11NotchContextGuid = Guid.NewGuid();

    // Correct CoClass CLSID for MMDeviceEnumerator
    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject { }

    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig]
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
        [PreserveSig]
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
        [PreserveSig]
        int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string pwstrId, out IMMDevice endpoint);
        [PreserveSig]
        int RegisterEndpointNotificationCallback(IMMNotificationClient pClient);
        [PreserveSig]
        int UnregisterEndpointNotificationCallback(IMMNotificationClient pClient);
    }

    [Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig]
        int GetCount(out uint pcDevices);
        [PreserveSig]
        int Item(uint nDevice, out IMMDevice ppDevice);
    }

    [ComImport]
    [Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
    private class PolicyConfigClient { }

    [Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat(string pwstrDeviceId, out IntPtr pFormat);
        [PreserveSig] int GetDeviceFormat(string pwstrDeviceId, int bDefault, out IntPtr pFormat);
        [PreserveSig] int ResetDeviceFormat(string pwstrDeviceId);
        [PreserveSig] int SetDeviceFormat(string pwstrDeviceId, IntPtr pEndpointFormat, IntPtr pMixFormat);
        [PreserveSig] int GetProcessingPeriod(string pwstrDeviceId, int bDefault, out long pmftDefaultPeriod, out long pmftMinimumPeriod);
        [PreserveSig] int SetProcessingPeriod(string pwstrDeviceId, ref long pmftPeriod);
        [PreserveSig] int GetShareMode(string pwstrDeviceId, out IntPtr pMode);
        [PreserveSig] int SetShareMode(string pwstrDeviceId, IntPtr pMode);
        [PreserveSig] int GetPropertyValue(string pwstrDeviceId, ref PropertyKey pKey, out PropVariant pv);
        [PreserveSig] int SetPropertyValue(string pwstrDeviceId, ref PropertyKey pKey, ref PropVariant pv);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string pwstrDeviceId, int role);
        [PreserveSig] int SetEndpointVisibility(string pwstrDeviceId, int bVisible);
    }

    [Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMNotificationClient
    {
        [PreserveSig]
        int OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string pwstrDeviceId, uint dwNewState);
        [PreserveSig]
        int OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string pwstrDeviceId);
        [PreserveSig]
        int OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string pwstrDeviceId);
        [PreserveSig]
        int OnDefaultDeviceChanged(int flow, int role, [MarshalAs(UnmanagedType.LPWStr)] string? pwstrDefaultDeviceId);
        [PreserveSig]
        int OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string pwstrDeviceId, PropertyKey key);
    }

    [Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig]
        int Activate(ref Guid id, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object interfacePointer);
        [PreserveSig]
        int OpenPropertyStore(int stgmAccess, out IPropertyStore properties);
        [PreserveSig]
        int GetId([MarshalAs(UnmanagedType.LPWStr)] out string ppstrId);
        [PreserveSig]
        int GetState(out uint pdwState);
    }

    [Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        int GetCount(out int count);
        int GetAt(int iProp, out IntPtr pkey);
        [PreserveSig]
        int GetValue(ref PropertyKey key, out PropVariant pv);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid fmtid;
        public uint pid;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public IntPtr pwszVal;
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant pvar);

    // Correct Windows SDK GUID for IAudioEndpointVolumeCallback: 657804FA-D6AD-4496-8A60-352752AF4F89
    [Guid("657804FA-D6AD-4496-8A60-352752AF4F89"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolumeCallback
    {
        [PreserveSig]
        int OnNotify(IntPtr pNotify);
    }

    [Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig]
        int RegisterControlChangeNotify(IAudioEndpointVolumeCallback client);
        [PreserveSig]
        int UnregisterControlChangeNotify(IAudioEndpointVolumeCallback client);
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

    [StructLayout(LayoutKind.Sequential)]
    private struct AUDIO_VOLUME_NOTIFICATION_DATA
    {
        public Guid  GuidEventContext;
        public int   bMuted;
        public float MasterVolume;
        public uint  Channels;
    }

    private class VolumeCallbackHandler : IAudioEndpointVolumeCallback
    {
        private readonly AudioService _owner;
        public VolumeCallbackHandler(AudioService owner) => _owner = owner;

        public int OnNotify(IntPtr pNotify)
        {
            try
            {
                if (pNotify != IntPtr.Zero)
                {
                    var data = Marshal.PtrToStructure<AUDIO_VOLUME_NOTIFICATION_DATA>(pNotify);
                    bool isSelfTriggered = data.GuidEventContext == Win11NotchContextGuid;
                    _owner.OnHardwareVolumeChanged(data.MasterVolume, data.bMuted != 0, isSelfTriggered);
                }
            }
            catch { /* best effort */ }
            return 0;
        }
    }

    private class NotificationClientHandler : IMMNotificationClient
    {
        private readonly AudioService _owner;
        public NotificationClientHandler(AudioService owner) => _owner = owner;

        public int OnDefaultDeviceChanged(int flow, int role, string? pwstrDefaultDeviceId)
        {
            if (flow == 0 /* eRender */ && role == 1 /* eMultimedia */)
            {
                _owner.OnDefaultPlaybackDeviceChanged();
            }
            return 0;
        }

        public int OnDeviceStateChanged(string pwstrDeviceId, uint dwNewState) => 0;
        public int OnDeviceAdded(string pwstrDeviceId) => 0;
        public int OnDeviceRemoved(string pwstrDeviceId) => 0;
        public int OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) => 0;
    }

    private static readonly Guid PKEY_Device_FriendlyName_Guid = new("A45C254E-DF1C-4EFD-8020-67D146A850E0");
    private const uint PKEY_Device_FriendlyName_Pid = 14;
    private const uint PKEY_Device_DeviceDesc_Pid   = 2;

    private static readonly Guid PKEY_DeviceInterface_FriendlyName_Guid = new("026E516E-B814-414B-83CD-856D6FEF4822");
    private const uint PKEY_DeviceInterface_FriendlyName_Pid = 2;

    private IMMDeviceEnumerator?       _deviceEnumerator;
    private NotificationClientHandler? _notificationHandler;
    private IAudioEndpointVolume?      _cachedEndpoint;
    private VolumeCallbackHandler?     _callbackHandler;
    private readonly object            _syncLock = new();

    public string ActiveDeviceName { get; private set; } = "Default Playback Device";

    public event Action<float, bool, bool>? VolumeChanged;
    public event Action<string>?            ActiveDeviceChanged;

    public AudioService()
    {
        _callbackHandler     = new VolumeCallbackHandler(this);
        _notificationHandler = new NotificationClientHandler(this);

        try
        {
            _deviceEnumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            _deviceEnumerator.RegisterEndpointNotificationCallback(_notificationHandler);
        }
        catch { /* best effort */ }

        InitEndpoint();
    }

    public void RefreshActiveDevice()
    {
        InitEndpoint();
    }

    private void OnDefaultPlaybackDeviceChanged()
    {
        if (Application.Current?.Dispatcher != null)
        {
            Application.Current.Dispatcher.InvokeAsync(InitEndpoint);
        }
        else
        {
            InitEndpoint();
        }
    }

    private void InitEndpoint()
    {
        lock (_syncLock)
        {
            try
            {
                // Clean up previous endpoint callback
                if (_cachedEndpoint != null && _callbackHandler != null)
                {
                    try { _cachedEndpoint.UnregisterControlChangeNotify(_callbackHandler); } catch { }
                    _cachedEndpoint = null;
                }

                _deviceEnumerator ??= (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();

                if (_deviceEnumerator.GetDefaultAudioEndpoint(0 /* eRender */, 1 /* eMultimedia */, out var device) == 0 && device != null)
                {
                    // Query friendly device name
                    string detectedName = QueryFriendlyName(device);
                    if (!string.IsNullOrWhiteSpace(detectedName))
                    {
                        ActiveDeviceName = detectedName;
                        ActiveDeviceChanged?.Invoke(ActiveDeviceName);
                    }

                    // Activate endpoint volume
                    var iid = typeof(IAudioEndpointVolume).GUID;
                    if (device.Activate(ref iid, 23 /* CLSCTX_ALL */, IntPtr.Zero, out var epvObj) == 0 && epvObj != null)
                    {
                        _cachedEndpoint = (IAudioEndpointVolume)epvObj;
                        _cachedEndpoint.RegisterControlChangeNotify(_callbackHandler!);

                        // Trigger initial volume sync
                        if (_cachedEndpoint.GetMasterVolumeLevelScalar(out float vol) == 0)
                        {
                            bool muted = GetMute();
                            VolumeChanged?.Invoke(vol, muted, true);
                        }
                    }
                }
            }
            catch { /* audio device might be absent/disabled */ }
        }
    }

    private static string QueryFriendlyName(IMMDevice device)
    {
        try
        {
            if (device.OpenPropertyStore(0 /* STGM_READ */, out var store) == 0 && store != null)
            {
                var candidates = new[]
                {
                    new PropertyKey { fmtid = PKEY_Device_FriendlyName_Guid, pid = PKEY_Device_FriendlyName_Pid },
                    new PropertyKey { fmtid = PKEY_Device_FriendlyName_Guid, pid = PKEY_Device_DeviceDesc_Pid },
                    new PropertyKey { fmtid = PKEY_DeviceInterface_FriendlyName_Guid, pid = PKEY_DeviceInterface_FriendlyName_Pid }
                };

                foreach (var key in candidates)
                {
                    var k = key;
                    var pv = new PropVariant();
                    try
                    {
                        if (store.GetValue(ref k, out pv) == 0 && pv.pwszVal != IntPtr.Zero)
                        {
                            var name = Marshal.PtrToStringUni(pv.pwszVal);
                            if (!string.IsNullOrWhiteSpace(name))
                            {
                                return name;
                            }
                        }
                    }
                    finally
                    {
                        PropVariantClear(ref pv);
                    }
                }
            }
        }
        catch { /* fallback */ }

        return "Default Playback Device";
    }

    private void OnHardwareVolumeChanged(float volume, bool isMuted, bool isSelfTriggered)
    {
        if (Application.Current?.Dispatcher != null)
        {
            Application.Current.Dispatcher.InvokeAsync(() => VolumeChanged?.Invoke(volume, isMuted, isSelfTriggered));
        }
        else
        {
            VolumeChanged?.Invoke(volume, isMuted, isSelfTriggered);
        }
    }

    public float GetMasterVolume()
    {
        lock (_syncLock)
        {
            if (_cachedEndpoint != null && _cachedEndpoint.GetMasterVolumeLevelScalar(out float level) == 0)
            {
                return level;
            }
            return 0.5f;
        }
    }

    public void SetMasterVolume(float level)
    {
        lock (_syncLock)
        {
            if (_cachedEndpoint != null)
            {
                var guid = Win11NotchContextGuid;
                _cachedEndpoint.SetMasterVolumeLevelScalar(Math.Clamp(level, 0.0f, 1.0f), ref guid);
            }
        }
    }

    public bool GetMute()
    {
        lock (_syncLock)
        {
            if (_cachedEndpoint != null && _cachedEndpoint.GetMute(out bool muted) == 0)
            {
                return muted;
            }
            return false;
        }
    }

    public void ToggleMute()
    {
        lock (_syncLock)
        {
            if (_cachedEndpoint != null)
            {
                bool current = GetMute();
                var guid = Win11NotchContextGuid;
                _cachedEndpoint.SetMute(!current, ref guid);
            }
        }
    }

    public IReadOnlyList<AudioDeviceItem> GetActivePlaybackDevices()
    {
        var result = new List<AudioDeviceItem>();
        lock (_syncLock)
        {
            try
            {
                _deviceEnumerator ??= (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();

                string defaultId = string.Empty;
                if (_deviceEnumerator.GetDefaultAudioEndpoint(0 /* eRender */, 1 /* eMultimedia */, out var defaultDev) == 0 && defaultDev != null)
                {
                    defaultDev.GetId(out defaultId);
                }

                if (_deviceEnumerator.EnumAudioEndpoints(0 /* eRender */, 1 /* DEVICE_STATE_ACTIVE */, out var collection) == 0 && collection != null)
                {
                    if (collection.GetCount(out uint count) == 0)
                    {
                        for (uint i = 0; i < count; i++)
                        {
                            if (collection.Item(i, out var dev) == 0 && dev != null)
                            {
                                if (dev.GetId(out string id) == 0)
                                {
                                    string name = QueryFriendlyName(dev);
                                    result.Add(new AudioDeviceItem
                                    {
                                        Id = id,
                                        Name = name,
                                        IsDefault = string.Equals(id, defaultId, StringComparison.OrdinalIgnoreCase)
                                    });
                                }
                            }
                        }
                    }
                }
            }
            catch { /* best effort */ }
        }
        return result;
    }

    public bool SetDefaultPlaybackDevice(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) return false;
        try
        {
            var policyConfig = (IPolicyConfig)new PolicyConfigClient();
            policyConfig.SetDefaultEndpoint(deviceId, 0 /* eConsole */);
            policyConfig.SetDefaultEndpoint(deviceId, 1 /* eMultimedia */);
            policyConfig.SetDefaultEndpoint(deviceId, 2 /* eCommunications */);
            InitEndpoint();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        lock (_syncLock)
        {
            try
            {
                if (_cachedEndpoint != null && _callbackHandler != null)
                {
                    _cachedEndpoint.UnregisterControlChangeNotify(_callbackHandler);
                    _cachedEndpoint = null;
                }

                if (_deviceEnumerator != null && _notificationHandler != null)
                {
                    _deviceEnumerator.UnregisterEndpointNotificationCallback(_notificationHandler);
                    _deviceEnumerator = null;
                }
            }
            catch { /* best effort */ }
        }
    }
}
