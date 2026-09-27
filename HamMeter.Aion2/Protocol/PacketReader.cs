using System.Text;

namespace HamMeter.Protocol;

// A packet that does not have the layout a parser expects (truncated, bad varint...).
public sealed class PacketFormatException(string message) : Exception(message);

// Varints as the game writes them: little-endian base-128, 7 value bits per byte,
// high bit set = another byte follows (docs/protocol.md §2).
public static class VarInt
{
    public static bool TryRead(ReadOnlySpan<byte> data, out uint value, out int length)
    {
        value = 0;
        length = 0;
        for (int shift = 0; shift < 35; shift += 7)
        {
            if (length >= data.Length)
            {
                return false;
            }

            byte b = data[length++];
            value |= (uint)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return true;
            }
        }

        return false; // longer than 5 bytes: not a 32-bit varint
    }
}

// Bounds-checked little-endian cursor over one game packet. Every read throws
// PacketFormatException on missing bytes, so a parser can never read past the end.
public sealed class PacketReader
{
    private readonly byte[] m_data;

    public PacketReader(byte[] data, int position = 0)
    {
        m_data = data;
        this.Position = position;
    }

    public int Position { get; private set; }

    public int Remaining => m_data.Length - this.Position;

    // A reader positioned after the length and opcode, at the first body byte.
    public static PacketReader Body(byte[] packet)
    {
        var r = new PacketReader(packet);
        r.ReadVarInt();
        r.Skip(2);
        return r;
    }

    public byte ReadU8()
    {
        this.Require(1);
        return m_data[this.Position++];
    }

    public ushort ReadU16()
    {
        this.Require(2);
        ushort v = (ushort)(m_data[this.Position] | (m_data[this.Position + 1] << 8));
        this.Position += 2;
        return v;
    }

    public uint ReadU32()
    {
        this.Require(4);
        uint v = BitConverter.ToUInt32(m_data, this.Position);
        this.Position += 4;
        return v;
    }

    public ulong ReadU64()
    {
        this.Require(8);
        ulong v = BitConverter.ToUInt64(m_data, this.Position);
        this.Position += 8;
        return v;
    }

    public uint ReadVarInt()
    {
        if (!VarInt.TryRead(m_data.AsSpan(this.Position), out uint value, out int length))
        {
            throw new PacketFormatException($"Bad varint at {this.Position}");
        }

        this.Position += length;
        return value;
    }

    // u8 length + UTF-8 bytes.
    public string ReadShortString()
    {
        int length = this.ReadU8();
        this.Require(length);
        string s = Encoding.UTF8.GetString(m_data, this.Position, length);
        this.Position += length;
        return s;
    }

    public void Skip(int count)
    {
        this.Require(count);
        this.Position += count;
    }

    private void Require(int count)
    {
        if (count < 0 || this.Remaining < count)
        {
            throw new PacketFormatException($"Need {count} bytes at {this.Position}, {this.Remaining} left");
        }
    }
}
