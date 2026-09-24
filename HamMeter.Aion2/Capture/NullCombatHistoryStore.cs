using AionDpsMeter.Services.Services.Session;
using AionDpsMeter.Services.Services.Session.Persistence;

namespace HamMeter.Capture;

// Data minimisation: Kuroukihime's CombatSessionManager persists every fight (player
// names, damage) to a SQLite file. HamMeter keeps its history in memory only, so this
// store accepts and forgets - no database file is ever written.
public sealed class NullCombatHistoryStore : ICombatHistoryStore
{
    public void Save(HistorySessionSnapshot snapshot)
    {
    }

    public void FlushPendingSaves()
    {
    }

    public IReadOnlyList<HistorySessionListItem> GetSessionList() => [];

    public int GetSessionCount(DateTime? dateFrom, DateTime? dateTo, string? bossNameContains, IReadOnlySet<Guid>? excludeSessionIds = null) => 0;

    public IReadOnlyList<HistorySessionListItem> GetSessionPage(
        DateTime? dateFrom,
        DateTime? dateTo,
        string? bossNameContains,
        int skip,
        int take,
        IReadOnlySet<Guid>? excludeSessionIds = null) => [];

    public HistorySessionSnapshot? GetSession(Guid sessionId) => null;
}
