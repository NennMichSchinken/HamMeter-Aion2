using K4os.Compression.LZ4;

namespace HamMeter.Protocol;

// Expands "FF FF" bundles - an LZ4 block holding more packets - into the packets they
// carry (docs/protocol.md §3). Game data is untrusted input: the uncompressed size and
// the nesting depth are capped, so a bad packet cannot exhaust memory.
public static class BundleDecoder
{
    private const int MaxUncompressed = 10_000_000;
    private const int MaxDepth = 8;

    public static void Expand(byte[] packet, List<byte[]> output) => Expand(packet, output, 0);

    private static void Expand(byte[] packet, List<byte[]> output, int depth)
    {
        if (!TryDecompress(packet, out byte[]? inner, out int innerLength))
        {
            output.Add(packet);
            return;
        }

        if (depth >= MaxDepth)
        {
            return;
        }

        foreach (byte[] p in Split(inner.AsSpan(0, innerLength)))
        {
            Expand(p, output, depth + 1);
        }
    }

    // The packets inside a decompressed bundle; single 00 bytes between them are padding.
    private static List<byte[]> Split(ReadOnlySpan<byte> data)
    {
        var packets = new List<byte[]>();
        int pos = 0;
        while (pos < data.Length)
        {
            if (data[pos] == 0x00)
            {
                pos++;
                continue;
            }

            if (!VarInt.TryRead(data[pos..], out uint value, out int varLength))
            {
                break;
            }

            long size = (long)value + varLength - 4;
            if (size < varLength + 2 || pos + size > data.Length)
            {
                break;
            }

            packets.Add(data.Slice(pos, (int)size).ToArray());
            pos += (int)size;
        }

        return packets;
    }

    private static bool TryDecompress(byte[] packet, out byte[] output, out int length)
    {
        output = [];
        length = 0;

        if (!VarInt.TryRead(packet, out _, out int pos))
        {
            return false;
        }

        // An optional flag byte F0-FE can precede the marker (meaning unknown).
        if (pos < packet.Length && packet[pos] is >= 0xF0 and < 0xFF)
        {
            pos++;
        }

        if (packet.Length < pos + 6 || packet[pos] != 0xFF || packet[pos + 1] != 0xFF)
        {
            return false;
        }

        int size = BitConverter.ToInt32(packet, pos + 2);
        if (size is < 1 or > MaxUncompressed)
        {
            return false;
        }

        ReadOnlySpan<byte> compressed = packet.AsSpan(pos + 6);
        if (compressed.IsEmpty)
        {
            return false;
        }

        output = new byte[size];
        length = LZ4Codec.Decode(compressed, output);
        return length > 0;
    }
}
