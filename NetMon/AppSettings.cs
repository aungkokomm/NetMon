using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace NetMon;

/// <summary>Persists user preferences to %AppData%\NetMon\settings.json (atomically).</summary>
public sealed class AppSettings
{
    [JsonPropertyName("bgColor")]
    public string BgColorHex { get; set; } = "#6CB6D8";

    [JsonPropertyName("monthlyLimitGB")]
    public double MonthlyLimitGB { get; set; } = 0.0;

    [JsonPropertyName("alwaysOnTop")]
    public bool AlwaysOnTop { get; set; } = true;

    [JsonPropertyName("opacity")]
    public double Opacity { get; set; } = 1.0;

    [JsonPropertyName("startWithWindows")]
    public bool StartWithWindows { get; set; } = false;

    [JsonPropertyName("startMinimized")]
    public bool StartMinimized { get; set; } = false;

    [JsonPropertyName("hotKeyEnabled")]
    public bool HotKeyEnabled { get; set; } = false;

    [JsonPropertyName("graphFillAlpha")]
    public int GraphFillAlpha { get; set; } = 85;

    // Window geometry — restored on next launch
    [JsonPropertyName("winX")]  public int WinX { get; set; } = int.MinValue;
    [JsonPropertyName("winY")]  public int WinY { get; set; } = int.MinValue;
    [JsonPropertyName("winW")]  public int WinW { get; set; } = 0;
    [JsonPropertyName("winH")]  public int WinH { get; set; } = 0;

    // ── derived ───────────────────────────────────────────────────────────

    [JsonIgnore]
    public Color BgColor
    {
        get
        {
            try   { return ColorTranslator.FromHtml(BgColorHex); }
            catch { return Color.FromArgb(108, 182, 216); }
        }
        set => BgColorHex = ColorTranslator.ToHtml(value);
    }

    [JsonIgnore]
    public long MonthlyLimitBytes => (long)(MonthlyLimitGB * 1_073_741_824L);

    // ── persistence ───────────────────────────────────────────────────────

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "NetMon", "settings.json");

    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new();
        }
        catch (Exception ex) { Debug.WriteLine($"AppSettings: load failed: {ex.Message}"); }
        return new();
    }

    /// <summary>Atomic save — write to .tmp then rename.</summary>
    public void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(FilePath)!;
            Directory.CreateDirectory(dir);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Opts));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex) { Debug.WriteLine($"AppSettings: save failed: {ex.Message}"); }
    }

    // ── Windows startup (HKCU Run key — no elevation needed) ─────────────

    private const string RunSubKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string RunName   = "NetMon";

    /// <summary>True when the HKCU Run entry exists for NetMon.</summary>
    public static bool IsStartupEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunSubKey, false);
            return key?.GetValue(RunName) != null;
        }
        catch (Exception ex) { Debug.WriteLine($"IsStartupEnabled: {ex.Message}"); return false; }
    }

    /// <summary>Creates or removes the Run entry and persists <see cref="StartWithWindows"/>.</summary>
    public void SetStartup(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunSubKey, true);
            if (enable)
            {
                string exePath = Environment.ProcessPath
                              ?? Process.GetCurrentProcess().MainModule?.FileName
                              ?? "";
                if (!string.IsNullOrEmpty(exePath))
                    key?.SetValue(RunName, $"\"{exePath}\"");
            }
            else
            {
                key?.DeleteValue(RunName, throwOnMissingValue: false);
            }
        }
        catch (Exception ex) { Debug.WriteLine($"SetStartup: {ex.Message}"); }

        StartWithWindows = enable;
        Save();
    }
}
