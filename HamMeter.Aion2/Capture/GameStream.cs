using HamMeter.Protocol;

namespace HamMeter.Capture;

// HamMeter's own path from TCP payload to parsers:
// stream bytes -> PacketFramer -> (recorder) -> BundleDecoder -> PacketRouter.
// Capture threads may call in concurrently; parsing runs under one lock.
public sealed class GameStream(PacketRouter router) : IStreamSink
{
    private readonly Lock m_sync = new();
    private readonly Dictionary<string, PacketFramer> m_framers = new();

    // Every framed packet with its arrival time (unix ms), before bundles are expanded.
    // This is what the packet recorder stores.
    public event Action<long, byte[]>? PacketFramed;

    public void AddData(string streamKey, byte[] payload, long receivedAtUnixMs)
    {
        lock (m_sync)
        {
            if (!m_framers.TryGetValue(streamKey, out PacketFramer? framer))
            {
                framer = new PacketFramer();
                m_framers[streamKey] = framer;
            }

            framer.Append(payload, packet =>
            {
                this.PacketFramed?.Invoke(receivedAtUnixMs, packet);
                this.DispatchLocked(packet);
            });
        }
    }

    public void ClearStream(string streamKey)
    {
        lock (m_sync)
        {
            m_framers.Remove(streamKey);
        }
    }

    // One framed packet, e.g. from a recording.
    public void Dispatch(byte[] framedPacket)
    {
        lock (m_sync)
        {
            this.DispatchLocked(framedPacket);
        }
    }

    private void DispatchLocked(byte[] packet)
    {
        var packets = new List<byte[]>();
        BundleDecoder.Expand(packet, packets);
        foreach (byte[] p in packets)
        {
            router.Dispatch(p);
        }
    }
}
