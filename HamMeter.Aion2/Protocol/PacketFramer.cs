namespace HamMeter.Protocol;

// Cuts one TCP stream into game packets (docs/protocol.md §2). Starts unsynchronised
// and waits for the keep-alive pattern, because a capture starts mid-stream and a TCP
// gap destroys the packet boundary; an impossible length re-enters that search.
public sealed class PacketFramer
{
    public const int MaxPacketSize = 40 * 1024;
    private const int MaxBuffered = 4 * 1024 * 1024;

    private static readonly byte[] SyncPattern = [0x0E, 0x00, 0x36];

    private byte[] m_buffer = new byte[64 * 1024];
    private int m_start;
    private int m_end;
    private bool m_synced;

    public bool Synced => m_synced;

    public void Append(ReadOnlySpan<byte> data, Action<byte[]> onPacket)
    {
        this.Store(data);

        while (m_end > m_start)
        {
            ReadOnlySpan<byte> pending = m_buffer.AsSpan(m_start, m_end - m_start);

            if (!m_synced)
            {
                int sync = pending.IndexOf(SyncPattern);
                if (sync < 0)
                {
                    // The pattern may be split across segments: keep its possible start.
                    m_start = Math.Max(m_start, m_end - (SyncPattern.Length - 1));
                    break;
                }

                m_start += sync;
                m_synced = true;
                continue;
            }

            if (!VarInt.TryRead(pending, out uint value, out int varLength))
            {
                if (pending.Length >= 5)
                {
                    this.Desync(); // 5 bytes and still no end: not a length field
                    continue;
                }

                break; // the length field itself is still incomplete
            }

            long size = (long)value + varLength - 4;
            if (size < varLength + 2 || size > MaxPacketSize)
            {
                this.Desync();
                continue;
            }

            if (pending.Length < size)
            {
                break;
            }

            byte[] packet = pending[..(int)size].ToArray();
            m_start += (int)size;

            if (Opcodes.TryRead(packet, out ushort opcode) && opcode != Opcodes.KeepAlive)
            {
                onPacket(packet);
            }
        }

        this.Compact();
    }

    public void Reset()
    {
        m_start = 0;
        m_end = 0;
        m_synced = false;
    }

    private void Desync()
    {
        m_synced = false;
        m_start++; // skip the byte that looked like a length and search again
    }

    private void Store(ReadOnlySpan<byte> data)
    {
        int pending = m_end - m_start;
        if (pending + data.Length > MaxBuffered)
        {
            // Nothing sensible fits this much unframed data: start over.
            this.Reset();
            pending = 0;
            if (data.Length > MaxBuffered)
            {
                return;
            }
        }

        if (m_end + data.Length > m_buffer.Length)
        {
            if (pending + data.Length > m_buffer.Length)
            {
                var bigger = new byte[Math.Max(m_buffer.Length * 2, pending + data.Length)];
                m_buffer.AsSpan(m_start, pending).CopyTo(bigger);
                m_buffer = bigger;
            }
            else
            {
                m_buffer.AsSpan(m_start, pending).CopyTo(m_buffer);
            }

            m_start = 0;
            m_end = pending;
        }

        data.CopyTo(m_buffer.AsSpan(m_end));
        m_end += data.Length;
    }

    private void Compact()
    {
        if (m_start == m_end)
        {
            m_start = 0;
            m_end = 0;
        }
    }
}
