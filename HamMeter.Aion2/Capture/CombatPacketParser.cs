using AionDpsMeter.Core.GameData.Services;
using AionDpsMeter.Core.Models;
using AionDpsMeter.Services.PacketProcessing.Routing;
using AionDpsMeter.Services.PacketProcessing.Shared;
using AionDpsMeter.Services.Services.Entity;
using HamMeter.Combat;
using Microsoft.Extensions.Logging;

namespace HamMeter.Capture;

// Reads the combat opcodes a second time for HamMeter. Kuroukihime's processors only
// keep player damage; this parser also keeps what they drop:
//   - healing (heal skills on 04 38, heal/HoT effect types on 05 38),
//   - damage taken (NPC actors hitting a known player),
//   - deaths of every known player.
// The byte layout follows Kuroukihime's DamagePacketProcessor / DotDamagePacketProcessor;
// the heal effect types and the NPC-skill rule for damage taken follow A2Tools.
public sealed class CombatPacketParser
{
    private const long MaxAmount = 99_999_999;

    private readonly EntityTracker m_entities;
    private readonly EncounterTracker m_tracker;
    private readonly GameDataProvider m_gameData = GameDataProvider.Instance;
    private readonly ILogger<CombatPacketParser> m_log;

    public CombatPacketParser(EntityTracker entities, EncounterTracker tracker, ILogger<CombatPacketParser> log)
    {
        m_entities = entities;
        m_tracker = tracker;
        m_log = log;

        m_entities.SummonRegistered += this.OnSummonRegistered;
    }

