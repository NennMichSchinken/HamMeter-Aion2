using System.Reflection;
using System.Text.Json;

namespace HamMeter.Game;

// One monster type from the monster list (Game/Data/SOURCE.md).
public sealed record NpcInfo(string Name, bool IsBoss, bool IsDummy, string? Category, string? Tier);

// Monster names and boss flags by mob code, loaded once from the embedded list.
public static class NpcData
{
    private static readonly Lazy<Dictionary<int, NpcInfo>> Npcs = new(Load);

    public static int Count => Npcs.Value.Count;

    public static NpcInfo? Get(int mobCode) => Npcs.Value.GetValueOrDefault(mobCode);

    private static Dictionary<int, NpcInfo> Load()
    {
        var npcs = new Dictionary<int, NpcInfo>();
        using Stream? stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("HamMeter.Npcs.json");
        if (stream is null)
        {
            return npcs;
        }

        using JsonDocument doc = JsonDocument.Parse(stream);
        foreach (JsonProperty p in doc.RootElement.EnumerateObject())
        {
            if (!int.TryParse(p.Name, out int code) || p.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            JsonElement v = p.Value;
            npcs[code] = new NpcInfo(
                Text(v, "name") ?? string.Empty,
                Flag(v, "isBoss"),
                Flag(v, "isDummy"),
                Text(v, "category"),
                Text(v, "tier"));
        }

        return npcs;
    }

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Flag(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.True;
}
