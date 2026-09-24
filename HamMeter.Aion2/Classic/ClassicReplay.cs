using AionDpsMeter.Services.Models;
using AionDpsMeter.Services.PacketCapture;
using HamMeter.Capture;
using HamMeter.Combat;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HamMeter.Classic;

// Runs a recording through the classic reader, so --replay can compare both readers on
// the same packets.
public static class ClassicReplay
{
    // Recordings hold framed packets without keep-alives. Their framer only synchronises
    // on a keep-alive, so one goes in front of every packet (it drops them again).
    private static readonly byte[] KeepAlive = [0x0E, 0x00, 0x36, 0, 0, 0, 0, 0, 0, 0, 0];

    public static void Run(IEnumerable<(DateTimeOffset Time, byte[] Packet)> recording, EncounterTracker tracker)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton(tracker);
        ClassicEngine.Register(services, npcap: false);

        using ServiceProvider sp = services.BuildServiceProvider();
        _ = sp.GetRequiredService<IPacketEngine>();  // installs the combat tap
        _ = sp.GetRequiredService<IPacketService>(); // connects their pipeline to the buffer
        TcpStreamBuffer buffer = sp.GetRequiredService<TcpStreamBuffer>();

        DateTime now = DateTime.MinValue;
        sp.GetRequiredService<CombatPacketParser>().Clock = () => now;

        foreach ((DateTimeOffset time, byte[] packet) in recording)
        {
            now = time.LocalDateTime;
            tracker.Tick(now);
            buffer.AddData("replay", [.. KeepAlive, .. packet], time.ToUnixTimeMilliseconds());
        }

        tracker.Tick(now.AddHours(1));
    }
}
