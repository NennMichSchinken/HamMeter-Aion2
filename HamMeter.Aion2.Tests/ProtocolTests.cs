using HamMeter.Capture;
using HamMeter.Combat;
using HamMeter.Game;
using HamMeter.Protocol;
using K4os.Compression.LZ4;
using Microsoft.Extensions.Logging.Abstractions;
using PacketDotNet;
using Xunit;
using static HamMeter.Tests.CombatPacketParserTests;

namespace HamMeter.Tests;

// HamMeter's own packet reader (docs/protocol.md), built from synthetic packets.
public class ProtocolTests
{
    private static readonly byte[] KeepAlive = Packet(Opcodes.KeepAlive, w => w.Bytes(8));

    // ----- Varint / framing (§2) -----------------------------------------------------

    [Fact]
    public void KeepAlive_IsElevenBytes_StartingWithTheSyncPattern()
    {
        Assert.Equal(11, KeepAlive.Length);
        Assert.Equal(new byte[] { 0x0E, 0x00, 0x36 }, KeepAlive[..3]);
    }

    [Fact]
    public void VarInt_ReadsMultiByteValues_AndRejectsOverlongOnes()
    {
        Assert.True(VarInt.TryRead([0xAC, 0x02], out uint v, out int n));
        Assert.Equal(300u, v);
        Assert.Equal(2, n);

        Assert.False(VarInt.TryRead([0x80, 0x80, 0x80, 0x80, 0x80, 0x01], out _, out _));
        Assert.False(VarInt.TryRead([0x80], out _, out _)); // incomplete
    }

    [Fact]
    public void Framer_WaitsForSync_ThenCutsPackets_AcrossSegments()
    {
        byte[] a = Packet(Opcodes.Hit, w => w.Bytes(20));
        byte[] b = Packet(Opcodes.Death, w => w.VarInt(7));
        byte[] stream = [.. new byte[] { 0x99, 0x42, 0x07 }, .. KeepAlive, .. a, .. b];

        var framer = new PacketFramer();
        var packets = new List<byte[]>();
        for (int i = 0; i < stream.Length; i += 5) // arrives in small pieces
        {
            framer.Append(stream.AsSpan(i, Math.Min(5, stream.Length - i)), packets.Add);
        }

        Assert.Equal([a, b], packets); // garbage and keep-alive are not passed on
    }

    [Fact]
    public void Framer_ResynchronisesAfterAnImpossibleLength()
    {
        byte[] good = Packet(Opcodes.Death, w => w.VarInt(7));
        byte[] stream = [.. KeepAlive, 0xFF, 0xFF, 0xFF, 0xFF, 0x7F, .. KeepAlive, .. good];

        var framer = new PacketFramer();
        var packets = new List<byte[]>();
        framer.Append(stream, packets.Add);

        Assert.Equal([good], packets);
    }

    [Fact]
    public void Framer_WithoutSync_KeepsOnlyAPossiblePatternStart()
    {
        var framer = new PacketFramer();
        var packets = new List<byte[]>();
        framer.Append(new byte[100_000], packets.Add);
        framer.Append([.. KeepAlive, .. Packet(Opcodes.Death, w => w.VarInt(1))], packets.Add);

        Assert.Single(packets);
        Assert.True(framer.Synced);
    }

    // ----- Bundles (§3) ----------------------------------------------------------------

    [Fact]
    public void Bundle_IsExpanded_WithPaddingAndNesting()
    {
        byte[] p1 = Packet(Opcodes.Hit, w => w.Bytes(30));
        byte[] p2 = Packet(Opcodes.Death, w => w.VarInt(9));
        byte[] p3 = Packet(Opcodes.Tick, w => w.Bytes(12));
        byte[] inner = Bundle([.. p2, 0x00, 0x00, .. p3]);
        byte[] outer = Bundle([.. p1, 0x00, .. inner]);

        var output = new List<byte[]>();
        BundleDecoder.Expand(outer, output);

        Assert.Equal([p1, p2, p3], output);
    }

    [Fact]
    public void Bundle_WithFlagByte_IsExpanded()
    {
        byte[] p = Packet(Opcodes.Death, w => w.VarInt(9));
        var output = new List<byte[]>();
        BundleDecoder.Expand(Bundle(p, flag: 0xF3), output);

        Assert.Equal([p], output);
    }

    [Fact]
    public void Bundle_WithAbsurdSize_IsNotDecompressed()
    {
        byte[] bundle = Bundle(Packet(Opcodes.Death, w => w.VarInt(9)));
        int sizeAt = Array.IndexOf(bundle, (byte)0xFF) + 2;
        BitConverter.GetBytes(2_000_000_000).CopyTo(bundle, sizeAt);

        var output = new List<byte[]>();
        BundleDecoder.Expand(bundle, output);

        Assert.Equal([bundle], output); // passed on as-is, no giant allocation
    }

    // ----- End to end: stream -> entities -> combat -------------------------------------

