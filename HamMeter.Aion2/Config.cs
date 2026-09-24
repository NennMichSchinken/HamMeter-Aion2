using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HamMeter;

public class Config
{
    public int Version { get; set; } = 1;

    // --- Display ---
    public bool OnlyInCombat = false;
    public bool ConfirmReset = true;
    public bool Locked = false;
    public float CombatTimeout = 10f;
    public float BackgroundOpacity = 0.8f;
    public Vector4 BackgroundColor = new(0.086f, 0.086f, 0.102f, 1f); // #16161A (settings window bg)

    // --- Header ---
    public float HeaderHeight = 40f;
    public float HeaderOpacity = 1f;
    public Vector4 HeaderColor = new(0.133f, 0.133f, 0.165f, 1f); // #22222A (settings frame)
    public int TopTextSize = 16;
    public float IconSize = 16f;
    public float IconSpacing = 8f;

    // --- Bars ---
    public float BarHeight = 30f;
    public float BarSpacing = 2f;
    public float BarOpacity = 1f;
    public Vector4 BarTrackColor = new(0.173f, 0.173f, 0.212f, 0.6f); // empty-bar background, fits the UI
    public string BarStyle = UI.BarStyles.DefaultName; // texture of the filled part (WispUI styles)
    public bool ShowRankNumbers = true;
    public bool RoundedBars = true;
    public bool SmoothBars = true;

    // Class indicator on the bar: 0 = off, 1 = text tag (TEM), 2 = class icon.
    public int JobIndicator = 2;

    // Bar color: 0 = by role, 1 = by class.
    public int BarColorMode = 1;

    // --- Bar text ---
    public int LeftTextSize = 16;
    public int RightTextSize = 16;
    public bool ShortNumbers = true;

    // --- Colors (by role) ---
    public Vector4 TankColor = new(0.30f, 0.50f, 0.90f, 1f);
    public Vector4 HealerColor = new(0.30f, 0.80f, 0.40f, 1f);
    public Vector4 DpsColor = new(0.85f, 0.35f, 0.35f, 1f);

    // --- Colors (by class) ---
    public Dictionary<string, Vector4> JobColors = new();

    // --- Testing ---
    public bool TestMode = false;

    // Records the game packets (encrypted) so a fight can be replayed and checked.
    // Never saved: recording is off on every start.
    [JsonIgnore]
    public bool RecordPackets = false;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        IncludeFields = true,
        WriteIndented = true,
    };

    public static string DataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HamMeter-Aion2");

    private static string FilePath => Path.Combine(DataDirectory, "config.json");

    public static Config Load()
    {
        Config config;
        try
        {
            config = File.Exists(FilePath)
                ? JsonSerializer.Deserialize<Config>(File.ReadAllText(FilePath), JsonOptions) ?? new Config()
                : new Config();
        }
        catch (Exception)
        {
            // A broken file must never keep the meter from starting.
            config = new Config();
        }

        config.EnsureJobColors();
        return config;
    }

    public void EnsureJobColors()
    {
        // Rebuild with a case-insensitive comparer: a config loaded from JSON comes back
        // with the default (case-sensitive) comparer.
        Dictionary<string, Vector4> ci = new(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, Vector4> kv in this.JobColors)
        {
            ci[kv.Key] = kv.Value;
        }

        foreach (KeyValuePair<string, Vector4> kv in ClassInfo.DefaultColors())
        {
            ci.TryAdd(kv.Key, kv.Value);
        }

        this.JobColors = ci;
    }

    public void ResetJobColors()
    {
        this.JobColors = ClassInfo.DefaultColors();
        this.Save();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (Exception)
        {
            // Settings are best-effort; the meter keeps running with the in-memory values.
        }
    }
}
