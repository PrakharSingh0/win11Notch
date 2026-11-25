using System.Runtime.InteropServices;
using System.Windows;

namespace Win11Notch.Services;

/// <summary>
/// Controls display brightness across laptop internal panels (WMI root\wmi) and
/// external DDC/CI monitors (dxva2.dll) with event-driven real-time WMI change sync.
/// </summary>
public sealed class BrightnessService : IDisposable
{
    private const int WbemErrTimedOut = unchecked((int)0x80043001);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PHYSICAL_MONITOR
    {
        public IntPtr hPhysicalMonitor;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szPhysicalMonitorDescription;
    }

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, out uint pdwNumberOfPhysicalMonitors);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint dwPhysicalMonitorArraySize, [Out] PHYSICAL_MONITOR[] pPhysicalMonitorArray);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool DestroyPhysicalMonitors(uint dwPhysicalMonitorArraySize, [In] PHYSICAL_MONITOR[] pPhysicalMonitorArray);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetMonitorBrightness(IntPtr hMonitor, out uint pdwMinimumBrightness, out uint pdwCurrentBrightness, out uint pdwMaximumBrightness);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool SetMonitorBrightness(IntPtr hMonitor, uint dwNewBrightness);

    private readonly MonitorService _monitorService;
    private readonly object _writeLock = new();
    private readonly object _watcherLock = new();

    private byte[]? _supportedWmiLevels;
    private int _pendingTarget = -1;
    private bool _isWriting;
    private bool _isRefreshing;
    private bool _watcherStarted;
    private volatile bool _disposed;
    private long _lastLocalWriteTicks;

    public int  CurrentBrightness    { get; private set; } = 70;
    public bool IsBrightnessSupported { get; private set; } = true;
    public string StatusTooltip      { get; private set; } = "Adjust display brightness";

    public event Action<int>? BrightnessChanged;
    public event Action<int>? ExternalBrightnessChanged;

    public BrightnessService(MonitorService monitorService)
    {
        _monitorService = monitorService;
    }

    public void RefreshBrightnessAsync(int monitorIndex = 0)
    {
        if (_disposed || _isRefreshing) return;
        _isRefreshing = true;

        _ = Task.Run(() =>
        {
            try
            {
                var target = _monitorService.GetTargetMonitor(monitorIndex);
                bool preferDdcFirst = target is { IsPrimary: false };

                if (preferDdcFirst && target != null && target.Handle != IntPtr.Zero && TryReadDdcBrightness(target.Handle, out int extVal))
                {
                    StatusTooltip = $"Display Brightness ({extVal}% via DDC/CI)";
                    PublishBrightness(extVal, supported: true);
                    return;
                }

                if (TryReadWmiBrightness(out int wmiVal))
                {
                    EnsureWmiWatcherStarted();
                    StatusTooltip = $"Display Brightness ({wmiVal}%)";
                    PublishBrightness(wmiVal, supported: true);
                    return;
                }

                if (!preferDdcFirst && target != null && target.Handle != IntPtr.Zero && TryReadDdcBrightness(target.Handle, out int ddcVal))
                {
                    StatusTooltip = $"Display Brightness ({ddcVal}% via DDC/CI)";
                    PublishBrightness(ddcVal, supported: true);
                    return;
                }

                StatusTooltip = "Brightness unavailable (DDC/CI not supported on this display)";
                PublishBrightness(CurrentBrightness, supported: false);
            }
            catch { }
            finally
            {
                _isRefreshing = false;
            }
        });
    }

    public void SetBrightness(int percent, int monitorIndex = 0)
    {
        if (_disposed) return;

        int clamped = Math.Clamp(percent, 0, 100);
        CurrentBrightness = clamped;
        StatusTooltip = $"Display Brightness ({clamped}%)";
        Interlocked.Exchange(ref _lastLocalWriteTicks, Environment.TickCount64);

        lock (_writeLock)
        {
            _pendingTarget = clamped;
            if (_isWriting) return;
            _isWriting = true;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                while (!_disposed)
                {
                    int valueToApply;
                    lock (_writeLock)
                    {
                        valueToApply = _pendingTarget;
                        _pendingTarget = -1;
                        if (valueToApply < 0)
                        {
                            _isWriting = false;
                            break;
                        }
                    }

                    Interlocked.Exchange(ref _lastLocalWriteTicks, Environment.TickCount64);
                    var target = _monitorService.GetTargetMonitor(monitorIndex);
                    bool preferDdcFirst = target is { IsPrimary: false };
                    bool ok = false;

                    if (preferDdcFirst && target != null && target.Handle != IntPtr.Zero)
                    {
                        ok = TryWriteDdcBrightness(target.Handle, valueToApply);
                    }

                    if (!ok)
                    {
                        ok = TryWriteWmiBrightness(valueToApply);
                    }

                    if (!ok && !preferDdcFirst && target != null && target.Handle != IntPtr.Zero)
                    {
                        ok = TryWriteDdcBrightness(target.Handle, valueToApply);
                    }

                    if (ok) IsBrightnessSupported = true;
                    await Task.Delay(20);
                }
            }
            catch
            {
                lock (_writeLock) { _isWriting = false; }
            }
        });
    }

    private void PublishBrightness(int value, bool supported, bool fromExternalEvent = false)
    {
        if (_disposed) return;
        IsBrightnessSupported = supported;
        CurrentBrightness = Math.Clamp(value, 0, 100);
        if (supported) StatusTooltip = $"Display Brightness ({CurrentBrightness}%)";
        Application.Current?.Dispatcher?.InvokeAsync(() =>
        {
            BrightnessChanged?.Invoke(CurrentBrightness);
            if (fromExternalEvent)
            {
                ExternalBrightnessChanged?.Invoke(CurrentBrightness);
            }
        });
    }

    private void EnsureWmiWatcherStarted()
    {
        if (_disposed || _watcherStarted) return;
        lock (_watcherLock)
        {
            if (_disposed || _watcherStarted) return;
            _watcherStarted = true;
        }

        var thread = new Thread(WatchWmiBrightnessEvents)
        {
            IsBackground = true,
            Name = "BrightnessWmiWatcher"
        };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
    }

    private void WatchWmiBrightnessEvents()
    {
        try
        {
            var locatorType = Type.GetTypeFromProgID("WbemScripting.SWbemLocator");
            if (locatorType == null) return;

            dynamic locator = Activator.CreateInstance(locatorType)!;
            dynamic svc = locator.ConnectServer(".", @"root\wmi");
            dynamic eventSource = svc.ExecNotificationQuery("SELECT * FROM WmiMonitorBrightnessEvent");

            while (!_disposed)
            {
                try
                {
                    dynamic ev = eventSource.NextEvent(1500);
                    object? valObj = GetWmiProp(ev, "Brightness");
                    if (valObj == null) continue;

                    int newVal = Math.Clamp(Convert.ToInt32(valObj), 0, 100);
                    bool recentLocalWrite = (Environment.TickCount64 - Interlocked.Read(ref _lastLocalWriteTicks)) < 420;
                    if (recentLocalWrite && Math.Abs(newVal - CurrentBrightness) <= 3)
                        continue;

                    if (newVal != CurrentBrightness || !IsBrightnessSupported)
                    {
                        PublishBrightness(newVal, supported: true, fromExternalEvent: true);
                    }
                }
                catch (COMException cex) when (cex.ErrorCode == WbemErrTimedOut)
                {
                    // Normal timeout heartbeat to honor _disposed cleanly
                }
                catch
                {
                    break;
                }
            }
        }
        catch { }
    }

    private bool TryReadWmiBrightness(out int percent)
    {
        percent = CurrentBrightness;
        try
        {
            var locatorType = Type.GetTypeFromProgID("WbemScripting.SWbemLocator");
            if (locatorType == null) return false;

            dynamic locator = Activator.CreateInstance(locatorType)!;
            dynamic svc = locator.ConnectServer(".", @"root\wmi");
            dynamic items = svc.ExecQuery("SELECT * FROM WmiMonitorBrightness");
            foreach (var item in items)
            {
                object? activeObj = GetWmiProp(item, "Active");
                if (activeObj is bool active && !active) continue;

                object? curObj = GetWmiProp(item, "CurrentBrightness");
                if (curObj == null) continue;

                percent = Math.Clamp(Convert.ToInt32(curObj), 0, 100);
                CacheSupportedLevels(GetWmiProp(item, "Level"));
                return true;
            }
        }
        catch { }
        return false;
    }

    private bool TryWriteWmiBrightness(int percent)
    {
        try
        {
            var locatorType = Type.GetTypeFromProgID("WbemScripting.SWbemLocator");
            if (locatorType == null) return false;

            byte levelToApply = SnapToSupportedLevel(percent);
            dynamic locator = Activator.CreateInstance(locatorType)!;
            dynamic svc = locator.ConnectServer(".", @"root\wmi");
            dynamic items = svc.ExecQuery("SELECT * FROM WmiMonitorBrightnessMethods");
            bool applied = false;
            foreach (var item in items)
            {
                object? activeObj = GetWmiProp(item, "Active");
                if (activeObj is bool active && !active) continue;

                dynamic method = item.Methods_.Item("WmiSetBrightness");
                dynamic inParams = method.InParameters.SpawnInstance_();
                inParams.Properties_.Item("Timeout").Value = 1;
                inParams.Properties_.Item("Brightness").Value = levelToApply;
                item.ExecMethod_("WmiSetBrightness", inParams);
                applied = true;
            }
            return applied;
        }
        catch
        {
            return false;
        }
    }

    private void CacheSupportedLevels(object? rawLevels)
    {
        if (rawLevels is Array arr && arr.Length > 0)
        {
            var list = new List<byte>(arr.Length);
            foreach (var entry in arr)
            {
                if (entry != null) list.Add(Convert.ToByte(entry));
            }
            if (list.Count > 0) _supportedWmiLevels = list.ToArray();
        }
    }

    private byte SnapToSupportedLevel(int percent)
    {
        byte clamped = (byte)Math.Clamp(percent, 0, 100);
        var levels = _supportedWmiLevels;
        if (levels == null || levels.Length == 0) return clamped;

        byte best = levels[0];
        int bestDiff = Math.Abs(best - clamped);
        for (int i = 1; i < levels.Length; i++)
        {
            int diff = Math.Abs(levels[i] - clamped);
            if (diff < bestDiff)
            {
                best = levels[i];
                bestDiff = diff;
            }
        }
        return best;
    }

    private static object? GetWmiProp(dynamic swbemObject, string propName)
    {
        try { return swbemObject.Properties_.Item(propName).Value; }
        catch { return null; }
    }

    private static bool TryReadDdcBrightness(IntPtr hMonitor, out int percent)
    {
        percent = 70;
        try
        {
            if (!GetNumberOfPhysicalMonitorsFromHMONITOR(hMonitor, out uint count) || count == 0)
                return false;

            var monitors = new PHYSICAL_MONITOR[count];
            if (!GetPhysicalMonitorsFromHMONITOR(hMonitor, count, monitors))
                return false;

            try
            {
                if (GetMonitorBrightness(monitors[0].hPhysicalMonitor, out uint min, out uint cur, out uint max) && max > min)
                {
                    percent = Math.Clamp((int)Math.Round(((cur - min) * 100.0) / (max - min)), 0, 100);
                    return true;
                }
            }
            finally
            {
                DestroyPhysicalMonitors(count, monitors);
            }
        }
        catch { }
        return false;
    }

    private static bool TryWriteDdcBrightness(IntPtr hMonitor, int percent)
    {
        try
        {
            if (!GetNumberOfPhysicalMonitorsFromHMONITOR(hMonitor, out uint count) || count == 0)
                return false;

            var monitors = new PHYSICAL_MONITOR[count];
            if (!GetPhysicalMonitorsFromHMONITOR(hMonitor, count, monitors))
                return false;

            try
            {
                if (GetMonitorBrightness(monitors[0].hPhysicalMonitor, out uint min, out _, out uint max) && max > min)
                {
                    uint target = (uint)Math.Round(min + (Math.Clamp(percent, 0, 100) / 100.0) * (max - min));
                    return SetMonitorBrightness(monitors[0].hPhysicalMonitor, target);
                }
            }
            finally
            {
                DestroyPhysicalMonitors(count, monitors);
            }
        }
        catch { }
        return false;
    }

    public void Dispose()
    {
        _disposed = true;
    }
}
