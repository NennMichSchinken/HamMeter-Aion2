using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;

namespace HamMeter.Setup;

public enum PayloadResult
{
    Success,
    Cancelled,
    Damaged,
    Failed,
}

// The Inno Setup engine is embedded in this exe and runs invisibly (/VERYSILENT) with
// the options chosen in our wizard. It is the only part that runs elevated.
//
// Integrity: the payload is written to a fresh per-user temp folder, re-opened
// read-only with sharing that denies writes and deletes, hashed from that locked handle
// and compared with the SHA-256 embedded at build time. The handle stays open until
// Inno has exited, so the verified file is exactly the file that runs.
internal static class Payload
{
    private const string PayloadResource = "HamMeter.Payload";
    private const string HashResource = "HamMeter.Payload.sha256";

    public static bool Available =>
        typeof(Payload).Assembly.GetManifestResourceInfo(PayloadResource) is not null;

    public static (PayloadResult Result, int ExitCode) Run(string arguments)
    {
        DirectoryInfo dir = Directory.CreateTempSubdirectory("HamMeterSetup-");
        string exe = Path.Combine(dir.FullName, "HamMeter-Core.exe");
        try
        {
            using (Stream src = typeof(Payload).Assembly.GetManifestResourceStream(PayloadResource)!)
            using (FileStream dst = new(exe, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                src.CopyTo(dst);
            }

            using FileStream locked = new(exe, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (!HashMatches(locked))
            {
                return (PayloadResult.Damaged, -1);
            }

            Process? p;
            try
            {
                p = Process.Start(new ProcessStartInfo(exe, arguments + $" /LOG=\"{Path.Combine(dir.FullName, "install.log")}\"")
                {
                    UseShellExecute = true,
                    Verb = "runas",
                    WorkingDirectory = dir.FullName,
                });
            }
            catch (Win32Exception)
            {
                return (PayloadResult.Cancelled, -1); // UAC declined
            }

            if (p is null)
            {
                return (PayloadResult.Failed, -1);
            }

            using (p)
            {
                p.WaitForExit();
                return p.ExitCode == 0 ? (PayloadResult.Success, 0) : (PayloadResult.Failed, p.ExitCode);
            }
        }
        finally
        {
            try
            {
                dir.Delete(true);
            }
            catch (IOException)
            {
                // Still in use for a moment; Windows cleans the temp folder later.
            }
        }
    }

    private static bool HashMatches(FileStream file)
    {
        using Stream? h = typeof(Payload).Assembly.GetManifestResourceStream(HashResource);
        if (h is null)
        {
            return false;
        }

        string expected = new StreamReader(h).ReadToEnd().Trim();
        string actual = Convert.ToHexString(SHA256.HashData(file));
        file.Position = 0;
        return CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.ASCII.GetBytes(expected.ToUpperInvariant()),
            System.Text.Encoding.ASCII.GetBytes(actual));
    }
}
