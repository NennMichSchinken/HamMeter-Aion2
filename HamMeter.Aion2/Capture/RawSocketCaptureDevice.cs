using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Principal;
using AionDpsMeter.Services.PacketCapture;
using Microsoft.Extensions.Logging;

namespace HamMeter.Capture;

// Packet capture without Npcap, using Windows raw sockets (needs administrator rights).
//
// Security model - the process runs elevated, so it must touch as little foreign data
// as possible:
//   - RCVALL_IPLEVEL, not RCVALL_ON: the network card is NOT put into promiscuous
//     mode; only packets addressed to this PC are seen.
//   - Before any payload byte is read, a packet must match an established TCP
//     connection owned by Aion2.exe exactly (remote ip:port -> local ip:port).
//     Everything else is dropped after the IP/TCP header checks.
//   - Every header length is bounds-checked; fragments and non-IPv4 are dropped.
//   - A connection only feeds the parser after Aion's heartbeat has been seen on it
//     several times (same rule as Kuroukihime's CaptureDevice).
// Server -> client traffic is all a damage meter needs, which is what raw sockets
// deliver reliably.
public sealed class RawSocketCaptureDevice : IPacketCaptureDevice
{
    private const int SioRcvall = unchecked((int)0x98000001);
    private const int RcvallIpLevel = 3;
    private const int RefreshMs = 2000;
    private const int HeartbeatThreshold = 5;
    private const int ReceiveBufferBytes = 4 * 1024 * 1024;
    private const int MaxDatagram = 65535;

    private static readonly byte[] Heartbeat = { 0x0E, 0x00, 0x36 };

    private readonly TcpStreamBuffer m_streamBuffer;
    private readonly ILogger<RawSocketCaptureDevice> m_log;
    private readonly Lock m_sync = new();
    private readonly Dictionary<uint, Socket> m_sockets = new();
    private readonly Dictionary<TcpConnection, StreamState> m_streams = new();

    private volatile HashSet<TcpConnection> m_allowed = new();
    private Timer? m_refresh;
    private volatile bool m_capturing;
    private long m_matchedPackets;
    private DateTime m_connectionsSince = DateTime.MaxValue;

    public RawSocketCaptureDevice(TcpStreamBuffer streamBuffer, ILogger<RawSocketCaptureDevice> log)
    {
        m_streamBuffer = streamBuffer;
        m_log = log;
    }

    public bool IsCapturing => m_capturing;

    public string? DeviceName
    {
        get
        {
            lock (m_sync)
            {
                return m_streams.Values.Any(s => s.Validated) ? "Raw socket" : null;
            }
        }
    }

    // A hint for the UI when Aion is connected but no packet of it ever arrived:
    // almost always the Windows Firewall dropping raw-socket traffic.
    public bool LooksBlocked =>
        m_capturing
        && Interlocked.Read(ref m_matchedPackets) == 0
        && (DateTime.Now - m_connectionsSince).TotalSeconds > 15;

