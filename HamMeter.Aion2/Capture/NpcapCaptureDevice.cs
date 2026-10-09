using System.Buffers.Binary;
using System.Net;
using Microsoft.Extensions.Logging;
using PacketDotNet;
using SharpPcap;
using SharpPcap.LibPcap;

namespace HamMeter.Capture;

// Packet capture through Npcap (no administrator rights needed).
//
// Same rules as the raw-socket capture, enforced twice:
//   - Only the adapters that carry one of Aion2.exe's connections are opened, in
//     non-promiscuous mode, with a kernel filter that lets through only packets from
//     the servers Aion is connected to (server -> client).
//   - TcpReassembler checks every packet against Aion's exact connections again.
// Unlike raw sockets, Npcap also sees loopback, so VPN/booster tunnels work.
public sealed class NpcapCaptureDevice : IGameCapture
{
    private const int RefreshMs = 2000;
    private const int ReadTimeoutMs = 500;

    // Kernel buffer for the 500 ms between reads: a big fight must not overflow it (the
    // default is 1 MB), or hits are lost.
    private const int KernelBufferBytes = 8 * 1024 * 1024;

    // Loopback segments are not cut to the MTU and can be far bigger than the usual 64 KB
    // capture length; a cut one is lost for good. 256 KB is the most Npcap takes.
    private const int SnapLength = 256 * 1024;

    private readonly TcpReassembler m_reassembler;
    private readonly ILogger<NpcapCaptureDevice> m_log;
    private readonly Lock m_sync = new();
    private readonly Dictionary<string, OpenDevice> m_open = new();

    private Timer? m_refresh;
    private volatile bool m_capturing;

    public NpcapCaptureDevice(IStreamSink sink, ILogger<NpcapCaptureDevice> log)
    {
        m_reassembler = new TcpReassembler(sink, log, "Npcap");
        m_log = log;
    }

    public bool IsCapturing => m_capturing;

    public string? DeviceName => m_reassembler.HasValidatedStream ? "Npcap" : null;

    public bool LooksBlocked => false;

    public void StartCapture()
    {
        if (m_capturing)
        {
            return;
        }

        _ = LibPcapLiveDeviceList.Instance; // throws here if Npcap is not usable
        m_capturing = true;
        m_refresh = new Timer(_ => this.Refresh(), null, 0, RefreshMs);
        m_log.LogInformation("[NPCAP] Capture started, waiting for Aion2.exe connections");
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
            foreach (OpenDevice d in m_open.Values)
            {
                Close(d.Device);
            }

            m_open.Clear();
        }

