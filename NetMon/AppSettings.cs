using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace NetMon;

/// <summary>Persists user preferences to %AppData%\NetMon\settings.json.</summary>
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

    // Window geometry — restored on next launch
    [JsonPropertyName("winX")]  public int WinX { get; set; } = int.MinValue; // MinValue = not set
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
        catch { }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Opts));
        }
        catch { }
    }

    // ── Windows startup (HKCU Run key — no elevation needed) ─────────────

    private const string RunSubKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string RunName   = "NetMon";

    /// <summary>Returns true when the Run registry entry exists for NetMon.</summary>
    public static bool IsStartupEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunSubKey, false);
            return key?.GetValue(RunName) != null;
        }
        catch { return false; }
    }

    /// <summary>Creates or removes the HKCU Run entry and updates <see cref="StartWithWindows"/>.</summary>
    public void SetStartup(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunSubKey, true);
            if (enable)
            {
                string exePath = Environment.ProcessPath
                              ?? System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName
                              ?? "";
                if (!string.IsNullOrEmpty(exePath))
                    key?.SetValue(RunName, $"\"{exePath}\"");
            }
            else
            {
                key?.DeleteValue(RunName, throwOnMissingValue: false);
            }
        }
        catch { }

        StartWithWindows = enable;
        Save();
    }
}
