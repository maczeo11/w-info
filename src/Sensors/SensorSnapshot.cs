namespace TempFanWidget.Sensors;

/// <summary>
/// Identifies where a <see cref="SensorSnapshot"/> came from. Increasing
/// trust &gt; accuracy as the list goes up.
/// </summary>
public enum SensorSourceKind
{
    /// <summary>No usable source answered — data must be considered stale.</summary>
    Unknown = 0,

    /// <summary>Windows built-in performance counters: CPU load plus the ACPI
    /// thermal zone (one coarse system temperature, no per-package CPU, no
    /// fans). Always available, no admin required.</summary>
    WindowsSystem = 1,

    /// <summary>NVIDIA's NVML (<c>nvml.dll</c> ships with the display driver).
    /// Exact GPU core temperature, no admin required.</summary>
    NvidiaNvml = 2,

    /// <summary>LibreHardwareMonitor's built-in web server (<c>data.json</c>).
    /// Best accuracy: real CPU Package / Tctl temp, GPU core temp, fan RPMs.</summary>
    LibreHardwareMonitor = 3,
}

/// <summary>One fan as reported by the system.</summary>
/// <param name="Label">Human-readable label, e.g. "CPU", "GPU", "Case".</param>
/// <param name="Rpm">Rotations per minute.</param>
public readonly record struct FanReading(string Label, int Rpm);

/// <summary>
/// An immutable, coherent set of sensor readings at one point in time.
/// Widgets and the hardware service both treat this as a value, never mutates it.
/// </summary>
/// <param name="CpuTemperatureC">Best available CPU temperature, optional.</param>
/// <param name="GpuTemperatureC">Best available GPU temperature, optional.</param>
/// <param name="Fans">Zero or more fans; each has a label and RPM.</param>
/// <param name="CpuLoadPercent">CPU utilisation 0–100, optional.</param>
/// <param name="SourceKind">Where these values came from.</param>
/// <param name="SourceDescription">Human-readable name of the source.</param>
/// <param name="TimestampUtc">When the readings were captured.</param>
/// <param name="DownloadBytesPerSec">Network receive rate over the real adapters, optional.</param>
/// <param name="UploadBytesPerSec">Network send rate over the real adapters, optional.</param>
/// <param name="CpuTemperatureIsEstimate">True when <paramref name="CpuTemperatureC"/> is a
/// coarse system reading (the ACPI thermal zone), not the CPU die temperature. The card
/// labels it so a low number is never mistaken for the real die temperature.</param>
public sealed record SensorSnapshot(
    double? CpuTemperatureC,
    double? GpuTemperatureC,
    IReadOnlyList<FanReading> Fans,
    double? CpuLoadPercent,
    SensorSourceKind SourceKind,
    string SourceDescription,
    DateTimeOffset TimestampUtc,
    double? DownloadBytesPerSec = null,
    double? UploadBytesPerSec = null,
    bool CpuTemperatureIsEstimate = false)
{
    /// <summary>Highest fan RPM in the snapshot, or null when none was read.</summary>
    public int? PeakFanRpm => Fans.Count == 0 ? null : Fans.Max(f => f.Rpm);

    /// <summary>The first fan whose label contains <paramref name="needle"/>
    /// (case-insensitive), or null. Used to pick "CPU fan" vs "GPU fan"</summary>
    public FanReading? FanContaining(string needle)
    {
        foreach (var f in Fans)
        {
            if (f.Label.Contains(needle, StringComparison.OrdinalIgnoreCase))
                return f;
        }
        return null;
    }

    /// <summary>
    /// Combines readings from several sources field by field.
    /// <paramref name="byPriority"/> is ordered most-trusted first, so each
    /// value comes from the best source that actually has one (e.g. CPU temp
    /// from LibreHardwareMonitor, GPU temp from NVML, CPU load from Windows).
    /// Returns null when no source produced anything.
    /// </summary>
    public static SensorSnapshot? Merge(IReadOnlyList<SensorSnapshot> byPriority)
    {
        var contributors = new HashSet<SensorSnapshot>(ReferenceEqualityComparer.Instance);

        double? Best(Func<SensorSnapshot, double?> field)
        {
            foreach (var snapshot in byPriority)
            {
                if (field(snapshot) is { } value)
                {
                    contributors.Add(snapshot);
                    return value;
                }
            }
            return null;
        }

        // Best() below picks this same snapshot; remember it for the estimate flag.
        var cpuTempSource = byPriority.FirstOrDefault(s => s.CpuTemperatureC.HasValue);
        var cpuTemp = Best(s => s.CpuTemperatureC);
        var gpuTemp = Best(s => s.GpuTemperatureC);
        var cpuLoad = Best(s => s.CpuLoadPercent);
        var download = Best(s => s.DownloadBytesPerSec);
        var upload = Best(s => s.UploadBytesPerSec);

        // Fans are a list, not a scalar: take the best source that has any.
        var fanSource = byPriority.FirstOrDefault(s => s.Fans.Count > 0);
        if (fanSource is not null)
            contributors.Add(fanSource);

        if (contributors.Count == 0)
            return null;

        var used = byPriority.Where(contributors.Contains).ToList();
        return new SensorSnapshot(
            CpuTemperatureC: cpuTemp,
            GpuTemperatureC: gpuTemp,
            Fans: fanSource?.Fans ?? Array.Empty<FanReading>(),
            CpuLoadPercent: cpuLoad,
            SourceKind: used[0].SourceKind,
            SourceDescription: string.Join(" + ", used.Select(s => s.SourceDescription).Distinct()),
            TimestampUtc: used.Max(s => s.TimestampUtc),
            DownloadBytesPerSec: download,
            UploadBytesPerSec: upload,
            CpuTemperatureIsEstimate: cpuTempSource?.CpuTemperatureIsEstimate ?? false);
    }
}

/// <summary>
/// A source of hardware sensor data. Implement this to add a new backend
/// (e.g., ADL, Intel oneAPI, or an in-process LibreHardwareMonitorLib
/// host). Every available source is polled each tick and the snapshots are
/// merged field by field in priority order (see <see cref="SensorSnapshot.Merge"/>),
/// so a source only needs to fill in what it can measure.
/// </summary>
public interface ISensorSource
{
    /// <summary>What kind of source this is (drives the UI badge and error text).</summary>
    SensorSourceKind Kind { get; }

    /// <summary>Human-display name, used in the card's "source:" line.</summary>
    string DisplayName { get; }

    /// <summary>
    /// Quick, cheap check: can this source still be used right now?
    /// Must not do heavy work — heavy checks belong inside <see cref="TryReadSnapshot"/>.
    /// </summary>
    bool IsAvailable();

    /// <summary>
    /// Capture one coherent set of readings, or return null to signal
    /// "not usable this tick" (so the orchestrator falls through to the next source).
    /// Called on a background thread; must not touch UI.
    /// </summary>
    SensorSnapshot? TryReadSnapshot();
}
