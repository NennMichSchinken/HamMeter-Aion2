using HamMeter.Protocol;

namespace HamMeter.Game;

// A party member from 02 97: the character id (low 32 bits of the database id) and name.
public readonly record struct PartyMember(uint CharacterId, string Name);

// 02 97, the party list (docs/protocol.md §6.4).
//
// How the game packs its single-bit fields is not settled (§6.6), and it decides where
// every member after the first starts. So the packet is read once per candidate packing
// and the one that reads cleanly to the last byte wins; the log names it, so a group
// recording settles the question.
public static class PartyPacket
{
    public enum BitPacking
    {
        // All bit fields of the packet share one byte until its 8 bits are used up.
        Shared,

        // Bit fields that follow each other share a byte; any other field ends the byte.
        PerRun,

        // Every bit field is a byte of its own.
        PerBit,
    }

    private const int MaxMembers = 48;
    private const int MaxNameBytes = 72;

    // The members, or null when no packing reads the packet cleanly.
    public static (IReadOnlyList<PartyMember> Members, BitPacking Packing)? Read(byte[] packet)
    {
        (List<PartyMember> Members, BitPacking Packing)? best = null;
        foreach (BitPacking packing in Enum.GetValues<BitPacking>())
        {
            if (TryRead(packet, packing, out List<PartyMember> members, out int leftOver))
            {
                if (leftOver == 0)
                {
                    return (members, packing);
                }

                // Bytes left over: only trusted with members to show for it, so a stray
                // packet never clears the party.
                if (members.Count > 0)
                {
                    best ??= (members, packing);
                }
            }
        }

        return best;
    }

    internal static bool TryRead(byte[] packet, BitPacking packing, out List<PartyMember> members, out int leftOver)
    {
        members = new List<PartyMember>();
        leftOver = -1;
        try
        {
            var r = new Reader(PacketReader.Body(packet), packing);
            r.U32();            // party key
            r.ShortBytes(byte.MaxValue); // party name
            int size = r.U8();  // party size
            r.U32();            // dungeon id
            r.U8();
            r.U8();
            r.U64();            // leader database id
            r.Bit();
            r.U8();
            r.U8();
            uint count = r.VarInt();
            if (count > MaxMembers)
            {
                return false;
            }

            for (int i = 0; i < count; i++)
            {
                int mask = r.U8();
                int slot = r.U8();
                ulong databaseId = r.U64();
                byte[] rawName = r.ShortBytes(MaxNameBytes);
                r.U32();                          // unknown
                uint level = r.U32();
                if ((mask & 0x01) != 0)
                {
                    r.U32();                      // conqueror level
                }

                uint itemLevel = r.U32();
                if ((mask & 0x02) != 0)
                {
                    r.Bit();                      // ready
                }

                r.Bit();                          // online
                if ((mask & 0x04) != 0)
                {
                    r.U16();                      // origin server
                }

                if ((mask & 0x08) != 0)
                {
                    r.U16();                      // current server
                }

                r.U8();                           // party role
                ulong combatPower = r.U64();
                uint tickets = r.VarInt();
                if (tickets > 64)
                {
                    return false;
                }

                for (int t = 0; t < tickets; t++)
                {
                    r.U8();
                    r.U32();
                }

                if ((mask & 0x10) != 0)
                {
                    r.U64();                      // unknown
                }

                r.U8();                           // mentoring role
                r.U8();                           // latency state

                if (mask == 0)
                {
                    continue;                     // empty slot
                }

                // Sanity: a wrong packing shifts every field and fails here quickly.
                if ((mask & ~0x1F) != 0 || slot < 1 || slot > Math.Max(size, 8) || level is 0 or > 1_000
                    || itemLevel > 1_000_000 || combatPower > 100_000_000_000
                    || EntityPacketParser.DecodeName(rawName) is not { } name)
                {
                    return false;
                }

                members.Add(new PartyMember((uint)databaseId, name));
            }

            leftOver = r.Remaining;
            return true;
        }
        catch (PacketFormatException)
        {
            return false;
        }
    }

    // PacketReader plus single-bit fields in the given packing.
    private sealed class Reader(PacketReader r, BitPacking packing)
    {
        private byte m_bits;
        private int m_bitsUsed = 8; // 8 = no byte loaded

        public int Remaining => r.Remaining;

        public bool Bit()
        {
            if (m_bitsUsed >= 8 || packing == BitPacking.PerBit)
            {
                m_bits = r.ReadU8();
                m_bitsUsed = 0;
            }

            return ((m_bits >> m_bitsUsed++) & 1) != 0;
        }

        public byte U8() => this.Field(r.ReadU8);

        public ushort U16() => this.Field(r.ReadU16);

        public uint U32() => this.Field(r.ReadU32);

        public ulong U64() => this.Field(r.ReadU64);

        public uint VarInt() => this.Field(r.ReadVarInt);

        // u8 length + bytes.
        public byte[] ShortBytes(int max)
        {
            int length = this.U8();
            if (length > max)
            {
                throw new PacketFormatException("Name too long");
            }

            byte[] bytes = new byte[length];
            for (int i = 0; i < length; i++)
            {
                bytes[i] = r.ReadU8();
            }

            return bytes;
        }

        private T Field<T>(Func<T> read)
        {
            if (packing == BitPacking.PerRun)
            {
                m_bitsUsed = 8;
            }

            return read();
        }
    }
}
