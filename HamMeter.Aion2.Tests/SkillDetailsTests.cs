using HamMeter.Capture;
using HamMeter.Combat;
using HamMeter.Game;
using HamMeter.Protocol;
using HamMeter.UI;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static HamMeter.Tests.CombatPacketParserTests;

namespace HamMeter.Tests;

// The skill details: what the hit packets say per skill (docs/protocol.md §5.1), the boss's
// HP (§5.4), Overall of the boss fights, and the skill names.
public class SkillDetailsTests
{
    private const int User = 3580;
    private const int Mob = 30824;
    private const int Boss = 37250;

    private readonly EncounterTracker m_tracker = new();
    private readonly PacketEngine m_engine;
    private DateTime m_now = new(2026, 10, 8, 22, 34, 3);

    public SkillDetailsTests()
    {
        m_engine = PacketEngine.Offline(m_tracker, NullLoggerFactory.Instance);
        m_engine.Parser.Clock = () => m_now;
        m_engine.Stream.Dispatch(Packet(Opcodes.UserState, w => { w.VarInt(User); w.Bytes(6); }));
    }

    [Fact]
    public void Layout6_CarriesCritDirectionAndFlags_PerSkill()
    {
        this.Hit(Mob, 12_060_010, 1_000, hitType: 3, flags: 0x08, direction: 1);  // crit, double, back
        this.Hit(Mob, 12_060_010, 500, hitType: 2, flags: 0x04, direction: 2);    // perfect, front
        this.Hit(Mob, 12_060_010, 0, hitType: 1);                                  // missed

        SkillTotals s = this.Skill(12_060_010);
        Assert.Equal(1_500, s.Amount);
        Assert.Equal(3, s.Hits);
        Assert.Equal((1, 1, 1, 1, 1, 1), (s.Crits, s.Backs, s.Fronts, s.Doubles, s.Perfects, s.Misses));
        Assert.Equal(1_000, s.MaxHit);

        HitRates rates = SkillDetails.Rates(this.Me());
        Assert.Equal(0.5f, rates.Crit);   // of the two hits that landed
        Assert.Equal(1f / 3f, rates.Missed);
    }

    [Fact]
    public void Ticks_CountApart_AndDoNotLowerTheCritRate()
    {
        this.Hit(Mob, 12_060_010, 1_000, hitType: 3);
        m_engine.Stream.Dispatch(Packet(Opcodes.Tick, w =>
        {
            w.VarInt(Mob);
            w.U8(0x02);
            w.VarInt(User);
            w.VarInt(0);
            w.U32(12_060_010 * 100);
            w.VarInt(300);
        }));

        SkillTotals s = this.Skill(12_060_010);
        Assert.Equal((1, 1, 1_300L), (s.Hits, s.Ticks, s.Amount));
        Assert.Equal(1f, SkillDetails.Rates(this.Me()).Crit);
    }

    [Fact]
    public void TheChart_HasDamagePerSecond_AndTheBossHp()
    {
        this.Hit(Boss, 12_060_010, 1_000, boss: true);
        m_now = m_now.AddSeconds(2);
        this.Hit(Boss, 12_060_010, 400, boss: true);
        this.RemainingHp(Boss, 600);
        m_now = m_now.AddSeconds(1);
        this.RemainingHp(Boss, 300);

        EncounterSnapshot fight = m_tracker.CurrentDetailed!;
        Assert.Equal([1_000f, 0f, 400f], fight.Combatants.Single().DamagePerSecond);
        Assert.Equal(1f, fight.BossHp[2]);     // the most it was seen with is full HP
        Assert.Equal(0.5f, fight.BossHp[3]);
    }

    [Fact]
    public void Overall_OfTheBossFightsOnly_LeavesTheTrashOut()
    {
        this.Hit(Mob, 12_060_010, 1_000);
        m_tracker.Tick(m_now.AddMinutes(1));
        m_now = m_now.AddMinutes(2);
        this.Hit(Boss, 12_060_010, 5_000, boss: true);
        m_tracker.Tick(m_now.AddMinutes(1));

        Assert.Equal(6_000, m_tracker.GetOverall()!.Combatants.Single().DamageTotal);
        EncounterSnapshot bosses = m_tracker.GetOverall(bossesOnly: true)!;
        Assert.Equal(5_000, bosses.Combatants.Single().DamageTotal);
        Assert.Equal("Overall (bosses)", bosses.Title);
        Assert.Equal(5_000, bosses.Combatants.Single().Skills.Single().Amount);
    }

