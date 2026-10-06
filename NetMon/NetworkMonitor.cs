using System.Diagnostics;
using System.Net.NetworkInformation;

namespace NetMon;

/// <summary>Speed reading for one polling interval.</summary>
public sealed class SpeedSample
{
    public long DownloadBps { get; init; }
    public long UploadBps  { get; init; }
}

/// <summary>
/// Polls active network adapters every 2 seconds and fires UI events.
///
/// Reports the throughput of the single <b>busiest</b> adapter each interval
/// rather than summing every adapter. This structurally prevents VPN tunnels
/// from double-counting: a VPN (Cloudflare WARP, WireGuard, hide.me, OpenVPN,
/// any brand) carries the same bytes as its underlying physical NIC, so
/// summing both inflates the reading (classic symptom: a reading above the
/// physical link speed). Picking the busiest interface yields the real rate
/// regardless of how many virtual twins Windows reports — no brand keyword
/// list to maintain.
///
/// The expensive <see cref="NetworkInterface.GetAllNetworkInterfaces"/> call
/// (which allocates a full managed object graph) is made at most once every
/// 60 seconds. Between rescans only <c>GetIPv4Statistics()</c> is called on
/// the cached adapter set — a thin native wrapper, an order of magnitude
/// cheaper.
/// </summary>
public sealed class NetworkMonitor : IDisposable
{
    private const int    PollMs    = 1_000;   // UI refresh cadence (finer graph detail)
    private const double RescanSec = 60.0;    // full re-enumerate interval

    private DateTime _lastTick;
    private DateTime _lastRescan = DateTime.MinValue;   // forces scan on first tick

    // Per-adapter cumulative byte counters from the previous tick, keyed by
    // the adapter's stable Id, so each interface's delta can be measured on
    // its own (needed to find the busiest one).
    private readonly Dictionary<string, (long rx, long tx)> _last = new();

    private NetworkInterface[] _adapters = Array.Empty<NetworkInterface>();
    private readonly System.Threading.Timer _timer;
    private volatile bool _disposed;

    public event EventHandler<SpeedSample>?          SpeedUpdated;
    public event EventHandler<(long down, long up)>? UsageRecorded;

    public NetworkMonitor()
    {
        ScanAdapters();        // initial enumeration
        SnapshotBaseline();    // baseline — prevents a first-tick spike
        _lastTick = DateTime.UtcNow;

        _timer = new System.Threading.Timer(Tick, null, PollMs, PollMs);
    }

    // ── adapter cache ─────────────────────────────────────────────────────

    private void ScanAdapters()
    {
        try
        {
            _adapters = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up
                         && n.NetworkInterfaceType != NetworkInterfaceType.Loopback
                         && n.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
                .ToArray();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"NetworkMonitor.ScanAdapters: {ex.Message}");
            _adapters = Array.Empty<NetworkInterface>();
        }

        _lastRescan = DateTime.UtcNow;
    }

    /// <summary>Record each adapter's current counters without emitting a sample.</summary>
    private void SnapshotBaseline()
    {
        _last.Clear();
        foreach (var ni in _adapters)
        {
            try
            {
                var s = ni.GetIPv4Statistics();
                _last[ni.Id] = (s.BytesReceived, s.BytesSent);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"NetworkMonitor.SnapshotBaseline: {ex.Message}");
            }
        }
    }

    // ── timer callback ────────────────────────────────────────────────────

    private void Tick(object? _)
    {
        if (_disposed) return;
        var now = DateTime.UtcNow;

        // Periodic full re-enumerate (catches VPN/dock/USB-tether topology changes)
        if ((now - _lastRescan).TotalSeconds >= RescanSec)
        {
            ScanAdapters();
            SnapshotBaseline();   // reset baselines — avoids a false spike
            _lastTick = now;
            return;
        }

        double secs = (now - _lastTick).TotalSeconds;
        _lastTick   = now;
        if (secs <= 0) return;

        // Find the adapter with the largest total delta this interval. A VPN
        // tunnel and its physical carrier move the same bytes, so the busiest
        // one alone equals the real throughput — summing would double-count.
        long bestRx = 0, bestTx = 0, bestSum = -1;

        foreach (var ni in _adapters)
        {
            try
            {
                var  s  = ni.GetIPv4Statistics();
                long rx = s.BytesReceived, tx = s.BytesSent;

                if (_last.TryGetValue(ni.Id, out var prev))
                {
                    long dRx = Math.Max(0, rx - prev.rx);
                    long dTx = Math.Max(0, tx - prev.tx);
                    long sum = dRx + dTx;
                    if (sum > bestSum) { bestSum = sum; bestRx = dRx; bestTx = dTx; }
                }

                _last[ni.Id] = (rx, tx);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"NetworkMonitor.Tick: adapter dropped ({ex.Message})");
                // Adapter disappeared mid-poll — force rescan on next tick
                _lastRescan = DateTime.MinValue;
            }
        }

        if (bestSum < 0) return;   // no adapter had a baseline yet

        SpeedUpdated?.Invoke(this, new SpeedSample
        {
            DownloadBps = (long)(bestRx / secs),
            UploadBps   = (long)(bestTx / secs)
        });

        if (bestRx > 0 || bestTx > 0)
            UsageRecorded?.Invoke(this, (bestRx, bestTx));
    }

    public void Dispose()
    {
        _disposed = true;
        _timer.Dispose();
    }
}
