using System.Reflection;
using System.Text.Json;

namespace HamMeter.Game;

// One monster type from the monster list (Game/Data/SOURCE.md).
public sealed record NpcInfo(string Name, bool IsBoss, bool IsDummy, string? Category, string? Tier);

// Monster names and boss flags by mob code, loaded once from the embedded list.
public static class NpcData
{
    private static readonly Lazy<(Dictionary<int, NpcInfo> Npcs, Dictionary<int, int> Dungeons)> Data = new(Load);

    public static int Count => Data.Value.Npcs.Count;

    public static NpcInfo? Get(int mobCode) => Data.Value.Npcs.GetValueOrDefault(mobCode);

    // The dungeon a monster belongs to, where the list says (mostly bosses).
    public static int? DungeonOf(int mobCode) => Data.Value.Dungeons.TryGetValue(mobCode, out int id) ? id : null;

    // Every dungeon id in the list (for --replay, to find the packet that names the zone).
    public static IEnumerable<int> DungeonIds => Data.Value.Dungeons.Values.Distinct();

    private static (Dictionary<int, NpcInfo>, Dictionary<int, int>) Load()
    {
        var npcs = new Dictionary<int, NpcInfo>();
        var dungeons = new Dictionary<int, int>();
        using Stream? stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("HamMeter.Npcs.json");
        if (stream is null)
        {
            return (npcs, dungeons);
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
            if (v.TryGetProperty("dungeonId", out JsonElement d) && d.ValueKind == JsonValueKind.Number && d.TryGetInt32(out int dungeon))
            {
                dungeons[code] = dungeon;
            }
        }

        return (npcs, dungeons);
    }

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Flag(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.True;
}
