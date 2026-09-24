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
//     non-promiscuous mode, with a kernel filter that matches exactly those
//     connections (server -> client). Other traffic never reaches HamMeter.
//   - TcpReassembler checks every packet against the same connection list again.
// Unlike raw sockets, Npcap also sees loopback, so VPN/booster tunnels work.
public sealed class NpcapCaptureDevice : IGameCapture
{
    private const int RefreshMs = 2000;
    private const int ReadTimeoutMs = 500;

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
                string filter = Filter(entry.Conns);
                if (m_open.TryGetValue(name, out OpenDevice? open))
                {
                    if (open.Filter != filter)
                    {
                        this.TrySetFilter(open.Device, filter);
                        open.Filter = filter;
                    }
                }
                else if (this.Open(entry.Device, filter))
                {
                    m_open[name] = new OpenDevice(entry.Device, filter);
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

    // Kernel filter: server -> client packets of exactly these connections.
    private static string Filter(List<TcpConnection> conns) =>
        "tcp and (" + string.Join(" or ", conns.Select(c =>
            $"(src host {new IPAddress(c.RemoteAddress)} and src port {c.RemotePort} " +
            $"and dst host {c.Local} and dst port {c.LocalPort})")) + ")";

    private bool Open(LibPcapLiveDevice device, string filter)
    {
        try
        {
            device.OnPacketArrival += this.OnPacketArrival;
            device.Open(new DeviceConfiguration { Mode = DeviceModes.None, ReadTimeout = ReadTimeoutMs });
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

    private sealed class OpenDevice(LibPcapLiveDevice device, string filter)
    {
        public LibPcapLiveDevice Device { get; } = device;

        public string Filter { get; set; } = filter;
    }
}
