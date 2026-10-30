using TempFanWidget.Sensors;

namespace TempFanWidget.Hardware;

/// <summary>
/// Orchestrates one or more <see cref="ISensorSource"/> implementations.
/// Every available source is read each tick and the results are merged field
/// by field in priority order (see <see cref="SensorSnapshot.Merge"/>), so
/// plugging in a new backend is a one-line change and no source has to
/// measure everything.
///
/// Responsibilities kept deliberately narrow:
///   • lifecycle (timer starts on first widget, stops on the last),
///   • smoothing (rolling median) to kill spurious spikes,
///   • diffing so downstream widgets only hear about meaningful changes.
/// It deliberately never touches UI — see <see cref="Widgets.TempFanWidgetProvider"/>.
/// </summary>
public sealed class HardwareMonitor : IDisposable
{
    private readonly ISensorSource[] _sources;
    private readonly object _gate = new();      // timer state + Current
    private readonly object _pollGate = new();  // one poll at a time: sources and buffers aren't thread-safe
    private readonly Queue<double> _cpuBuffer = new();
    private readonly Queue<double> _gpuBuffer = new();

    private System.Threading.Timer? _timer;
    private bool _running;

    private const int SmoothingWindow = 3;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private const double TempThresholdC = 1.0;
    private const double LoadThresholdPercent = 2.0;
    private const double NetworkThresholdBytesPerSec = 10 * 1024; // idle background chatter stays under this
    private const int FanThresholdRpm = 50;

    /// <summary>Singleton, wired with the built-in source priority list (most trusted first).</summary>
    public static HardwareMonitor Default { get; } = new(new ISensorSource[]
    {
        new LibreHardwareMonitorSource(), // best accuracy; only when its web server is running
        new NvmlSource(),                 // NVIDIA GPU temperature, no admin needed
        new WindowsSystemSource(),        // CPU load + coarse temperature, always there
    });

    public HardwareMonitor(IEnumerable<ISensorSource> sources)
        => _sources = sources.ToArray();

    /// <summary>Latest known (possibly smoothed / stale) snapshot, or null.</summary>
    public SensorSnapshot? Current { get; private set; }

    /// <summary>Raised on a background thread whenever a meaningful change lands.</summary>
    public event Action<SensorSnapshot>? SnapshotChanged;

    /// <summary>
    /// The provider reports the authoritative widget count after every
    /// recovery (re-activation), pin or unpin. Replaces the older
    /// pin/unpin counter — recovery doesn't replay callbacks, so a stored
    /// count is the only truth. 0 widgets ⇒ timer fully stopped; ≥1 ⇒ running.
    /// </summary>
    public void SetWidgetCount(int count)
    {
        lock (_gate)
        {
            if (count > 0 && !_running)
            {
                _running = true;
                StartTimer();
                PollNow(); // don't make a freshly pinned widget wait a full interval
            }
            else if (count <= 0 && _running)
            {
                _running = false;
                StopTimer();
            }
        }
    }

    /// <summary>
    /// Manually triggered (the card's ↻ Refresh button, or a fresh pin). Runs
    /// on the thread pool so COM callbacks return immediately, and always
    /// publishes: even an unchanged reading refreshes the card's timestamp.
    /// </summary>
    public void PollNow() => ThreadPool.QueueUserWorkItem(_ => Poll(force: true));

    // A stopped timer is re-armed rather than recreated, so pin → unpin → pin
    // keeps working.
    private void StartTimer()
    {
        _timer ??= new System.Threading.Timer(_ => Poll(force: false));
        _timer.Change(PollInterval, PollInterval);
    }

    private void StopTimer() => _timer?.Change(Timeout.Infinite, Timeout.Infinite);

    private void Poll(bool force)
    {
        try
        {
            SensorSnapshot snapshot;
            lock (_pollGate)
            {
                var merged = SensorSnapshot.Merge(ReadAvailableSources());
                if (merged is null)
                    return; // keep last values; widgets show the same frame

                snapshot = Smooth(merged);
                lock (_gate)
                {
                    if (!force && !HasMeaningfulChange(snapshot, Current))
                        return;
                    Current = snapshot;
                }
            }

            // Outside the locks: the handler talks to the widget host.
            SnapshotChanged?.Invoke(snapshot);
        }
        catch
        {
            // Sensor backends never crash the widget host; keep stale values.
        }
    }

    private List<SensorSnapshot> ReadAvailableSources()
    {
        var readings = new List<SensorSnapshot>();
        foreach (var source in _sources)
        {
            try
            {
                if (source.IsAvailable() && source.TryReadSnapshot() is { } snapshot)
                    readings.Add(snapshot);
            }
            catch
            {
                // One misbehaving backend must not hide the others.
            }
        }
        return readings;
    }

    private SensorSnapshot Smooth(SensorSnapshot latest) => latest with
    {
        CpuTemperatureC = Smooth(_cpuBuffer, latest.CpuTemperatureC),
        GpuTemperatureC = Smooth(_gpuBuffer, latest.GpuTemperatureC),
    };

    private static double? Smooth(Queue<double> history, double? value)
    {
        if (!value.HasValue)
        {
            history.Clear(); // a sensor that vanished shouldn't resurface with stale samples
            return null;
        }
        history.Enqueue(value.Value);
        while (history.Count > SmoothingWindow) history.Dequeue();
        return Median(history);
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(v => v).ToArray();
        var middle = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
    }

    internal static bool HasMeaningfulChange(SensorSnapshot candidate, SensorSnapshot? previous)
    {
        if (previous is null) return true;
        return Differs(candidate.CpuTemperatureC, previous.CpuTemperatureC, TempThresholdC)
            || Differs(candidate.GpuTemperatureC, previous.GpuTemperatureC, TempThresholdC)
            || Differs(candidate.CpuLoadPercent, previous.CpuLoadPercent, LoadThresholdPercent)
            || Differs(candidate.DownloadBytesPerSec, previous.DownloadBytesPerSec, NetworkThresholdBytesPerSec)
            || Differs(candidate.UploadBytesPerSec, previous.UploadBytesPerSec, NetworkThresholdBytesPerSec)
            || FansDiffer(candidate.Fans, previous.Fans)
            || candidate.SourceDescription != previous.SourceDescription; // e.g. LHM just started
    }

    private static bool Differs(double? a, double? b, double threshold)
        => a.HasValue != b.HasValue
           || (a.HasValue && Math.Abs(a.Value - b!.Value) >= threshold);

    private static bool FansDiffer(IReadOnlyList<FanReading> now, IReadOnlyList<FanReading> before)
    {
        int NowRpm(string label) =>
            now.Where(f => f.Label.Contains(label, StringComparison.OrdinalIgnoreCase))
               .Select(f => f.Rpm).DefaultIfEmpty(-1).Max();

        int BeforeRpm(string label) =>
            before.Where(f => f.Label.Contains(label, StringComparison.OrdinalIgnoreCase))
                  .Select(f => f.Rpm).DefaultIfEmpty(-1).Max();

        return NowRpm("cpu") is var c && Math.Abs(c - BeforeRpm("cpu")) >= FanThresholdRpm
            || NowRpm("gpu") is var g && Math.Abs(g - BeforeRpm("gpu")) >= FanThresholdRpm;
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;

        foreach (var source in _sources.OfType<IDisposable>())
            source.Dispose();
    }
}
