namespace HamMeter.Game;

// HamMeter's own reading of skill codes (docs/protocol.md §7).
public sealed class SkillRules : ISkillRules
{
    // Direct-heal skill families (code with the last four digits cleared). A starting
    // list to be confirmed with recordings: the skill log after each fight shows every
    // self-cast code, so missing heals show up there (docs/protocol.md §7).
    private static readonly HashSet<int> HealFamilies =
    [
        16_190_000, 16_770_000,
        17_100_000, 17_120_000, 17_410_000, 17_800_000,
        18_120_000, 18_170_000,

        // Cleric: self-heal of 83-99 HP while attacking after Light of Regeneration /
        // Earth's Retribution (confirmed in game, recording of 2026-09-24).
        17_720_000,
    ];

    public static int Family(int skillCode) => skillCode / 10_000 * 10_000;

    // The first two decimal digits (pet skills 1xxxxx included). Callers reject NPC
    // codes first: a 7-digit NPC skill 12xxxxx would otherwise read as a Templar.
    public long? ClassOf(int skillCode)
    {
        long classId = skillCode;
        while (classId >= 100)
        {
            classId /= 10;
        }

        return ClassInfo.KeyFromId(classId).Length > 0 ? classId : null;
    }

    public bool IsTheostone(int skillCode) => skillCode is >= 3_000_000 and <= 3_099_999;

    public bool IsHealing(int skillCode) => HealFamilies.Contains(Family(skillCode));

    // Every damage tick of a player skill counts until recordings show otherwise; the
    // skill log lists ticks per skill so they can be checked against the in-game meter.
    public bool CountsAsTickDamage(int skillCode) => !this.IsHealing(skillCode);
}
