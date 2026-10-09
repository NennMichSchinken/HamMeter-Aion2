using System.Numerics;
using System.Text;
using HamMeter.Protocol;
using Microsoft.Extensions.Logging;

namespace HamMeter.Game;

// Reads who is who from the entity packets (docs/protocol.md §6) into the registry:
// the user's own character, other players, the party and summon owners.
public sealed class EntityPacketParser(EntityRegistry registry, ILogger<EntityPacketParser> log)
{
    private const int MaxNameBytes = 72;
    private const int MobCodeScanBytes = 60;

    private static readonly byte[] SummonBoundary = [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF];
    private static readonly byte[][] SummonHeaders = [[0x07, 0x02, 0x06], [0x07, 0x02, 0x01]];

    public void Register(PacketRouter router)
    {
        router.On(Opcodes.OwnCharacter, p => this.OnCharacter(p, isUser: true));
        router.On(Opcodes.OtherCharacter, p => this.OnCharacter(p, isUser: false));
        router.On(Opcodes.Spawn, this.OnSpawn);
        router.On(Opcodes.UserState, this.OnUserState);
        router.On(Opcodes.Death, this.OnDeath);
        router.On(Opcodes.PartyList, this.OnParty);
        router.On(Opcodes.PartyMember, this.OnPartyMember);
        router.On(Opcodes.Position, p => this.OnPosition(p, movement: false));
        router.On(Opcodes.Movement, p => this.OnPosition(p, movement: true));
        router.On(Opcodes.EntityLink, this.OnEntityLink);
    }

    // ----- 02 97: the party (§6.4) ------------------------------------------------------

    // The game sends the list again on every member change; the log only gets news.
    private string? m_lastParty;

    private void OnParty(byte[] packet)
    {
        string party;
        if (PartyPacket.Read(packet) is (var members, var packing))
        {
            registry.SetParty(members);
            party = $"Party: {(members.Count == 0 ? "none" : string.Join(", ", members.Select(m => m.Name)))} (bit packing {packing})";
        }
        else
        {
            party = "Party list not readable";
        }

        if (party != m_lastParty)
        {
            m_lastParty = party;
            log.LogInformation("{Party} ({Length} bytes)", party, packet.Length);
        }
    }

    // ----- 1C 92: a party member (§6.11) -----------------------------------------------

    // Body: database id u64, unknown u32, zone u32 (the dungeon id inside a dungeon),
    // 8 unknown bytes, position, ...
    private void OnPartyMember(byte[] packet)
    {
        PacketReader r = PacketReader.Body(packet);
        ulong databaseId = r.ReadU64();
        r.ReadU32(); // unknown
        uint zone = r.ReadU32();
        r.Skip(8);
        Vector3 position = ReadPosition(r);
        if ((uint)databaseId > 0)
        {
            registry.SetMember(databaseId, zone, position);
        }
    }

    // ----- 1A 37 / 1B 37: an entity's position (§6.12) ---------------------------------

    // Body: entity varint, then 1A 37: 2 unknown bytes; 1B 37: a flag byte and, when it is
    // odd, one more byte (all 1,457 of a recording read that way); then the position.
    private void OnPosition(byte[] packet, bool movement)
    {
        PacketReader r = PacketReader.Body(packet);
        int entityId = (int)r.ReadVarInt();
        if (!movement)
        {
            r.Skip(2);
        }
        else if ((r.ReadU8() & 1) != 0)
        {
            r.Skip(1);
        }

        Vector3 position = ReadPosition(r);
        if (entityId > 0 && float.IsFinite(position.X) && float.IsFinite(position.Y) && float.IsFinite(position.Z))
        {
            registry.SetPosition(entityId, position);
        }
    }

    private static Vector3 ReadPosition(PacketReader r) => new(
        BitConverter.UInt32BitsToSingle(r.ReadU32()),
        BitConverter.UInt32BitsToSingle(r.ReadU32()),
        BitConverter.UInt32BitsToSingle(r.ReadU32()));

    // ----- 20 36: entity id <-> character id (§6.3) ------------------------------------

    // Body: 2 unknown bytes, entity varint, 4 unknown bytes, character id u32.
    private void OnEntityLink(byte[] packet)
    {
        PacketReader r = PacketReader.Body(packet);
        r.Skip(2);
        int entityId = (int)r.ReadVarInt();
        r.Skip(4);
        uint characterId = r.ReadU32();
        if (entityId > 0 && characterId > 0)
        {
            registry.LinkCharacter(entityId, characterId);
        }
    }

    // ----- 04 8D: a monster's death names the player who killed it (§5.3) -------------

    // Body: dead entity, u32, killer entity (0 = none), killer's server u16, u8 length +
    // killer name. Read as varints the two middle fields swallowed the name's length byte
    // ("HeranorHeranor", 2026-10-08).
    private void OnDeath(byte[] packet)
    {
        PacketReader r = PacketReader.Body(packet);
        r.ReadVarInt();         // the dead entity
        r.ReadU32();            // unknown
        int killer = (int)r.ReadVarInt();
        r.ReadU16();            // the killer's server
        int length = r.ReadU8();
        if (length is < 1 or > MaxNameBytes || length > r.Remaining || registry.Player(killer) is null)
        {
            return;
        }

        byte[] raw = new byte[length];
        for (int i = 0; i < length; i++)
        {
            raw[i] = r.ReadU8();
        }

        if (DecodeName(raw) is { } name)
        {
            registry.SetName(killer, name, isUser: killer == registry.UserId);
        }
    }

