using System.ComponentModel;
using System.Diagnostics;
using HamMeter.Capture;
using HamMeter.Classic;
using HamMeter.Combat;
using HamMeter.UI;
using HamMeter.Wizard;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace HamMeter;

public static class Program
{
    public static async Task Main(string[] args)
    {
        // "Apps & Features" -> Uninstall. Runs without elevation and without the meter.
        if (args.Contains("--uninstall", StringComparer.OrdinalIgnoreCase))
        {
            using var wizard = new WizardWindow(new UninstallFlow());
            await wizard.Run();
            return;
        }

        // Development: run a packet recording through HamMeter's own reader and write
        // a report next to it. Only the Windows account that recorded it can read it.
        int replay = Array.FindIndex(args, a => a.Equals("--replay", StringComparison.OrdinalIgnoreCase));
        if (replay >= 0 && replay + 1 < args.Length)
        {
            Replay.Run(args[replay + 1]);
            return;
        }

        using var single = new Mutex(true, SystemInfo.MutexName, out bool first);
        if (!first)
        {
            return;
        }

        SystemInfo.DeleteSetupLeftovers();

        // Npcap captures without administrator rights, so it is preferred. Without it
        // (or when it is restricted to administrators) HamMeter falls back to raw
        // sockets, which need elevation: restart elevated (UAC prompt) instead of
        // always running as administrator.
        bool npcap = SystemInfo.Npcap() == NpcapState.Ok;
        bool elevated = RawSocketCaptureDevice.IsElevated();
        string? captureError = null;
        if (!npcap && !elevated)
        {
            single.ReleaseMutex();
            if (TryRestartElevated())
            {
                return;
            }

            captureError = "HamMeter needs administrator rights to read the game traffic without Npcap.\n\n"
                + "Restart HamMeter and allow the Windows prompt, or install Npcap from npcap.com "
                + "(\"WinPcap API-compatible Mode\") to run without administrator rights.";
        }

        // Kuroukihime's services write appsettings.user.json relative to the working
        // directory; keep it with our config.
        Directory.CreateDirectory(Config.DataDirectory);
        Environment.CurrentDirectory = Config.DataDirectory;

        Config config = Config.Load();
        var tracker = new EncounterTracker();

        // Development: fake bars and open settings, and never write the config back.
        bool preview = args.Contains("--preview", StringComparer.OrdinalIgnoreCase);
        if (preview)
        {
            config.TestMode = true;
        }

        var services = new ServiceCollection();
        services.AddLogging(b => b
            .SetMinimumLevel(LogLevel.Information)
            .AddProvider(new FileLoggerProvider(Path.Combine(Config.DataDirectory, "HamMeter.log"), LogLevel.Information)));

        // --own-reader forces the beta reader for this run without touching the setting.
        bool own = config.OwnPacketReader || args.Contains("--own-reader", StringComparer.OrdinalIgnoreCase);
        if (own)
        {
            services.AddSingleton<IPacketEngine>(sp => OwnEngine.Live(npcap, tracker, sp.GetRequiredService<ILoggerFactory>()));
        }
        else
        {
            ClassicEngine.Register(services, npcap);
        }

        services.AddSingleton(tracker);
        services.AddSingleton<PacketRecorder>();
        services.AddSingleton(config);
        services.AddSingleton<Update.UpdateService>();
        services.AddSingleton<Update.UpdateController>();

        await using ServiceProvider sp = services.BuildServiceProvider();
        ILogger log = sp.GetRequiredService<ILoggerFactory>().CreateLogger("HamMeter");

        IPacketEngine engine = sp.GetRequiredService<IPacketEngine>();
        config.ActiveReader = own ? "HamMeter (beta)" : "Classic";
        log.LogInformation("HamMeter for Aion 2 starting (reader: {Engine}, capture: {Mode}, elevated: {Elevated})",
            engine.Name, npcap ? "Npcap" : "raw socket", elevated);

        PacketRecorder recorder = sp.GetRequiredService<PacketRecorder>();
        recorder.DeleteExpired();
        engine.PacketFramed += recorder.Record;

        tracker.Finished += encounter =>
        {
            if (engine.SkillLog?.Drain() is { } skills)
            {
                log.LogInformation("Fight ended after {Duration}.\n{Skills}", encounter.Duration, skills);
            }
        };

        if (captureError is null)
        {
            try
            {
                engine.Start();
            }
            catch (Exception ex)
            {
                captureError = $"Packet capture could not start:\n{ex.Message}";
                log.LogError(ex, "Packet capture failed to start");
            }
        }

        string? Status()
        {
            if (captureError is not null)
            {
                return captureError;
            }

            if (engine.LooksBlocked)
            {
                return "Aion 2 is connected, but no game packets arrive.\n\n"
                    + "Windows Firewall is probably blocking HamMeter. Allow HamMeter in the firewall, or install Npcap.";
            }

            return engine.DeviceName is null ? "Waiting for Aion 2...\n\nStart the game and log in to a character." : null;
        }

        using (var overlay = new HamMeterOverlay(config, tracker, Status, sp.GetRequiredService<Update.UpdateController>()))
        {
            overlay.SettingsChanged += () => recorder.SetRecording(config.RecordPackets);
            if (preview)
            {
                var updates = sp.GetRequiredService<Update.UpdateController>();
                updates.PreviewMode = true;
                if (args.Contains("--preview-update", StringComparer.OrdinalIgnoreCase))
                {
                    updates.ShowPreviewUpdate(args.Contains("--important", StringComparer.OrdinalIgnoreCase));
                }

                if (args.Contains("--preview-whatsnew", StringComparer.OrdinalIgnoreCase))
                {
                    updates.WhatsNewOpen = true;
                }

                if (args.Contains("--preview-corner", StringComparer.OrdinalIgnoreCase))
                {
                    overlay.ShowCornerPreview();
                }
                else
                {
                    overlay.ShowSettingsPreview(args.Contains("--preview-list", StringComparer.OrdinalIgnoreCase));
                }
            }

            await overlay.Run();
        }

        engine.PacketFramed -= recorder.Record;
        recorder.Dispose();
        engine.Stop();
        if (!preview)
        {
            config.Save();
        }

        log.LogInformation("HamMeter stopped");
    }

    private static bool TryRestartElevated()
    {
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = true,
                Verb = "runas",
            });
            return true;
        }
        catch (Win32Exception)
        {
            // The user declined the UAC prompt.
            return false;
        }
    }
}
