using HamMeter.Combat;
using HamMeter.Game;
using HamMeter.Protocol;
using Microsoft.Extensions.Logging;

namespace HamMeter.Capture;

// Turns the combat packets into HamMeter events (docs/protocol.md §5):
//   - damage done by players and their summons,
//   - healing (heal skills on 04 38, heal/HoT effect types on 05 38),
//   - damage taken (monsters hitting a player),
//   - deaths of players, and of monsters (they pause the fight clock).
// Only the user and the party reach the tracker; players nearby are dropped here.
// Who is who comes from an IEntityDirectory, what skill codes mean from ISkillRules.
public sealed class CombatPacketParser
{
    private const long MaxAmount = 99_999_999;

    private readonly IEntityDirectory m_entities;
    private readonly ISkillRules m_skills;
    private readonly EncounterTracker m_tracker;
    private readonly ILogger<CombatPacketParser> m_log;

    public CombatPacketParser(IEntityDirectory entities, ISkillRules skills, EncounterTracker tracker, ILogger<CombatPacketParser> log)
    {
        m_entities = entities;
        m_skills = skills;
        m_tracker = tracker;
        m_log = log;

        m_entities.SummonRegistered += this.OnSummonRegistered;
    }

    // Time of the packet being parsed; a replay sets it to the recorded time.
    public Func<DateTime> Clock { get; set; } = () => DateTime.Now;

    // Per-skill totals for checking against the in-game meter; null = not collected.
    public SkillLog? SkillLog { get; set; }

    public void Register(PacketRouter router)
    {
        router.On(Opcodes.Hit, p => this.Process(Opcodes.Hit, p));
        router.On(Opcodes.Tick, p => this.Process(Opcodes.Tick, p));
        router.On(Opcodes.Death, p => this.Process(Opcodes.Death, p));
    }

    public void Process(ushort opcode, byte[] packet)
    {
        try
        {
            switch (opcode)
            {
                case Opcodes.Hit:
                    this.ProcessHit(packet);
                    break;
                case Opcodes.Tick:
                    this.ProcessTick(packet);
                    break;
                case Opcodes.Death:
                    this.ProcessDeath(packet);
                    break;
            }
        }
        catch (Exception ex)
        {
            // Malformed or truncated frames are expected now and then; never let them
            // escape into the capture thread.
            m_log.LogTrace(ex, "HamMeter parse failed for opcode 0x{Opcode:X4}", opcode);
        }
    }

    // ----- 04 38: direct hit / direct heal ------------------------------------------

    private void ProcessHit(byte[] packet)
    {
        PacketReader reader = PacketReader.Body(packet);

        int targetId = (int)reader.ReadVarInt();
        int layout = LayoutSwitch(reader.ReadVarInt());
        if (layout < 0)
        {
            return;
        }

        reader.ReadVarInt(); // unknown
        int actorId = (int)reader.ReadVarInt();
        int skillCode = (int)reader.ReadU32();
        if (skillCode is < 1 or > 299_999_999)
        {
            return;
        }

        reader.ReadU8();      // unknown
        reader.ReadVarInt();  // hit type (3 = critical)
        if (layout != 4)
        {
            reader.Skip(3);   // hit flags, unknown, direction
        }

        reader.Skip(8);       // unknown
        reader.ReadVarInt();  // scales with the actor's power
        long amount = reader.ReadVarInt();
        if (amount is <= 0 or > MaxAmount)
        {
            return;
        }

        DateTime now = this.Clock();
        PlayerRef? source = this.ResolveSource(actorId, skillCode);

        if (source is null)
        {
            // Not a player skill: a monster hitting someone. On one of ours that's damage taken.
            this.MonsterHit(now, actorId, targetId, amount, tick: false);
            return;
        }

        if (m_skills.IsHealing(skillCode))
        {
            // Self-casts (actor == target) are instant self-heals.
            PlayerRef? healed = actorId == targetId ? source : this.ResolvePlayer(targetId);
            this.Heal(now, source.Value, healed, amount);
            this.SkillLog?.Add(source.Value, "heal", skillCode, amount);
            return;
        }

        if (actorId == targetId)
        {
            // A self-cast that is not a known heal: listed so missing heal skills show up.
            this.SkillLog?.Add(source.Value, "self?", skillCode, amount);
            return;
        }

        this.PlayerHit(now, source.Value, targetId, skillCode, amount, tick: false);
    }

