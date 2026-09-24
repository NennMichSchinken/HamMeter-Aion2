using HamMeter.Capture;
using HamMeter.Combat;
using HamMeter.Game;
using HamMeter.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HamMeter.Tests;

// Synthetic packets in the layout of docs/protocol.md §5. They check HamMeter's classification
// (damage / heal / damage taken / death), not the real game's byte layout.
public class CombatPacketParserTests
{
    private const int Gladiator = 1001;
    private const int Cleric = 1002;
    private const int Mob = 5000;

    private const int GladiatorSkill = 11_020_000;
    private const int ClericHeal = 17_120_000;     // a heal family in SkillRules
    private const int NpcSkill = 1_234_567;        // 7-digit NPC skill, "12" must NOT read as Templar

    private readonly EncounterTracker m_tracker = new();
    private readonly EntityRegistry m_entities = new();
    private readonly CombatPacketParser m_parser;

    public CombatPacketParserTests()
    {
        m_parser = new CombatPacketParser(m_entities, new SkillRules(), m_tracker, NullLogger<CombatPacketParser>.Instance);
    }

    [Fact]
    public void PlayerHitOnMob_IsDamageDone()
    {
        this.Hit(Gladiator, Mob, GladiatorSkill, 12_345);

        Combatant g = this.Player(Gladiator);
        Assert.Equal(12_345, g.DamageTotal);
        Assert.Equal("GLA", g.Job);
        Assert.True(m_tracker.InCombat);
    }

    [Fact]
    public void HealSkillOnPlayer_IsHealingDoneAndTaken()
    {
        this.Hit(Gladiator, Mob, GladiatorSkill, 1_000); // starts the fight
        this.Hit(Cleric, Mob, 17_020_000, 500);          // cleric attacks once, becomes known
        this.Hit(Cleric, Gladiator, ClericHeal, 4_000);

        Assert.Equal(4_000, this.Player(Cleric).HealedTotal);
        Assert.Equal(4_000, this.Player(Gladiator).HealingTaken);
        Assert.Equal(500, this.Player(Cleric).DamageTotal);
        Assert.Equal("CLR", this.Player(Cleric).Job);
    }

    [Fact]
    public void SelfCastHeal_IsSelfHealing()
    {
        this.Hit(Gladiator, Mob, GladiatorSkill, 1_000);
        this.Hit(Cleric, Cleric, ClericHeal, 2_500);

        Assert.Equal(2_500, this.Player(Cleric).HealedTotal);
        Assert.Equal(2_500, this.Player(Cleric).HealingTaken);
    }

    [Fact]
    public void NpcSkillOnPlayer_IsDamageTaken_NotATemplar()
    {
        this.Hit(Gladiator, Mob, GladiatorSkill, 1_000);
        this.Hit(Mob, Gladiator, NpcSkill, 7_777);

        Assert.Equal(7_777, this.Player(Gladiator).DamageTaken);
        Assert.DoesNotContain(m_tracker.Current!.Combatants, c => c.Id == Mob);
    }

    [Fact]
    public void HealingOutsideCombat_IsIgnored()
    {
        this.Hit(Cleric, Cleric, ClericHeal, 2_500);

        Assert.Null(m_tracker.Current);
    }

    [Fact]
    public void HotTick_IsHealing_DotOnPlayer_IsDamageTaken()
    {
        this.Hit(Gladiator, Mob, GladiatorSkill, 1_000);
        this.Hit(Cleric, Mob, 17_020_000, 500);

        this.Effect(Gladiator, Cleric, 0x0B, ClericHeal, 300); // HoT tick on the gladiator
        this.Effect(Gladiator, Mob, 0x02, NpcSkill, 150);      // mob DoT tick on the gladiator

        Assert.Equal(300, this.Player(Cleric).HealedTotal);
        Assert.Equal(300, this.Player(Gladiator).HealingTaken);
        Assert.Equal(150, this.Player(Gladiator).DamageTaken);
    }

    [Fact]
    public void Death_OfKnownPlayer_IsCounted()
    {
        this.Hit(Gladiator, Mob, GladiatorSkill, 1_000);
        m_parser.Process(Opcodes.Death, Packet(Opcodes.Death, w => w.VarInt(Gladiator)));

        Assert.Equal(1, this.Player(Gladiator).DeathCount);
    }

