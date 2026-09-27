using Microsoft.Extensions.Logging;

namespace HamMeter.Protocol;

// Hands each game packet to the handlers registered for its opcode. Several handlers
// may listen to one opcode; a failing handler never stops the others.
public sealed class PacketRouter(ILogger<PacketRouter> log)
{
    private readonly Dictionary<ushort, List<Action<byte[]>>> m_handlers = new();

    public void On(ushort opcode, Action<byte[]> handler)
    {
        if (!m_handlers.TryGetValue(opcode, out List<Action<byte[]>>? list))
        {
            list = new List<Action<byte[]>>();
            m_handlers[opcode] = list;
        }

        list.Add(handler);
    }

    public void Dispatch(byte[] packet)
    {
        if (!Opcodes.TryRead(packet, out ushort opcode) || !m_handlers.TryGetValue(opcode, out List<Action<byte[]>>? list))
        {
            return;
        }

        foreach (Action<byte[]> handler in list)
        {
            try
            {
                handler(packet);
            }
            catch (Exception ex)
            {
                // Malformed or unexpected packets happen; they must never reach the capture thread.
                log.LogTrace(ex, "Handler failed for opcode 0x{Opcode:X4}", opcode);
            }
        }
    }
}
