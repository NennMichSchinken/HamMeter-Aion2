using HamMeter.Capture;
using HamMeter.Combat;
using HamMeter.Game;
using HamMeter.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static HamMeter.Tests.CombatPacketParserTests;
using Packing = HamMeter.Game.PartyPacket.BitPacking;

namespace HamMeter.Tests;

// The party (docs/protocol.md §6.3, §6.4) from synthetic packets.
public class PartyTests
{
    private const int User = 3580;
    private const int Tank = 3600;
    private const int Healer = 3601;
    private const int Mob = 30824;

    private static readonly (uint Id, string Name, byte Mask)[] Party =
    [
        (0x0A01, "Freitag", 0x0E),
        (0x0A02, "Hamzi", 0x03),
        (0x0A03, "Brotkrume", 0x1C),
    ];

    [Theory]
    [InlineData(Packing.Shared)]
    [InlineData(Packing.PerRun)]
    [InlineData(Packing.PerBit)]
    public void PartyList_IsRead_InEveryCandidateBitPacking(Packing packing)
    {
        var read = PartyPacket.Read(PartyList(packing));

        Assert.NotNull(read);
        Assert.Equal(packing, read.Value.Packing);
        Assert.Equal(Party.Select(p => p.Name), read.Value.Members.Select(m => m.Name));
        Assert.Equal(Party.Select(p => p.Id), read.Value.Members.Select(m => m.CharacterId));
    }

    [Fact]
    public void PartyList_Garbage_IsNotRead() =>
        Assert.Null(PartyPacket.Read(Packet(Opcodes.PartyList, w => w.Bytes(40))));

    [Fact]
    public void PartyMembers_CountOnTheirOwnEnemies_WithTankDamageAndHealing()
    {
        var tracker = new EncounterTracker();
        using PacketEngine engine = PacketEngine.Offline(tracker, NullLoggerFactory.Instance);

        engine.Stream.Dispatch(Packet(Opcodes.UserState, w => { w.VarInt(User); w.Bytes(6); }));
        engine.Stream.Dispatch(PartyList(Packing.PerRun));
        engine.Stream.Dispatch(OtherCharacter(Tank, "Hamzi", 12));  // named: matched by name
        engine.Stream.Dispatch(Link(Healer, 0x0A03));               // only linked: gets the name

        engine.Stream.Dispatch(Hit(Tank, Mob, 12_020_000, 800));        // the user never hits this mob
        engine.Stream.Dispatch(Hit(Mob, Tank, 1_234_567, 300));         // the mob hits the tank
        engine.Stream.Dispatch(Hit(Healer, Tank, 17_120_000, 250));     // heal on the tank

        var fight = tracker.Current!.Combatants.ToDictionary(c => c.Id);
        Assert.Equal(800, fight[Tank].DamageTotal);
        Assert.Equal(300, fight[Tank].DamageTaken);
        Assert.Equal(250, fight[Tank].HealingTaken);
        Assert.Equal(250, fight[Healer].HealedTotal);
        Assert.Equal("Brotkrume", fight[Healer].Name);
    }

    [Fact]
    public void WithoutParty_StrangersStillOnlyCountOnOurEnemies()
    {
        var tracker = new EncounterTracker();
        using PacketEngine engine = PacketEngine.Offline(tracker, NullLoggerFactory.Instance);

        engine.Stream.Dispatch(Packet(Opcodes.UserState, w => { w.VarInt(User); w.Bytes(6); }));
        engine.Stream.Dispatch(OtherCharacter(Tank, "Hamzi", 12));
        engine.Stream.Dispatch(Hit(Tank, Mob, 12_020_000, 800));

        Assert.Null(tracker.Current);
    }

