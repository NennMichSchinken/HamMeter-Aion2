using System.Reflection;
using System.Text.Json;

namespace HamMeter.Game;

// Skill names by skill code, German or English, from the embedded lists
// (Game/Data/SOURCE.md). A variant of a skill (12240347) not in the list takes the name of
// its base skill (12240000); without either the code itself is shown.
public static class SkillNames
{
    private static readonly Lazy<Dictionary<int, string>> German = new(() => Load("HamMeter.Skills.de.json"));
    private static readonly Lazy<Dictionary<int, string>> English = new(() => Load("HamMeter.Skills.en.json"));

    // language: "de" or "en".
    public static string Get(int skillCode, string language)
    {
        Dictionary<int, string> names = language == "de" ? German.Value : English.Value;
        if (names.TryGetValue(skillCode, out string? name) || names.TryGetValue(skillCode / 10_000 * 10_000, out name))
        {
            return name;
        }

        return skillCode.ToString();
    }

    // The language setting's default: German on a German Windows, English otherwise.
    public static string DefaultLanguage() =>
        System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "de" ? "de" : "en";

    private static Dictionary<int, string> Load(string resource)
    {
        var names = new Dictionary<int, string>();
        using Stream? stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource);
        if (stream is null)
        {
            return names;
        }

        using JsonDocument doc = JsonDocument.Parse(stream);
        foreach (JsonProperty p in doc.RootElement.EnumerateObject())
        {
            if (int.TryParse(p.Name, out int code) && p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() is { Length: > 0 } name)
            {
                names[code] = name;
            }
        }

        return names;
    }
}