    public static bool IsElevated()
    {
        using WindowsIdentity id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public void DiscoverAdapters()
    {
        // Interfaces are chosen from Aion's own connections at runtime.
    }

    public void StartCapture()
    {
        if (m_capturing)
        {
            return;
        }

        if (!IsElevated())
        {
            throw new UnauthorizedAccessException("Raw-socket capture needs administrator rights.");
        }

        m_capturing = true;
        m_refresh = new Timer(_ => this.Refresh(), null, 0, RefreshMs);
        m_log.LogInformation("[RAW] Capture started, waiting for Aion2.exe connections");
    }

    public void StopCapture()
    {
        if (!m_capturing)
        {
            return;
        }

        m_capturing = false;
        m_refresh?.Dispose();
        m_refresh = null;

        lock (m_sync)
        {
            foreach (Socket s in m_sockets.Values)
            {
                s.Dispose(); // unblocks the receive thread
            }

            m_sockets.Clear();
            m_streams.Clear();
        }

        m_log.LogInformation("[RAW] Capture stopped");
    }

    public void Dispose() => this.StopCapture();

    // ----- Connection tracking --------------------------------------------------------

    private void Refresh()
    {
        if (!m_capturing)
        {
            return;
        }

        List<TcpConnection> conns;
        try
        {
            conns = GameConnections.Find();
        }
        catch (Exception ex)
        {
            m_log.LogWarning("[RAW] Could not read the TCP table: {Message}", ex.Message);
            return;
        }

        this.SetAllowed(conns);
        if (conns.Count > 0 && m_connectionsSince == DateTime.MaxValue)
        {
            m_connectionsSince = DateTime.Now;
        }
        else if (conns.Count == 0)
        {
            m_connectionsSince = DateTime.MaxValue;
        }

        HashSet<uint> neededInterfaces = conns.Select(c => c.LocalAddress).ToHashSet();

        lock (m_sync)
        {
            foreach (uint addr in m_sockets.Keys.Where(a => !neededInterfaces.Contains(a)).ToList())
            {
                m_sockets[addr].Dispose();
                m_sockets.Remove(addr);
            }

            foreach (uint addr in neededInterfaces.Where(a => !m_sockets.ContainsKey(a)))
            {
                this.OpenSocket(addr);
            }

            foreach (TcpConnection gone in m_streams.Keys.Where(c => !m_allowed.Contains(c)).ToList())
            {
                m_streams.Remove(gone);
                m_streamBuffer.ClearStream(StreamKey(gone));
            }
        }
    }

    internal void SetAllowed(IEnumerable<TcpConnection> connections) =>
        m_allowed = new HashSet<TcpConnection>(connections);

    internal long MatchedPackets => Interlocked.Read(ref m_matchedPackets);

    private void OpenSocket(uint localAddress)
    {
        var address = new IPAddress(localAddress);
        try
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Raw, ProtocolType.IP)
            {
                ReceiveBufferSize = ReceiveBufferBytes,
            };
            socket.Bind(new IPEndPoint(address, 0));
            socket.IOControl(SioRcvall, BitConverter.GetBytes(RcvallIpLevel), new byte[4]);

            m_sockets[localAddress] = socket;
            new Thread(() => this.ReceiveLoop(socket))
            {
                Name = "HamMeter raw capture",
                IsBackground = true,
            }.Start();

            m_log.LogInformation("[RAW] Listening on the interface of Aion's connection");
        }
        catch (Exception ex)
        {
            m_log.LogWarning("[RAW] Could not open a raw socket: {Message}", ex.Message);
        }
    }

    private void ReceiveLoop(Socket socket)
    {
        byte[] buffer = new byte[MaxDatagram];
        while (m_capturing)
        {
            int n;
            try
            {
                n = socket.Receive(buffer);
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                return; // socket closed by Refresh/Stop
            }

            try
            {
                this.HandleDatagram(buffer.AsSpan(0, n));
            }
            catch (Exception ex)
            {
                m_log.LogDebug(ex, "[RAW] Dropped a malformed packet");
            }
        }
    }

    // ----- IPv4 / TCP ---------------------------------------------------------------------

    internal void HandleDatagram(ReadOnlySpan<byte> ip)
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

    // Same in-order handling as Kuroukihime's CaptureDevice: duplicates are skipped,
    // gaps are logged and the stream continues from the new position.
    private void HandleSegment(TcpConnection conn, uint seq, byte flags, ReadOnlySpan<byte> payload)
    {
        const byte Fin = 0x01;
        const byte Syn = 0x02;
        const byte Rst = 0x04;

        string key = StreamKey(conn);
        if ((flags & (Fin | Rst)) != 0)
        {
            m_streams.Remove(conn);
            m_streamBuffer.ClearStream(key);
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
                m_log.LogInformation("[RAW] Aion game stream detected");
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
                m_log.LogDebug("[RAW] TCP gap of {Bytes} bytes", delta);
            }
        }

        state.ExpectedSeq = next;
        if (payload.Length > 0)
        {
            m_streamBuffer.AddData(key, payload.ToArray(), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }
    }

    private static string StreamKey(TcpConnection c) => $"Raw:{c.RemotePort}:{c.LocalPort}";

    private sealed class StreamState
    {
        public int Heartbeats;
        public bool Validated;
        public uint? ExpectedSeq;
    }
}
