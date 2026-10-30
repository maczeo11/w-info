using System.Text.Json;

namespace TempFanWidget.Sensors;

/// <summary>
/// Reads sensors from LibreHardwareMonitor's built-in web server
/// (<c>Options → Remote Web Server → Run</c>, default port 8085). This is the
/// most accurate source on Windows: real CPU Package / Tctl temperature and
/// fan RPMs straight from the chipset, which Windows itself does not expose.
///
/// LibreHardwareMonitor used to publish the same data through WMI, but that
/// provider was removed in v0.9.5 (.NET 10 dropped the API it was built on),
/// so the web server is now the only out-of-process feed.
///
/// Trade-offs: LibreHardwareMonitor must be running (it needs administrator
/// rights and a kernel driver for most chipset sensors — which is exactly why
/// this widget talks to it instead of loading the driver itself).
/// </summary>
/// <remarks>
/// Needs LibreHardwareMonitor 0.9.5 or newer: older web servers only send
/// pre-formatted strings, while this reads the unit-free <c>RawValue</c>.
/// </remarks>
public sealed class LibreHardwareMonitorSource : ISensorSource
{
    // 127.0.0.1, not "localhost": skips a slow IPv6-then-IPv4 fallback.
    private static readonly Uri Endpoint = new("http://127.0.0.1:8085/data.json");
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(15);
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(2) };

    private DateTimeOffset _retryAfter = DateTimeOffset.MinValue;

    public SensorSourceKind Kind => SensorSourceKind.LibreHardwareMonitor;
    public string DisplayName => "LibreHardwareMonitor";

    /// <summary>False for a short while after a failed request, so a machine
    /// without LibreHardwareMonitor doesn't open a socket every tick.</summary>
    public bool IsAvailable() => DateTimeOffset.UtcNow >= _retryAfter;

    public SensorSnapshot? TryReadSnapshot()
    {
        try
        {
            var json = Http.GetStringAsync(Endpoint).GetAwaiter().GetResult();
            return Parse(json);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            // Not running, restarting, or something else owns the port.
            _retryAfter = DateTimeOffset.UtcNow + RetryDelay;
            return null;
        }
    }

    /// <summary>
    /// Pure function over the <c>data.json</c> tree (hardware → sensor-type →
    /// sensor), so it can be tested without a running LibreHardwareMonitor.
    /// Sensors are classified by their <c>SensorId</c> prefix (for example
    /// <c>/amdcpu/0/temperature/2</c>, <c>/gpu-nvidia/0/fan/1</c>).
    /// </summary>
    internal static SensorSnapshot? Parse(string json)
    {
        using var document = JsonDocument.Parse(json);

        var cpuTemps = new List<(string Name, double Value)>();
        var gpuTemps = new List<(string Name, double Value)>();
        var fans = new List<FanReading>();
        double? cpuLoad = null;

        Walk(document.RootElement);

        var cpuTemp = Pick(cpuTemps, "package", "tctl", "tdie", "core", "cpu");
        var gpuTemp = Pick(gpuTemps, "core", "hot spot", "hotspot");

        if (cpuTemp is null && gpuTemp is null && fans.Count == 0 && cpuLoad is null)
            return null;

        return new SensorSnapshot(
            CpuTemperatureC: cpuTemp,
            GpuTemperatureC: gpuTemp,
            Fans: fans,
            CpuLoadPercent: cpuLoad,
            SourceKind: SensorSourceKind.LibreHardwareMonitor,
            SourceDescription: "LibreHardwareMonitor",
            TimestampUtc: DateTimeOffset.UtcNow);

        void Walk(JsonElement node)
        {
            if (node.TryGetProperty("SensorId", out var id) &&
                node.TryGetProperty("Type", out var type) &&
                node.TryGetProperty("RawValue", out var raw) && raw.ValueKind == JsonValueKind.Number)
            {
                Classify(
                    id.GetString() ?? string.Empty,
                    type.GetString(),
                    node.TryGetProperty("Text", out var text) ? text.GetString() ?? string.Empty : string.Empty,
                    raw.GetDouble());
            }

            if (node.TryGetProperty("Children", out var children) && children.ValueKind == JsonValueKind.Array)
            {
                foreach (var child in children.EnumerateArray())
                    Walk(child);
            }
        }

        void Classify(string id, string? type, string name, double value)
        {
            switch (type)
            {
                case "Temperature":
                    if (id.StartsWith("/amdcpu/", StringComparison.OrdinalIgnoreCase) ||
                        id.StartsWith("/intelcpu/", StringComparison.OrdinalIgnoreCase) ||
                        id.Contains("/cpu/", StringComparison.OrdinalIgnoreCase) ||
                        id.Contains("cpu", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("cpu", StringComparison.OrdinalIgnoreCase))
                    {
                        cpuTemps.Add((name, value));
                    }
                    else if (id.StartsWith("/gpu-", StringComparison.OrdinalIgnoreCase) ||
                             id.Contains("/gpu/", StringComparison.OrdinalIgnoreCase) ||
                             id.Contains("gpu", StringComparison.OrdinalIgnoreCase) ||
                             name.Contains("gpu", StringComparison.OrdinalIgnoreCase))
                    {
                        gpuTemps.Add((name, value));
                    }
                    break;

                case "Load":
                    if ((id.Contains("cpu", StringComparison.OrdinalIgnoreCase) ||
                         id.StartsWith("/amdcpu/", StringComparison.OrdinalIgnoreCase) ||
                         id.StartsWith("/intelcpu/", StringComparison.OrdinalIgnoreCase)) &&
                        (name.Equals("CPU Total", StringComparison.OrdinalIgnoreCase) ||
                         name.Contains("total", StringComparison.OrdinalIgnoreCase)))
                    {
                        cpuLoad = Math.Clamp(value, 0, 100);
                    }
                    break;

                case "Fan" when value > 0:
                    fans.Add(new FanReading(FanLabel(id, name), (int)Math.Round(value)));
                    break;
            }
        }
    }

    /// <summary>
    /// Prefers the sensors whose names contain a <paramref name="preferred"/>
    /// keyword (the whole-package reading, mirroring HWiNFO) and reports the
    /// hottest of them; otherwise the hottest of what there is. "Distance to
    /// TjMax" is a margin, not a temperature, even though LHM types it as one.
    /// </summary>
    private static double? Pick(List<(string Name, double Value)> temps, params string[] preferred)
    {
        var real = temps
            .Where(t => !t.Name.Contains("distance", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (real.Count == 0)
            return null;

        var favoured = real
            .Where(t => preferred.Any(p => t.Name.Contains(p, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        return (favoured.Count > 0 ? favoured : real).Max(t => t.Value);
    }

    private static string FanLabel(string id, string raw)
    {
        if (id.StartsWith("/gpu-", StringComparison.OrdinalIgnoreCase) || id.Contains("gpu", StringComparison.OrdinalIgnoreCase)) return "GPU";

        var lower = raw.ToLowerInvariant();
        if (lower.Contains("gpu")) return "GPU";
        if (lower.Contains("cpu")) return "CPU";
        if (lower.Contains("case") || lower.Contains("chassis")) return "Case";
        return string.IsNullOrWhiteSpace(raw) ? "Fan" : raw.Trim();
    }
}