    // Damage by a player skill. Only the user and the party count; there is no friendly
    // fire, so an amount on one of ours is a heal or buff HamMeter does not know yet.
    private void PlayerHit(DateTime now, PlayerRef source, int targetId, int skillCode, long amount, bool tick)
    {
        if (!this.IsOurs(source))
        {
            return;
        }

        if (this.ResolvePlayer(targetId) is { } ally && this.IsOurs(ally))
        {
            this.SkillLog?.Add(source, "ally?", skillCode, amount);
            return;
        }

        m_tracker.DamageDone(now, source, amount, targetId, m_entities.TargetName(targetId), m_entities.IsBoss(targetId), tick);
        this.SkillLog?.Add(source, tick ? "tick" : "hit", skillCode, amount);
    }

    // An action without a player skill: damage taken when it lands on one of ours. A known
    // player as the actor is a skill HamMeter does not know, not an enemy.
    private void MonsterHit(DateTime now, int actorId, int targetId, long amount, bool tick)
    {
        if (actorId != targetId && this.ResolvePlayer(actorId) is null
            && this.ResolvePlayer(targetId) is { } victim && this.IsOurs(victim))
        {
            m_tracker.DamageTaken(now, victim, amount, actorId, tick);
        }
    }

    // Healing done counts for ours, healing taken on ours, whoever healed.
    private void Heal(DateTime now, PlayerRef healer, PlayerRef? healed, long amount) =>
        m_tracker.Healing(
            now,
            this.IsOurs(healer) ? healer : null,
            healed is { } h && this.IsOurs(h) ? h : null,
            amount);

    // Valid only if it fits a byte and the low nibble is 4-7.
    private static int LayoutSwitch(uint raw)
    {
        if (raw > 255)
        {
            return -1;
        }

        int v = (int)(raw & 0x0F);
        return v is >= 4 and <= 7 ? v : -1;
    }

    // ----- 05 38: damage-over-time / heal-over-time tick ------------------------------

    private void ProcessTick(byte[] packet)
    {
        PacketReader reader = PacketReader.Body(packet);

        int targetId = (int)reader.ReadVarInt();

        // Exact match, not a bit mask: 0x0B (HoT) has the damage bit set too.
        byte effectType = reader.ReadU8();
        bool isDamage = effectType is 0x02 or 0x0A;
        bool isHeal = effectType is 0x01 or 0x09 or 0x0B;
        if (!isDamage && !isHeal)
        {
            return;
        }

        int actorId = (int)reader.ReadVarInt();
        reader.ReadVarInt(); // unknown
        int skillCode = (int)(reader.ReadU32() / 100);
        long amount = reader.ReadVarInt();
        if (amount is <= 0 or > MaxAmount)
        {
            return;
        }

        DateTime now = this.Clock();
        PlayerRef? source = this.ResolveSource(actorId, skillCode);

        if (isHeal)
        {
            if (source is null)
            {
                return;
            }

            PlayerRef? healed = actorId == targetId ? source : this.ResolvePlayer(targetId);
            this.Heal(now, source.Value, healed, amount);
            this.SkillLog?.Add(source.Value, "hot", skillCode, amount);
            return;
        }

        if (actorId == targetId)
        {
            return;
        }

        if (source is null)
        {
            this.MonsterHit(now, actorId, targetId, amount, tick: true);
            return;
        }

        if (!m_skills.IsTheostone(skillCode) && !m_skills.CountsAsTickDamage(skillCode))
        {
            this.SkillLog?.Add(source.Value, "tick-ignored", skillCode, amount);
            return;
        }

        this.PlayerHit(now, source.Value, targetId, skillCode, amount, tick: true);
    }

    // ----- 04 8D: death ---------------------------------------------------------------