    [Fact]
    public void FightEnds_AfterTimeout_AndGoesToHistory()
    {
        m_tracker.CombatTimeoutSeconds = 5;
        this.Hit(Gladiator, Mob, GladiatorSkill, 1_000);

        m_tracker.Tick(DateTime.Now.AddSeconds(6));

        Assert.False(m_tracker.InCombat);
        Assert.Equal(1, m_tracker.PastCount);
        Assert.Equal(1_000, m_tracker.Current!.Combatants.Single().DamageTotal);
    }

    [Fact]
    public void ChainPulls_StayOneFight_AndWalkingIsNotFightTime()
    {
        DateTime t0 = new(2026, 9, 24, 12, 0, 0);
        DateTime now = t0;
        m_parser.Clock = () => now;

        this.Hit(Gladiator, Mob, GladiatorSkill, 1_000);
        now = t0.AddSeconds(4);
        this.Hit(Gladiator, Mob, GladiatorSkill, 1_000);
        this.Died(Mob);                                   // clock pauses: 4 s so far

        now = t0.AddSeconds(20);                          // 16 s walking to the next mob
        m_tracker.Tick(now);
        this.Hit(Gladiator, Mob + 1, GladiatorSkill, 1_000);
        now = t0.AddSeconds(25);
        this.Hit(Gladiator, Mob + 1, GladiatorSkill, 1_000);
        this.Died(Mob + 1);                               // 4 + 5 = 9 s

        m_tracker.Tick(t0.AddSeconds(60));

        Assert.Equal(1, m_tracker.PastCount);
        EncounterSnapshot fight = m_tracker.GetPast(0)!;
        Assert.Equal(9, fight.Seconds, 3);
        Assert.Equal(4_000, fight.Combatants.Single().DamageTotal);
    }

    [Fact]
    public void WithoutDeaths_TheClockRunsUntilTheLastHit()
    {
        DateTime t0 = new(2026, 9, 24, 12, 0, 0);
        DateTime now = t0;
        m_parser.Clock = () => now;

        this.Hit(Gladiator, Mob, GladiatorSkill, 1_000);
        now = t0.AddSeconds(20);
        this.Hit(Gladiator, Mob, GladiatorSkill, 1_000);
        m_tracker.Tick(t0.AddSeconds(60));

        Assert.Equal(20, m_tracker.GetPast(0)!.Seconds, 3);
    }

    [Fact]
    public void LongerThanTheTimeout_StartsANewFight()
    {
        DateTime t0 = new(2026, 9, 24, 12, 0, 0);
        DateTime now = t0;
        m_parser.Clock = () => now;

        this.Hit(Gladiator, Mob, GladiatorSkill, 1_000);
        this.Died(Mob);
        m_tracker.Tick(t0.AddSeconds(31));               // default timeout: 30 s
        now = t0.AddSeconds(40);
        this.Hit(Gladiator, Mob + 1, GladiatorSkill, 1_000);

        Assert.Equal(1, m_tracker.PastCount);
        Assert.True(m_tracker.InCombat);
    }

    [Fact]
    public void AnEnemyStillHittingUs_KeepsTheClockRunning()
    {
        DateTime t0 = new(2026, 9, 24, 12, 0, 0);
        DateTime now = t0;
        m_parser.Clock = () => now;

        this.Hit(Gladiator, Mob, GladiatorSkill, 1_000);
        this.Hit(Mob + 1, Gladiator, NpcSkill, 100);     // a second mob joins in
        now = t0.AddSeconds(3);
        this.Died(Mob);                                   // one of two dead: keeps running
        now = t0.AddSeconds(10);
        this.Hit(Mob + 1, Gladiator, NpcSkill, 100);
        this.Died(Mob + 1);
        m_tracker.Tick(t0.AddSeconds(60));

        Assert.Equal(10, m_tracker.GetPast(0)!.Seconds, 3);
    }

