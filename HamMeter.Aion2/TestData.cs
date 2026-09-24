using HamMeter.Combat;

namespace HamMeter;

public static class TestData
{
    public static EncounterSnapshot Build()
    {
        const float seconds = 90f;

        Combatant Make(int id, string name, string job, float dmg, float taken, float healed, int deaths) => new()
        {
            Id = id,
            Name = name,
            Job = job,
            DamageTotal = dmg,
            Dps = dmg / seconds,
            DamageTaken = taken,
            HealedTotal = healed,
            Hps = healed / seconds,
            HealingTaken = taken * 0.8f,
            DeathCount = deaths,
        };

        return new EncounterSnapshot
        {
            Title = "Test Encounter",
            Seconds = seconds,
            Active = true,
            Combatants =
            [
                Make(1, "Test Templar", "TEM", 1_240_000, 540_000, 90_000, 0),
                Make(2, "Test Sorcerer", "SOR", 1_980_000, 210_000, 0, 1),
                Make(3, "Test Assassin", "ASN", 1_710_000, 180_000, 0, 0),
                Make(4, "Test Ranger", "RNG", 1_410_000, 160_000, 60_000, 0),
                Make(5, "Test Gladiator", "GLA", 1_520_000, 330_000, 40_000, 0),
                Make(6, "Test Elementalist", "ELE", 1_300_000, 120_000, 0, 0),
                Make(7, "Test Brawler", "BRW", 1_650_000, 260_000, 70_000, 1),
                Make(8, "Test Cleric", "CLR", 620_000, 150_000, 1_350_000, 0),
                Make(9, "Test Chanter", "CHN", 780_000, 200_000, 980_000, 0),
            ],
        };
    }
}
