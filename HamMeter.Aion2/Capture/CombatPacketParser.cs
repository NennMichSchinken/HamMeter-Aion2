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
// Only the user and the party reach the tracker; players nearby are dropped here. A player
// without a name while party members in a dungeon are unaccounted for may be one of them
// and counts on the fight's enemies. Until the user is known the packets wait.
// Who is who comes from an IEntityDirectory, what skill codes mean from ISkillRules.
public sealed class CombatPacketParser
{
    private const long MaxAmount = 99_999_999;
    private const int MaxWaiting = 50_000;

    private readonly IEntityDirectory m_entities;
    private readonly ISkillRules m_skills;
    private readonly EncounterTracker m_tracker;
    private readonly ILogger<CombatPacketParser> m_log;

    // Combat packets from before the user was known, with their time (see Process).
    private readonly Queue<(DateTime Time, ushort Opcode, byte[] Packet)> m_waiting = new();
    private DateTime? m_waitingTime; // time of the waiting packet being parsed

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

    // How far back combat packets are kept while the user is not known (see Process).
    public TimeSpan UserWait { get; init; } = TimeSpan.FromSeconds(30);

    // False: every player counts until the user is known, nothing waits (tests).
    public bool WaitForUser { get; init; } = true;

    // Per-skill totals for checking against the in-game meter; null = not collected.
    public SkillLog? SkillLog { get; set; }

    public void Register(PacketRouter router)
    {
        router.On(Opcodes.Hit, p => this.Process(Opcodes.Hit, p));
        router.On(Opcodes.Tick, p => this.Process(Opcodes.Tick, p));
        router.On(Opcodes.Death, p => this.Process(Opcodes.Death, p));
        router.On(Opcodes.RemainingHp, p => this.Process(Opcodes.RemainingHp, p));

        // Registered after the entity parser, so the registry knows the user by now.
        router.On(Opcodes.UserState, _ => this.ReleaseWaiting());
        router.On(Opcodes.OwnCharacter, _ => this.ReleaseWaiting());
    }

    // Until the user is known HamMeter cannot tell the user and the party from players
    // nearby and would list everyone (open world, 2026-10-08: the user became known 7 to
    // 16 s after the start and strangers filled the first fight). So the packets wait for
    // the user and are parsed with their own time once it is known; only the last
    // UserWait of them are kept. They never count without the user: idle at the start, the
    // user was still unknown after minutes (2026-10-08, 21:55), and counting everyone
    // after a while listed the strangers again.
    public void Process(ushort opcode, byte[] packet)
    {
        DateTime now = this.Clock();
        if (!m_entities.UserKnown && this.WaitForUser)
        {
            m_waiting.Enqueue((now, opcode, packet));
            while (m_waiting.Count > MaxWaiting || now - m_waiting.Peek().Time > this.UserWait)
            {
                m_waiting.Dequeue();
            }

            return;
        }

        this.ReleaseWaiting();
        this.Parse(opcode, packet);
    }

    private void ReleaseWaiting()
    {
        if (m_waiting.Count == 0 || !m_entities.UserKnown)
        {
            return;
        }

        while (m_waiting.TryDequeue(out var waiting))
        {
            m_waitingTime = waiting.Time;
            this.Parse(waiting.Opcode, waiting.Packet);
        }

        m_waitingTime = null;
    }

    private DateTime Now() => m_waitingTime ?? this.Clock();

    private void Parse(ushort opcode, byte[] packet)
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
                case Opcodes.RemainingHp:
                    this.ProcessRemainingHp(packet);
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
        uint layoutSwitch = reader.ReadVarInt();
        int layout = LayoutSwitch(layoutSwitch);
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
        uint hitType = reader.ReadVarInt(); // 2 = normal, 3 = critical, 1 = miss, 6 = resist
        if (hitType is HitMiss or HitResist)
        {
            // No damage follows: counted on the skill only.
            if (actorId != targetId && this.ResolveSource(actorId, skillCode) is { } shooter && this.IsOurs(shooter))
            {
                m_tracker.Missed(shooter, skillCode);
            }

            return;
        }

        // Layout 6 carries the hit flags, the HP restored (a varint) and the direction
        // (§5.1); the other layouts' 3 bytes are not decoded. Bit 20 of the layout switch
        // marks a multi hit (the game's "Mehrfachtreffer").
        var detail = new HitDetail { Crit = hitType == HitCritical, Multi = (layoutSwitch & 0x20) != 0 };
        if (layout == 6)
        {
            byte flags = reader.ReadU8();
            reader.ReadVarInt(); // HP restored
            byte direction = reader.ReadU8();
            detail = detail with
            {
                Back = direction == 1,
                Front = direction == 2,
                Block = (flags & 0x01) != 0,
                Parry = (flags & 0x02) != 0,
                Perfect = (flags & 0x04) != 0,
                Double = (flags & 0x08) != 0,
            };
        }
        else if (layout != 4)
        {
            reader.Skip(3);
        }

        reader.Skip(8);       // unknown
        reader.ReadVarInt();  // scales with the actor's power
        long amount = reader.ReadVarInt();
        if (amount is <= 0 or > MaxAmount)
        {
            return;
        }

        DateTime now = this.Now();
        PlayerRef? source = this.ResolveSource(actorId, skillCode);

