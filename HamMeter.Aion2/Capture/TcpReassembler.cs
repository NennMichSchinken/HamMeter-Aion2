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
//   - Segments are passed on in order: duplicates are skipped, segments ahead of a gap
//     are held until the lost segment is sent again (a VPN or ping booster loses some
//     on the way), and a gap that never fills is skipped after a while.
// Server -> client traffic is all a damage meter needs.
public sealed class TcpReassembler(IStreamSink sink, ILogger log, string keyPrefix)
{
    private const int HeartbeatThreshold = 5;

    // How long and how much is held for a gap before it is skipped. A resend comes after
    // a few hundred milliseconds; the meter waits that long at most.
    private const long MaxHoldMs = 3000;
    private const int MaxHeldBytes = 2 * 1024 * 1024;

    // Milliseconds, for the hold time (tests set their own).
    public Func<long> Clock { get; init; } = () => Environment.TickCount64;

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

        if ((flags & Syn) != 0)
        {
            seq++; // the SYN takes one sequence number before the data
        }

        if (state.ExpectedSeq is not uint expected)
        {
            state.ExpectedSeq = seq;
            expected = seq;
        }

        // Serial-number arithmetic so wrap-around at 2^32 is handled.
        int delta = (int)(seq - expected);
        if (delta > 0)
        {
            // Ahead of the stream: a segment before it was lost on the way and will be sent
            // again, or arrives late. Held back until the gap is filled.
            if (payload.Length > 0 && (!state.Held.TryGetValue(seq, out byte[]? held) || held.Length < payload.Length))
            {
                state.HeldBytes += payload.Length - (held?.Length ?? 0);
                state.Held[seq] = payload.ToArray();
                state.HeldSince ??= this.Clock();
            }
        }
        else if ((int)(seq + (uint)payload.Length - expected) > 0)
        {
            // In order, or a resend that overlaps what was passed on: only the new bytes.
            this.Pass(key, state, payload[(-delta)..]);
        }

        this.Drain(key, state);

        // A gap that never fills (the capture missed it): continue after it.
        if (state.Held.Count > 0 && (state.HeldBytes > MaxHeldBytes || this.Clock() - state.HeldSince >= MaxHoldMs))
        {
            uint first = state.Held.Keys.MinBy(s => (int)(s - state.ExpectedSeq!.Value));
            log.LogInformation("[{Prefix}] TCP gap of {Bytes} bytes skipped", keyPrefix, (int)(first - state.ExpectedSeq!.Value));
            state.ExpectedSeq = first;
            this.Drain(key, state);
        }
    }

    // Passes on the held segments the stream has now reached.
    private void Drain(string key, StreamState state)
    {
        while (state.Held.Count > 0)
        {
            uint expected = state.ExpectedSeq!.Value;
            (uint seq, byte[] data) = state.Held.MinBy(kv => (int)(kv.Key - expected));
            int delta = (int)(seq - expected);
            if (delta > 0)
            {
                return;
            }

            state.Held.Remove(seq);
            state.HeldBytes -= data.Length;
            if (state.Held.Count == 0)
            {
                state.HeldSince = null;
            }

            if (data.Length + delta > 0)
            {
                this.Pass(key, state, data.AsSpan(-delta));
            }
        }
    }

    private void Pass(string key, StreamState state, ReadOnlySpan<byte> data)
    {
        state.ExpectedSeq = state.ExpectedSeq!.Value + (uint)data.Length;
        sink.AddData(key, data.ToArray(), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    private string StreamKey(TcpConnection c) => $"{keyPrefix}:{c.RemotePort}:{c.LocalPort}";

    private sealed class StreamState
    {
        public int Heartbeats;
        public bool Validated;
        public uint? ExpectedSeq;

        // Segments that arrived ahead of the stream, by sequence number.
        public readonly Dictionary<uint, byte[]> Held = new();
        public int HeldBytes;
        public long? HeldSince;
    }
}
