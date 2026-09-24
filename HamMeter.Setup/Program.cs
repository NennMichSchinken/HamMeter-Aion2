using HamMeter.Wizard;

namespace HamMeter.Setup;

// HamMeter-Setup.exe: our wizard in the HamMeter design in front, the Inno Setup engine
// (embedded, invisible) behind it. This process never runs elevated.
public static class Program
{
    public static async Task Main(string[] args)
    {
        string? preview = args.FirstOrDefault(a => a.StartsWith("--preview=", StringComparison.OrdinalIgnoreCase))?["--preview=".Length..];
        using var wizard = new WizardWindow(new SetupFlow(preview));
        await wizard.Run();
    }
}
