using System.Buffers.Binary;
using Microsoft.Extensions.Logging;

namespace HamMeter.Capture;

// The part both capture devices share (docs/protocol.md §1):
//   - Before any payload byte is read, a packet must match an established TCP
//     connection owned by Aion2.exe exactly (remote ip:port -> local ip:port).
//     Everything else is dropped after the IP/TCP header checks.
//   - Every header length is bounds-checked; fragments and non-IPv4 are dropped.
//   - A connection only feeds the sink after Aion's keep-alive has been seen on it
//     several times.
//   - Segments are passed on in order: duplicates are skipped, gaps are logged and
//     the stream continues from the new position.
// Server -> client traffic is all a damage meter needs.
public sealed class TcpReassembler(IStreamSink sink, ILogger log, string keyPrefix)
{
    private const int HeartbeatThreshold = 5;

    private static readonly byte[] Heartbeat = [0x0E, 0x00, 0x36];

    private readonly Lock m_sync = new();
    private readonly Dictionary<TcpConnection, StreamState> m_streams = new();
    private volatile HashSet<TcpConnection> m_allowed = new();
    private long m_matchedPackets;

    public long MatchedPackets => Interlocked.Read(ref m_matchedPackets);

    public bool HasValidatedStream
    {
        get
        {
            lock (m_sync)
            {
                return m_streams.Values.Any(s => s.Validated);
            }
        }
    }

    public IReadOnlyCollection<TcpConnection> Allowed => m_allowed;

    // Replaces the set of accepted connections; streams of closed connections are dropped.
    public void SetAllowed(IEnumerable<TcpConnection> connections)
    {
        m_allowed = new HashSet<TcpConnection>(connections);
        lock (m_sync)
        {
            foreach (TcpConnection gone in m_streams.Keys.Where(c => !m_allowed.Contains(c)).ToList())
            {
                m_streams.Remove(gone);
                sink.ClearStream(this.StreamKey(gone));
            }
        }
    }

    public void Clear()
    {
        lock (m_sync)
        {
            foreach (TcpConnection c in m_streams.Keys)
            {
                sink.ClearStream(this.StreamKey(c));
            }

            m_streams.Clear();
        }
    }

    // One IPv4 packet, starting at the IP header.
    public void HandleIpPacket(ReadOnlySpan<byte> ip)
    {
        if (ip.Length < 20 || (ip[0] >> 4) != 4)
        {
            return;
        }

        int ihl = (ip[0] & 0x0F) * 4;
        int total = BinaryPrimitives.ReadUInt16BigEndian(ip[2..]);
        if (ihl < 20 || total < ihl || total > ip.Length)
        {
            return;
        }

        // Drop fragments (more-fragments flag or a non-zero offset).
        ushort fragment = BinaryPrimitives.ReadUInt16BigEndian(ip[6..]);
        if ((fragment & 0x3FFF) != 0 || ip[9] != 6)
        {
            return;
        }

        // Addresses kept in network byte order, like the Windows TCP table.
        uint source = BinaryPrimitives.ReadUInt32LittleEndian(ip[12..]);
        uint destination = BinaryPrimitives.ReadUInt32LittleEndian(ip[16..]);

        ReadOnlySpan<byte> tcp = ip[ihl..total];
        if (tcp.Length < 20)
        {
            return;
        }

        ushort sourcePort = BinaryPrimitives.ReadUInt16BigEndian(tcp);
        ushort destinationPort = BinaryPrimitives.ReadUInt16BigEndian(tcp[2..]);

        // Server -> client only, and only Aion's own connections.
        var conn = new TcpConnection(destination, destinationPort, source, sourcePort);
        if (!m_allowed.Contains(conn))
        {
            return;
        }

        int dataOffset = (tcp[12] >> 4) * 4;
        if (dataOffset < 20 || dataOffset > tcp.Length)
        {
            return;
        }

        Interlocked.Increment(ref m_matchedPackets);

        byte flags = tcp[13];
        uint seq = BinaryPrimitives.ReadUInt32BigEndian(tcp[4..]);
        ReadOnlySpan<byte> payload = tcp[dataOffset..];

        lock (m_sync)
        {
            this.HandleSegment(conn, seq, flags, payload);
        }
    }

    private void HandleSegment(TcpConnection conn, uint seq, byte flags, ReadOnlySpan<byte> payload)
    {
        const byte Fin = 0x01;
        const byte Syn = 0x02;
        const byte Rst = 0x04;

        string key = this.StreamKey(conn);
        if ((flags & (Fin | Rst)) != 0)
        {
            m_streams.Remove(conn);
            sink.ClearStream(key);
            return;
        }

        if (!m_streams.TryGetValue(conn, out StreamState? state))
        {
            state = new StreamState();
            m_streams[conn] = state;
        }

        if (!state.Validated)
        {
            if (payload.IndexOf(Heartbeat) >= 0 && ++state.Heartbeats >= HeartbeatThreshold)
            {
                state.Validated = true;
                log.LogInformation("[{Prefix}] Aion game stream detected", keyPrefix);
            }

            return;
        }

        uint length = (uint)payload.Length + ((flags & Syn) != 0 ? 1u : 0u);
        uint next = seq + length;

        if (state.ExpectedSeq is uint expected)
        {
            // Serial-number arithmetic so wrap-around at 2^32 is handled.
            int delta = (int)(seq - expected);
            if (delta < 0 && (int)(next - expected) <= 0)
            {
                return; // duplicate / retransmission
            }

            if (delta > 0)
            {
                log.LogDebug("[{Prefix}] TCP gap of {Bytes} bytes", keyPrefix, delta);
            }
        }

        state.ExpectedSeq = next;
        if (payload.Length > 0)
        {
            sink.AddData(key, payload.ToArray(), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }
    }

    private string StreamKey(TcpConnection c) => $"{keyPrefix}:{c.RemotePort}:{c.LocalPort}";

    private sealed class StreamState
    {
        public int Heartbeats;
        public bool Validated;
        public uint? ExpectedSeq;
    }
}
