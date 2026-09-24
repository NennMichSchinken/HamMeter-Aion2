using HamMeter.Combat;
using HamMeter.Game;
using HamMeter.Protocol;
using Microsoft.Extensions.Logging;

namespace HamMeter.Capture;

// HamMeter's own packet reader (docs/protocol.md): no code of other meters involved.
public sealed class OwnEngine : IPacketEngine
{
    private readonly IGameCapture? m_capture;

    private OwnEngine(EncounterTracker tracker, ILoggerFactory logs, Func<IStreamSink, IGameCapture>? capture)
    {
        var router = new PacketRouter(logs.CreateLogger<PacketRouter>());
        this.Router = router;
        this.Stream = new GameStream(router);
        this.Entities = new EntityRegistry();
        new EntityPacketParser(this.Entities, logs.CreateLogger<EntityPacketParser>()).Register(router);
        this.Parser = new CombatPacketParser(this.Entities, new SkillRules(), tracker, logs.CreateLogger<CombatPacketParser>())
        {
            SkillLog = new SkillLog(),
        };
        this.Parser.Register(router);
        m_capture = capture?.Invoke(this.Stream);
    }

    public event Action<long, byte[]>? PacketFramed
    {
        add => this.Stream.PacketFramed += value;
        remove => this.Stream.PacketFramed -= value;
    }

    public string Name => "HamMeter";

    public GameStream Stream { get; }

    public PacketRouter Router { get; }

    public EntityRegistry Entities { get; }

    public CombatPacketParser Parser { get; }

    public string? DeviceName => m_capture?.DeviceName;

    public bool LooksBlocked => m_capture?.LooksBlocked == true;

    public SkillLog? SkillLog => this.Parser.SkillLog;

    public static OwnEngine Live(bool npcap, EncounterTracker tracker, ILoggerFactory logs) =>
        new(tracker, logs, sink => npcap
            ? new NpcapCaptureDevice(sink, logs.CreateLogger<NpcapCaptureDevice>())
            : new RawSocketCaptureDevice(sink, logs.CreateLogger<RawSocketCaptureDevice>()));

    // No capture: packets come from a recording through Stream.Dispatch.
    public static OwnEngine Offline(EncounterTracker tracker, ILoggerFactory logs) => new(tracker, logs, null);

    public void Start() => m_capture?.StartCapture();

    public void Stop() => m_capture?.StopCapture();

    public void Dispose() => m_capture?.Dispose();
}
