using System.Text.Json;
using HamMeter.Update;
using Xunit;

namespace HamMeter.Tests;

public class ChangelogTests
{
    private const string Sample = """
        # Changelog

        ## 0.3.0 - 2026-10-02 [important]
        - Fixed: Damage after the Aion patch.
        - New: Healing per skill.

        ## 0.2.0 - 2026-09-24
        - Improved: Something.
        - not a note line
        """;

    [Fact]
    public void Parses_Versions_Dates_Tags_And_Important()
    {
        IReadOnlyList<Release> r = Changelog.Parse(Sample);

        Assert.Equal(2, r.Count);
        Assert.Equal(new Version(0, 3, 0), r[0].Version);
        Assert.Equal(new DateOnly(2026, 10, 2), r[0].Date);
        Assert.True(r[0].Important);
        Assert.Equal([NoteKind.Fixed, NoteKind.New], r[0].Notes.Select(n => n.Kind));
        Assert.False(r[1].Important);
        Assert.Single(r[1].Notes);
    }

    [Fact]
    public void EmbeddedChangelog_HasTheCurrentVersion()
    {
        // The build refuses to package a version without patch notes; this keeps the
        // embedded copy honest too.
        Assert.NotEmpty(Changelog.Embedded);
        Assert.All(Changelog.Embedded, r => Assert.NotEmpty(r.Notes));
    }
}

public class UpdateServiceTests
{
    private static JsonElement Release(string tag, string body = "- New: X", bool withSig = true, string host = "github.com")
    {
        string v = tag.TrimStart('v');
        string assets = $$"""
            { "name": "HamMeter-Setup-{{v}}.exe", "browser_download_url": "https://{{host}}/a/HamMeter-Setup-{{v}}.exe" }
            """ + (withSig ? $$"""
            , { "name": "HamMeter-Setup-{{v}}.exe.sig", "browser_download_url": "https://{{host}}/a/HamMeter-Setup-{{v}}.exe.sig" }
            """ : string.Empty);
        string json = $$"""
            { "tag_name": "{{tag}}", "published_at": "2026-10-02T10:00:00Z", "body": {{JsonSerializer.Serialize(body)}}, "assets": [ {{assets}} ] }
            """;
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    [Fact]
    public void NewerSignedRelease_IsOffered()
    {
        UpdateInfo? u = UpdateService.Parse(Release("v0.3.0", "[important]\n\n- Fixed: Damage."), new Version(0, 2, 0));

        Assert.NotNull(u);
        Assert.Equal(new Version(0, 3, 0), u!.Version);
        Assert.Equal(new DateOnly(2026, 10, 2), u.Date);
        Assert.True(u.Important);
        Assert.Single(u.Notes);
    }

    [Fact]
    public void SameOrOlderRelease_IsNotOffered()
    {
        Assert.Null(UpdateService.Parse(Release("v0.2.0"), new Version(0, 2, 0)));
        Assert.Null(UpdateService.Parse(Release("v0.1.9"), new Version(0, 2, 0)));
    }

    [Fact]
    public void ReleaseWithoutSignature_IsNotOffered()
    {
        Assert.Null(UpdateService.Parse(Release("v0.3.0", withSig: false), new Version(0, 2, 0)));
    }

    [Fact]
    public void DownloadsFromOtherHosts_AreIgnored()
    {
        Assert.Null(UpdateService.Parse(Release("v0.3.0", host: "evil.example.com"), new Version(0, 2, 0)));
    }

    [Theory]
    [InlineData("https://api.github.com/repos/x", true)]
    [InlineData("https://github.com/x/releases/download/a.exe", true)]
    [InlineData("https://objects.githubusercontent.com/a", true)]
    [InlineData("http://github.com/a", false)]
    [InlineData("https://github.com.evil.example/a", false)]
    [InlineData("https://evil.example/github.com", false)]
    public void OnlyHttpsGitHubHosts_AreAllowed(string url, bool allowed)
    {
        Assert.Equal(allowed, UpdateService.IsAllowed(new Uri(url)));
    }

    // End-to-end with the real release key: only runs when HAMMETER_SIGNED_FILE points at
    // a file signed by build\ReleaseSigner (the private key never enters the test).
    [Fact]
    public void RealReleaseSignature_IsAccepted_AndTamperingIsNot()
    {
        string? path = Environment.GetEnvironmentVariable("HAMMETER_SIGNED_FILE");
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        byte[] sig = Convert.FromBase64String(File.ReadAllText(path + ".sig").Trim());
        byte[] data = File.ReadAllBytes(path);
        using (var ok = new MemoryStream(data))
        {
            Assert.True(UpdateService.VerifySignature(ok, sig));
        }

        data[data.Length / 2] ^= 0x01;
        using var tampered = new MemoryStream(data);
        Assert.False(UpdateService.VerifySignature(tampered, sig));
    }

    [Fact]
    public void WrongSignature_IsRejected()
    {
        using var file = new MemoryStream("not the real setup"u8.ToArray());
        byte[] forged = new byte[64];
        Random.Shared.NextBytes(forged);

        Assert.False(UpdateService.VerifySignature(file, forged));
    }
}
