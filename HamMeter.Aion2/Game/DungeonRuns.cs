using HamMeter.Protocol;

namespace HamMeter.Game;

// Which dungeon run the user is in (docs/protocol.md §6.13, §6.14), so the meter can start
// fresh with every new run: 01 40 names the zone the user enters, 00 61 the state of the
// dungeon run (3 = cleared). Porting out of a run and back in is the same run; a new run
// starts in another dungeon, after the last one was cleared, or once the run's hour is up.
public sealed class DungeonRuns
{
    private const byte Cleared = 3;

    // A run lasts an hour at most (the 00 61 deadline, 2026-10-08).
    private static readonly TimeSpan RunLimit = TimeSpan.FromHours(1);

    private static readonly Lazy<HashSet<uint>> Dungeons = new(() => NpcData.DungeonIds.Select(id => (uint)id).ToHashSet());

    private uint? m_dungeon;      // the dungeon of the current or last run
    private DateTime m_runStart;
    private bool m_cleared;

    // (dungeon id): the user entered a dungeon for a new run.
    public event Action<int>? NewRun;

    // Time of the packet being read; a replay sets it to the recorded time.
    public Func<DateTime> Clock { get; set; } = () => DateTime.Now;

    // The zone the user is in, once a zone change was seen.
    public uint? Zone { get; private set; }

    public void Register(PacketRouter router)
    {
        router.On(Opcodes.ZoneEntered, this.OnZoneEntered);
        router.On(Opcodes.DungeonState, this.OnDungeonState);
    }

    public static bool IsDungeon(uint zone) => Dungeons.Value.Contains(zone);

    // Body: zone u32, ... It also comes again inside the zone (the same zone, nothing new).
    private void OnZoneEntered(byte[] packet)
    {
        uint zone = PacketReader.Body(packet).ReadU32();
        if (zone == this.Zone)
        {
            return;
        }

        this.Zone = zone;
        if (!IsDungeon(zone))
        {
            return; // leaving keeps everything, so the fights can still be looked at
        }

        DateTime now = this.Clock();
        if (zone == m_dungeon && !m_cleared && now - m_runStart < RunLimit)
        {
            return; // back into the same run, e.g. after repairing outside
        }

        m_dungeon = zone;
        m_runStart = now;
        m_cleared = false;
        this.NewRun?.Invoke((int)zone);
    }

    // Body: zone u32, state u8, then two times. Started inside a dungeon, HamMeter learns
    // the run from here (no zone change was seen).
    private void OnDungeonState(byte[] packet)
    {
        PacketReader r = PacketReader.Body(packet);
        uint zone = r.ReadU32();
        byte state = r.ReadU8();
        if (!IsDungeon(zone))
        {
            return;
        }

        if (m_dungeon != zone)
        {
            m_dungeon = zone;
            m_runStart = this.Clock();
            m_cleared = false;
        }

        m_cleared |= state == Cleared;
    }
}