    // ----- 4A 36: its first field is always the user (docs/protocol.md §6.10) -----------

    private void OnUserState(byte[] packet)
    {
        int entityId = (int)PacketReader.Body(packet).ReadVarInt();
        if (entityId > 0 && registry.UserId != entityId)
        {
            registry.SetUser(entityId);
            log.LogInformation("Own character recognised (entity {Id})", entityId);
        }
    }

    // ----- 33 36 / 45 36: a named character ---------------------------------------------

    private void OnCharacter(byte[] packet, bool isUser)
    {
        PacketReader r = PacketReader.Body(packet);
        int entityId = (int)r.ReadVarInt();
        if (entityId < 1 || ReadName(r) is not { } name)
        {
            return;
        }

        registry.SetName(entityId, name, isUser);
        if (isUser)
        {
            log.LogInformation("Own character detected (entity {Id})", entityId);
        }
        else
        {
            registry.LinkByAppearance(entityId, packet);
        }
    }

    // Name block (§6.5): 4 unknown bytes, a flag byte (bit 0 = name follows),
    // varint byte length, name bytes.
    private static string? ReadName(PacketReader r)
    {
        r.Skip(4);
        if ((r.ReadU8() & 0x01) == 0)
        {
            return null;
        }

        int length = (int)r.ReadVarInt();
        if (length is < 1 or > MaxNameBytes || length > r.Remaining)
        {
            return null;
        }

        byte[] raw = new byte[length];
        for (int i = 0; i < length; i++)
        {
            raw[i] = r.ReadU8();
        }

        return DecodeName(raw);
    }

    // Names are UTF-8, except that a byte below 0x20 stands for a repeat of the first
    // n bytes decoded so far (observed; §6.5 lists it as open).
    internal static string? DecodeName(ReadOnlySpan<byte> raw)
    {
        var bytes = new List<byte>(raw.Length * 2);
        foreach (byte b in raw)
        {
            if (b == 0)
            {
                break;
            }

            if (b < 0x20)
            {
                int repeat = Math.Min(b, bytes.Count);
                for (int j = 0; j < repeat && bytes.Count < raw.Length * 4; j++)
                {
                    bytes.Add(bytes[j]);
                }
            }
            else
            {
                bytes.Add(b);
            }
        }

        var name = new StringBuilder();
        foreach (char c in Encoding.UTF8.GetString(bytes.ToArray()))
        {
            if (char.IsLetterOrDigit(c))
            {
                name.Append(c);
            }
        }

        return name.Length == 0 || name.ToString().All(char.IsDigit) ? null : name.ToString();
    }

    // ----- 41 36: spawn (§6.7, pattern based) -------------------------------------------

    private void OnSpawn(byte[] packet)
    {
        PacketReader r = PacketReader.Body(packet);
        int entityId = (int)r.ReadVarInt();
        ReadOnlySpan<byte> rest = packet.AsSpan(r.Position);

        if (FindSummonOwner(rest) is int owner && registry.Player(owner) is not null)
        {
            // Only owners we already know as players: a mis-read owner would turn a
            // monster's hits into some player's damage.
            registry.RegisterSummon(entityId, owner);
            return;
        }

        if (FindMobCode(rest) is int mobCode)
        {
            registry.SetMobCode(entityId, mobCode);
        }
    }

    // After a run of eight FF bytes a short header follows; the owner's entity id sits
    // 3 bytes after the header start as u16.
    private static int? FindSummonOwner(ReadOnlySpan<byte> data)
    {
        int boundary = data.IndexOf(SummonBoundary);
        if (boundary < 0)
        {
            return null;
        }

        ReadOnlySpan<byte> after = data[(boundary + SummonBoundary.Length)..];
        foreach (byte[] header in SummonHeaders)
        {
            int at = after.IndexOf(header);
            if (at >= 0 && at + 5 <= after.Length)
            {
                int owner = after[at + 3] | (after[at + 4] << 8);
                return owner > 1 ? owner : null;
            }
        }

        return null;
    }

    // The mob code is the 3-byte number right before a "00 (00|40) 02" marker near the
    // start of the spawn body.
    private static int? FindMobCode(ReadOnlySpan<byte> data)
    {
        int limit = Math.Min(MobCodeScanBytes, data.Length - 3);
        for (int i = 3; i <= limit; i++)
        {
            if (data[i] == 0x00 && (data[i + 1] & 0xBF) == 0 && data[i + 2] == 0x02)
            {
                int code = data[i - 3] | (data[i - 2] << 8) | (data[i - 1] << 16);
                return code > 0 ? code : null;
            }
        }

        return null;
    }
}
