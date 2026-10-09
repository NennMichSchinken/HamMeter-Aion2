using HamMeter.Game;
using HamMeter.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static HamMeter.Tests.CombatPacketParserTests;

namespace HamMeter.Tests;

// A new dungeon run (docs/protocol.md §6.13, §6.14) from synthetic 01 40 / 00 61 packets
// in the layout of the recording of 2026-10-08.
public class DungeonRunsTests
{
    private const uint OpenWorld = 1110;
    private const uint Auldor = 600_011;
    private const uint Berk = 600_002;
    private const uint QuestInstance = 510_034; // not in the monster list

    private readonly DungeonRuns m_runs = new();
    private readonly PacketRouter m_router = new(NullLogger<PacketRouter>.Instance);
    private readonly List<int> m_newRuns = new();
    private DateTime m_now = new(2026, 10, 8, 22, 26, 54);

    public DungeonRunsTests()
    {
        m_runs.Register(m_router);
        m_runs.Clock = () => m_now;
        m_runs.NewRun += m_newRuns.Add;
    }

    [Fact]
    public void EnteringADungeon_StartsARun_LeavingDoesNot()
    {
        this.Zone(OpenWorld);
        this.Zone(Auldor);
        this.State(Auldor, 1);
        this.Zone(Auldor); // comes again inside the dungeon
        this.Zone(OpenWorld);

        Assert.Equal([(int)Auldor], m_newRuns);
    }

    [Fact]
    public void BackIntoTheSameRun_DoesNotStartANewOne()
    {
        this.Zone(Auldor);
        this.State(Auldor, 2);
        this.Later(minutes: 5);
        this.Zone(OpenWorld); // repairing outside
        this.Later(minutes: 3);
        this.Zone(Auldor);

        Assert.Single(m_newRuns);
    }

    [Fact]
    public void AfterTheDungeonWasCleared_TheNextEntryIsANewRun()
    {
        this.Zone(Auldor);
        this.State(Auldor, 3); // the last boss died
        this.Zone(OpenWorld);
        this.Later(minutes: 2);
        this.Zone(Auldor);

        Assert.Equal(2, m_newRuns.Count);
    }

    [Fact]
    public void AnotherDungeon_OrAnHourLater_IsANewRun()
    {
        this.Zone(Auldor);
        this.Zone(OpenWorld);
        this.Zone(Berk);
        this.Zone(OpenWorld);
        this.Later(minutes: 61);
        this.Zone(Berk);

        Assert.Equal([(int)Auldor, (int)Berk, (int)Berk], m_newRuns);
    }

    [Fact]
    public void StartedInsideARun_PortingOutAndBackIn_KeepsIt()
    {
        this.State(Berk, 2); // HamMeter started inside: no zone change seen
        this.Zone(OpenWorld);
        this.Zone(Berk);

        Assert.Empty(m_newRuns);
    }

    [Fact]
    public void InstancesThatAreNoDungeon_NeverStartARun()
    {
        this.Zone(QuestInstance);
        this.State(QuestInstance, 1);
        this.Zone(OpenWorld);

        Assert.Empty(m_newRuns);
    }

    private void Zone(uint zone) => m_router.Dispatch(Packet(Opcodes.ZoneEntered, w =>
    {
        w.U32((int)zone);
        w.Bytes(11);
    }));

    private void State(uint zone, byte state) => m_router.Dispatch(Packet(Opcodes.DungeonState, w =>
    {
        w.U32((int)zone);
        w.U8(state);
        w.Bytes(16);
    }));

    private void Later(int minutes) => m_now = m_now.AddMinutes(minutes);
}