    [Fact]
    public void Stream_NamesTheUser_AndCreditsSummonDamageToTheOwner()
    {
        var tracker = new EncounterTracker();
        using OwnEngine engine = OwnEngine.Offline(tracker, NullLoggerFactory.Instance);

        const int User = 1001;
        const int Spirit = 7000;
        const int Mob = 5000;

        byte[] ownCharacter = Packet(Opcodes.OwnCharacter, w =>
        {
            w.VarInt(User);
            w.Bytes(4);
            w.U8(0x01);        // name follows
            w.VarInt(5);
            w.Raw("Hamzi"u8.ToArray());
            w.Raw([0xD1, 0x07]); // server 2001
            w.U8(16);          // Elementalist
        });
        byte[] userHit = Hit(User, Mob, 16_020_000, 1_000);
        byte[] spawnSpirit = Packet(Opcodes.Spawn, w =>
        {
            w.VarInt(Spirit);
            w.Bytes(10);
            w.Raw([0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF]);
            w.Raw([0x07, 0x02, 0x06, User & 0xFF, User >> 8]);
            w.Bytes(6);
        });
        byte[] spiritHit = Hit(Spirit, Mob, 10_010_000, 500);

        engine.Stream.AddData("s", [.. KeepAlive, .. ownCharacter, .. Bundle([.. userHit, .. spawnSpirit, .. spiritHit])], 0);

        Combatant user = Assert.Single(tracker.Current!.Combatants);
        Assert.Equal("Hamzi", user.Name);
        Assert.True(user.IsUser);
        Assert.Equal("ELE", user.Job);
        Assert.Equal(1_500, user.DamageTotal);
    }

    [Fact]
    public void Spawn_WithUnknownOwner_IsNotASummon()
    {
        var tracker = new EncounterTracker();
        using OwnEngine engine = OwnEngine.Offline(tracker, NullLoggerFactory.Instance);

        engine.Stream.Dispatch(Packet(Opcodes.Spawn, w =>
        {
            w.VarInt(7000);
            w.Raw([0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x07, 0x02, 0x06, 0x39, 0x05]);
            w.Bytes(4);
        }));

        Assert.False(engine.Entities.IsSummon(7000));
    }

    // ----- Names (§6.5) ----------------------------------------------------------------

    [Theory]
    [InlineData(new byte[] { 0x48, 0x61, 0x6D }, "Ham")]
    [InlineData(new byte[] { 0x41, 0x62, 0x02, 0x63 }, "AbAbc")] // 02 repeats the first two bytes
    [InlineData(new byte[] { 0xED, 0x95, 0x9C }, "한")]            // UTF-8
    [InlineData(new byte[] { 0x31, 0x32, 0x33 }, null)]            // all digits: not a name
    [InlineData(new byte[] { 0x00, 0x41 }, null)]
    public void Names_AreDecoded(byte[] raw, string? expected) =>
        Assert.Equal(expected, EntityPacketParser.DecodeName(raw));

    // ----- Skill codes (§7) ------------------------------------------------------------

    [Fact]
    public void SkillRules_ReadClassFromTheFirstTwoDigits()
    {
        var rules = new SkillRules();
        Assert.Equal(11, rules.ClassOf(11_020_000));
        Assert.Equal(19, rules.ClassOf(19_000_450));
        Assert.Equal(15, rules.ClassOf(150_123)); // pet skill
        Assert.Null(rules.ClassOf(99_000_000));
        Assert.True(rules.IsTheostone(3_000_500));
        Assert.True(rules.IsHealing(17_120_040)); // any variant of a heal family
        Assert.False(rules.IsHealing(17_020_000));
        Assert.True(rules.IsHealing(17_720_001)); // Cleric self-heal while attacking
        Assert.False(rules.IsHealing(17_730_001)); // its damage part
    }

    // ----- Capture link layers ---------------------------------------------------------

    [Fact]
    public void Npcap_FindsTheIpHeader_ForEachLinkType()
    {
        byte[] ethernet = new byte[40];
        ethernet[12] = 0x08;
        Assert.Equal(14, NpcapCaptureDevice.IpPayload(LinkLayers.Ethernet, ethernet));

        byte[] vlan = new byte[40];
        vlan[12] = 0x81;
        vlan[16] = 0x08;
        Assert.Equal(18, NpcapCaptureDevice.IpPayload(LinkLayers.Ethernet, vlan));

        byte[] arp = new byte[40];
        arp[12] = 0x08;
        arp[13] = 0x06;
        Assert.Null(NpcapCaptureDevice.IpPayload(LinkLayers.Ethernet, arp));

        Assert.Equal(4, NpcapCaptureDevice.IpPayload(LinkLayers.Null, [2, 0, 0, 0, 0x45]));
        Assert.Null(NpcapCaptureDevice.IpPayload(LinkLayers.Null, [24, 0, 0, 0, 0x60])); // IPv6
        Assert.Equal(0, NpcapCaptureDevice.IpPayload(LinkLayers.Raw, [0x45]));
    }

    // ----- helpers ---------------------------------------------------------------------

    private static byte[] Bundle(byte[] content, byte? flag = null)
    {
        byte[] compressed = new byte[LZ4Codec.MaximumOutputSize(content.Length)];
        int length = LZ4Codec.Encode(content, compressed);

        var w = new Writer();
        if (flag is { } f)
        {
            w.U8(f);
        }

        w.Raw([0xFF, 0xFF]);
        w.U32(content.Length);
        w.Raw(compressed[..length]);
        byte[] body = w.ToArray();

        // Length field + body, without a separate opcode: FF FF is the opcode.
        var p = new Writer();
        p.VarInt(body.Length + 4);
        p.Raw(body);
        return p.ToArray();
    }

    private static byte[] Hit(int actor, int target, int skill, int amount) => Packet(Opcodes.Hit, w =>
    {
        w.VarInt(target);
        w.VarInt(5);
        w.VarInt(0);
        w.VarInt(actor);
        w.U32(skill);
        w.U8(0);
        w.VarInt(1);
        w.Bytes(3);
        w.Bytes(8);
        w.VarInt(10_000);
        w.VarInt(amount);
    });
}
