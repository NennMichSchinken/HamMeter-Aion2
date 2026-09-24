using System.Net;
using System.Net.Sockets;
using System.Security.Principal;
using Microsoft.Extensions.Logging;

namespace HamMeter.Capture;

// Packet capture without Npcap, using Windows raw sockets (needs administrator rights).
//
// Security model - the process runs elevated, so it must touch as little foreign data
// as possible:
//   - RCVALL_IPLEVEL, not RCVALL_ON: the network card is NOT put into promiscuous
//     mode; only packets addressed to this PC are seen.
//   - TcpReassembler drops everything that is not Aion's own connection before any
//     payload byte is read.
// Raw sockets never see loopback traffic, so VPN/booster tunnels need Npcap.
public sealed class RawSocketCaptureDevice : IGameCapture
{
    private const int SioRcvall = unchecked((int)0x98000001);
    private const int RcvallIpLevel = 3;
    private const int RefreshMs = 2000;
    private const int ReceiveBufferBytes = 4 * 1024 * 1024;
    private const int MaxDatagram = 65535;

    private readonly TcpReassembler m_reassembler;
    private readonly ILogger<RawSocketCaptureDevice> m_log;
    private readonly Lock m_sync = new();
    private readonly Dictionary<uint, Socket> m_sockets = new();

    private Timer? m_refresh;
    private volatile bool m_capturing;
    private DateTime m_connectionsSince = DateTime.MaxValue;

    public RawSocketCaptureDevice(IStreamSink sink, ILogger<RawSocketCaptureDevice> log)
    {
        m_reassembler = new TcpReassembler(sink, log, "Raw");
        m_log = log;
    }

    public bool IsCapturing => m_capturing;

    public string? DeviceName => m_reassembler.HasValidatedStream ? "Raw socket" : null;

    // A hint for the UI when Aion is connected but no packet of it ever arrived:
    // almost always the Windows Firewall dropping raw-socket traffic.
    public bool LooksBlocked =>
        m_capturing
        && m_reassembler.MatchedPackets == 0
        && (DateTime.Now - m_connectionsSince).TotalSeconds > 15;

    internal long MatchedPackets => m_reassembler.MatchedPackets;

    public static bool IsElevated()
    {
        using WindowsIdentity id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
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
        }

        m_reassembler.Clear();
        m_log.LogInformation("[RAW] Capture stopped");
    }

    public void Dispose() => this.StopCapture();

    internal void SetAllowed(IEnumerable<TcpConnection> connections) => m_reassembler.SetAllowed(connections);

    internal void HandleDatagram(ReadOnlySpan<byte> ip) => m_reassembler.HandleIpPacket(ip);

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
            conns = GameConnections.Find(includeLoopback: false);
        }
        catch (Exception ex)
        {
            m_log.LogWarning("[RAW] Could not read the TCP table: {Message}", ex.Message);
            return;
        }

        m_reassembler.SetAllowed(conns);
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
        }
    }

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
                m_reassembler.HandleIpPacket(buffer.AsSpan(0, n));
            }
            catch (Exception ex)
            {
                m_log.LogDebug(ex, "[RAW] Dropped a malformed packet");
            }
        }
    }
}
