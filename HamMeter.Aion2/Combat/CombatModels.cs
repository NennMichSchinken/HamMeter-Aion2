namespace HamMeter.Combat;

public enum HitKind
{
    Damage,
    Heal,
}

// One parsed combat record. Actor/target are raw entity ids from the packet; the
// tracker resolves them to players (summons are folded into their owner there).
public readonly record struct CombatHit(
    DateTime Time,
    int ActorId,
    int TargetId,
    int SkillCode,
    long Amount,
    HitKind Kind,
    bool IsDot);

// What a hit packet says beyond the amount (docs/protocol.md §5.1): the hit type and the
// flags and direction that layout 6 carries.
public readonly record struct HitDetail(bool Crit, bool Back, bool Front, bool Double, bool Perfect, bool Block, bool Parry)
{
    public static readonly HitDetail None = default;
}

// One skill of one player in one fight: damage or healing.
// Hits are direct hits (crits, directions and misses count among them), Ticks the damage- or
// heal-over-time ticks, which carry none of that.
public sealed record SkillTotals(int SkillCode, bool Heal, long Amount, int Hits, long MaxHit, int Crits, int Backs, int Fronts, int Doubles, int Perfects, int Misses, int Ticks);

// Immutable per-player totals handed to the UI.
public sealed class Combatant
{
    public int Id { get; init; }
    public string Name { get; init; } = string.Empty;

    // Aion class key (see ClassInfo), empty when the class is not known yet.
    public string Job { get; init; } = string.Empty;
    public bool IsUser { get; init; }

    public float DamageTotal { get; init; }
    public float DamageTaken { get; init; }
    public float HealedTotal { get; init; }
    public float HealingTaken { get; init; }
    public int DeathCount { get; init; }

    public float Dps { get; init; }
    public float Hps { get; init; }

    // Details (only in snapshots taken with details): per skill, and damage and healing per
    // second of the fight for the chart.
    public IReadOnlyList<SkillTotals> Skills { get; init; } = [];
    public IReadOnlyList<float> DamagePerSecond { get; init; } = [];
    public IReadOnlyList<float> HealPerSecond { get; init; } = [];

    // Hits this player took and how many of them it blocked or parried.
    public int HitsTaken { get; init; }
    public int Blocks { get; init; }
    public int Parries { get; init; }
}

// Immutable snapshot of one encounter (current, a past one, or the combined "Overall").
public sealed class EncounterSnapshot
{
    public string Title { get; init; } = string.Empty;
    public double Seconds { get; init; }
    public bool Active { get; init; }

    // A boss fight (monster list); marked with a crown in the history.
    public bool IsBoss { get; init; }
    public IReadOnlyList<Combatant> Combatants { get; init; } = [];

    // The boss's HP per second as a fraction of its full HP (00 8D); empty without a boss.
    public IReadOnlyList<float> BossHp { get; init; } = [];

    public string Duration => FormatDuration(this.Seconds);

    public static string FormatDuration(double seconds)
    {
        TimeSpan ts = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours:00}:{ts.Minutes:00}:{ts.Seconds:00}"
            : $"{ts.Minutes:00}:{ts.Seconds:00}";
    }

    // Sums every finished encounter into one synthetic "Overall" snapshot, skills included
    // (no chart: the fights have no common timeline).
    public static EncounterSnapshot BuildOverall(IReadOnlyList<EncounterSnapshot> encounters, string title = "Overall")
    {
        double totalSeconds = encounters.Sum(e => e.Seconds);
        double rateSeconds = Math.Max(1, totalSeconds);

        Dictionary<int, Combatant> agg = new();
        Dictionary<int, List<SkillTotals>> skills = new();
        foreach (EncounterSnapshot enc in encounters)
        {
            foreach (Combatant c in enc.Combatants)
            {
                agg.TryGetValue(c.Id, out Combatant? a);
                agg[c.Id] = new Combatant
                {
                    Id = c.Id,
                    Name = c.Name,
                    Job = string.IsNullOrEmpty(c.Job) ? a?.Job ?? string.Empty : c.Job,
                    IsUser = c.IsUser || (a?.IsUser ?? false),
                    DamageTotal = (a?.DamageTotal ?? 0) + c.DamageTotal,
                    DamageTaken = (a?.DamageTaken ?? 0) + c.DamageTaken,
                    HealedTotal = (a?.HealedTotal ?? 0) + c.HealedTotal,
                    HealingTaken = (a?.HealingTaken ?? 0) + c.HealingTaken,
                    DeathCount = (a?.DeathCount ?? 0) + c.DeathCount,
                    HitsTaken = (a?.HitsTaken ?? 0) + c.HitsTaken,
                    Blocks = (a?.Blocks ?? 0) + c.Blocks,
                    Parries = (a?.Parries ?? 0) + c.Parries,
                };
                if (!skills.TryGetValue(c.Id, out List<SkillTotals>? list))
                {
                    skills[c.Id] = list = new List<SkillTotals>();
                }

                list.AddRange(c.Skills);
            }
        }

        List<Combatant> combatants = agg.Values
            .Select(c => new Combatant
            {
                Id = c.Id,
                Name = c.Name,
                Job = c.Job,
                IsUser = c.IsUser,
                DamageTotal = c.DamageTotal,
                DamageTaken = c.DamageTaken,
                HealedTotal = c.HealedTotal,
                HealingTaken = c.HealingTaken,
                DeathCount = c.DeathCount,
                Dps = (float)(c.DamageTotal / rateSeconds),
                Hps = (float)(c.HealedTotal / rateSeconds),
                HitsTaken = c.HitsTaken,
                Blocks = c.Blocks,
                Parries = c.Parries,
                Skills = SumSkills(skills[c.Id]),
            })
            .ToList();

        return new EncounterSnapshot
        {
            Title = title,
            Seconds = totalSeconds,
            Active = false,
            Combatants = combatants,
        };
    }

    private static List<SkillTotals> SumSkills(IEnumerable<SkillTotals> all) => all
        .GroupBy(s => (s.SkillCode, s.Heal))
        .Select(g => new SkillTotals(
            g.Key.SkillCode,
            g.Key.Heal,
            g.Sum(s => s.Amount),
            g.Sum(s => s.Hits),
            g.Max(s => s.MaxHit),
            g.Sum(s => s.Crits),
            g.Sum(s => s.Backs),
            g.Sum(s => s.Fronts),
            g.Sum(s => s.Doubles),
            g.Sum(s => s.Perfects),
            g.Sum(s => s.Misses),
            g.Sum(s => s.Ticks)))
        .ToList();
}
