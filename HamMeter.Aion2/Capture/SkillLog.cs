using System.Text;
using HamMeter.Combat;

namespace HamMeter.Capture;

// Per-skill totals of one fight, written to the log when it ends. Lets a fight be
// checked against the in-game meter skill by skill, and shows skill codes HamMeter
// does not know yet (e.g. "self?" = a self-cast that is not a known heal).
public sealed class SkillLog
{
    private readonly Lock m_sync = new();
    private readonly Dictionary<(string Player, string Kind, int Skill), (int Count, long Total)> m_rows = new();

    public void Add(PlayerRef player, string kind, int skillCode, long amount)
    {
        lock (m_sync)
        {
            var key = (player.IsUser ? $"{player.Name} (you)" : player.Name, kind, skillCode);
            m_rows.TryGetValue(key, out var row);
            m_rows[key] = (row.Count + 1, row.Total + amount);
        }
    }

    // The collected rows as text, then starts over. Null when nothing was collected.
    public string? Drain()
    {
        lock (m_sync)
        {
            if (m_rows.Count == 0)
            {
                return null;
            }

            var text = new StringBuilder("Skill log (player | kind | skill | count | total):");
            foreach (var group in m_rows.GroupBy(kv => kv.Key.Player).OrderByDescending(g => g.Sum(kv => kv.Value.Total)))
            {
                foreach (var kv in group.OrderBy(kv => kv.Key.Kind).ThenByDescending(kv => kv.Value.Total))
                {
                    text.AppendLine().Append($"  {group.Key} | {kv.Key.Kind} | {kv.Key.Skill} | {kv.Value.Count} | {kv.Value.Total:N0}");
                }

                foreach (var kind in group.GroupBy(kv => kv.Key.Kind))
                {
                    text.AppendLine().Append($"  {group.Key} | {kind.Key} total | | {kind.Sum(kv => kv.Value.Count)} | {kind.Sum(kv => kv.Value.Total):N0}");
                }
            }

            m_rows.Clear();
            return text.ToString();
        }
    }
}
