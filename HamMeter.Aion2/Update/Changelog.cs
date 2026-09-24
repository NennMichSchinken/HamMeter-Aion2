using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;

namespace HamMeter.Update;

public enum NoteKind
{
    New,
    Improved,
    Fixed,
}

public sealed record Note(NoteKind Kind, string Text);

public sealed record Release(Version Version, DateOnly? Date, bool Important, IReadOnlyList<Note> Notes);

// Patch notes from CHANGELOG.md: embedded into HamMeter for "What's new", and the same
// section text is the GitHub release body the update check reads.
//   ## 0.2.0 - 2026-09-24 [important]
//   - New: ... / - Improved: ... / - Fixed: ...
public static partial class Changelog
{
    [GeneratedRegex(@"^##\s+v?(?<v>\d+(\.\d+){1,3})(\s*-\s*(?<d>\d{4}-\d{2}-\d{2}))?(?<imp>.*\[important\])?", RegexOptions.IgnoreCase)]
    private static partial Regex HeadingRegex();

    [GeneratedRegex(@"^[-*]\s+(?<k>New|Improved|Fixed)\s*:\s*(?<t>.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex NoteRegex();

    private static IReadOnlyList<Release>? s_embedded;

    // The releases shipped with this build, newest first.
    public static IReadOnlyList<Release> Embedded => s_embedded ??= LoadEmbedded();

    public static Version Current { get; } = CurrentVersion();

    // The installed version's own entry (for its date), if the changelog has one.
    public static Release? CurrentRelease => Embedded.FirstOrDefault(r => r.Version == Current);

    public static IReadOnlyList<Release> Parse(string markdown)
    {
        List<Release> releases = new();
        Version? version = null;
        DateOnly? date = null;
        bool important = false;
        List<Note> notes = new();

        void Flush()
        {
            if (version is not null)
            {
                releases.Add(new Release(version, date, important, notes.ToList()));
            }

            notes.Clear();
        }

        foreach (string raw in markdown.Split('\n'))
        {
            string line = raw.Trim();
            Match h = HeadingRegex().Match(line);
            if (h.Success)
            {
                Flush();
                version = Version.Parse(h.Groups["v"].Value);
                date = DateOnly.TryParseExact(h.Groups["d"].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly d) ? d : null;
                important = h.Groups["imp"].Success;
                continue;
            }

            Match n = NoteRegex().Match(line);
            if (version is not null && n.Success)
            {
                NoteKind kind = Enum.Parse<NoteKind>(n.Groups["k"].Value, true);
                notes.Add(new Note(kind, n.Groups["t"].Value.Trim()));
            }
        }

        Flush();
        return releases.OrderByDescending(r => r.Version).ToList();
    }

    // Notes of a release body that has no "## version" heading (GitHub release text).
    public static IReadOnlyList<Note> ParseNotes(string body) =>
        Parse("## 0.0.0\n" + body).FirstOrDefault()?.Notes ?? [];

    public static string FormatDate(DateOnly? date) =>
        date?.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture) ?? string.Empty;

    private static IReadOnlyList<Release> LoadEmbedded()
    {
        using Stream? s = typeof(Changelog).Assembly.GetManifestResourceStream("HamMeter.CHANGELOG.md");
        return s is null ? [] : Parse(new StreamReader(s).ReadToEnd());
    }

    private static Version CurrentVersion()
    {
        string? info = typeof(Changelog).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return Version.TryParse(info?.Split('+')[0], out Version? v) ? v : new Version(0, 0, 0);
    }
}
