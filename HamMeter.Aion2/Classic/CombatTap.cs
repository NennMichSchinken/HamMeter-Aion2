using System.Reflection;
using AionDpsMeter.Services.PacketProcessing.Routing;
using HamMeter.Capture;
using Microsoft.Extensions.Logging;

namespace HamMeter.Classic;

// Kuroukihime's registry maps one processor per opcode and its [PacketOpcode] attribute
// is internal, so we cannot register a second processor for the combat opcodes. Instead
// we wrap the existing entries: their processor runs first (so their entity tracking,
// targets and history stay intact), then ours reads the same packet for HamMeter.
public static class CombatTap
{
    public static readonly ushort[] Opcodes =
    [
        PacketOpcodes.Damage,
        PacketOpcodes.DotDamage,
        PacketOpcodes.EntityDeath,
    ];

    public static void Install(OpcodeProcessorRegistry registry, CombatPacketParser parser, ILogger logger)
    {
        FieldInfo field = typeof(OpcodeProcessorRegistry).GetField("map", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                "OpcodeProcessorRegistry.map not found - the AionDpsMeter submodule changed, update CombatTap.");

        var map = (Dictionary<ushort, IOpcodeProcessor>)field.GetValue(registry)!;
        foreach (ushort opcode in Opcodes)
        {
            map.TryGetValue(opcode, out IOpcodeProcessor? inner);
            map[opcode] = new Wrapper(opcode, inner, parser, logger);
        }
    }

    private sealed class Wrapper(ushort opcode, IOpcodeProcessor? inner, CombatPacketParser parser, ILogger logger)
        : IOpcodeProcessor
    {
        public void Process(Packet packet)
        {
            try
            {
                inner?.Process(packet);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Upstream processor failed for opcode 0x{Opcode:X4}", opcode);
            }

            parser.Process(opcode, packet.Data);
        }
    }
}
