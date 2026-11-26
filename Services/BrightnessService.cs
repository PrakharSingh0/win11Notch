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
    private int _pendingMonitorIndex;
    private bool _isWriting;
    private int _isRefreshingFlag;
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
        if (_disposed || Interlocked.CompareExchange(ref _isRefreshingFlag, 1, 0) != 0) return;

        _ = Task.Run(() =>
        {
            try
            {
                var target = _monitorService.GetTargetMonitor(monitorIndex);
                bool preferDdcFirst = target is { IsPrimary: false };

                if (preferDdcFirst && target != null && target.Handle != IntPtr.Zero && TryReadDdcBrightness(target.Handle, out int extVal))
                {
                    PublishBrightness(extVal, supported: true, customTooltip: $"Display Brightness ({extVal}% via DDC/CI)");
                    return;
                }

                if (TryReadWmiBrightness(out int wmiVal))
                {
                    EnsureWmiWatcherStarted();
                    PublishBrightness(wmiVal, supported: true, customTooltip: $"Display Brightness ({wmiVal}%)");
                    return;
                }

                if (!preferDdcFirst && target != null && target.Handle != IntPtr.Zero && TryReadDdcBrightness(target.Handle, out int ddcVal))
                {
                    PublishBrightness(ddcVal, supported: true, customTooltip: $"Display Brightness ({ddcVal}% via DDC/CI)");
                    return;
                }

                PublishBrightness(CurrentBrightness, supported: false, customTooltip: "Brightness unavailable (DDC/CI not supported on this display)");
            }
            catch { }
            finally
            {
                Interlocked.Exchange(ref _isRefreshingFlag, 0);
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
            _pendingMonitorIndex = monitorIndex;
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
                    int targetMonIdx;
                    lock (_writeLock)
                    {
                        valueToApply = _pendingTarget;
                        targetMonIdx = _pendingMonitorIndex;
                        _pendingTarget = -1;
                        if (valueToApply < 0)
                        {
                            _isWriting = false;
                            break;
                        }
                    }

                    Interlocked.Exchange(ref _lastLocalWriteTicks, Environment.TickCount64);
                    var target = _monitorService.GetTargetMonitor(targetMonIdx);
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

    private void PublishBrightness(int value, bool supported, bool fromExternalEvent = false, string? customTooltip = null)
    {
        if (_disposed) return;
        int clamped = Math.Clamp(value, 0, 100);
        string tooltip = customTooltip ?? (supported ? $"Display Brightness ({clamped}%)" : StatusTooltip);

        Application.Current?.Dispatcher?.InvokeAsync(() =>
        {
            if (_disposed) return;
            IsBrightnessSupported = supported;
            CurrentBrightness = clamped;
            StatusTooltip = tooltip;
            BrightnessChanged?.Invoke(clamped);
            if (fromExternalEvent)
            {
                ExternalBrightnessChanged?.Invoke(clamped);
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
        object? locatorObj = null;
        object? svcObj = null;
        object? eventSourceObj = null;
        try
        {
            var locatorType = Type.GetTypeFromProgID("WbemScripting.SWbemLocator");
            if (locatorType == null) return;

            locatorObj = Activator.CreateInstance(locatorType);
            if (locatorObj == null) return;
            dynamic locator = locatorObj;
            svcObj = locator.ConnectServer(".", @"root\wmi");
            dynamic svc = svcObj;
            eventSourceObj = svc.ExecNotificationQuery("SELECT * FROM WmiMonitorBrightnessEvent");
            dynamic eventSource = eventSourceObj;

            while (!_disposed)
            {
                object? evObj = null;
                try
                {
                    evObj = eventSource.NextEvent(4500);
                    if (evObj == null) continue;
                    dynamic ev = evObj;
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
                finally
                {
                    ReleaseComSafe(evObj);
                }
            }
        }
        catch { }
        finally
        {
            ReleaseComSafe(eventSourceObj);
            ReleaseComSafe(svcObj);
            ReleaseComSafe(locatorObj);
        }
    }

    private bool TryReadWmiBrightness(out int percent)
    {
        percent = CurrentBrightness;
        object? locatorObj = null;
        object? svcObj = null;
        object? itemsObj = null;
        try
        {
            var locatorType = Type.GetTypeFromProgID("WbemScripting.SWbemLocator");
            if (locatorType == null) return false;

            locatorObj = Activator.CreateInstance(locatorType);
            if (locatorObj == null) return false;
            dynamic locator = locatorObj;
            svcObj = locator.ConnectServer(".", @"root\wmi");
            dynamic svc = svcObj;
            itemsObj = svc.ExecQuery("SELECT * FROM WmiMonitorBrightness");
            dynamic items = itemsObj;
            foreach (var item in items)
            {
                try
                {
                    object? activeObj = GetWmiProp(item, "Active");
                    if (activeObj is bool active && !active) continue;

                    object? curObj = GetWmiProp(item, "CurrentBrightness");
                    if (curObj == null) continue;

                    percent = Math.Clamp(Convert.ToInt32(curObj), 0, 100);
                    CacheSupportedLevels(GetWmiProp(item, "Level"));
                    return true;
                }
                finally
                {
                    ReleaseComSafe(item);
                }
            }
        }
        catch { }
        finally
        {
            ReleaseComSafe(itemsObj);
            ReleaseComSafe(svcObj);
            ReleaseComSafe(locatorObj);
        }
        return false;
    }

    private bool TryWriteWmiBrightness(int percent)
    {
        object? locatorObj = null;
        object? svcObj = null;
        object? itemsObj = null;
        try
        {
            var locatorType = Type.GetTypeFromProgID("WbemScripting.SWbemLocator");
            if (locatorType == null) return false;

            byte levelToApply = SnapToSupportedLevel(percent);
            locatorObj = Activator.CreateInstance(locatorType);
            if (locatorObj == null) return false;
            dynamic locator = locatorObj;
            svcObj = locator.ConnectServer(".", @"root\wmi");
            dynamic svc = svcObj;
            itemsObj = svc.ExecQuery("SELECT * FROM WmiMonitorBrightnessMethods");
            dynamic items = itemsObj;
            bool applied = false;
            foreach (var item in items)
            {
                object? methodObj = null;
                object? inParamsObj = null;
                try
                {
                    object? activeObj = GetWmiProp(item, "Active");
                    if (activeObj is bool active && !active) continue;

                    methodObj = item.Methods_.Item("WmiSetBrightness");
                    dynamic method = methodObj;
                    inParamsObj = method.InParameters.SpawnInstance_();
                    dynamic inParams = inParamsObj;
                    inParams.Properties_.Item("Timeout").Value = 1;
                    inParams.Properties_.Item("Brightness").Value = levelToApply;
                    item.ExecMethod_("WmiSetBrightness", inParams);
                    applied = true;
                }
                finally
                {
                    ReleaseComSafe(inParamsObj);
                    ReleaseComSafe(methodObj);
                    ReleaseComSafe(item);
                }
            }
            return applied;
        }
        catch
        {
            return false;
        }
        finally
        {
            ReleaseComSafe(itemsObj);
            ReleaseComSafe(svcObj);
            ReleaseComSafe(locatorObj);
        }
    }

    private static void ReleaseComSafe(object? comObj)
    {
        if (comObj != null && Marshal.IsComObject(comObj))
        {
            try { Marshal.FinalReleaseComObject(comObj); } catch { }
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
