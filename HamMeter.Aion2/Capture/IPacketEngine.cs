namespace HamMeter.Capture;

// The packet side of HamMeter: capture -> parse -> EncounterTracker. Two engines exist
// while HamMeter moves to its own reader: OwnEngine and the Classic one built on
// Kuroukihime's library (Config.OwnPacketReader picks one).
public interface IPacketEngine : IDisposable
{
    string Name { get; }

    // Every framed game packet (arrival time in unix ms), for the packet recorder.
    event Action<long, byte[]>? PacketFramed;

    // Non-null once the game stream was found.
    string? DeviceName { get; }

    bool LooksBlocked { get; }

    // Per-skill totals of the current fight, or null when the engine does not collect them.
    SkillLog? SkillLog { get; }

    void Start();

    void Stop();
}
