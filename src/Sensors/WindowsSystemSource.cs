using System.Diagnostics;
using System.Management;
using System.Net.NetworkInformation;

namespace TempFanWidget.Sensors;

/// <summary>
/// Universal baseline source. Needs no administrator rights and no external
/// software: it reads Windows performance counters, which any user may query.
///
///   • CPU load  — <c>Processor Information\% Processor Utility</c>, the same
///     figure Task Manager shows (falls back to <c>% Processor Time</c>, then
///     to WMI <c>Win32_PerfFormattedData_PerfOS_Processor</c>).
///   • CPU temp  — <c>Thermal Zone Information</c>, the ACPI thermal zones the
///     firmware exposes (falls back to WMI <c>Win32_PerfFormattedData_Counters_ThermalZoneInformation</c>).
///     This is a coarse *system* temperature, not the die temperature, so it is the lowest-trust
///     temperature in the app.
///   • Network   — download / upload rate from the adapters' byte counters
///     (see <see cref="NetworkThroughput"/>).
///
/// The WMI route (<c>MSAcpi_ThermalZoneTemperature</c>) is deliberately not
/// used: it demands administrator rights, which the widget host never has.
/// </summary>
/// <remarks>
/// <see cref="PerformanceCounter"/> instances are kept between ticks because
/// load is a delta between two samples. They are not thread-safe;
/// <see cref="Hardware.HardwareMonitor"/> serialises polls so that holds.
/// </remarks>
public sealed class WindowsSystemSource : ISensorSource, IDisposable
{
    private const string ThermalCategory = "Thermal Zone Information";

    /// <summary>
    /// After a performance-counter failure, back off before retrying so a
    /// machine whose counters are genuinely missing doesn't throw on every
    /// 5-second tick. Matches the pattern in <see cref="NvmlSource"/>.
    /// </summary>
    private static readonly TimeSpan CounterRetryDelay = TimeSpan.FromSeconds(30);

    private readonly NetworkThroughput _network = new();
    private PerformanceCounter? _load;
    private PerformanceCounter[]? _zones;
    private double _zoneUnitsPerKelvin = 1;

    /// <summary>Tracks consecutive failures to throttle retry attempts.</summary>
    private int _loadFailures;
    private DateTimeOffset _loadRetryAfter = DateTimeOffset.MinValue;
    private int _zoneFailures;
    private DateTimeOffset _zoneRetryAfter = DateTimeOffset.MinValue;

    /// <summary>
    /// Whether the last successful CPU load came from WMI instead of a
    /// performance counter. Sticky: once we know perf counters are broken,
    /// we skip straight to WMI rather than burning a tick on a doomed attempt.
    /// Reset to false whenever perf-counter recreation succeeds.
    /// </summary>
    private bool _useWmiLoad;
    private bool _useWmiZone;

    public SensorSourceKind Kind => SensorSourceKind.WindowsSystem;
    public string DisplayName => "Windows";

    public bool IsAvailable() => OperatingSystem.IsWindows();

    public SensorSnapshot? TryReadSnapshot()
    {
        var cpuLoad = ReadLoad();
        var cpuTemp = ReadHottestZone();
        var network = ReadNetwork();

        if (cpuLoad is null && cpuTemp is null && network is null)
            return null;

        return new SensorSnapshot(
            CpuTemperatureC: cpuTemp,
            GpuTemperatureC: null,
            Fans: Array.Empty<FanReading>(),
            CpuLoadPercent: cpuLoad,
            SourceKind: Kind,
            SourceDescription: DisplayName,
            TimestampUtc: DateTimeOffset.UtcNow,
            DownloadBytesPerSec: network?.Download,
            UploadBytesPerSec: network?.Upload,
            CpuTemperatureIsEstimate: cpuTemp is not null); // the ACPI zone is a system temperature, not the die
    }

    private (double Download, double Upload)? ReadNetwork()
    {
        try
        {
            return _network.Read();
        }
        catch (Exception e) when (e is NetworkInformationException or InvalidOperationException)
        {
            return null; // adapter list unavailable this tick
        }
    }

    private double? ReadLoad()
    {
        // If perf counters have been consistently broken, skip straight to WMI.
        if (_useWmiLoad)
            return ReadLoadWmi();

        // Back off after failures so a missing category doesn't throw every tick.
        if (_load is null && DateTimeOffset.UtcNow < _loadRetryAfter)
            return ReadLoadWmi(); // try WMI while waiting

        try
        {
            if (_load is null)
            {
                _load = CreateLoadCounter();
                // A rate counter's first sample is always 0; take one now and a
                // second shortly after so the very first card shows a real value.
                _load.NextValue();
                Thread.Sleep(100);
                _loadFailures = 0; // constructor + first read succeeded
            }
            return Math.Clamp(_load.NextValue(), 0, 100); // "Utility" can exceed 100 while boosting
        }
        catch
        {
            // Counter set missing or rebuilt (e.g. after `lodctr /r`): drop it.
            _load?.Dispose();
            _load = null;
            _loadFailures++;
            _loadRetryAfter = DateTimeOffset.UtcNow + CounterRetryDelay;

            // After 3 consecutive perf-counter failures, switch to WMI
            // permanently (until the source is re-created).
            if (_loadFailures >= 3)
                _useWmiLoad = true;

            return ReadLoadWmi(); // try WMI as an immediate fallback
        }
    }

