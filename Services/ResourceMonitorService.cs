using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace Win11Notch.Services;

/// <summary>
/// Monitors CPU %, RAM %, and Battery on-demand.
/// ZERO background polling in IdleHandle — starts 1s timer ONLY while PeekExpanded is active.
/// </summary>
public sealed class ResourceMonitorService : IDisposable
{
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

    private DispatcherTimer? _timer;
    private long _prevIdle, _prevKernel, _prevUser;

    public int CpuPercent     { get; private set; }
    public int MemoryPercent  { get; private set; }
    public int BatteryPercent { get; private set; } = 100;
    public bool IsCharging    { get; private set; }

    public event Action? StatsUpdated;

    public void StartMonitoring()
    {
        if (_timer != null) return;

        UpdateStats(); // initial sample
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => UpdateStats();
        _timer.Start();
    }

    public void StopMonitoring()
    {
        _timer?.Stop();
        _timer = null;
    }

    private void UpdateStats()
    {
        // ── CPU ──────────────────────────────────────────────────────────
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
                    CpuPercent = Math.Clamp((int)((busy * 100) / sysTotal), 0, 100);
                }
            }
            _prevIdle   = idle;
            _prevKernel = kernel;
            _prevUser   = user;
        }

        // ── RAM ──────────────────────────────────────────────────────────
        var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (GlobalMemoryStatusEx(ref mem))
        {
            MemoryPercent = (int)mem.dwMemoryLoad;
        }

        // ── Battery ──────────────────────────────────────────────────────
        if (GetSystemPowerStatus(out var pwr))
        {
            BatteryPercent = pwr.BatteryLifePercent <= 100 ? pwr.BatteryLifePercent : 100;
            IsCharging     = pwr.ACLineStatus == 1;
        }

        StatsUpdated?.Invoke();
    }

    public void Dispose() => StopMonitoring();
}
