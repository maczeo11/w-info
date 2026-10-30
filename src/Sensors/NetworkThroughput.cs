using System.Diagnostics;
using System.Net.NetworkInformation;

namespace TempFanWidget.Sensors;

/// <summary>
/// Turns the cumulative byte counters Windows keeps for every network adapter
/// into a transfer rate. Windows has no "current speed" figure: a rate is the
/// difference between two samples divided by the time between them, so this
/// remembers the previous sample.
///
/// Needs no administrator rights. Which adapters count matters, because a
/// byte is often counted twice (once on the physical NIC, again on a virtual
/// switch or VPN adapter riding on top of it), so virtual adapters are left
/// out. If that leaves nothing (e.g. inside a VM whose only NIC is virtual),
/// every adapter that is up is used instead.
/// </summary>
/// <remarks>
/// Not thread-safe; <see cref="Hardware.HardwareMonitor"/> serialises polls.
/// </remarks>
internal sealed class NetworkThroughput
{
    // Two samples closer than this give a noisy rate (a forced refresh right
    // after a poll), so the previous figure is returned instead.
    private static readonly TimeSpan MinWindow = TimeSpan.FromMilliseconds(250);

    private static readonly string[] VirtualMarkers =
    {
        "virtual", "vethernet", "hyper-v", "vmware", "virtualbox", "tap-",
        "wireguard", "wintun", "vpn", "tunnel", "tailscale", "zerotier",
        "loopback", "bluetooth", "miniport",
    };

    private Dictionary<string, (long Received, long Sent)>? _baseline;
    private long _baselineAt;
    private (double Download, double Upload)? _last;

    /// <summary>Bytes per second since the last call, or null when there is nothing to measure yet.</summary>
    public (double Download, double Upload)? Read()
    {
        if (_baseline is null)
        {
            // First ever read: take the baseline now and a second sample just
            // after, so the very first card already has a real rate.
            Compute(Snapshot(), Stopwatch.GetTimestamp());
            Thread.Sleep(MinWindow + TimeSpan.FromMilliseconds(50));
        }
        return Compute(Snapshot(), Stopwatch.GetTimestamp());
    }

    /// <summary>
    /// The rate maths, free of any real adapter so it can be tested with
    /// synthetic counters. <paramref name="timestamp"/> is a
    /// <see cref="Stopwatch.GetTimestamp"/> value.
    /// </summary>
    internal (double Download, double Upload)? Compute(
        IReadOnlyDictionary<string, (long Received, long Sent)> now, long timestamp)
    {
        if (_baseline is null)
        {
            Rebase(now, timestamp);
            return null;
        }

        var elapsed = Stopwatch.GetElapsedTime(_baselineAt, timestamp);
        if (elapsed < MinWindow)
            return _last;

        double received = 0, sent = 0;
        foreach (var (id, current) in now)
        {
            // An adapter that just appeared, or whose counters went backwards
            // (adapter reset / reconnect), has no valid delta this window.
            if (!_baseline.TryGetValue(id, out var before) ||
                current.Received < before.Received || current.Sent < before.Sent)
                continue;

            received += current.Received - before.Received;
            sent += current.Sent - before.Sent;
        }

        Rebase(now, timestamp);

        if (now.Count == 0)
            return _last = null; // no usable adapter: offline

        return _last = (received / elapsed.TotalSeconds, sent / elapsed.TotalSeconds);
    }

    private void Rebase(IReadOnlyDictionary<string, (long Received, long Sent)> now, long timestamp)
    {
        _baseline = new Dictionary<string, (long, long)>(now);
        _baselineAt = timestamp;
    }

    private static Dictionary<string, (long Received, long Sent)> Snapshot()
    {
        var candidates = new List<NetworkInterface>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus == OperationalStatus.Up &&
                nic.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
                candidates.Add(nic);
        }

        var real = candidates.Where(nic => !IsVirtual(nic)).ToList();

        var counters = new Dictionary<string, (long, long)>();
        foreach (var nic in real.Count > 0 ? real : candidates)
        {
            try
            {
                // GetIPStatistics (64-bit counters), not GetIPv4Statistics:
                // the IPv4 one is 32-bit and wraps every 4 GB.
                var stats = nic.GetIPStatistics();
                counters[nic.Id] = (stats.BytesReceived, stats.BytesSent);
            }
            catch (NetworkInformationException)
            {
                // The adapter vanished between listing and reading; skip it.
            }
        }
        return counters;
    }

    private static bool IsVirtual(NetworkInterface nic)
        => VirtualMarkers.Any(marker =>
               nic.Description.Contains(marker, StringComparison.OrdinalIgnoreCase) ||
               nic.Name.Contains(marker, StringComparison.OrdinalIgnoreCase));
}
