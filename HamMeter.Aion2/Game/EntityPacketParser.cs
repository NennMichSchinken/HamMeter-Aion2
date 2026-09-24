using System.Text;
using HamMeter.Protocol;
using Microsoft.Extensions.Logging;

namespace HamMeter.Game;

// Reads who is who from the entity packets (docs/protocol.md §6) into the registry:
// the user's own character, other players and summon owners.
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
