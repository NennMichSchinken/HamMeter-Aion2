using HamMeter.Combat;

namespace HamMeter;

// The made-up boss fight of the test mode: bars, and skills, hit rates and a chart for the
// skill details, so the layout can be tried (and shown) without a fight.
public static class TestData
{
    private const int Seconds = 90;

    // Skill weights within a player's damage, biggest first.
    private static readonly float[] Weights = [0.24f, 0.18f, 0.15f, 0.12f, 0.10f, 0.08f, 0.07f, 0.06f];

    private static readonly Lazy<EncounterSnapshot> Fight = new(Make);

    public static EncounterSnapshot Build() => Fight.Value;

    private static EncounterSnapshot Make() => new()
    {
        Title = "Test Boss",
        Seconds = Seconds,
        Active = true,
        IsBoss = true,
        BossHp = Enumerable.Range(0, Seconds + 1).Select(s => 1f - (s / (float)Seconds)).ToArray(),
        Combatants =
        [
            Player(1, "Test Templar", "TEM", 1_240_000, 540_000, 90_000, 0),
            Player(2, "Test Sorcerer", "SOR", 1_980_000, 210_000, 0, 1),
            Player(3, "Test Assassin", "ASN", 1_710_000, 180_000, 0, 0),
            Player(4, "Test Ranger", "RNG", 1_410_000, 160_000, 60_000, 0),
            Player(5, "Test Gladiator", "GLA", 1_520_000, 330_000, 40_000, 0),
            Player(6, "Test Elementalist", "ELE", 1_300_000, 120_000, 0, 0),
            Player(7, "Test Brawler", "BRW", 1_650_000, 260_000, 70_000, 1),
            Player(8, "Test Cleric", "CLR", 620_000, 150_000, 1_350_000, 0),
            Player(9, "Test Chanter", "CHN", 780_000, 200_000, 980_000, 0),
        ],
    };

    private static Combatant Player(int id, string name, string job, long damage, long taken, long healed, int deaths)
    {
        var random = new Random(id);
        int hitsTaken = (int)(taken / 2_500);
        return new Combatant
        {
            Id = id,
            Name = name,
            Job = job,
            DamageTotal = damage,
            Dps = damage / (float)Seconds,
            DamageTaken = taken,
            HealedTotal = healed,
            Hps = healed / (float)Seconds,
            HealingTaken = taken * 0.8f,
            DeathCount = deaths,
            Skills = [.. DamageSkills(job, damage, random), .. HealSkills(job, healed, random)],
            DamagePerSecond = Line(damage, random),
            HealPerSecond = healed > 0 ? Line(healed, random) : [],
            HitsTaken = hitsTaken,
            Parries = job == "TEM" ? hitsTaken / 6 : 0,
            Blocks = job == "TEM" ? hitsTaken / 2 : 0,
        };
    }

    // The class's first eight skills (their names come from the skill list), with hit rates
    // like the ones seen in game.
    private static IEnumerable<SkillTotals> DamageSkills(string job, long damage, Random random)
    {
        int prefix = ClassInfo.SkillPrefix(job);
        for (int i = 0; i < Weights.Length; i++)
        {
            long amount = (long)(damage * Weights[i]);
            int hits = 4 + random.Next(4, 40);
            int Share(float min, float max) => (int)(hits * (min + (random.NextSingle() * (max - min))));
            yield return new SkillTotals(
                (prefix * 1_000_000) + ((i + 1) * 10_000),
                false,
                amount,
                hits,
                amount / hits * 2,
                Share(0.15f, 0.35f),
                Share(0.15f, 0.60f),
                Share(0f, 0.10f),
                Share(0f, 0.05f),
                Share(0f, 0.05f),
                Share(0.10f, 0.30f),
                0,
                0);
        }
    }

    private static IEnumerable<SkillTotals> HealSkills(string job, long healed, Random random)
    {
        if (healed <= 0)
        {
            return [];
        }

        // Cleric: Light of Regeneration (ticks) and Radiant Recovery; Chanter: Recuperation;
        // everyone else: a life potion.
        (int Code, float Weight, bool Ticks)[] skills = job switch
        {
            "CLR" => [(17_090_000, 0.7f, true), (17_120_000, 0.3f, false)],
            "CHN" => [(18_120_000, 1f, false)],
            _ => [(2_011_101, 1f, true)],
        };

        return skills.Select(s =>
        {
            long amount = (long)(healed * s.Weight);
            int count = random.Next(20, 60);
            return s.Ticks
                ? new SkillTotals(s.Code, true, amount, 0, amount / count * 2, 0, 0, 0, 0, 0, 0, 0, count)
                : new SkillTotals(s.Code, true, amount, count, amount / count * 2, count / 4, 0, 0, 0, 0, 0, 0, 0);
        });
    }

    // Per second around the average, in waves, like a real fight.
    private static float[] Line(long total, Random random)
    {
        float average = total / (float)Seconds;
        float phase = random.NextSingle() * 6f;
        return Enumerable.Range(0, Seconds)
            .Select(s => average * (1f + (0.45f * MathF.Sin((s / 7f) + phase)) + (0.25f * (random.NextSingle() - 0.5f))))
            .ToArray();
    }
}
