using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace HamMeter.Update;

public sealed record UpdateInfo(Version Version, DateOnly? Date, bool Important, IReadOnlyList<Note> Notes, Uri SetupUrl, Uri SignatureUrl);

public enum UpdateCheckResult
{
    UpToDate,
    Available,
    Failed,
}

// Update check and self-update against GitHub Releases.
//
// Privacy: one HTTPS GET to the GitHub API. Nothing is sent besides the request itself
// (GitHub sees the IP address, like any page visit). No IDs, no statistics.
//
// Security, in order:
//   - HTTPS only, and only to GitHub hosts (checked again after redirects).
//   - Response and download sizes are capped.
//   - The setup is written to a fresh per-user temp folder, re-opened read-only with
//     sharing that denies writes and deletes, and its ECDSA-P256 signature is verified
//     against the public key built into HamMeter. Only the release key (kept offline by
//     the author) can produce that signature, so a hijacked GitHub account alone cannot
//     push code to users. The handle stays open until the setup has started.
public sealed class UpdateService
{
    public const string Repository = "NennMichSchinken/HamMeter-Aion2";

    private const long MaxApiBytes = 512 * 1024;
    private const long MaxSetupBytes = 150L * 1024 * 1024;

    private static readonly string[] AllowedHosts =
    [
        "api.github.com",
        "github.com",
        "objects.githubusercontent.com",
        "release-assets.githubusercontent.com",
    ];

    private readonly ILogger<UpdateService> m_log;
    private readonly HttpClient m_http;

    public UpdateService(ILogger<UpdateService> log)
    {
        m_log = log;
        m_http = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 3,
            ConnectTimeout = TimeSpan.FromSeconds(5),
        })
        {
            Timeout = TimeSpan.FromMinutes(3),
            MaxResponseContentBufferSize = MaxApiBytes,
        };
        m_http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("HamMeter", Changelog.Current.ToString()));
        m_http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    // Latest release newer than this build, or null when up to date. Throws on failure.
    public async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(10));

        Uri api = new($"https://api.github.com/repos/{Repository}/releases/latest");
        using HttpResponseMessage response = await m_http.GetAsync(api, cts.Token);
        EnsureAllowed(response);
        response.EnsureSuccessStatusCode();

        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cts.Token));
        return Parse(doc.RootElement, Changelog.Current);
    }

    // Pure parsing of the GitHub "latest release" JSON (unit tested).
    internal static UpdateInfo? Parse(JsonElement release, Version current)
    {
        string tag = release.GetProperty("tag_name").GetString() ?? string.Empty;
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out Version? version) || version <= current)
        {
            return null;
        }

        string body = release.TryGetProperty("body", out JsonElement b) ? b.GetString() ?? string.Empty : string.Empty;
        DateOnly? date = release.TryGetProperty("published_at", out JsonElement p)
            && DateTime.TryParse(p.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out DateTime published)
            ? DateOnly.FromDateTime(published)
            : null;

        string setupName = $"HamMeter-Setup-{version.ToString(3)}.exe";
        Uri? setup = null;
        Uri? signature = null;
        foreach (JsonElement asset in release.GetProperty("assets").EnumerateArray())
        {
            string? name = asset.GetProperty("name").GetString();
            string? url = asset.GetProperty("browser_download_url").GetString();
            if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || !IsAllowed(uri))
            {
                continue;
            }

            if (string.Equals(name, setupName, StringComparison.OrdinalIgnoreCase))
            {
                setup = uri;
            }
            else if (string.Equals(name, setupName + ".sig", StringComparison.OrdinalIgnoreCase))
            {
                signature = uri;
            }
        }

        // A release without a signed setup is not offered.
        if (setup is null || signature is null)
        {
            return null;
        }

        return new UpdateInfo(
            version,
            date,
            body.Contains("[important]", StringComparison.OrdinalIgnoreCase),
            Changelog.ParseNotes(body),
            setup,
            signature);
    }

    // Downloads, verifies and starts the setup's quick update. Returns false when the
    // download is not correctly signed (nothing is run then). Throws on network errors.
    public async Task<bool> DownloadAndRunAsync(UpdateInfo update, IProgress<string>? progress, CancellationToken ct = default)
    {
        DirectoryInfo dir = Directory.CreateTempSubdirectory("HamMeterUpdate-");
        string setup = Path.Combine(dir.FullName, $"HamMeter-Setup-{update.Version.ToString(3)}.exe");

        progress?.Report("download");
        byte[] signature = Convert.FromBase64String((await this.GetSmallAsync(update.SignatureUrl, ct)).Trim());
        await this.DownloadAsync(update.SetupUrl, setup, ct);

        progress?.Report("verify");
        FileStream locked = new(setup, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (!VerifySignature(locked, signature))
        {
            locked.Dispose();
            m_log.LogWarning("Update {Version}: signature check failed, not running it", update.Version);
            TryDelete(dir);
            return false;
        }

        // Start the setup while the verified file is still locked, then let go: the setup
        // itself is running from that exact file now.
        Process.Start(new ProcessStartInfo(setup, "--auto-update") { UseShellExecute = true, WorkingDirectory = dir.FullName })?.Dispose();
        locked.Dispose();
        m_log.LogInformation("Update {Version} verified and started", update.Version);
        return true;
    }

    internal static bool VerifySignature(Stream file, byte[] signature)
    {
        using Stream? keyStream = typeof(UpdateService).Assembly.GetManifestResourceStream("HamMeter.ReleasePublicKey");
        if (keyStream is null)
        {
            return false;
        }

        using ECDsa key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(new StreamReader(keyStream).ReadToEnd().Trim()), out _);
        file.Position = 0;
        bool ok = key.VerifyData(file, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        file.Position = 0;
        return ok;
    }

    private async Task<string> GetSmallAsync(Uri url, CancellationToken ct)
    {
        using HttpResponseMessage response = await m_http.GetAsync(url, ct);
        EnsureAllowed(response);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }

    private async Task DownloadAsync(Uri url, string path, CancellationToken ct)
    {
        using HttpResponseMessage response = await m_http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        EnsureAllowed(response);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaxSetupBytes)
        {
            throw new InvalidDataException("Update file is too large.");
        }

        await using Stream src = await response.Content.ReadAsStreamAsync(ct);
        await using FileStream dst = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        byte[] buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await src.ReadAsync(buffer, ct)) > 0)
        {
            total += read;
            if (total > MaxSetupBytes)
            {
                throw new InvalidDataException("Update file is too large.");
            }

            await dst.WriteAsync(buffer.AsMemory(0, read), ct);
        }
    }

    private static void EnsureAllowed(HttpResponseMessage response)
    {
        Uri? final = response.RequestMessage?.RequestUri;
        if (final is null || !IsAllowed(final))
        {
            throw new HttpRequestException($"Refused update source {final?.Host}.");
        }
    }

    internal static bool IsAllowed(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps && AllowedHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase);

    private static void TryDelete(DirectoryInfo dir)
    {
        try
        {
            dir.Delete(true);
        }
        catch (IOException)
        {
        }
    }
}
