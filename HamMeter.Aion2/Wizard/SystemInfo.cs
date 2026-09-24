using System.Diagnostics;
using Microsoft.Win32;

namespace HamMeter.Wizard;

public enum NpcapState
{
    Missing,
    NoWinPcapMode,
    AdminOnly,
    Ok,
}

// Read-only checks of what is installed on this PC. Nothing here needs elevation.
internal static class SystemInfo
{
    public const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\HamMeter-Aion2";
    public const string MutexName = "HamMeter-Aion2-SingleInstance";

    // Npcap release the setup links to. Update with each HamMeter release (npcap.com).
    public const string NpcapVersion = "1.89";
    public const string NpcapFile = "npcap-" + NpcapVersion + ".exe";
    public const string NpcapDirectUrl = "https://npcap.com/dist/" + NpcapFile;
    public const string NpcapPageUrl = "https://npcap.com/#download";

    private static string System32 => Environment.GetFolderPath(Environment.SpecialFolder.System);

    public static NpcapState Npcap()
    {
        bool driver = File.Exists(Path.Combine(System32, "Npcap", "wpcap.dll"));
        if (!driver)
        {
            return NpcapState.Missing;
        }

        using RegistryKey? p = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\npcap\Parameters");
        if (p?.GetValue("AdminOnly") is int adminOnly && adminOnly != 0)
        {
            return NpcapState.AdminOnly;
        }

        // WinPcap API-compatible mode puts wpcap.dll straight into System32.
        return File.Exists(Path.Combine(System32, "wpcap.dll")) ? NpcapState.Ok : NpcapState.NoWinPcapMode;
    }

    // Npcap's own uninstaller, from its uninstall entry.
    public static string? NpcapUninstaller()
    {
        using RegistryKey? k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\NpcapInst")
            ?? Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\NpcapInst");
        string? cmd = (k?.GetValue("UninstallString") as string)?.Trim('"');
        return cmd is not null && File.Exists(cmd) ? cmd : null;
    }

    // Tasks: the options chosen at install ("startmenu,desktop,firewall"), null for
    // installs made before they were recorded. UpdateCheck: "auto" or "manual".
    public sealed record Installation(string Directory, string Version, bool NpcapByUs, string? Tasks, string? UpdateCheck);

    public static Installation? Installed()
    {
        using RegistryKey? k = Registry.LocalMachine.OpenSubKey(UninstallKey);
        if (k?.GetValue("InstallLocation") is not string dir || !System.IO.Directory.Exists(dir))
        {
            return null;
        }

        return new Installation(
            dir,
            k.GetValue("DisplayVersion") as string ?? "?",
            k.GetValue("NpcapInstalledByHamMeter") is int n && n != 0,
            k.GetValue("SetupTasks") as string,
            k.GetValue("UpdateCheck") as string);
    }

    // True while the HamMeter overlay is running (it holds the single-instance mutex).
    public static bool MeterRunning()
    {
        if (Mutex.TryOpenExisting(MutexName, out Mutex? m))
        {
            m.Dispose();
            return true;
        }

        return false;
    }

    // A single-file .NET exe unpacks its native DLLs to %TEMP%\.net\<name> and never
    // removes them. The setup's copy is deleted by the installed meter on start and by
    // the uninstaller, so nothing of HamMeter stays behind.
    public static void DeleteSetupLeftovers()
    {
        List<string> dirs = [Path.Combine(Path.GetTempPath(), ".net", "HamMeter-Setup")];

        // Downloaded updates (HamMeter's self-update puts each into its own folder).
        try
        {
            dirs.AddRange(Directory.EnumerateDirectories(Path.GetTempPath(), "HamMeterUpdate-*"));
        }
        catch (IOException)
        {
        }

        foreach (string dir in dirs)
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, true);
                }
            }
            catch (IOException)
            {
                // Still in use (the setup is running); next time.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    public static void OpenInBrowser(string url) =>
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
}
