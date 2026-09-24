namespace HamMeter.Protocol;

// Opcodes as numbers: the two wire bytes read little-endian, so "04 38" is 0x3804.
// Comments give the wire order used in docs/protocol.md.
public static class Opcodes
{
    public const ushort KeepAlive = 0x3600;      // 00 36
    public const ushort ServerTime = 0x3603;     // 03 36
    public const ushort EntityLink = 0x3620;     // 20 36
    public const ushort OwnCharacter = 0x3633;   // 33 36
    public const ushort Spawn = 0x3641;          // 41 36
    public const ushort OtherCharacter = 0x3645; // 45 36
    public const ushort UserState = 0x364A;      // 4A 36
    public const ushort Hit = 0x3804;            // 04 38
    public const ushort Tick = 0x3805;           // 05 38
    public const ushort RemainingHp = 0x8D00;    // 00 8D
    public const ushort Death = 0x8D04;          // 04 8D
    public const ushort PartyList = 0x9702;      // 02 97
    public const ushort Bundle = 0xFFFF;         // FF FF

    public static bool TryRead(ReadOnlySpan<byte> packet, out ushort opcode)
    {
        opcode = 0;
        if (!VarInt.TryRead(packet, out _, out int length) || packet.Length < length + 2)
        {
            return false;
        }

        opcode = (ushort)(packet[length] | (packet[length + 1] << 8));
        return true;
    }
}
