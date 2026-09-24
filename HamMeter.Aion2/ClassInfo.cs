using System.Numerics;

namespace HamMeter;

// Aion 2 classes, keyed by a short tag like the FFXIV job abbreviations in HamMeter.
public static class ClassInfo
{
    // Game class id (first two digits of a skill code) -> tag. 10 is the Elementalist's
    // spirit, shown with the Elementalist's colours when its owner is not known yet.
    private static readonly Dictionary<long, string> ById = new()
    {
        [10] = "ELE",
        [11] = "GLA",
        [12] = "TEM",
        [13] = "ASN",
        [14] = "RNG",
        [15] = "SOR",
        [16] = "ELE",
        [17] = "CLR",
        [18] = "CHN",
        [19] = "BRW",
    };

    public const long SpiritClassId = 10;

    public static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["GLA"] = "Gladiator",
        ["TEM"] = "Templar",
        ["ASN"] = "Assassin",
        ["RNG"] = "Ranger",
        ["SOR"] = "Sorcerer",
        ["ELE"] = "Elementalist",
        ["CLR"] = "Cleric",
        ["CHN"] = "Chanter",
        ["BRW"] = "Brawler",
    };

    public static readonly HashSet<string> Tanks = new(StringComparer.OrdinalIgnoreCase) { "TEM" };

    public static readonly HashSet<string> Healers = new(StringComparer.OrdinalIgnoreCase) { "CLR", "CHN" };

    // Role groups for the settings palette.
    public static readonly (string Role, string[] Jobs)[] Groups =
    {
        ("Tank", new[] { "TEM" }),
        ("Healer / Support", new[] { "CLR", "CHN" }),
        ("Melee", new[] { "GLA", "ASN", "BRW" }),
        ("Ranged / Caster", new[] { "RNG", "SOR", "ELE" }),
    };

    private static readonly Dictionary<string, string> IconFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["GLA"] = "gladiator.png",
        ["TEM"] = "templar.png",
        ["ASN"] = "assassin.png",
        ["RNG"] = "ranger.png",
        ["SOR"] = "sorcerer.png",
        ["ELE"] = "elementalist.png",
        ["CLR"] = "cleric.png",
        ["CHN"] = "chanter.png",
        ["BRW"] = "brawler.png",
    };

    public static string KeyFromId(long classId) => ById.TryGetValue(classId, out string? k) ? k : string.Empty;

    public static bool IsKnown(string job) => Names.ContainsKey(job);

    public static string FullName(string job) => Names.TryGetValue(job, out string? n) ? n : job;

    // Resource name of the official class icon (Assets/Classes, embedded).
    public static string? IconResource(string job) =>
        IconFiles.TryGetValue(job, out string? f) ? "HamMeter.Classes." + f : null;

    private static Vector4 Rgb(int r, int g, int b) => new(r / 255f, g / 255f, b / 255f, 1f);

    // The game's own class colours (the main colour of each official icon) at 80 %
    // brightness, so the icon stands out on its bar and white text stays readable.
    public static Dictionary<string, Vector4> DefaultColors() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["TEM"] = Rgb(70, 97, 165),
        ["CLR"] = Rgb(148, 119, 53),
        ["CHN"] = Rgb(145, 100, 46),
        ["GLA"] = Rgb(59, 118, 132),
        ["ASN"] = Rgb(44, 109, 35),
        ["BRW"] = Rgb(125, 29, 32),
        ["RNG"] = Rgb(38, 102, 74),
        ["SOR"] = Rgb(99, 59, 154),
        ["ELE"] = Rgb(128, 47, 129),
    };

    // Defaults before v3 of the config; colours still equal to these were never changed
    // by the user and move to the new defaults (Config.Migrate).
    public static Dictionary<string, Vector4> DefaultColorsV2() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["TEM"] = Rgb(150, 190, 230),
        ["CLR"] = Rgb(250, 245, 225),
        ["CHN"] = Rgb(240, 200, 60),
        ["GLA"] = Rgb(214, 120, 40),
        ["ASN"] = Rgb(140, 70, 160),
        ["BRW"] = Rgb(215, 55, 55),
        ["RNG"] = Rgb(110, 190, 90),
        ["SOR"] = Rgb(70, 110, 230),
        ["ELE"] = Rgb(60, 190, 190),
    };
}