        if (source is null)
        {
            // Not a player skill: a monster hitting someone. On one of ours that's damage taken.
            this.MonsterHit(now, actorId, targetId, amount, tick: false, detail);
            return;
        }

        if (m_skills.IsHealing(skillCode))
        {
            // Self-casts (actor == target) are instant self-heals.
            PlayerRef? healed = actorId == targetId ? source : this.ResolvePlayer(targetId);
            this.Heal(now, source.Value, healed, amount, skillCode, detail.Crit);
            this.SkillLog?.Add(source.Value, "heal", skillCode, amount);
            return;
        }

        if (actorId == targetId)
        {
            // A self-cast that is not a known heal: listed so missing heal skills show up.
            this.SkillLog?.Add(source.Value, "self?", skillCode, amount);
            return;
        }

        this.PlayerHit(now, source.Value, targetId, skillCode, amount, tick: false, detail);
    }

    private const uint HitMiss = 1;
    private const uint HitCritical = 3;
    private const uint HitResist = 6;

    // Damage by a player skill. Only the user and the party count; there is no friendly
    // fire, so an amount on one of ours is a heal or buff HamMeter does not know yet.
    private void PlayerHit(DateTime now, PlayerRef source, int targetId, int skillCode, long amount, bool tick, HitDetail detail = default)
    {
        bool sure = this.IsOurs(source);
        if (!sure && !this.MaybeOurs(source))
        {
            return;
        }

        if (this.ResolvePlayer(targetId) is { } ally && (this.IsOurs(ally) || this.MaybeOurs(ally)))
        {
            this.SkillLog?.Add(source, "ally?", skillCode, amount);
            return;
        }

        m_tracker.DamageDone(now, source, amount, targetId, m_entities.TargetName(targetId), m_entities.IsBoss(targetId), tick, sure, skillCode, detail);
        this.SkillLog?.Add(source, tick ? "tick" : "hit", skillCode, amount);
    }

    // An action without a player skill: damage taken when it lands on one of ours. A known
    // player as the actor is a skill HamMeter does not know, not an enemy.
    private void MonsterHit(DateTime now, int actorId, int targetId, long amount, bool tick, HitDetail detail = default)
    {
        if (actorId != targetId && this.ResolvePlayer(actorId) is null && this.ResolvePlayer(targetId) is { } victim)
        {
            bool sure = this.IsOurs(victim);
            if (sure || this.MaybeOurs(victim))
            {
                m_tracker.DamageTaken(now, victim, amount, actorId, tick, sure, detail);
            }
        }
    }

    // Healing done counts for ours, healing taken on ours, whoever healed.
    private void Heal(DateTime now, PlayerRef healer, PlayerRef? healed, long amount, int skillCode = 0, bool crit = false, bool tick = false)
    {
        bool healerSure = this.IsOurs(healer);
        bool healedSure = healed is { } h && this.IsOurs(h);
        m_tracker.Healing(
            now,
            healerSure || this.MaybeOurs(healer) ? healer : null,
            healed is { } t && (healedSure || this.MaybeOurs(t)) ? t : null,
            amount,
            healerSure,
            healedSure,
            skillCode,
            crit,
            tick);
    }

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

        DateTime now = this.Now();
        PlayerRef? source = this.ResolveSource(actorId, skillCode);

        if (isHeal)
        {
            if (source is null)
            {
                return;
            }

            PlayerRef? healed = actorId == targetId ? source : this.ResolvePlayer(targetId);
            this.Heal(now, source.Value, healed, amount, skillCode, tick: true);
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

        if (this.ResolvePlayer(entityId) is { } player && (this.IsOurs(player) || this.MaybeOurs(player)))
        {
            m_tracker.Death(this.Now(), player, this.IsOurs(player));
            return;
        }

        // Anything else that dies is no enemy any more, whatever HamMeter took it for: once
        // the last engaged one is dead the fight clock pauses (a no-op for the rest).
        m_tracker.EnemyDied(this.Now(), entityId);
    }

    // ----- 00 8D: HP left, for the boss line of the chart (§5.4) -------------------------

    // Body: entity, three varints, HP left as u64.
    private void ProcessRemainingHp(byte[] packet)
    {
        PacketReader reader = PacketReader.Body(packet);
        int entityId = (int)reader.ReadVarInt();
        if (!m_entities.IsBoss(entityId))
        {
            return;
        }

        reader.ReadVarInt();
        reader.ReadVarInt();
        reader.ReadVarInt();
        m_tracker.EnemyHp(this.Now(), entityId, (long)reader.ReadU64());
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
    // Until the user is known every player counts (only when nothing waits, see WaitForUser).
    private bool IsOurs(PlayerRef p) => !m_entities.UserKnown || p.IsUser || m_entities.InParty(p.Id);

    // A player HamMeter has no name for while party members in a dungeon are still
    // unaccounted for: may be one of them. Started inside a dungeon, the party's name
    // packets came before HamMeter did and two of four members went missing (replay of
    // 2026-10-07). Strangers who come into view while HamMeter runs have names, solo there
    // is no party to miss, and in the open world the party is often elsewhere while
    // strangers HamMeter has no name for are around (see EntityRegistry.PartyIncomplete).
    private bool MaybeOurs(PlayerRef p) =>
        !m_entities.IsSummon(p.Id) && !m_entities.IsMonster(p.Id)
        && m_entities.Player(p.Id) is { Name: null, IsUser: false }
        && m_entities.PartyIncomplete;

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
