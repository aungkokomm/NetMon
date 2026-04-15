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
/// Persists daily and monthly bandwidth usage in a JSON file at
/// %AppData%\NetMon\usage.json.  Thread-safe.
/// </summary>
public sealed class UsageStore
{
    private readonly string    _path;
    private          UsageData _data;
    private readonly object    _lock = new();

    private static readonly JsonSerializerOptions JsonOpts =
        new() { WriteIndented = true };

    public UsageStore()
    {
        _path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "NetMon", "usage.json");
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        _data = Load();
    }

    public void Add(long bytesDown, long bytesUp)
    {
        lock (_lock)
        {
            string dayKey   = DateTime.Today.ToString("yyyy-MM-dd");
            string monthKey = DateTime.Today.ToString("yyyy-MM");

            if (!_data.Daily.TryGetValue(dayKey, out var day))
                _data.Daily[dayKey] = day = new DayUsage();
            day.BytesReceived += bytesDown;
            day.BytesSent     += bytesUp;

            if (!_data.Monthly.TryGetValue(monthKey, out var mon))
                _data.Monthly[monthKey] = mon = new DayUsage();
            mon.BytesReceived += bytesDown;
            mon.BytesSent     += bytesUp;

            SaveLocked();
        }
    }

    public DayUsage GetToday()
    {
        lock (_lock)
        {
            _data.Daily.TryGetValue(DateTime.Today.ToString("yyyy-MM-dd"), out var d);
            return d ?? new DayUsage();
        }
    }

    public DayUsage GetThisMonth()
    {
        lock (_lock)
        {
            _data.Monthly.TryGetValue(DateTime.Today.ToString("yyyy-MM"), out var m);
            return m ?? new DayUsage();
        }
    }

    public UsageData Snapshot()
    {
        lock (_lock)
        {
            // Return a deep copy so callers can read freely
            var json = JsonSerializer.Serialize(_data, JsonOpts);
            return JsonSerializer.Deserialize<UsageData>(json)!;
        }
    }

    // ── private ────────────────────────────────────────────────────────────

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
        catch { /* corrupt file – start fresh */ }
        return new UsageData();
    }

    private void SaveLocked() // must be called inside _lock
    {
        try { File.WriteAllText(_path, JsonSerializer.Serialize(_data, JsonOpts)); }
        catch { /* disk full etc. – skip this tick */ }
    }
}
