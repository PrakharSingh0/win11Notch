using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Windows;
using Win11Notch.Models;

namespace Win11Notch.Services;

/// <summary>
/// Monitors CPU, RAM, GPU, VRAM, Network, and Battery telemetry on-demand on a background worker thread.
/// ZERO background polling in IdleHandle — runs ONLY while PeekExpanded or CollapsedHover is active.
/// All heavy system calls, performance counters, and network enumerations run in Task.Run,
/// completely eliminating UI thread freezes and RPC delays.
/// </summary>
public sealed class ResourceMonitorService : IDisposable
{
    private const int MaxHistory = 30;

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out long lpIdleTime, out long lpKernelTime, out long lpUserTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS lpSystemPowerStatus);

    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr ppFactory);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private unsafe struct DXGI_ADAPTER_DESC1
    {
        public fixed char Description[128];
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public nuint DedicatedVideoMemory;
        public nuint DedicatedSystemMemory;
        public nuint SharedSystemMemory;
        public uint LuidLowPart;
        public int LuidHighPart;
        public uint Flags;
    }

    private static readonly Guid IID_IDXGIFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");

    private CancellationTokenSource? _cts;
    private long _prevIdle, _prevKernel, _prevUser;

    // Network delta tracking
    private long _prevBytesRecv;
    private long _prevBytesSent;
    private DateTime _prevNetSample = DateTime.UtcNow;
    private NetworkInterface[]? _cachedNics;
    private DateTime _lastNicQuery = DateTime.MinValue;

    // GPU and Disk performance counters
    private sealed class GpuAdapterSlot
    {
        public string Name { get; init; } = string.Empty;
        public string LuidKey { get; init; } = string.Empty;
        public bool IsDiscrete { get; init; }
        public double DedicatedTotalGb { get; init; }
        public double SharedTotalGb { get; init; }
        public List<PerformanceCounter> EngineCounters { get; } = new();
        public PerformanceCounter? DedicatedVramCounter { get; set; }
        public PerformanceCounter? SharedVramCounter { get; set; }
        public List<double> History { get; } = new();
        public int CurrentPercent { get; set; }
        public double CurrentVramUsedGb { get; set; }
    }

    private readonly List<GpuAdapterSlot> _gpuSlots = new();
    private DateTime _lastGpuEngineRefresh = DateTime.MinValue;
    private List<PerformanceCounter>? _gpuCounters;
    private List<PerformanceCounter>? _vramCounters;
    private bool _gpuCountersInitialized;
    private PerformanceCounter? _diskCounter;
    private bool _diskCounterInitialized;

    // Telemetry state (read by ViewModel on UI thread)
    public int    CpuPercent           { get; private set; }
    public int    MemoryPercent        { get; private set; }
    public double RamUsedGb            { get; private set; }
    public double RamTotalGb           { get; private set; }
    public int    GpuPercent           { get; private set; }
    public double VramUsedGb           { get; private set; }
    public double VramTotalGb          { get; private set; }
    public ObservableCollection<GpuAdapterInfo> GpuAdapters { get; } = new();
    public int    DiskPercent          { get; private set; }
    public double DownloadBytesPerSec  { get; private set; }
    public double UploadBytesPerSec    { get; private set; }
    public string DownloadSpeedText    { get; private set; } = "0 B/s";
    public string UploadSpeedText      { get; private set; } = "0 B/s";
    public string NetworkName          { get; private set; } = "WiFi";
    public string NetworkDetail        { get; private set; } = "Wi-Fi - Connected";
    public int    BatteryPercent       { get; private set; } = 100;
    public bool   IsCharging           { get; private set; }
    public string BatteryStatusText    { get; private set; } = "Fully charged";
    public string BatteryHealthText    { get; private set; } = "Health 100%";

    // Storage partitions
    public ObservableCollection<DriveItem> Drives { get; } = new();
    public DriveItem? SelectedDrive { get; set; }

    // Sparkline rolling histories (last 30 samples)
    public List<double> CpuHistory      { get; } = new();
    public List<double> RamHistory      { get; } = new();
    public List<double> GpuHistory      { get; } = new();
    public List<double> DiskHistory     { get; } = new();
    public List<double> DownloadHistory { get; } = new();
    public List<double> UploadHistory   { get; } = new();

    public event Action? StatsUpdated;

    public void StartMonitoring()
    {
        if (_cts != null) return;

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _ = Task.Run(() => MonitorLoopAsync(token), token);
    }

    public void StopMonitoring()
    {
        try
        {
            _cts?.Cancel();
            _cts?.Dispose();
        }
        catch { }
        finally
        {
            _cts = null;
        }
    }

    private async Task MonitorLoopAsync(CancellationToken token)
    {
        // Safe lazy background initialization of performance counters
        InitGpuCounters();
        InitDiskCounter();

        // Initial sample immediately
        CollectAndDispatchStats();

        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(1000, token);
                if (token.IsCancellationRequested) break;
                CollectAndDispatchStats();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch { }
        }
    }

    private void InitDiskCounter()
    {
        if (_diskCounterInitialized) return;
        _diskCounterInitialized = true;
        try
        {
            _diskCounter = new PerformanceCounter("PhysicalDisk", "% Disk Time", "_Total", true);
            _ = _diskCounter.NextValue();
        }
        catch { }
    }

    private void InitGpuCounters()
    {
        if (_gpuCountersInitialized) return;
        _gpuCountersInitialized = true;

        EnumerateDxgiAdapters();

        try
        {
            var catVram = new PerformanceCounterCategory("GPU Adapter Memory");
            var namesVram = catVram.GetInstanceNames();

            if (_gpuSlots.Count > 0)
            {
                foreach (var name in namesVram)
                {
                    var slot = _gpuSlots.FirstOrDefault(s =>
                        !string.IsNullOrEmpty(s.LuidKey) &&
                        name.Contains(s.LuidKey, StringComparison.OrdinalIgnoreCase));

                    if (slot != null)
                    {
                        try
                        {
                            var ded = new PerformanceCounter("GPU Adapter Memory", "Dedicated Usage", name, true);
                            _ = ded.NextValue();
                            slot.DedicatedVramCounter = ded;
                        }
                        catch { }

                        try
                        {
                            var sh = new PerformanceCounter("GPU Adapter Memory", "Shared Usage", name, true);
                            _ = sh.NextValue();
                            slot.SharedVramCounter = sh;
                        }
                        catch { }
                    }
                }
            }
            else
            {
                _vramCounters = new List<PerformanceCounter>();
                foreach (var name in namesVram)
                {
                    try
                    {
                        var pc = new PerformanceCounter("GPU Adapter Memory", "Dedicated Usage", name, true);
                        _ = pc.NextValue();
                        _vramCounters.Add(pc);
                    }
                    catch { }
                }
            }
        }
        catch { /* GPU Adapter Memory counters may be restricted */ }

        RefreshGpuEngineCounters();

        // Legacy total VRAM fallback if DXGI didn't enumerate any slots
        if (_gpuSlots.Count == 0)
        {
            try
            {
                using var classKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\ControlSet001\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
                if (classKey != null)
                {
                    ulong maxBytes = 0;
                    foreach (var subName in classKey.GetSubKeyNames())
                    {
                        if (!char.IsDigit(subName[0])) continue;
                        try
                        {
                            using var sub = classKey.OpenSubKey(subName);
                            if (sub == null) continue;
                            var qw = sub.GetValue("HardwareInformation.qwMemorySize");
                            if (qw is long l && l > 0) maxBytes = Math.Max(maxBytes, (ulong)l);
                            else if (qw is byte[] b && b.Length == 8) maxBytes = Math.Max(maxBytes, BitConverter.ToUInt64(b, 0));
                            else if (sub.GetValue("HardwareInformation.MemorySize") is int dw && dw > 0) maxBytes = Math.Max(maxBytes, (ulong)(uint)dw);
                        }
                        catch { }
                    }
                    if (maxBytes > 0)
                    {
                        VramTotalGb = Math.Round(maxBytes / (1024.0 * 1024.0 * 1024.0), 1);
                    }
                }
            }
            catch { }
        }
    }

    private unsafe void EnumerateDxgiAdapters()
    {
        IntPtr factory = IntPtr.Zero;
        try
        {
            var iid = IID_IDXGIFactory1;
            if (CreateDXGIFactory1(ref iid, out factory) != 0 || factory == IntPtr.Zero)
                return;

            var factoryVtbl = *(IntPtr**)factory;
            var enumAdapters1 = (delegate* unmanaged[Stdcall]<IntPtr, uint, out IntPtr, int>)factoryVtbl[12];

            uint i = 0;
            while (enumAdapters1(factory, i, out IntPtr adapter) == 0 && adapter != IntPtr.Zero)
            {
                try
                {
                    var adapterVtbl = *(IntPtr**)adapter;
                    var getDesc1 = (delegate* unmanaged[Stdcall]<IntPtr, out DXGI_ADAPTER_DESC1, int>)adapterVtbl[10];

                    if (getDesc1(adapter, out var desc) == 0)
                    {
                        // Skip software renderers (e.g. Microsoft Basic Render Driver)
                        bool isSoftware = (desc.Flags & 2u) != 0 || (desc.VendorId == 0x1414 && desc.DeviceId == 0x008C);
                        if (!isSoftware)
                        {
                            string name = Marshal.PtrToStringUni((IntPtr)desc.Description)?.Trim() ?? $"GPU {i}";
                            double dedGb = Math.Round(desc.DedicatedVideoMemory / (1024.0 * 1024.0 * 1024.0), 1);
                            double shGb = Math.Round(desc.SharedSystemMemory / (1024.0 * 1024.0 * 1024.0), 1);
                            bool isDiscrete = dedGb >= 0.5;
                            string luidKey = $"luid_0x{(uint)desc.LuidHighPart:X8}_0x{desc.LuidLowPart:X8}";

                            if (!_gpuSlots.Any(s => string.Equals(s.LuidKey, luidKey, StringComparison.OrdinalIgnoreCase)))
                            {
                                _gpuSlots.Add(new GpuAdapterSlot
                                {
                                    Name = name,
                                    LuidKey = luidKey,
                                    IsDiscrete = isDiscrete,
                                    DedicatedTotalGb = dedGb,
                                    SharedTotalGb = shGb
                                });
                            }
                        }
                    }
                }
                finally
                {
                    var adapterVtbl = *(IntPtr**)adapter;
                    var releaseAdapter = (delegate* unmanaged[Stdcall]<IntPtr, uint>)adapterVtbl[2];
                    releaseAdapter(adapter);
                }
                i++;
            }

            // Order discrete GPU(s) first, then integrated GPU(s)
            _gpuSlots.Sort((a, b) => b.IsDiscrete.CompareTo(a.IsDiscrete));
        }
        catch { /* DXGI unavailable */ }
        finally
        {
            if (factory != IntPtr.Zero)
            {
                var factoryVtbl = *(IntPtr**)factory;
                var releaseFactory = (delegate* unmanaged[Stdcall]<IntPtr, uint>)factoryVtbl[2];
                releaseFactory(factory);
            }
        }
    }

    private void RefreshGpuEngineCounters()
    {
        _lastGpuEngineRefresh = DateTime.UtcNow;
        try
        {
            var catGpu = new PerformanceCounterCategory("GPU Engine");
            var namesGpu = catGpu.GetInstanceNames();

            if (_gpuSlots.Count > 0)
            {
                foreach (var slot in _gpuSlots)
                {
                    foreach (var c in slot.EngineCounters) { try { c.Dispose(); } catch { } }
                    slot.EngineCounters.Clear();
                }

                foreach (var name in namesGpu)
                {
                    if (!name.Contains("engtype_3D", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var slot = _gpuSlots.FirstOrDefault(s =>
                        !string.IsNullOrEmpty(s.LuidKey) &&
                        name.Contains(s.LuidKey, StringComparison.OrdinalIgnoreCase));

                    if (slot != null)
                    {
                        try
                        {
                            var pc = new PerformanceCounter("GPU Engine", "Utilization Percentage", name, true);
                            _ = pc.NextValue();
                            slot.EngineCounters.Add(pc);
                        }
                        catch { }
                    }
                }
            }
            else
            {
                if (_gpuCounters != null)
                {
                    foreach (var c in _gpuCounters) { try { c.Dispose(); } catch { } }
                }
                _gpuCounters = new List<PerformanceCounter>();
                foreach (var name in namesGpu)
                {
                    if (name.Contains("engtype_3D", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            var pc = new PerformanceCounter("GPU Engine", "Utilization Percentage", name, true);
                            _ = pc.NextValue();
                            _gpuCounters.Add(pc);
                        }
                        catch { }
                    }
                }
            }
        }
        catch { }
    }

    private void CollectAndDispatchStats()
    {
        // ── 1. CPU Load ──────────────────────────────────────────────────
        int cpu = CpuPercent;
        if (GetSystemTimes(out long idle, out long kernel, out long user))
        {
            if (_prevKernel != 0)
            {
                long usrDiff = user - _prevUser;
                long kerDiff = kernel - _prevKernel;
                long idlDiff = idle - _prevIdle;

                long sysTotal = usrDiff + kerDiff;
                if (sysTotal > 0)
                {
                    long busy = sysTotal - idlDiff;
                    cpu = Math.Clamp((int)((busy * 100) / sysTotal), 0, 100);
                }
            }
            _prevIdle   = idle;
            _prevKernel = kernel;
            _prevUser   = user;
        }

        // ── 2. RAM ───────────────────────────────────────────────────────
        int memPercent = MemoryPercent;
        double ramTotal = RamTotalGb;
        double ramUsed = RamUsedGb;
        var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (GlobalMemoryStatusEx(ref mem))
        {
            memPercent = (int)mem.dwMemoryLoad;
            ramTotal   = Math.Round(mem.ullTotalPhys / (1024.0 * 1024.0 * 1024.0), 1);
            ramUsed    = Math.Round((mem.ullTotalPhys - mem.ullAvailPhys) / (1024.0 * 1024.0 * 1024.0), 1);
        }

        // ── 3. GPU & VRAM (Per-Adapter DXGI + LUID Telemetry) ──────────────
        int gpu = GpuPercent;
        double vram = VramUsedGb;
        double vramTotal = VramTotalGb;
        var gpuSnapshots = new List<GpuAdapterInfo>();

        if (_gpuSlots.Count > 0)
        {
            if ((DateTime.UtcNow - _lastGpuEngineRefresh).TotalSeconds > 15)
            {
                RefreshGpuEngineCounters();
            }

            foreach (var slot in _gpuSlots)
            {
                float sum = 0;
                foreach (var c in slot.EngineCounters)
                {
                    try { sum += c.NextValue(); } catch { }
                }
                int pct = Math.Clamp((int)Math.Round(sum), 0, 100);
                slot.CurrentPercent = pct;
                AppendHistory(slot.History, pct);

                double dedUsed = 0;
                if (slot.DedicatedVramCounter != null)
                {
                    try { dedUsed = slot.DedicatedVramCounter.NextValue() / (1024.0 * 1024.0 * 1024.0); } catch { }
                }

                double shUsed = 0;
                if (slot.SharedVramCounter != null)
                {
                    try { shUsed = slot.SharedVramCounter.NextValue() / (1024.0 * 1024.0 * 1024.0); } catch { }
                }

                double usedGb = slot.IsDiscrete
                    ? Math.Round(dedUsed, 2)
                    : Math.Round(slot.DedicatedTotalGb >= 0.25 ? dedUsed : (dedUsed + shUsed), 2);
                double totalGb = slot.IsDiscrete
                    ? slot.DedicatedTotalGb
                    : (slot.DedicatedTotalGb >= 0.25 ? slot.DedicatedTotalGb : slot.SharedTotalGb);

                slot.CurrentVramUsedGb = usedGb;

                int vramPct = totalGb > 0 ? Math.Clamp((int)Math.Round((usedGb * 100.0) / totalGb), 0, 100) : 0;
                string vramText = slot.IsDiscrete
                    ? (totalGb > 0 ? $"VRAM {usedGb:F1} / {totalGb:F1} GB" : $"VRAM {usedGb:F1} GB")
                    : (totalGb > 0 ? $"Shared {usedGb:F1} / {totalGb:F1} GB" : $"Shared {usedGb:F1} GB");

                gpuSnapshots.Add(new GpuAdapterInfo
                {
                    Name = slot.Name,
                    LuidKey = slot.LuidKey,
                    IsDiscrete = slot.IsDiscrete,
                    GpuPercent = pct,
                    VramUsedGb = usedGb,
                    VramTotalGb = totalGb,
                    VramPercent = vramPct,
                    VramText = vramText,
                    RoleLabel = slot.IsDiscrete ? "dGPU" : "iGPU",
                    AccentHex = slot.IsDiscrete ? "#69F0AE" : "#40C4FF",
                    History = new List<double>(slot.History)
                });
            }

            int maxLoad = gpuSnapshots.Max(g => g.GpuPercent);
            foreach (var snap in gpuSnapshots)
            {
                snap.IsActive = snap.GpuPercent >= 2 && (snap.GpuPercent == maxLoad || snap.GpuPercent >= 10);
            }

            // Summary aggregate favors the active/busiest GPU (or discrete GPU when both idle)
            var primarySnap = gpuSnapshots.FirstOrDefault(g => g.IsActive)
                              ?? gpuSnapshots.FirstOrDefault(g => g.IsDiscrete)
                              ?? gpuSnapshots[0];
            gpu = maxLoad;
            vram = primarySnap.VramUsedGb;
            vramTotal = primarySnap.VramTotalGb;
        }
        else
        {
            if (_gpuCounters != null && _gpuCounters.Count > 0)
            {
                float gpuSum = 0;
                foreach (var c in _gpuCounters)
                {
                    try { gpuSum += c.NextValue(); } catch { }
                }
                gpu = Math.Clamp((int)gpuSum, 0, 100);
            }

            if (_vramCounters != null && _vramCounters.Count > 0)
            {
                float vramBytes = 0;
                foreach (var c in _vramCounters)
                {
                    try { vramBytes += c.NextValue(); } catch { }
                }
                vram = Math.Round(vramBytes / (1024.0 * 1024.0 * 1024.0), 2);
            }
        }

        // ── 3b. Disk Activity (% Disk Time) ──────────────────────────────
        int disk = DiskPercent;
        if (_diskCounter != null)
        {
            try
            {
                float dVal = _diskCounter.NextValue();
                disk = Math.Clamp((int)dVal, 0, 100);
            }
            catch { }
        }

        // ── 4. Network Activity ──────────────────────────────────────────
        double dnSpeed = DownloadBytesPerSec;
        double upSpeed = UploadBytesPerSec;
        string dnText = DownloadSpeedText;
        string upText = UploadSpeedText;
        string netName = NetworkName;
        string netDetail = NetworkDetail;
        double dnGraph = 4;
        double upGraph = 4;

        try
        {
            // Cache network interfaces for 10 seconds to avoid repetitive DNS/ALPC lookups
            if (_cachedNics == null || (DateTime.UtcNow - _lastNicQuery).TotalSeconds > 10)
            {
                _cachedNics = NetworkInterface.GetAllNetworkInterfaces();
                _lastNicQuery = DateTime.UtcNow;
            }

            NetworkInterface? activeNic = null;
            long currentRecv = 0;
            long currentSent = 0;

            if (_cachedNics != null)
            {
                foreach (var nic in _cachedNics)
                {
                    if (nic.OperationalStatus == OperationalStatus.Up &&
                        nic.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                    {
                        var s = nic.GetIPv4Statistics();
                        currentRecv += s.BytesReceived;
                        currentSent += s.BytesSent;

                        if (activeNic == null || nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
                        {
                            activeNic = nic;
                        }
                    }
                }
            }

            if (activeNic != null)
            {
                if (activeNic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
                {
                    netName = "WiFi";
                    double mbps = activeNic.Speed > 0 ? Math.Round(activeNic.Speed / 1_000_000.0) : 0;
                    netDetail = mbps > 0 ? $"Wi-Fi - {mbps} Mbps" : "Wi-Fi - Connected";
                }
                else if (activeNic.NetworkInterfaceType == NetworkInterfaceType.Ethernet)
                {
                    netName = "Ethernet";
                    double mbps = activeNic.Speed > 0 ? Math.Round(activeNic.Speed / 1_000_000.0) : 0;
                    netDetail = mbps >= 1000 ? $"Ethernet - {mbps / 1000:F0} Gbps" : $"Ethernet - {mbps} Mbps";
                }
                else
                {
                    netName = "Network";
                    netDetail = activeNic.Name;
                }
            }

            var now = DateTime.UtcNow;
            double elapsedSec = (now - _prevNetSample).TotalSeconds;
            if (elapsedSec > 0 && _prevBytesRecv > 0)
            {
                dnSpeed = Math.Max(0, (currentRecv - _prevBytesRecv) / elapsedSec);
                upSpeed = Math.Max(0, (currentSent - _prevBytesSent) / elapsedSec);

                dnText = FormatSpeed(dnSpeed, "");
                upText = FormatSpeed(upSpeed, "");

                double downMbps = dnSpeed / (1024.0 * 1024.0);
                double upMbps   = upSpeed / (1024.0 * 1024.0);

                dnGraph = Math.Clamp(downMbps * 20.0, 4, 100);
                upGraph = Math.Clamp(upMbps * 20.0, 4, 100);
            }

            _prevBytesRecv  = currentRecv;
            _prevBytesSent  = currentSent;
            _prevNetSample  = now;
        }
        catch { }

        // ── 5. Battery ───────────────────────────────────────────────────
        int batPercent = BatteryPercent;
        bool isChg = IsCharging;
        string batStatus = BatteryStatusText;
        string batHealth = BatteryHealthText;

        if (GetSystemPowerStatus(out var pwr))
        {
            batPercent = pwr.BatteryLifePercent <= 100 ? pwr.BatteryLifePercent : 100;
            isChg      = pwr.ACLineStatus == 1;

            if (isChg)
            {
                batHealth = "⚡ Charging";
                batStatus = batPercent >= 100 ? "Fully charged" : $"{batPercent}% charging";
            }
            else
            {
                batHealth = "Health 100%";
                if (pwr.BatteryLifeTime != 0xFFFFFFFF && pwr.BatteryLifeTime > 0)
                {
                    var ts = TimeSpan.FromSeconds(pwr.BatteryLifeTime);
                    batStatus = $"Discharging • {ts.Hours}h {ts.Minutes}m left";
                }
                else
                {
                    batStatus = $"{batPercent}% Remaining";
                }
            }
        }

        // ── 6. Storage Partitions ───────────────────────────────────────
        var driveList = new List<DriveItem>();
        try
        {
            foreach (var d in DriveInfo.GetDrives())
            {
                if (d.IsReady && (d.DriveType == DriveType.Fixed || d.DriveType == DriveType.Removable))
                {
                    double total = Math.Round(d.TotalSize / (1024.0 * 1024.0 * 1024.0), 1);
                    double free  = Math.Round(d.AvailableFreeSpace / (1024.0 * 1024.0 * 1024.0), 1);
                    double used  = Math.Max(0, total - free);
                    int percent  = total > 0 ? Math.Clamp((int)((used * 100.0) / total), 0, 100) : 0;
                    string label = !string.IsNullOrWhiteSpace(d.VolumeLabel) ? d.VolumeLabel : "Local Disk";
                    string cleanName = d.Name.TrimEnd('\\');
                    driveList.Add(new DriveItem
                    {
                        Name = d.Name,
                        DisplayName = $"{cleanName} ({label})",
                        VolumeLabel = label,
                        TotalGb = total,
                        FreeGb = free,
                        UsedGb = used,
                        UsedPercent = percent,
                        SummaryText = $"{free:F0} GB free of {total:F0} GB"
                    });
                }
            }
        }
        catch { }

        // ── Dispatch to UI Thread cleanly ────────────────────────────────
        Application.Current?.Dispatcher?.InvokeAsync(() =>
        {
            CpuPercent          = cpu;
            MemoryPercent       = memPercent;
            RamTotalGb          = ramTotal;
            RamUsedGb           = ramUsed;
            GpuPercent          = gpu;
            VramUsedGb          = vram;
            VramTotalGb         = vramTotal;
            DiskPercent         = disk;
            DownloadBytesPerSec = dnSpeed;
            UploadBytesPerSec   = upSpeed;
            DownloadSpeedText   = dnText;
            UploadSpeedText     = upText;
            NetworkName         = netName;
            NetworkDetail       = netDetail;
            BatteryPercent      = batPercent;
            IsCharging          = isChg;
            BatteryStatusText   = batStatus;
            BatteryHealthText   = batHealth;

            AppendHistory(CpuHistory, CpuPercent);
            AppendHistory(RamHistory, MemoryPercent);
            AppendHistory(GpuHistory, GpuPercent);
            AppendHistory(DiskHistory, DiskPercent);
            AppendHistory(DownloadHistory, dnGraph);
            AppendHistory(UploadHistory, upGraph);

            // Sync GpuAdapters collection on UI thread
            if (GpuAdapters.Count != gpuSnapshots.Count)
            {
                GpuAdapters.Clear();
                foreach (var g in gpuSnapshots) GpuAdapters.Add(g);
            }
            else
            {
                for (int i = 0; i < gpuSnapshots.Count; i++)
                {
                    GpuAdapters[i] = gpuSnapshots[i];
                }
            }

            // Sync Drives collection on UI thread
            if (Drives.Count != driveList.Count || !Drives.Select(x => x.Name).SequenceEqual(driveList.Select(x => x.Name)))
            {
                Drives.Clear();
                foreach (var item in driveList) Drives.Add(item);
            }
            else
            {
                for (int i = 0; i < driveList.Count; i++)
                {
                    Drives[i] = driveList[i];
                }
            }

            if (SelectedDrive == null || !Drives.Any(d => d.Name == SelectedDrive.Name))
            {
                SelectedDrive = Drives.FirstOrDefault();
            }
            else
            {
                SelectedDrive = Drives.FirstOrDefault(d => d.Name == SelectedDrive.Name);
            }

            StatsUpdated?.Invoke();
        });
    }

    private static void AppendHistory(List<double> list, double value)
    {
        list.Add(value);
        if (list.Count > MaxHistory)
        {
            list.RemoveAt(0);
        }
    }

    private static string FormatSpeed(double bytesPerSec, string prefix)
    {
        if (bytesPerSec >= 1024 * 1024)
        {
            return $"{prefix} {bytesPerSec / (1024.0 * 1024.0):F1} MB/s";
        }
        if (bytesPerSec >= 1024)
        {
            return $"{prefix} {bytesPerSec / 1024.0:F0} KB/s";
        }
        return $"{prefix} {bytesPerSec:F0} B/s";
    }

    public void Dispose()
    {
        StopMonitoring();
        foreach (var slot in _gpuSlots)
        {
            foreach (var c in slot.EngineCounters) { try { c.Dispose(); } catch { } }
            slot.EngineCounters.Clear();
            try { slot.DedicatedVramCounter?.Dispose(); } catch { }
            try { slot.SharedVramCounter?.Dispose(); } catch { }
        }
        _gpuSlots.Clear();
        if (_gpuCounters != null)
        {
            foreach (var c in _gpuCounters) { try { c.Dispose(); } catch { } }
            _gpuCounters.Clear();
        }
        if (_vramCounters != null)
        {
            foreach (var c in _vramCounters) { try { c.Dispose(); } catch { } }
            _vramCounters.Clear();
        }
        try { _diskCounter?.Dispose(); } catch { }
    }
}
