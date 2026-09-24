using HamMeter.Combat;

namespace HamMeter;

public enum Metric
{
    DamageDone,
    DamageTaken,
    HealingDone,
    HealingTaken,
    Deaths,
}

public static class Metrics
{
    public static readonly Metric[] All =
    {
        Metric.DamageDone,
        Metric.DamageTaken,
        Metric.HealingDone,
        Metric.HealingTaken,
        Metric.Deaths,
    };

    public static string Name(Metric m) => m switch
    {
        Metric.DamageDone => "Damage Done",
        Metric.DamageTaken => "Damage Taken",
        Metric.HealingDone => "Healing Done",
        Metric.HealingTaken => "Healing Taken",
        Metric.Deaths => "Deaths",
        _ => "Damage Done",
    };

    // Main value the bar length is based on.
    public static float Value(Combatant c, Metric m) => m switch
    {
        Metric.DamageDone => c.DamageTotal,
        Metric.DamageTaken => c.DamageTaken,
        Metric.HealingDone => c.HealedTotal,
        Metric.HealingTaken => c.HealingTaken,
        Metric.Deaths => c.DeathCount,
        _ => 0f,
    };

    // Per-second rate shown in parentheses, where it makes sense.
    public static float Rate(Combatant c, Metric m) => m switch
    {
        Metric.DamageDone => c.Dps,
        Metric.HealingDone => c.Hps,
        _ => 0f,
    };

    public static bool HasRate(Metric m) => m is Metric.DamageDone or Metric.HealingDone;

    public static bool IsCount(Metric m) => m is Metric.Deaths;
}
