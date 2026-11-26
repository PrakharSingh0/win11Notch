using System.Collections.Concurrent;
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

    [Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioMeterInformation
    {
        [PreserveSig] int GetPeakValue(out float pfPeak);
        [PreserveSig] int GetMeteringChannelCount(out uint pnChannelCount);
        [PreserveSig] int GetChannelsPeakValues(uint u32ChannelCount, [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] float[] afPeakValues);
        [PreserveSig] int QueryHardwareSupport(out uint pdwHardwareSupportMask);
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

    private static readonly Guid PKEY_Device_ContainerId_Guid = new("8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C");
    private const uint PKEY_Device_ContainerId_Pid = 2;

    private IMMDeviceEnumerator?       _deviceEnumerator;
    private NotificationClientHandler? _notificationHandler;
    private IAudioEndpointVolume?      _cachedEndpoint;
    private IAudioMeterInformation?    _cachedMeter;
    private VolumeCallbackHandler?     _callbackHandler;
    private readonly float[]           _peakChannelBuf = new float[8];
    private readonly object            _syncLock = new();

    public string ActiveDeviceName { get; private set; } = "Default Playback Device";
    public Guid   ActiveDeviceContainerId { get; private set; }

    public event Action<float, bool, bool>? VolumeChanged;
    public event Action<string>?            ActiveDeviceChanged;
    public event Action<int>?               BluetoothBatteryChanged;

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

    private int _cachedBtBattery = -1;
    private int _btScanFlag;
    private volatile bool _disposed;
    private DateTime _lastBtCheck = DateTime.MinValue;
    private readonly ConcurrentDictionary<Guid, int> _btBatteryByContainer = new();
    private readonly ConcurrentDictionary<string, int> _btBatteryByName = new(StringComparer.OrdinalIgnoreCase);

    public void RefreshActiveDevice()
    {
        if (!_disposed && _cachedEndpoint == null)
        {
            InitEndpoint();
        }
    }

    private void OnDefaultPlaybackDeviceChanged()
    {
        if (_disposed) return;
        if (Application.Current?.Dispatcher != null)
        {
            Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (!_disposed) InitEndpoint();
            });
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
            if (_disposed) return;
            IMMDevice? device = null;
            try
            {
                if (_cachedEndpoint != null)
                {
                    if (_callbackHandler != null)
                    {
                        try { _cachedEndpoint.UnregisterControlChangeNotify(_callbackHandler); } catch { }
                    }
                    ReleaseComSafe(_cachedEndpoint);
                    _cachedEndpoint = null;
                }
                if (_cachedMeter != null)
                {
                    ReleaseComSafe(_cachedMeter);
                    _cachedMeter = null;
                }

                _deviceEnumerator ??= (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();

                if (_deviceEnumerator.GetDefaultAudioEndpoint(0 /* eRender */, 1 /* eMultimedia */, out device) == 0 && device != null)
                {
                    string detectedName = QueryFriendlyName(device);
                    Guid detectedCid = QueryContainerId(device);
                    ActiveDeviceContainerId = detectedCid;

                    if (!string.IsNullOrWhiteSpace(detectedName))
                    {
                        bool changed = !string.Equals(ActiveDeviceName, detectedName, StringComparison.Ordinal);
                        ActiveDeviceName = detectedName;
                        if (changed)
                        {
                            _cachedBtBattery = -1;
                            _lastBtCheck = DateTime.MinValue;
                            ActiveDeviceChanged?.Invoke(ActiveDeviceName);
                            TriggerBluetoothBatteryScanIfStale(force: true);
                        }
                    }

                    var iid = typeof(IAudioEndpointVolume).GUID;
                    if (device.Activate(ref iid, 23 /* CLSCTX_ALL */, IntPtr.Zero, out var epvObj) == 0 && epvObj != null)
                    {
                        _cachedEndpoint = (IAudioEndpointVolume)epvObj;
                        _cachedEndpoint.RegisterControlChangeNotify(_callbackHandler!);

                        if (_cachedEndpoint.GetMasterVolumeLevelScalar(out float vol) == 0)
                        {
                            bool muted = GetMute();
                            VolumeChanged?.Invoke(vol, muted, true);
                        }
                    }

                    try
                    {
                        var meterIid = typeof(IAudioMeterInformation).GUID;
                        if (device.Activate(ref meterIid, 23 /* CLSCTX_ALL */, IntPtr.Zero, out var meterObj) == 0 && meterObj != null)
                        {
                            _cachedMeter = (IAudioMeterInformation)meterObj;
                        }
                    }
                    catch { /* audio meter optional */ }
                }
            }
            catch { /* audio device might be absent/disabled */ }
            finally
            {
                ReleaseComSafe(device);
            }
        }
    }

    private static string QueryFriendlyName(IMMDevice device)
    {
        IPropertyStore? store = null;
        try
        {
            if (device.OpenPropertyStore(0 /* STGM_READ */, out store) == 0 && store != null)
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
        finally
        {
            ReleaseComSafe(store);
        }

        return "Default Playback Device";
    }

    private static Guid QueryContainerId(IMMDevice device)
    {
        IPropertyStore? store = null;
        try
        {
            if (device.OpenPropertyStore(0 /* STGM_READ */, out store) == 0 && store != null)
            {
                var k = new PropertyKey { fmtid = PKEY_Device_ContainerId_Guid, pid = PKEY_Device_ContainerId_Pid };
                var pv = new PropVariant();
                try
                {
                    if (store.GetValue(ref k, out pv) == 0 && pv.vt == 72 /* VT_CLSID */ && pv.pwszVal != IntPtr.Zero)
                    {
                        return Marshal.PtrToStructure<Guid>(pv.pwszVal);
                    }
                }
                finally
                {
                    PropVariantClear(ref pv);
                }
            }
        }
        catch { /* fallback */ }
        finally
        {
            ReleaseComSafe(store);
        }

        return Guid.Empty;
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
            if (_disposed) return result;
            IMMDevice? defaultDev = null;
            IMMDeviceCollection? collection = null;
            try
            {
                _deviceEnumerator ??= (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();

                string defaultId = string.Empty;
                if (_deviceEnumerator.GetDefaultAudioEndpoint(0 /* eRender */, 1 /* eMultimedia */, out defaultDev) == 0 && defaultDev != null)
                {
                    defaultDev.GetId(out defaultId);
                }

                if (_deviceEnumerator.EnumAudioEndpoints(0 /* eRender */, 1 /* DEVICE_STATE_ACTIVE */, out collection) == 0 && collection != null)
                {
                    if (collection.GetCount(out uint count) == 0)
                    {
                        for (uint i = 0; i < count; i++)
                        {
                            IMMDevice? dev = null;
                            try
                            {
                                if (collection.Item(i, out dev) == 0 && dev != null && dev.GetId(out string id) == 0)
                                {
                                    string name = QueryFriendlyName(dev);
                                    Guid cid = QueryContainerId(dev);
                                    result.Add(new AudioDeviceItem
                                    {
                                        Id = id,
                                        Name = name,
                                        ContainerId = cid,
                                        IsDefault = string.Equals(id, defaultId, StringComparison.OrdinalIgnoreCase),
                                        BluetoothBatteryPercent = GetBluetoothBatteryForDevice(cid, name)
                                    });
                                }
                            }
                            finally
                            {
                                ReleaseComSafe(dev);
                            }
                        }
                    }
                }
            }
            catch { /* best effort */ }
            finally
            {
                ReleaseComSafe(collection);
                ReleaseComSafe(defaultDev);
            }
        }
        TriggerBluetoothBatteryScanIfStale(force: false);
        return result;
    }

    public bool SetDefaultPlaybackDevice(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId) || _disposed) return false;
        IPolicyConfig? policyConfig = null;
        try
        {
            policyConfig = (IPolicyConfig)new PolicyConfigClient();
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
        finally
        {
            ReleaseComSafe(policyConfig);
        }
    }

    private static bool IsLikelyWiredOrDisplayAudio(string name)
    {
        return name.Contains("Realtek", StringComparison.OrdinalIgnoreCase)
            || name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)
            || name.Contains("AMD", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Display Audio", StringComparison.OrdinalIgnoreCase)
            || name.Contains("HDMI", StringComparison.OrdinalIgnoreCase)
            || name.Contains("USB Audio", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeBtName(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

        string s = raw.Trim();
        string[] prefixes = { "Headphones (", "Headset (", "Speakers (" };
        foreach (var p in prefixes)
        {
            if (s.StartsWith(p, StringComparison.OrdinalIgnoreCase) && s.EndsWith(')'))
            {
                s = s.Substring(p.Length, s.Length - p.Length - 1);
                break;
            }
        }

        string[] suffixes = { " Hands-Free AG", " Hands-Free HF", " Hands-Free", " Avrcp Transport", " A2DP SNK", " Stereo", " Audio" };
        foreach (var suf in suffixes)
        {
            s = s.Replace(suf, string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        return s.Trim(' ', '(', ')', '-', '_');
    }

    public int GetBluetoothBatteryForDevice(Guid containerId, string audioDeviceName)
    {
        // 1. Exact hardware ContainerId match (links AudioEndpoint <-> BTHENUM HFP/A2DP DevNode)
        if (containerId != Guid.Empty && _btBatteryByContainer.TryGetValue(containerId, out int byCid) && byCid > 0)
        {
            return byCid;
        }

        if (string.IsNullOrWhiteSpace(audioDeviceName) || IsLikelyWiredOrDisplayAudio(audioDeviceName))
        {
            return -1;
        }

        // 2. Normalized device-name fallback
        string normAudio = NormalizeBtName(audioDeviceName);
        int bestPercent = -1;
        int bestMatchLen = 0;

        foreach (var kv in _btBatteryByName)
        {
            if (string.IsNullOrWhiteSpace(kv.Key) || kv.Value <= 0) continue;

            if (audioDeviceName.Contains(kv.Key, StringComparison.OrdinalIgnoreCase) ||
                kv.Key.Contains(audioDeviceName, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrWhiteSpace(normAudio) &&
                 (normAudio.Contains(kv.Key, StringComparison.OrdinalIgnoreCase) ||
                  kv.Key.Contains(normAudio, StringComparison.OrdinalIgnoreCase))))
            {
                if (kv.Key.Length > bestMatchLen)
                {
                    bestMatchLen = kv.Key.Length;
                    bestPercent = kv.Value;
                }
            }
        }

        return bestPercent;
    }

    public int GetActiveBluetoothBatteryPercent()
    {
        TriggerBluetoothBatteryScanIfStale(force: false);
        return _cachedBtBattery;
    }

    private void TriggerBluetoothBatteryScanIfStale(bool force)
    {
        if (_disposed) return;
        if (!force && (DateTime.UtcNow - _lastBtCheck).TotalSeconds <= 30) return;
        string currentAudioName = ActiveDeviceName;
        if (!force && IsLikelyWiredOrDisplayAudio(currentAudioName)) return;
        if (Interlocked.CompareExchange(ref _btScanFlag, 1, 0) != 0) return;

        _lastBtCheck = DateTime.UtcNow;
        Guid currentContainerId = ActiveDeviceContainerId;

        _ = Task.Run(async () =>
        {
            try
            {
                const string BatteryPropertyKey = "{104ea319-6ee2-4701-bd47-8ddbf425bbe5} 2";
                const string ContainerIdKey     = "System.Devices.ContainerId";
                const string IsConnectedKey     = "System.Devices.Aep.IsConnected";
                const string IsPresentKey       = "System.Devices.Present";
                string[] props = { BatteryPropertyKey, ContainerIdKey, IsConnectedKey, IsPresentKey };

                var scannedByCid  = new Dictionary<Guid, int>();
                var scannedByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

                void RecordBattery(Windows.Devices.Enumeration.DeviceInformation dev, bool requireConnected)
                {
                    if (!dev.Properties.TryGetValue(BatteryPropertyKey, out var val) || val == null)
                        return;

                    int percent = Convert.ToInt32(val);
                    if (percent <= 0 || percent > 100) return;

                    if (requireConnected)
                    {
                        bool present = !dev.Properties.TryGetValue(IsPresentKey, out var presObj)
                                       || presObj is not bool p || p;
                        bool connected = !dev.Properties.TryGetValue(IsConnectedKey, out var connObj)
                                         || connObj is not bool c || c;
                        if (!present || !connected) return;
                    }

                    Guid cid = Guid.Empty;
                    if (dev.Properties.TryGetValue(ContainerIdKey, out var cidObj))
                    {
                        if (cidObj is Guid g) cid = g;
                        else if (cidObj is string s && Guid.TryParse(s, out var parsed)) cid = parsed;
                    }

                    if (cid != Guid.Empty)
                    {
                        scannedByCid[cid] = percent;
                    }

                    string norm = NormalizeBtName(dev.Name);
                    if (!string.IsNullOrWhiteSpace(norm))
                    {
                        scannedByName[norm] = percent;
                    }
                }

                // 1. Query PnP Device Nodes (DeviceInformationKind.Device) — where Windows HFP AG Audio & BTHLE battery levels live
                try
                {
                    var pnpDevs = await Windows.Devices.Enumeration.DeviceInformation.FindAllAsync(
                        string.Empty,
                        props,
                        Windows.Devices.Enumeration.DeviceInformationKind.Device);
                    if (pnpDevs != null)
                    {
                        foreach (var dev in pnpDevs)
                        {
                            RecordBattery(dev, requireConnected: true);
                        }
                    }
                }
                catch { }

                // 2. Also query connected Classic Bluetooth & BLE AssociationEndpoints as backup
                var aepSelectors = new[]
                {
                    Windows.Devices.Bluetooth.BluetoothDevice.GetDeviceSelectorFromConnectionStatus(
                        Windows.Devices.Bluetooth.BluetoothConnectionStatus.Connected),
                    Windows.Devices.Bluetooth.BluetoothLEDevice.GetDeviceSelectorFromConnectionStatus(
                        Windows.Devices.Bluetooth.BluetoothConnectionStatus.Connected)
                };

                foreach (var selector in aepSelectors)
                {
                    try
                    {
                        var devs = await Windows.Devices.Enumeration.DeviceInformation.FindAllAsync(
                            selector,
                            props,
                            Windows.Devices.Enumeration.DeviceInformationKind.AssociationEndpoint);
                        if (devs == null) continue;

                        foreach (var dev in devs)
                        {
                            RecordBattery(dev, requireConnected: true);
                        }
                    }
                    catch { }
                }

                bool cidChanged = scannedByCid.Count != _btBatteryByContainer.Count ||
                                  scannedByCid.Any(kv => !_btBatteryByContainer.TryGetValue(kv.Key, out int prev) || prev != kv.Value);
                bool nameChanged = scannedByName.Count != _btBatteryByName.Count ||
                                   scannedByName.Any(kv => !_btBatteryByName.TryGetValue(kv.Key, out int prev) || prev != kv.Value);

                _btBatteryByContainer.Clear();
                foreach (var kv in scannedByCid) _btBatteryByContainer[kv.Key] = kv.Value;

                _btBatteryByName.Clear();
                foreach (var kv in scannedByName) _btBatteryByName[kv.Key] = kv.Value;

                int resolved = GetBluetoothBatteryForDevice(ActiveDeviceContainerId, ActiveDeviceName);
                bool activeChanged = _cachedBtBattery != resolved;
                _cachedBtBattery = resolved;

                if ((activeChanged || cidChanged || nameChanged) && !_disposed)
                {
                    if (Application.Current?.Dispatcher != null)
                    {
                        _ = Application.Current.Dispatcher.InvokeAsync(() => BluetoothBatteryChanged?.Invoke(resolved));
                    }
                    else
                    {
                        BluetoothBatteryChanged?.Invoke(resolved);
                    }
                }
            }
            catch { }
            finally
            {
                Interlocked.Exchange(ref _btScanFlag, 0);
            }
        });
    }

    public (float Left, float Right, float Peak) GetAudioPeakLevels()
    {
        lock (_syncLock)
        {
            if (_cachedMeter == null) return (0f, 0f, 0f);
            try
            {
                float peak = 0f;
                _cachedMeter.GetPeakValue(out peak);

                if (_cachedMeter.GetMeteringChannelCount(out uint ch) == 0 && ch >= 2)
                {
                    uint count = Math.Min(ch, (uint)_peakChannelBuf.Length);
                    if (_cachedMeter.GetChannelsPeakValues(count, _peakChannelBuf) == 0)
                    {
                        return (
                            Math.Clamp(_peakChannelBuf[0], 0f, 1f),
                            Math.Clamp(_peakChannelBuf[1], 0f, 1f),
                            Math.Clamp(peak, 0f, 1f));
                    }
                }

                float clamped = Math.Clamp(peak, 0f, 1f);
                return (clamped, clamped, clamped);
            }
            catch
            {
                return (0f, 0f, 0f);
            }
        }
    }

    public void Dispose()
    {
        lock (_syncLock)
        {
            _disposed = true;
            try
            {
                if (_cachedMeter != null)
                {
                    ReleaseComSafe(_cachedMeter);
                    _cachedMeter = null;
                }
                if (_cachedEndpoint != null)
                {
                    if (_callbackHandler != null)
                    {
                        _cachedEndpoint.UnregisterControlChangeNotify(_callbackHandler);
                    }
                    ReleaseComSafe(_cachedEndpoint);
                    _cachedEndpoint = null;
                }

                if (_deviceEnumerator != null)
                {
                    if (_notificationHandler != null)
                    {
                        _deviceEnumerator.UnregisterEndpointNotificationCallback(_notificationHandler);
                    }
                    ReleaseComSafe(_deviceEnumerator);
                    _deviceEnumerator = null;
                }
            }
            catch { /* best effort */ }
        }
    }

    private static void ReleaseComSafe(object? comObj)
    {
        if (comObj != null && Marshal.IsComObject(comObj))
        {
            try { Marshal.ReleaseComObject(comObj); } catch { }
        }
    }
}