    [Fact]
    public void StartedInsideADungeon_UnnamedPlayersCountOnOurEnemies_WhilePartyMembersAreMissing()
    {
        var tracker = new EncounterTracker();
        using PacketEngine engine = PacketEngine.Offline(tracker, NullLoggerFactory.Instance);
        const int TheirMob = Mob + 1;

        engine.Stream.Dispatch(Packet(Opcodes.UserState, w => { w.VarInt(User); w.Bytes(6); }));
        engine.Stream.Dispatch(PartyList(Packing.PerRun));                // no names, no links
        engine.Stream.Dispatch(Hit(Tank, Mob, 12_020_000, 800));          // nothing to join yet
        Assert.Null(tracker.Current);

        engine.Stream.Dispatch(Hit(User, Mob, 17_010_000, 1_000));
        engine.Stream.Dispatch(Hit(Tank, Mob, 12_020_000, 800));          // on our enemy: counts
        engine.Stream.Dispatch(Hit(Tank, TheirMob, 12_020_000, 5_000));   // not our enemy
        engine.Stream.Dispatch(Hit(Mob, Tank, 1_234_567, 300));           // the tank takes a hit
        engine.Stream.Dispatch(Hit(User, Tank, 17_120_000, 250));         // and our heal

        var fight = tracker.Current!.Combatants.ToDictionary(c => c.Id);
        Assert.Equal(2, fight.Count);
        Assert.Equal(800, fight[Tank].DamageTotal);
        Assert.Equal(300, fight[Tank].DamageTaken);
        Assert.Equal(250, fight[Tank].HealingTaken);
        Assert.Equal(250, fight[User].HealedTotal);
    }

    [Fact]
    public void OnceEveryPartyMemberIsKnown_UnnamedPlayersNoLongerCount()
    {
        var tracker = new EncounterTracker();
        using PacketEngine engine = PacketEngine.Offline(tracker, NullLoggerFactory.Instance);
        const int Stranger = 3700;

        engine.Stream.Dispatch(Packet(Opcodes.UserState, w => { w.VarInt(User); w.Bytes(6); }));
        engine.Stream.Dispatch(PartyList(Packing.PerRun));                // the user is Freitag
        engine.Stream.Dispatch(OtherCharacter(Tank, "Hamzi", 12));
        engine.Stream.Dispatch(Link(Healer, 0x0A03));
        engine.Stream.Dispatch(Hit(User, Mob, 17_010_000, 1_000));
        engine.Stream.Dispatch(Hit(Stranger, Mob, 11_020_000, 500));

        Assert.Single(tracker.Current!.Combatants);
    }

    // ----- helpers ---------------------------------------------------------------------

    // 02 97 in the layout of §6.4, with the bit fields in the given packing.
    private static byte[] PartyList(Packing packing) => Packet(Opcodes.PartyList, w =>
    {
        w.U32(77);              // party key
        w.U8(0);                // party name (empty)
        w.U8(4);                // party size
        w.U32(0);               // dungeon id
        w.U8(0);
        w.U8(0);
        w.U64(0x0001_0000_0000_0000UL | Party[0].Id); // leader
        w.Bit(true, packing);
        w.U8(0);
        w.U8(0);
        w.VarInt(Party.Length);
        byte slot = 1;
        foreach ((uint id, string name, byte mask) in Party)
        {
            w.U8(mask);
            w.U8(slot++);
            w.U64(0x0001_0000_0000_0000UL | id);
            byte[] raw = System.Text.Encoding.UTF8.GetBytes(name);
            w.U8((byte)raw.Length);
            w.Raw(raw);
            w.U32(0);
            w.U32(45);          // level
            if ((mask & 0x01) != 0)
            {
                w.U32(3);
            }

            w.U32(2_100);       // item level
            if ((mask & 0x02) != 0)
            {
                w.Bit(true, packing);
            }

            w.Bit(true, packing);
            if ((mask & 0x04) != 0)
            {
                w.U16(2001);
            }

            if ((mask & 0x08) != 0)
            {
                w.U16(2001);
            }

            w.U8(1);            // role
            w.U64(55_000);      // combat power
            w.VarInt(1);        // one ticket
            w.U8(1);
            w.U32(5);
            if ((mask & 0x10) != 0)
            {
                w.U64(0);
            }

            w.U8(0);
            w.U8(0);
        }
    });

    // 45 36: entity, name block, class.
    private static byte[] OtherCharacter(int entity, string name, int classId) => Packet(Opcodes.OtherCharacter, w =>
    {
        w.VarInt(entity);
        w.Bytes(4);
        w.U8(0x01);
        w.VarInt(name.Length);
        w.Raw(System.Text.Encoding.UTF8.GetBytes(name));
        w.VarInt(classId);
        w.Bytes(8);
    });

    // 20 36: 2 unknown bytes, entity, 4 unknown bytes, character id.
    private static byte[] Link(int entity, int characterId) => Packet(Opcodes.EntityLink, w =>
    {
        w.Bytes(2);
        w.VarInt(entity);
        w.Bytes(4);
        w.U32(characterId);
    });

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
