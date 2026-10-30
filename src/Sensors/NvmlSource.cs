using System.Runtime.InteropServices;

namespace TempFanWidget.Sensors;

/// <summary>
/// Reads the NVIDIA GPU core temperature through NVML, the library behind
/// <c>nvidia-smi</c>. <c>nvml.dll</c> ships with every NVIDIA display driver, so
/// there is nothing to install, and unlike kernel-driver based tools it works
/// for a normal (non-elevated) user.
///
/// Scope is deliberately just the temperature: NVML reports fans as a percentage
/// (not RPM) and laptop GPUs report "not supported" because the embedded
/// controller owns the fan.
/// </summary>
/// <remarks>
/// Initialisation is lazy and failure is cached for <see cref="RetryDelay"/>:
/// machines without an NVIDIA GPU pay one failed <c>LoadLibrary</c> per
/// minute, not one per tick.
/// </remarks>
public sealed class NvmlSource : ISensorSource, IDisposable
{
    private const int NvmlSuccess = 0;
    private const int TemperatureGpu = 0; // NVML_TEMPERATURE_GPU
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(1);

    private IntPtr[] _devices = Array.Empty<IntPtr>();
    private bool _initialised;
    private DateTimeOffset _retryAfter = DateTimeOffset.MinValue;

    public SensorSourceKind Kind => SensorSourceKind.NvidiaNvml;
    public string DisplayName => "NVIDIA NVML";

    public bool IsAvailable() => _initialised || DateTimeOffset.UtcNow >= _retryAfter;

    public SensorSnapshot? TryReadSnapshot()
    {
        if (!_initialised && !TryInitialise())
        {
            _retryAfter = DateTimeOffset.UtcNow + RetryDelay;
            return null;
        }

        double? hottest = null;
        foreach (var device in _devices)
        {
            if (Native.GetTemperature(device, TemperatureGpu, out var celsius) == NvmlSuccess)
                hottest = Math.Max(hottest ?? double.MinValue, celsius);
        }

        if (hottest is null)
            return null;

        return new SensorSnapshot(
            CpuTemperatureC: null,
            GpuTemperatureC: hottest,
            Fans: Array.Empty<FanReading>(),
            CpuLoadPercent: null,
            SourceKind: Kind,
            SourceDescription: DisplayName,
            TimestampUtc: DateTimeOffset.UtcNow);
    }

    private bool TryInitialise()
    {
        try
        {
            if (Native.Init() != NvmlSuccess)
                return false;

            if (Native.GetCount(out var count) != NvmlSuccess || count == 0)
            {
                Native.Shutdown();
                return false;
            }

            var devices = new List<IntPtr>();
            for (uint index = 0; index < count; index++)
            {
                if (Native.GetHandle(index, out var handle) == NvmlSuccess)
                    devices.Add(handle);
            }

            _devices = devices.ToArray();
            _initialised = _devices.Length > 0;
            return _initialised;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return false; // no NVIDIA driver on this machine
        }
    }

    public void Dispose()
    {
        if (!_initialised)
            return;

        try { Native.Shutdown(); } catch { /* process is exiting anyway */ }
        _initialised = false;
    }

    /// <summary>The handful of NVML entry points we need (all return an <c>nvmlReturn_t</c>, 0 = success).</summary>
    private static class Native
    {
        private const string Library = "nvml.dll";

        [DllImport(Library, EntryPoint = "nvmlInit_v2")]
        public static extern int Init();

        [DllImport(Library, EntryPoint = "nvmlShutdown")]
        public static extern int Shutdown();

        [DllImport(Library, EntryPoint = "nvmlDeviceGetCount_v2")]
        public static extern int GetCount(out uint count);

        [DllImport(Library, EntryPoint = "nvmlDeviceGetHandleByIndex_v2")]
        public static extern int GetHandle(uint index, out IntPtr device);

        [DllImport(Library, EntryPoint = "nvmlDeviceGetTemperature")]
        public static extern int GetTemperature(IntPtr device, int sensorType, out uint celsius);
    }
}