    [Fact]
    public void SkillNames_ComeInGermanAndEnglish_AndVariantsTakeTheirBaseName()
    {
        Assert.Equal("Vergeltungsschlag", SkillNames.Get(12_060_010, "de"));
        Assert.Equal("Urteil", SkillNames.Get(12_240_347, "de"));     // variant, by its base 12240000
        Assert.Equal("Judgment", SkillNames.Get(12_240_000, "en"));
        Assert.Equal("99999999", SkillNames.Get(99_999_999, "en"));   // unknown: the code
    }

    [Fact]
    public void Rows_PutTheVariantsOfASkillOnOneLine()
    {
        this.Hit(Mob, 12_240_340, 3_000, hitType: 3);
        this.Hit(Mob, 12_240_347, 1_000);
        this.Hit(Mob, 12_060_010, 2_000);

        List<SkillRow> rows = SkillDetails.Rows(this.Me(), heal: false, "de");
        Assert.Equal(["Urteil", "Vergeltungsschlag"], rows.Select(r => r.Name));
        Assert.Equal((4_000L, 2, 0.5f), (rows[0].Amount, rows[0].Hits, rows[0].Crit));
    }

    // Uneven dash sizes (the chart scaled to the text size) once froze the overlay: the
    // dashes must always come to an end and cover about dash / (dash + gap) of the line.
    [Fact]
    public void Dashes_FinishAtUnevenSizes_OnALongJaggedLine()
    {
        var rng = new Random(7);
        System.Numerics.Vector2[] line = Enumerable.Range(0, 3600)
            .Select(i => new System.Numerics.Vector2(i * 0.19f, rng.NextSingle() * 171.43f))
            .ToArray();

        var dashes = DetailWindow.DashSegments(line, 6.857f, 4.571f);

        float total = line.Zip(line.Skip(1), System.Numerics.Vector2.Distance).Sum();
        float drawn = dashes.Sum(d => System.Numerics.Vector2.Distance(d.From, d.To));
        Assert.InRange(drawn / total, 0.55f, 0.65f);
    }

    [Fact]
    public void Smooth_AveragesTheLastFiveSeconds()
    {
        float[] s = SkillDetails.Smooth([10f, 0f, 0f, 0f, 0f, 0f], 6);
        Assert.Equal([10f, 5f, 10f / 3f, 2.5f, 2f, 0f], s);
    }

    private Combatant Me() => m_tracker.CurrentDetailed!.Combatants.Single(c => c.Id == User);

    private SkillTotals Skill(int code) => this.Me().Skills.Single(s => s.SkillCode == code);

    // 04 38 in layout 6: hit type, then flags, HP restored, direction (§5.1).
    private void Hit(int target, int skill, int amount, int hitType = 2, byte flags = 0, byte direction = 0, bool boss = false)
    {
        if (boss)
        {
            // A boss of the monster list: Divine Auldor, mob 2310218.
            m_engine.Entities.SetMobCode(target, 2_310_218);
        }

        m_engine.Stream.Dispatch(Packet(Opcodes.Hit, w =>
        {
            w.VarInt(target);
            w.VarInt(6);
            w.VarInt(0);
            w.VarInt(User);
            w.U32(skill);
            w.U8(0);
            w.VarInt(hitType);
            if (hitType is 1 or 6)
            {
                return;
            }

            w.U8(flags);
            w.VarInt(0);
            w.U8(direction);
            w.Bytes(8);
            w.VarInt(10_000);
            w.VarInt(amount);
        }));
    }

    // 00 8D: entity, three varints, HP left as u64.
    private void RemainingHp(int entity, long hp) => m_engine.Stream.Dispatch(Packet(Opcodes.RemainingHp, w =>
    {
        w.VarInt(entity);
        w.VarInt(0);
        w.VarInt(0);
        w.VarInt(0);
        w.U64((ulong)hp);
    }));
}
