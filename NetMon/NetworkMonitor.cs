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
/// Key optimisation: the expensive <see cref="NetworkInterface.GetAllNetworkInterfaces"/>
/// call (which allocates a full managed object graph) is made at most once every 60 seconds.
/// Between rescans, only <c>GetIPv4Statistics()</c> is called on the cached adapter set —
/// a thin native wrapper that is an order of magnitude cheaper.
/// </summary>
public sealed class NetworkMonitor : IDisposable
{
    private const int    PollMs    = 2_000;   // UI refresh cadence
    private const double RescanSec = 60.0;    // full re-enumerate interval

    private long     _lastRx, _lastTx;
    private DateTime _lastTick;
    private DateTime _lastRescan = DateTime.MinValue;   // forces scan on first tick

    private NetworkInterface[] _adapters = Array.Empty<NetworkInterface>();
    private readonly System.Threading.Timer _timer;
    private volatile bool _disposed;

    public event EventHandler<SpeedSample>?          SpeedUpdated;
    public event EventHandler<(long down, long up)>? UsageRecorded;

    public NetworkMonitor()
    {
        ScanAdapters();                            // initial enumeration
        GetTotals(out _lastRx, out _lastTx);       // baseline — prevents a first-tick spike
        _lastTick = DateTime.UtcNow;

        _timer = new System.Threading.Timer(Tick, null, PollMs, PollMs);
    }

    // ── adapter cache ─────────────────────────────────────────────────────

    // Substrings (case-insensitive) found in NetworkInterface.Name or Description
    // that identify a VPN / virtual-overlay adapter whose traffic is already
    // being counted on the underlying physical NIC. Including these would
    // double-count every byte while the VPN is active (classic symptom: NetMon
    // shows ~2× the real speed under Cloudflare WARP).
    private static readonly string[] VpnKeywords =
    {
        "cloudflare", "warp",
        "wireguard",
        "openvpn", "tap-windows", "tap-win32", "tap adapter",
        "tailscale",
        "zerotier",
        "hamachi",
        "nordvpn", "expressvpn", "surfshark", "protonvpn", "mullvad",
        "cyberghost", "private internet access",
        "wan miniport",                     // PPP / L2TP / PPTP / IKEv2 / SSTP
        "anyconnect", "globalprotect", "pulse secure",
        "forticlient", "sophos connect", "zscaler",
        "juniper network connect",
        "vpn"                               // catch-all last-resort
    };

    private static bool IsVirtualOverlay(NetworkInterface n)
    {
        string desc = (n.Description ?? "").ToLowerInvariant();
        string name = (n.Name        ?? "").ToLowerInvariant();
        foreach (var kw in VpnKeywords)
            if (desc.Contains(kw) || name.Contains(kw)) return true;
        return false;
    }

    private void ScanAdapters()
    {
        try
        {
            var all = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up
                         && n.NetworkInterfaceType != NetworkInterfaceType.Loopback
                         && n.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
                .ToArray();

            var physical = all.Where(n => !IsVirtualOverlay(n)).ToArray();

            // If at least one physical adapter is up, only count those — this
            // avoids the double-counting seen with Cloudflare WARP / WireGuard
            // / etc.  If *no* physical adapter is up (laptop on a corporate
            // VPN with no other NIC), fall back to counting everything so the
            // user still sees their real traffic.
            _adapters = physical.Length > 0 ? physical : all;

            if (physical.Length == 0 && all.Length > 0)
                Debug.WriteLine($"NetworkMonitor: no physical adapter — " +
                                $"falling back to {all.Length} overlay adapter(s)");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"NetworkMonitor.ScanAdapters: {ex.Message}");
            _adapters = Array.Empty<NetworkInterface>();
        }

        _lastRescan = DateTime.UtcNow;
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
            // Reset baseline after re-scan — avoids false bandwidth spike
            GetTotals(out _lastRx, out _lastTx);
            _lastTick = now;
            return;
        }

        // Fast path: iterate cached adapters only
        GetTotals(out long rx, out long tx);
        double secs = (now - _lastTick).TotalSeconds;
        _lastTick   = now;
        if (secs <= 0) return;

        long dRx = Math.Max(0, rx - _lastRx);
        long dTx = Math.Max(0, tx - _lastTx);
        _lastRx = rx;
        _lastTx = tx;

        SpeedUpdated?.Invoke(this, new SpeedSample
        {
            DownloadBps = (long)(dRx / secs),
            UploadBps   = (long)(dTx / secs)
        });

        if (dRx > 0 || dTx > 0)
            UsageRecorded?.Invoke(this, (dRx, dTx));
    }

    // ── totals ────────────────────────────────────────────────────────────

    private void GetTotals(out long received, out long sent)
    {
        received = sent = 0;
        foreach (var ni in _adapters)
        {
            try
            {
                var s = ni.GetIPv4Statistics();
                received += s.BytesReceived;
                sent     += s.BytesSent;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"NetworkMonitor.GetTotals: adapter dropped ({ex.Message})");
                // Adapter disappeared mid-poll — force rescan on next tick
                _lastRescan = DateTime.MinValue;
            }
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _timer.Dispose();
    }
}
