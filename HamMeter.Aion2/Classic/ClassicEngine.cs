using AionDpsMeter.Services.Extensions;
using AionDpsMeter.Services.Models;
using AionDpsMeter.Services.PacketCapture;
using AionDpsMeter.Services.PacketProcessing.Routing;
using AionDpsMeter.Services.Services;
using AionDpsMeter.Services.Services.Entity;
using AionDpsMeter.Services.Services.Session;
using AionDpsMeter.Services.Services.Session.Persistence;
using AionDpsMeter.Services.Services.Settings;
using HamMeter.Capture;
using HamMeter.Game;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace HamMeter.Classic;

// The packet reader HamMeter shipped with: capture, parsing and game data from
// Kuroukihime's AIon2-Dps-Meter (GPL-3.0), plus HamMeter's CombatPacketParser tapped
// into their combat opcodes. Everything in this folder goes away with the submodule.
public sealed class ClassicEngine : IPacketEngine
{
    private readonly IPacketService m_packets;
    private readonly IPacketCaptureDevice m_capture;
    private readonly TcpStreamBuffer m_buffer;

    public ClassicEngine(
        IPacketService packets,
        IPacketCaptureDevice capture,
        OpcodeProcessorRegistry registry,
        CombatPacketParser parser,
        TcpStreamBuffer buffer,
        ILogger<ClassicEngine> log)
    {
        m_packets = packets;
        m_capture = capture;
        m_buffer = buffer;
        m_buffer.PacketExtracted += this.OnPacketExtracted;
        CombatTap.Install(registry, parser, log);
    }

    public event Action<long, byte[]>? PacketFramed;

    public string Name => "Classic";

    public string? DeviceName => m_capture.DeviceName;

    public bool LooksBlocked => m_capture is ClassicCaptureAdapter { Inner.LooksBlocked: true };

    public SkillLog? SkillLog => null;

    // Same registrations as Kuroukihime's App.xaml.cs, minus their WPF UI, their
    // update checker (no network access at all), their SQLite history (nothing is
    // persisted) and their plaintext packet log (see PacketRecorder).
    public static void Register(IServiceCollection services, bool npcap)
    {
        services.AddSingleton<ICombatHistoryStore, NullCombatHistoryStore>();
        services.AddSingleton<IAppSettingsService, AppSettingsService>();
        services.AddSingleton<TcpStreamBuffer>();
        if (npcap)
        {
            services.AddSingleton<IPacketCaptureDevice, CaptureDevice>();
        }
        else
        {
            services.AddSingleton<IStreamSink>(sp => new TcpStreamBufferSink(sp.GetRequiredService<TcpStreamBuffer>()));
            services.AddSingleton<RawSocketCaptureDevice>();
            services.AddSingleton<IPacketCaptureDevice>(sp => new ClassicCaptureAdapter(sp.GetRequiredService<RawSocketCaptureDevice>()));
        }

        services.AddSingleton<EntityTracker>();
        services.AddSingleton<CombatSessionManager>();
        services.AddPacketProcessingRouting();
        services.AddSingleton<IPacketService, PacketPipelineService>();

        services.AddSingleton<IEntityDirectory>(sp => new ClassicEntities(sp.GetRequiredService<EntityTracker>()));
        services.AddSingleton<ISkillRules, ClassicSkillRules>();
        services.AddSingleton<CombatPacketParser>();
        services.AddSingleton<IPacketEngine, ClassicEngine>();
    }

    public void Start() => m_packets.Start();

    public void Stop() => m_packets.Stop();

    public void Dispose() => m_buffer.PacketExtracted -= this.OnPacketExtracted;

    private void OnPacketExtracted(object? sender, TcpPacketEventArgs e) => this.PacketFramed?.Invoke(e.ReceivedAt, e.Payload);
}
