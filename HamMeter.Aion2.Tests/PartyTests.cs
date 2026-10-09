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
        engine.Stream.Dispatch(Member(0x0A02, Dungeon));                  // inside a dungeon
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
        engine.Stream.Dispatch(Member(0x0A02, Dungeon));
        engine.Stream.Dispatch(OtherCharacter(Tank, "Hamzi", 12));
        engine.Stream.Dispatch(Link(Healer, 0x0A03));
        engine.Stream.Dispatch(Hit(User, Mob, 17_010_000, 1_000));
        engine.Stream.Dispatch(Hit(Stranger, Mob, 11_020_000, 500));

        Assert.Single(tracker.Current!.Combatants);
    }

    // Open world, HamMeter started with the party already formed: no 02 97 comes, only the
    // members' 1C 92; the appearance (45 36) carries the member's database id (2026-10-08).
    [Fact]
    public void OpenWorld_AMemberKnownFromMemberPackets_CountsOnceItAppears()
    {
        var tracker = new EncounterTracker();
        using PacketEngine engine = PacketEngine.Offline(tracker, NullLoggerFactory.Instance);
        const int TheirMob = Mob + 1;

        engine.Stream.Dispatch(Packet(Opcodes.UserState, w => { w.VarInt(User); w.Bytes(6); }));
        engine.Stream.Dispatch(Member(0x0A03, OpenWorld));
        engine.Stream.Dispatch(OtherCharacter(Healer, "Brotkrume", 15, Server | 0x0A03));
        engine.Stream.Dispatch(OtherCharacter(Tank, "Hamzi", 12, Server | 0x0B07)); // not in the party
        engine.Stream.Dispatch(Hit(Healer, TheirMob, 15_020_000, 900));           // her own pull
        engine.Stream.Dispatch(Hit(Tank, TheirMob, 12_020_000, 800));

        var fight = tracker.Current!.Combatants;
        Assert.Equal("Brotkrume", Assert.Single(fight).Name);
        Assert.Equal(900, fight[0].DamageTotal);
    }

    // A member already in view when HamMeter started: no 45 36, but its 1C 92 carries the
    // position of its entity's latest 1A 37 / 1B 37 (dungeon, 2026-10-07).
    [Fact]
    public void AMemberInViewBeforeTheStart_IsFoundByItsPosition()
    {
        var tracker = new EncounterTracker();
        using PacketEngine engine = PacketEngine.Offline(tracker, NullLoggerFactory.Instance);
        const int Stranger = 3700;

        engine.Stream.Dispatch(Packet(Opcodes.UserState, w => { w.VarInt(User); w.Bytes(6); }));
        engine.Stream.Dispatch(Position(Stranger, -24_000f, 34_700f, -660f));
        engine.Stream.Dispatch(Movement(Healer, flags: 0x05, -24_256f, 34_761f, -663f));
        engine.Stream.Dispatch(Member(0x0A03, Dungeon, -24_256f, 34_761f, -662f));
        engine.Stream.Dispatch(Hit(Healer, Mob, 15_020_000, 900));
        engine.Stream.Dispatch(Hit(Stranger, Mob, 11_020_000, 500));

        Assert.Equal(Healer, Assert.Single(tracker.Current!.Combatants).Id);
    }

    [Fact]
    public void OpenWorld_UnnamedStrangers_DoNotCount_WhileMembersAreFarAway()
    {
        var tracker = new EncounterTracker();
        using PacketEngine engine = PacketEngine.Offline(tracker, NullLoggerFactory.Instance);
        const int Stranger = 3700;

        engine.Stream.Dispatch(Packet(Opcodes.UserState, w => { w.VarInt(User); w.Bytes(6); }));
        engine.Stream.Dispatch(Member(0x0A02, OpenWorld));                // never appears
        engine.Stream.Dispatch(Hit(User, Mob, 17_010_000, 1_000));
        engine.Stream.Dispatch(Hit(Stranger, Mob, 11_020_000, 500));

        Assert.Single(tracker.Current!.Combatants);
    }

    [Fact]
    public void AReusedEntityId_LosesThePartyLink()
    {
        var tracker = new EncounterTracker();
        using PacketEngine engine = PacketEngine.Offline(tracker, NullLoggerFactory.Instance);

        engine.Stream.Dispatch(Packet(Opcodes.UserState, w => { w.VarInt(User); w.Bytes(6); }));
        engine.Stream.Dispatch(Member(0x0A03, OpenWorld));
        engine.Stream.Dispatch(OtherCharacter(Healer, "Brotkrume", 15, Server | 0x0A03));
        engine.Stream.Dispatch(OtherCharacter(Healer, "Fremder", 15, Server | 0x0B07));
        engine.Stream.Dispatch(Hit(Healer, Mob, 15_020_000, 900));

        Assert.Null(tracker.Current);
    }

    // 04 8D: dead entity, u32, killer, the killer's server, name (recording of 2026-10-08).
    [Fact]
    public void AKillingBlow_NamesTheKiller()
    {
        var tracker = new EncounterTracker();
        using PacketEngine engine = PacketEngine.Offline(tracker, NullLoggerFactory.Instance);

        engine.Stream.Dispatch(Packet(Opcodes.UserState, w => { w.VarInt(User); w.Bytes(6); }));
        engine.Stream.Dispatch(Hit(User, Mob, 12_020_000, 1_000));
        engine.Stream.Dispatch(Packet(Opcodes.Death, w =>
        {
            w.VarInt(Mob);
            w.U32(0x0112_CFAE);
            w.VarInt(User);
            w.U16(0x090C);
            w.U8(7);
            w.Raw("Shinken"u8.ToArray());
            w.U8(9);
            w.Raw("SurvSided"u8.ToArray());
        }));

        Assert.Equal("Shinken", engine.Entities.Player(User)!.Value.Name);
    }

    // Before the user is known nobody can be told apart; the packets wait and only ours count.
    [Fact]
    public void BeforeTheUserIsKnown_HitsWait_AndOnlyOursCountAfterwards()
    {
        var tracker = new EncounterTracker();
        using PacketEngine engine = PacketEngine.Offline(tracker, NullLoggerFactory.Instance);
        DateTime t0 = new(2026, 10, 8, 21, 12, 40), now = t0;
        engine.Parser.Clock = () => now;
        const int Stranger = 3700;

        engine.Stream.Dispatch(Hit(Stranger, Mob, 11_020_000, 500));
        engine.Stream.Dispatch(Hit(User, Mob, 12_020_000, 1_000));
        now = t0.AddSeconds(7);
        Assert.Null(tracker.Current);

        engine.Stream.Dispatch(Packet(Opcodes.UserState, w => { w.VarInt(User); w.Bytes(6); }));

        var fight = tracker.Current!;
        Assert.Equal(1_000, Assert.Single(fight.Combatants).DamageTotal);
        Assert.True(fight.Combatants[0].IsUser);
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

    // 45 36: entity, name block, class; somewhere later the database id (§6.11).
    private static byte[] OtherCharacter(int entity, string name, int classId, ulong databaseId = 0) => Packet(Opcodes.OtherCharacter, w =>
    {
        w.VarInt(entity);
        w.Bytes(4);
        w.U8(0x01);
        w.VarInt(name.Length);
        w.Raw(System.Text.Encoding.UTF8.GetBytes(name));
        w.VarInt(classId);
        w.Bytes(8);
        w.U64(databaseId);
    });

    // 1C 92: database id, unknown, zone, 8 unknown bytes, position, more.
    private static byte[] Member(uint characterId, uint zone, float x = 0, float y = 0, float z = 0) => Packet(Opcodes.PartyMember, w =>
    {
        w.U64(Server | characterId);
        w.U32(6);
        w.U32((int)zone);
        w.Bytes(8);
        Floats(w, x, y, z);
        w.Bytes(12);
    });

    // 1A 37: entity, 2 unknown bytes, position.
    private static byte[] Position(int entity, float x, float y, float z) => Packet(Opcodes.Position, w =>
    {
        w.VarInt(entity);
        w.Bytes(2);
        Floats(w, x, y, z);
        w.Bytes(2);
    });

    // 1B 37: entity, flag byte (odd: one more byte), position.
    private static byte[] Movement(int entity, byte flags, float x, float y, float z) => Packet(Opcodes.Movement, w =>
    {
        w.VarInt(entity);
        w.U8(flags);
        w.Bytes(flags & 1);
        Floats(w, x, y, z);
        w.Bytes(4);
    });

    private static void Floats(Writer w, params float[] values)
    {
        foreach (float v in values)
        {
            w.U32((int)BitConverter.SingleToUInt32Bits(v));
        }
    }

    private const ulong Server = 0x090C_0000_0000_0000UL;
    private const uint OpenWorld = 1110;
    private const uint Dungeon = 600_002; // Ultimate Berk's dungeon in the monster list

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
        w.VarInt(2);        // hit type (2 = normal)
        w.Bytes(3);
        w.Bytes(8);
        w.VarInt(10_000);
        w.VarInt(amount);
    });
}
