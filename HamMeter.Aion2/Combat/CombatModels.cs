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
}

// Immutable snapshot of one encounter (current, a past one, or the combined "Overall").
public sealed class EncounterSnapshot
{
    public string Title { get; init; } = string.Empty;
    public double Seconds { get; init; }
    public bool Active { get; init; }
    public IReadOnlyList<Combatant> Combatants { get; init; } = [];

    public string Duration => FormatDuration(this.Seconds);

    public static string FormatDuration(double seconds)
    {
        TimeSpan ts = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours:00}:{ts.Minutes:00}:{ts.Seconds:00}"
            : $"{ts.Minutes:00}:{ts.Seconds:00}";
    }

    // Sums every finished encounter into one synthetic "Overall" snapshot.
    public static EncounterSnapshot BuildOverall(IReadOnlyList<EncounterSnapshot> encounters)
    {
        double totalSeconds = encounters.Sum(e => e.Seconds);
        double rateSeconds = Math.Max(1, totalSeconds);

        Dictionary<int, Combatant> agg = new();
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
                };
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
            })
            .ToList();

        return new EncounterSnapshot
        {
            Title = "Overall",
            Seconds = totalSeconds,
            Active = false,
            Combatants = combatants,
        };
    }
}