    private void ProcessDeath(byte[] packet)
    {
        PacketReader reader = PacketReader.Body(packet);
        int entityId = (int)reader.ReadVarInt();

        if (this.ResolvePlayer(entityId) is { } player && this.IsOurs(player))
        {
            m_tracker.Death(this.Clock(), player);
            return;
        }

        // Anything else that dies is no enemy any more, whatever HamMeter took it for: once
        // the last engaged one is dead the fight clock pauses (a no-op for the rest).
        m_tracker.EnemyDied(this.Clock(), entityId);
    }

    // ----- Entity resolution ------------------------------------------------------------

    // The player behind an action: summons resolve to their owner, otherwise the class
    // encoded in the skill code decides whether the actor is a player at all.
    private PlayerRef? ResolveSource(int actorId, int skillCode)
    {
        if (this.SummonPlayer(actorId) is { } owner)
        {
            return owner;
        }

        if (m_entities.IsMonster(actorId) || !IsPlayerSkillCode(skillCode))
        {
            return null;
        }

        long? classId = m_skills.IsTheostone(skillCode)
            ? m_entities.Player(actorId)?.ClassId
            : m_skills.ClassOf(skillCode);
        if (classId is not > 0)
        {
            return null;
        }

        return ToRef(m_entities.AddPlayer(actorId, classId.Value));
    }

    // docs/protocol.md §7. The class is read from the first two digits of a skill code,
    // so 7-digit NPC skills (e.g. 12xxxxx) must be rejected here or they would pass as a
    // class-12 player.
    private static bool IsPlayerSkillCode(int code)
    {
        if (code is < 1 or > 299_999_999)
        {
            return false;
        }

        if (code is >= 3_000_000 and <= 3_099_999)
        {
            return true; // theostone
        }

        if (code is >= 1_000_000 and <= 9_999_999)
        {
            return false; // NPC skill
        }

        if (code is >= 100_000 and < 200_000 or >= 11_000_000 and < 20_000_000)
        {
            return true; // pet / player skill
        }

        foreach (int offset in SkillOffsets)
        {
            int baseCode = code - offset;
            if (baseCode is >= 11_000_000 and < 19_000_000 or >= 100_000 and < 200_000)
            {
                return true;
            }
        }

        return false;
    }

    private static readonly int[] SkillOffsets = [0, 10, 20, 30, 40, 50, 120, 130, 140, 150, 230, 240, 250, 340, 350, 450];

    private PlayerRef? ResolvePlayer(int entityId)
    {
        if (m_entities.IsSummon(entityId) || m_entities.IsMonster(entityId))
        {
            return null;
        }

        return m_entities.Player(entityId) is { } p ? ToRef(p) : null;
    }

    // The user and the party versus players who only happen to be nearby, who never count.
    // Until the user is known every player counts.
    private bool IsOurs(PlayerRef p) => !m_entities.UserKnown || p.IsUser || m_entities.InParty(p.Id);

    private static PlayerRef ToRef(KnownPlayer p)
    {
        string name = p.Name ?? (p.IsUser ? "You" : p.ClassId == ClassInfo.SpiritClassId ? "Spirit" : $"Player_{p.Id}");
        return new PlayerRef(p.Id, name, ClassInfo.KeyFromId(p.ClassId), p.IsUser);
    }

    // The player behind a summon, or null when the entity is no summon. A summon's own
    // summon belongs to the same player, so the chain is followed a few steps (no loops);
    // stopping at the first owner showed such damage as a grey "Player_<id>" that was not
    // in the party (dungeon, 2026-10-07).
    private PlayerRef? SummonPlayer(int entityId)
    {
        int owner = entityId;
        for (int i = 0; i < 4 && m_entities.SummonOwner(owner) is int next; i++)
        {
            owner = next;
        }

        return owner == entityId ? null : this.ResolvePlayer(owner) ?? new PlayerRef(owner, $"Player_{owner}", string.Empty, false);
    }

    private void OnSummonRegistered(int summonId, int ownerId) =>
        m_tracker.MergeSummon(summonId, this.SummonPlayer(summonId) ?? new PlayerRef(ownerId, $"Player_{ownerId}", string.Empty, false));
}