    /// <summary>
    /// WMI-based CPU load fallback. Slower than a performance counter (~20–80 ms
    /// per call) but works on machines where the perf-counter categories are
    /// missing or corrupted (common after OS updates and on some OEM images).
    /// Does not need administrator rights.
    /// </summary>
    private static double? ReadLoadWmi()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT PercentProcessorTime FROM Win32_PerfFormattedData_PerfOS_Processor WHERE Name='_Total'");
            foreach (var obj in searcher.Get())
            {
                var val = obj["PercentProcessorTime"];
                if (val is not null)
                    return Math.Clamp(Convert.ToDouble(val), 0, 100);
            }
            return null;
        }
        catch
        {
            return null; // WMI unavailable — give up for this tick
        }
    }

    private static PerformanceCounter CreateLoadCounter()
    {
        try { return new PerformanceCounter("Processor Information", "% Processor Utility", "_Total", readOnly: true); }
        catch (InvalidOperationException) { return new PerformanceCounter("Processor", "% Processor Time", "_Total", readOnly: true); }
    }

    private double? ReadHottestZone()
    {
        // If perf counters have been consistently broken, skip straight to WMI.
        if (_useWmiZone)
            return ReadHottestZoneWmi();

        // Back off after failures.
        if (_zones is null && DateTimeOffset.UtcNow < _zoneRetryAfter)
            return ReadHottestZoneWmi();

        try
        {
            _zones ??= CreateZoneCounters();
            if (_zones.Length == 0)
            {
                _zoneFailures++;
                _zoneRetryAfter = DateTimeOffset.UtcNow + CounterRetryDelay;
                if (_zoneFailures >= 3)
                    _useWmiZone = true;

                return ReadHottestZoneWmi();
            }

            double? hottest = null;
            foreach (var zone in _zones)
            {
                var celsius = zone.NextValue() / _zoneUnitsPerKelvin - 273.15;
                // Some firmwares publish a dummy zone stuck at 0 K or a bogus
                // value; only believe readings a CPU could plausibly have.
                if (celsius is > 0 and < 125)
                    hottest = Math.Max(hottest ?? double.MinValue, celsius);
            }
            _zoneFailures = 0;
            return hottest ?? ReadHottestZoneWmi();
        }
        catch
        {
            DisposeZones();
            _zones = null;
            _zoneFailures++;
            _zoneRetryAfter = DateTimeOffset.UtcNow + CounterRetryDelay;

            if (_zoneFailures >= 3)
                _useWmiZone = true;

            return ReadHottestZoneWmi();
        }
    }

    /// <summary>
    /// WMI-based thermal zone fallback. Slower than a performance counter (~20–80 ms)
    /// but works without administrator rights or registry permissions (such as inside
    /// packaged MSIX / AppContainer / standard user contexts).
    /// </summary>
    private static double? ReadHottestZoneWmi()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT HighPrecisionTemperature, Temperature FROM Win32_PerfFormattedData_Counters_ThermalZoneInformation");
            double? hottest = null;
            foreach (var obj in searcher.Get())
            {
                double? kelvin = null;
                var hpVal = obj["HighPrecisionTemperature"];
                if (hpVal is not null)
                {
                    var val = Convert.ToDouble(hpVal);
                    if (val > 0)
                        kelvin = val / 10.0;
                }

                if (kelvin is null)
                {
                    var tVal = obj["Temperature"];
                    if (tVal is not null)
                    {
                        var val = Convert.ToDouble(tVal);
                        if (val > 0)
                            kelvin = val;
                    }
                }

                if (kelvin is not null)
                {
                    var celsius = kelvin.Value - 273.15;
                    // Filter plausible CPU/thermal zone temperatures (0 to 125 °C)
                    if (celsius is > 0 and < 125)
                        hottest = Math.Max(hottest ?? double.MinValue, celsius);
                }
            }
            return hottest;
        }
        catch
        {
            return null;
        }
    }

    private PerformanceCounter[] CreateZoneCounters()
    {
        try
        {
            if (!PerformanceCounterCategory.Exists(ThermalCategory))
                return Array.Empty<PerformanceCounter>();

            var instances = new PerformanceCounterCategory(ThermalCategory).GetInstanceNames();

            // "High Precision Temperature" is in tenths of a Kelvin; the plain
            // "Temperature" counter is whole Kelvin. Prefer the finer one.
            var highPrecision = PerformanceCounterCategory.CounterExists("High Precision Temperature", ThermalCategory);
            _zoneUnitsPerKelvin = highPrecision ? 10 : 1;
            var counterName = highPrecision ? "High Precision Temperature" : "Temperature";

            return instances
                .Select(instance => new PerformanceCounter(ThermalCategory, counterName, instance, readOnly: true))
                .ToArray();
        }
        catch
        {
            return Array.Empty<PerformanceCounter>();
        }
    }

    private void DisposeZones()
    {
        foreach (var zone in _zones ?? Array.Empty<PerformanceCounter>())
            zone.Dispose();
    }

    public void Dispose()
    {
        _load?.Dispose();
        DisposeZones();
        _load = null;
        _zones = null;
    }
}
