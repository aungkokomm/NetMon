using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetMon;

public sealed class DayUsage
{
    [JsonPropertyName("rx")] public long BytesReceived { get; set; }
    [JsonPropertyName("tx")] public long BytesSent     { get; set; }
}

public sealed class UsageData
{
    [JsonPropertyName("daily")]   public Dictionary<string, DayUsage> Daily   { get; set; } = new();
    [JsonPropertyName("monthly")] public Dictionary<string, DayUsage> Monthly { get; set; } = new();
}

/// <summary>
/// Persists daily and monthly bandwidth usage to %AppData%\NetMon\usage.json.
///
/// Writes are batched: in-memory deltas are accumulated on every Add() but
/// flushed to disk at most once per <see cref="FlushIntervalSec"/> seconds,
/// and always on Flush() / Dispose().  Writes are atomic — data is written
/// to a .tmp file and then renamed over the real file, so a crash or power
/// loss mid-write cannot corrupt existing history.
/// </summary>
public sealed class UsageStore : IDisposable
{
    private const int FlushIntervalSec = 30;

    private readonly string    _path;
    private readonly string    _tmpPath;
    private readonly object    _lock = new();
    private          UsageData _data;
    private          DateTime  _lastFlush = DateTime.UtcNow;
    private          bool      _dirty;

    private static readonly JsonSerializerOptions JsonOpts =
        new() { WriteIndented = true };

    public UsageStore()
    {
        _path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "NetMon", "usage.json");
        _tmpPath = _path + ".tmp";

        try { Directory.CreateDirectory(Path.GetDirectoryName(_path)!); }
        catch (Exception ex) { Debug.WriteLine($"UsageStore: mkdir failed: {ex.Message}"); }

        _data = Load();
    }

    // ── public API ────────────────────────────────────────────────────────

    public void Add(long bytesDown, long bytesUp)
    {
        lock (_lock)
        {
            var now = DateTime.Now;
            string dayKey   = now.ToString("yyyy-MM-dd");
            string monthKey = now.ToString("yyyy-MM");

            if (!_data.Daily.TryGetValue(dayKey, out var day))
                _data.Daily[dayKey] = day = new DayUsage();
            day.BytesReceived += bytesDown;
            day.BytesSent     += bytesUp;

            if (!_data.Monthly.TryGetValue(monthKey, out var mon))
                _data.Monthly[monthKey] = mon = new DayUsage();
            mon.BytesReceived += bytesDown;
            mon.BytesSent     += bytesUp;

            _dirty = true;

            // Throttled flush — at most once every FlushIntervalSec
            if ((DateTime.UtcNow - _lastFlush).TotalSeconds >= FlushIntervalSec)
                FlushLocked();
        }
    }

    public DayUsage GetToday()
    {
        lock (_lock)
        {
            _data.Daily.TryGetValue(DateTime.Now.ToString("yyyy-MM-dd"), out var d);
            return Clone(d);
        }
    }

    public DayUsage GetThisMonth()
    {
        lock (_lock)
        {
            _data.Monthly.TryGetValue(DateTime.Now.ToString("yyyy-MM"), out var m);
            return Clone(m);
        }
    }

    /// <summary>Returns a deep copy of all usage history.</summary>
    public UsageData Snapshot()
    {
        lock (_lock)
        {
            var snap = new UsageData();
            foreach (var kv in _data.Daily)   snap.Daily[kv.Key]   = Clone(kv.Value);
            foreach (var kv in _data.Monthly) snap.Monthly[kv.Key] = Clone(kv.Value);
            return snap;
        }
    }

    /// <summary>Erase all stored usage history and flush immediately.</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _data  = new UsageData();
            _dirty = true;
            FlushLocked();
        }
    }

    /// <summary>Force a flush — used on shutdown and after destructive ops.</summary>
    public void Flush() { lock (_lock) FlushLocked(); }

    public void Dispose() => Flush();

    // ── private ────────────────────────────────────────────────────────────

    private static DayUsage Clone(DayUsage? src) =>
        src is null
            ? new DayUsage()
            : new DayUsage { BytesReceived = src.BytesReceived, BytesSent = src.BytesSent };

    private UsageData Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var text = File.ReadAllText(_path);
                return JsonSerializer.Deserialize<UsageData>(text) ?? new UsageData();
            }
        }
        catch (Exception ex) { Debug.WriteLine($"UsageStore: load failed, starting fresh: {ex.Message}"); }
        return new UsageData();
    }

    /// <summary>Atomic write. Must be called inside _lock.</summary>
    private void FlushLocked()
    {
        if (!_dirty) return;
        try
        {
            File.WriteAllText(_tmpPath, JsonSerializer.Serialize(_data, JsonOpts));
            File.Move(_tmpPath, _path, overwrite: true);
            _dirty     = false;
            _lastFlush = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"UsageStore: flush failed: {ex.Message}");
            try { if (File.Exists(_tmpPath)) File.Delete(_tmpPath); } catch { }
        }
    }
}