    public void Process(ushort opcode, Packet packet)
    {
        try
        {
            switch (opcode)
            {
                case PacketOpcodes.Damage:
                    this.ProcessHit(packet.Data);
                    break;
                case PacketOpcodes.DotDamage:
                    this.ProcessEffect(packet.Data);
                    break;
                case PacketOpcodes.EntityDeath:
                    this.ProcessDeath(packet.Data);
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

    private void ProcessHit(byte[] data)
    {
        var reader = new PacketReader(data);
        reader.ReadVarInt(); // length
        reader.ReadU16();    // opcode

        int targetId = (int)reader.ReadVarInt();
        int switchValue = SwitchValue((int)reader.ReadVarInt());
        if (switchValue < 0)
        {
            return;
        }

        reader.ReadVarInt(); // unknown flag
        int actorId = (int)reader.ReadVarInt();
        int skillCode = (int)reader.ReadU32();
        if (skillCode < 1 || skillCode > 299_999_999)
        {
            return;
        }

        reader.ReadU8(); // unknown
        reader.ReadVarInt(); // damage type (crit = 3)
        SkipSpecialBlock(reader, switchValue);
        reader.ReadVarInt(); // actor power scalar
        long amount = reader.ReadVarInt();
        if (amount <= 0 || amount > MaxAmount)
        {
            return;
        }

        DateTime now = DateTime.Now;
        PlayerRef? source = this.ResolveSource(actorId, skillCode);

        if (source is null)
        {
            // Not a player skill: an NPC (or unknown summon) hitting someone. If the
            // target is a known player, that's damage taken.
            if (actorId != targetId && this.ResolvePlayer(targetId) is { } victim)
            {
                m_tracker.DamageTaken(now, victim, amount);
            }

            return;
        }

        if (m_gameData.IsHealingSkill(skillCode))
        {
            // Self-casts (actor == target) are instant self-heals.
            PlayerRef? healed = actorId == targetId ? source : this.ResolvePlayer(targetId);
            m_tracker.Healing(now, source.Value, healed, amount);
            return;
        }

        if (actorId == targetId)
        {
            return;
        }

        m_tracker.DamageDone(now, source.Value, amount, targetId, this.TargetName(targetId));
    }

    private static int SwitchValue(int raw)
    {
        if (raw > 255)
        {
            return -1;
        }

        int v = raw & 0x0F;
        return v is >= 4 and <= 7 ? v : -1;
    }

    private static void SkipSpecialBlock(PacketReader reader, int switchValue)
    {
        if (switchValue != 4)
        {
            reader.Skip(3); // damage flags, unknown, attack direction
        }

        reader.Skip(8); // unknown u32 + 4 tail bytes
    }

    // ----- 05 38: damage-over-time / heal-over-time tick ------------------------------

    private void ProcessEffect(byte[] data)
    {
        var reader = new PacketReader(data);
        reader.ReadVarInt(); // length
        reader.ReadU16();    // opcode

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
        int skillCode = (int)reader.ReadU32() / 100;
        long amount = reader.ReadVarInt();
        if (amount <= 0 || amount > MaxAmount)
        {
            return;
        }

        DateTime now = DateTime.Now;
        PlayerRef? source = this.ResolveSource(actorId, skillCode);

        if (isHeal)
        {
            if (source is null)
            {
                return;
            }

            PlayerRef? healed = actorId == targetId ? source : this.ResolvePlayer(targetId);
            m_tracker.Healing(now, source.Value, healed, amount);
            return;
        }

        if (actorId == targetId)
        {
            return;
        }

        if (source is null)
        {
            if (this.ResolvePlayer(targetId) is { } victim)
            {
                m_tracker.DamageTaken(now, victim, amount);
            }

            return;
        }

        // Same gate as Kuroukihime: only curated DoT skills count as damage ticks.
        if (!m_gameData.IsTheostone(skillCode)
            && (!m_gameData.IsDotDamageSkill(skillCode) || m_gameData.IsHealingSkill(skillCode)))
        {
            return;
        }

        m_tracker.DamageDone(now, source.Value, amount, targetId, this.TargetName(targetId));
    }

    // ----- 04 8D: death ---------------------------------------------------------------

    private void ProcessDeath(byte[] data)
    {
        var reader = new PacketReader(data);
        reader.ReadVarInt(); // length
        reader.ReadU16();    // opcode
        int entityId = (int)reader.ReadVarInt();

        if (this.ResolvePlayer(entityId) is { } player)
        {
            m_tracker.Death(DateTime.Now, player);
        }
    }

    // ----- Entity resolution ------------------------------------------------------------

    // The player behind an action: summons resolve to their owner, otherwise the class
    // encoded in the skill code decides whether the actor is a player at all.
    private PlayerRef? ResolveSource(int actorId, int skillCode)
    {
        if (m_entities.GetSummonOwner(actorId) is int ownerId)
        {
            return this.ResolvePlayer(ownerId) ?? new PlayerRef(ownerId, $"Player_{ownerId}", string.Empty, false);
        }

        if (!IsPlayerSkillCode(skillCode))
        {
            return null;
        }

        CharacterClass? cls = m_gameData.IsTheostone(skillCode)
            ? m_entities.GetPlayerEntity(actorId)?.CharacterClass
            : m_gameData.GetClassBySkillCode(skillCode);
        if (cls is null)
        {
            return null;
        }

        Player player = m_entities.GetOrCreateSessionPlayer(actorId, cls);
        return ToRef(player);
    }

    // Mirrors Kuroukihime's DataValidationHelper.IsReasonableSkillCode (internal there).
    // The class is read from the first two digits of a skill code, so 7-digit NPC skills
    // (e.g. 12xxxxx) must be rejected here or they would pass as a class-12 player.
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
        if (m_entities.IsSummon(entityId))
        {
            return null;
        }

        Player? player = m_entities.GetPlayerEntity(entityId);
        return player is null ? null : ToRef(player);
    }

    private static PlayerRef ToRef(Player p)
    {
        long classId = p.CharacterClass?.Id ?? 0;
        string name = classId == ClassInfo.SpiritClassId && p.Name.StartsWith("Player_", StringComparison.Ordinal)
            ? "Spirit"
            : p.Name;
        return new PlayerRef(p.Id, name, ClassInfo.KeyFromId(classId), p.IsUser);
    }

    private string TargetName(int targetId)
    {
        Mob? mob = m_entities.GetTargetMob(targetId);
        return mob is null || mob.MobCode == 0 ? string.Empty : mob.Name;
    }

    private void OnSummonRegistered(int summonId, int ownerId)
    {
        PlayerRef owner = this.ResolvePlayer(ownerId) ?? new PlayerRef(ownerId, $"Player_{ownerId}", string.Empty, false);
        m_tracker.MergeSummon(summonId, owner);
    }
}