    [Fact]
    public void Boss_GetsItsOwnFight_EvenRightAfterTrash_AndEndsWithItsDeath()
    {
        const int Boss = 9000;
        m_entities.SetMobCode(Mob, 2100456);  // Red Cap Fungen (trash)
        m_entities.SetMobCode(Boss, 2300000); // Submerged Emon (boss)

        DateTime t0 = new(2026, 9, 24, 12, 0, 0);
        DateTime now = t0;
        m_parser.Clock = () => now;

        this.Hit(Gladiator, Mob, GladiatorSkill, 1_000);
        this.Died(Mob);
        now = t0.AddSeconds(10);                          // well inside the 30 s window
        this.Hit(Gladiator, Boss, GladiatorSkill, 5_000);

        Assert.Equal(1, m_tracker.PastCount);             // trash closed on the first boss hit
        Assert.Equal("Red Cap Fungen", m_tracker.GetPast(0)!.Title);
        Assert.Equal("Submerged Emon", m_tracker.Current!.Title);

        now = t0.AddSeconds(40);
        this.Hit(Gladiator, Boss, GladiatorSkill, 5_000);
        this.Died(Boss);                                  // no timeout needed

        Assert.False(m_tracker.InCombat);
        Assert.Equal(2, m_tracker.PastCount);
        EncounterSnapshot boss = m_tracker.GetPast(1)!;
        Assert.Equal(10_000, boss.Combatants.Single().DamageTotal);
        Assert.Equal(30, boss.Seconds, 3);

        now = t0.AddSeconds(45);                          // trash after the boss: a new fight
        this.Hit(Gladiator, Mob + 1, GladiatorSkill, 1_000);
        Assert.Equal(1_000, m_tracker.Current!.Combatants.Single().DamageTotal);
    }

    [Fact]
    public void AddsDuringABoss_StayInTheBossFight()
    {
        const int Boss = 9000;
        m_entities.SetMobCode(Boss, 2300000);

        this.Hit(Gladiator, Boss, GladiatorSkill, 5_000);
        this.Hit(Gladiator, Mob, GladiatorSkill, 1_000);  // an add
        this.Died(Mob);

        Assert.Equal(0, m_tracker.PastCount);
        Assert.Equal(6_000, m_tracker.Current!.Combatants.Single().DamageTotal);
    }

    [Fact]
    public void MonsterList_HasNamesBossFlagsAndDungeons()
    {
        Assert.True(NpcData.Count > 9_000);
        Assert.Equal(new NpcInfo("Red Cap Fungen", false, false, null, null), NpcData.Get(2100456));
        NpcInfo boss = NpcData.Get(2300000)!;
        Assert.True(boss.IsBoss);
        Assert.Equal("Transcendence", boss.Category);
        Assert.Null(NpcData.Get(1));
    }

    // ----- helpers ---------------------------------------------------------------------

    private void Died(int entity) => m_parser.Process(Opcodes.Death, Packet(Opcodes.Death, w => w.VarInt(entity)));

    private Combatant Player(int id) => m_tracker.Current!.Combatants.Single(c => c.Id == id);

    // 04 38 with switch value 5 (3 flag bytes + 8 unknown bytes before the values).
    private void Hit(int actor, int target, int skill, int amount) =>
        m_parser.Process(Opcodes.Hit, Packet(Opcodes.Hit, w =>
        {
            w.VarInt(target);
            w.VarInt(5);      // switch value
            w.VarInt(0);      // unknown flag
            w.VarInt(actor);
            w.U32(skill);
            w.U8(0);          // unknown
            w.VarInt(1);      // damage type (normal)
            w.Bytes(3);       // damage flags, unknown, direction
            w.Bytes(8);       // unknown u32 + tail
            w.VarInt(10_000); // power scalar
            w.VarInt(amount);
        }));

    // 05 38: target, effect type, actor, unknown, skill * 100, amount.
    private void Effect(int target, int actor, byte effectType, int skill, int amount) =>
        m_parser.Process(Opcodes.Tick, Packet(Opcodes.Tick, w =>
        {
            w.VarInt(target);
            w.U8(effectType);
            w.VarInt(actor);
            w.VarInt(0);
            w.U32(skill * 100);
            w.VarInt(amount);
        }));

    internal static byte[] Packet(ushort opcode, Action<Writer> body)
    {
        var w = new Writer();
        body(w);
        byte[] payload = w.ToArray();

        var p = new Writer();
        p.VarInt(payload.Length + 6); // size = value + length bytes - 4 (docs/protocol.md §2)
        p.U8((byte)(opcode & 0xFF));
        p.U8((byte)(opcode >> 8));
        p.Raw(payload);
        return p.ToArray();
    }

    internal sealed class Writer
    {
        private readonly List<byte> m_bytes = new();

        public void U8(byte b) => m_bytes.Add(b);

        public void U32(int v) => m_bytes.AddRange(BitConverter.GetBytes(v));

        public void Bytes(int count) => m_bytes.AddRange(new byte[count]);

        public void Raw(byte[] b) => m_bytes.AddRange(b);

        public void VarInt(int value)
        {
            uint v = (uint)value;
            while (v >= 0x80)
            {
                m_bytes.Add((byte)(v | 0x80));
                v >>= 7;
            }

            m_bytes.Add((byte)v);
        }

        public byte[] ToArray() => m_bytes.ToArray();
    }
}