        m_reassembler.Clear();
        m_log.LogInformation("[NPCAP] Capture stopped");
    }

    public void Dispose() => this.StopCapture();

    // ----- Adapters -------------------------------------------------------------------

    private void Refresh()
    {
        if (!m_capturing)
        {
            return;
        }

        List<TcpConnection> conns;
        try
        {
            conns = GameConnections.Find(includeLoopback: true);
        }
        catch (Exception ex)
        {
            m_log.LogWarning("[NPCAP] Could not read the TCP table: {Message}", ex.Message);
            return;
        }

        m_reassembler.SetAllowed(conns);

        lock (m_sync)
        {
            if (!m_capturing)
            {
                return;
            }

            Dictionary<string, (LibPcapLiveDevice Device, List<TcpConnection> Conns)> wanted = new();
            if (conns.Count > 0)
            {
                LibPcapLiveDeviceList devices = LibPcapLiveDeviceList.Instance;
                foreach (TcpConnection c in conns)
                {
                    if (FindDevice(devices, c) is not { } device)
                    {
                        continue;
                    }

                    if (!wanted.TryGetValue(device.Name, out var entry))
                    {
                        entry = (device, new List<TcpConnection>());
                        wanted[device.Name] = entry;
                    }

                    entry.Conns.Add(c);
                }
            }

            foreach (string name in m_open.Keys.Where(n => !wanted.ContainsKey(n)).ToList())
            {
                Close(m_open[name].Device);
                m_open.Remove(name);
            }

            foreach ((string name, var entry) in wanted)
            {
                if (m_open.TryGetValue(name, out OpenDevice? open))
                {
                    // Setting a filter makes Npcap drop what waits in its buffer: only when
                    // Aion talks to a server endpoint it has not before.
                    int before = open.Endpoints.Count;
                    open.Endpoints.UnionWith(Endpoints(entry.Conns));
                    if (open.Endpoints.Count != before)
                    {
                        this.TrySetFilter(open.Device, Filter(open.Endpoints));
                        m_log.LogInformation("[NPCAP] Capture filter now covers {Count} server endpoints", open.Endpoints.Count);
                    }
                }
                else
                {
                    var endpoints = new SortedSet<string>(Endpoints(entry.Conns), StringComparer.Ordinal);
                    if (this.Open(entry.Device, Filter(endpoints)))
                    {
                        m_open[name] = new OpenDevice(entry.Device, endpoints);
                    }
                }
            }
        }
    }

    // The adapter a connection runs over: loopback for tunnels, otherwise the adapter
    // that owns the connection's local address.
    private static LibPcapLiveDevice? FindDevice(LibPcapLiveDeviceList devices, TcpConnection c)
    {
        if (GameConnections.IsLoopback(c.LocalAddress) || GameConnections.IsLoopback(c.RemoteAddress))
        {
            return devices.FirstOrDefault(d => d.Name.Contains("NPF_Loopback", StringComparison.OrdinalIgnoreCase));
        }

        IPAddress local = c.Local;
        return devices.FirstOrDefault(d => d.Addresses.Any(a => local.Equals(a.Addr?.ipAddress)));
    }

    // Kernel filter: packets from the servers Aion is connected to. It names the server
    // side only and in a fixed order, so it stays the same while Aion opens and closes
    // connections (each new filter drops Npcap's buffer: 16 lost pieces of the stream in
    // 7 minutes on 2026-10-09). TcpReassembler still takes Aion's own connections only.
    internal static IEnumerable<string> Endpoints(IEnumerable<TcpConnection> conns) =>
        conns.Select(c => $"(src host {new IPAddress(c.RemoteAddress)} and src port {c.RemotePort})");

    internal static string Filter(IEnumerable<string> endpoints) =>
        "tcp and (" + string.Join(" or ", endpoints.Order(StringComparer.Ordinal)) + ")";

    private bool Open(LibPcapLiveDevice device, string filter)
    {
        try
        {
            device.OnPacketArrival += this.OnPacketArrival;
            device.Open(new DeviceConfiguration { Mode = DeviceModes.None, ReadTimeout = ReadTimeoutMs, BufferSize = KernelBufferBytes, Snaplen = SnapLength });
            device.Filter = filter;
            device.StartCapture();
            m_log.LogInformation("[NPCAP] Listening on the adapter of Aion's connection ({Kind})",
                device.Name.Contains("Loopback", StringComparison.OrdinalIgnoreCase) ? "loopback" : "network");
            return true;
        }
        catch (Exception ex)
        {
            m_log.LogWarning("[NPCAP] Could not open an adapter: {Message}", ex.Message);
            device.OnPacketArrival -= this.OnPacketArrival;
            Close(device);
            return false;
        }
    }

    private void TrySetFilter(LibPcapLiveDevice device, string filter)
    {
        try
        {
            device.Filter = filter;
        }
        catch (Exception ex)
        {
            m_log.LogWarning("[NPCAP] Could not update the capture filter: {Message}", ex.Message);
        }
    }

    private void Close(LibPcapLiveDevice device)
    {
        device.OnPacketArrival -= this.OnPacketArrival;
        try
        {
            device.StopCapture();
        }
        catch (Exception)
        {
            // Not capturing.
        }

        try
        {
            device.Close();
        }
        catch (Exception)
        {
            // Already closed.
        }
    }

    // ----- Packets --------------------------------------------------------------------

    private void OnPacketArrival(object sender, PacketCapture e)
    {
        try
        {
            ReadOnlySpan<byte> frame = e.Data;
            if (IpPayload(e.Device.LinkType, frame) is int offset)
            {
                m_reassembler.HandleIpPacket(frame[offset..]);
            }
        }
        catch (Exception ex)
        {
            m_log.LogDebug(ex, "[NPCAP] Dropped a malformed packet");
        }
    }

    // Where the IPv4 header starts in a captured frame, or null if it is not IPv4.
    internal static int? IpPayload(LinkLayers link, ReadOnlySpan<byte> frame)
    {
        switch (link)
        {
            case LinkLayers.Ethernet:
                if (frame.Length < 14)
                {
                    return null;
                }

                ushort type = BinaryPrimitives.ReadUInt16BigEndian(frame[12..]);
                if (type == 0x8100 && frame.Length >= 18)
                {
                    return BinaryPrimitives.ReadUInt16BigEndian(frame[16..]) == 0x0800 ? 18 : null; // VLAN tag
                }

                return type == 0x0800 ? 14 : null;

            case LinkLayers.Null:
            case LinkLayers.Loop:
                // 4-byte address family (2 = IPv4), host or network byte order.
                if (frame.Length < 4)
                {
                    return null;
                }

                uint family = BinaryPrimitives.ReadUInt32LittleEndian(frame);
                return family is 2 or 0x02000000 ? 4 : null;

            case LinkLayers.Raw:
            case LinkLayers.RawLegacy:
                return 0;

            default:
                return null;
        }
    }

    // The server endpoints only grow while the adapter is open: see Filter.
    private sealed class OpenDevice(LibPcapLiveDevice device, SortedSet<string> endpoints)
    {
        public LibPcapLiveDevice Device { get; } = device;

        public SortedSet<string> Endpoints { get; } = endpoints;
    }
}
