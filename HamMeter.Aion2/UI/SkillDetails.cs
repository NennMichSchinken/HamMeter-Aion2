using HamMeter.Combat;
using HamMeter.Game;

namespace HamMeter.UI;

// One line of the skill list: the variants of a skill share its name and one line.
public sealed record SkillRow(string Name, long Amount, int Hits, int Ticks, long MaxHit, float Share, float? Crit, float? Back);

// Shares of a player's direct hits (null when there were none to count): the hits it
// dealt, and for Parry and Block the hits it took. Multi: the game's "Mehrfachtreffer".
public sealed record HitRates(float? Crit, float? Back, float? Front, float? Double, float? Perfect, float? Multi, float? Missed, float? Parry, float? Block);

// What the skill details show, worked out from a combatant's numbers (no drawing here).
public static class SkillDetails
{
    // heal: the healing skills instead of the damage skills.
    public static List<SkillRow> Rows(Combatant c, bool heal, string language)
    {
        List<SkillTotals> skills = c.Skills.Where(s => s.Heal == heal && (s.Amount > 0 || s.Hits > 0)).ToList();
        long total = Math.Max(1, skills.Sum(s => s.Amount));
        return skills
            .GroupBy(s => SkillNames.Get(s.SkillCode, language))
            .Select(g =>
            {
                int hits = g.Sum(s => s.Hits);
                int landed = hits - g.Sum(s => s.Misses);
                long amount = g.Sum(s => s.Amount);
                return new SkillRow(
                    g.Key,
                    amount,
                    hits,
                    g.Sum(s => s.Ticks),
                    g.Max(s => s.MaxHit),
                    (float)amount / total,
                    Share(g.Sum(s => s.Crits), landed),
                    Share(g.Sum(s => s.Backs), landed));
            })
            .OrderByDescending(r => r.Amount)
            .ToList();
    }

    public static HitRates Rates(Combatant c)
    {
        List<SkillTotals> dealt = c.Skills.Where(s => !s.Heal).ToList();
        int hits = dealt.Sum(s => s.Hits);
        int landed = hits - dealt.Sum(s => s.Misses);
        return new HitRates(
            Share(dealt.Sum(s => s.Crits), landed),
            Share(dealt.Sum(s => s.Backs), landed),
            Share(dealt.Sum(s => s.Fronts), landed),
            Share(dealt.Sum(s => s.Doubles), landed),
            Share(dealt.Sum(s => s.Perfects), landed),
            Share(dealt.Sum(s => s.Multis), landed),
            Share(dealt.Sum(s => s.Misses), hits),
            Share(c.Parries, c.HitsTaken),
            Share(c.Blocks, c.HitsTaken));
    }

    // Per second over the last `window` seconds: the line follows the fight without
    // jumping with every single hit.
    public static float[] Smooth(IReadOnlyList<float> perSecond, int length, int window = 5)
    {
        var smooth = new float[length];
        float sum = 0f;
        for (int i = 0; i < length; i++)
        {
            sum += i < perSecond.Count ? perSecond[i] : 0f;
            int drop = i - window;
            if (drop >= 0 && drop < perSecond.Count)
            {
                sum -= perSecond[drop];
            }

            smooth[i] = sum / Math.Min(window, i + 1);
        }

        return smooth;
    }

    private static float? Share(int part, int whole) => whole > 0 ? (float)part / whole : null;
}
