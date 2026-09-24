namespace HamMeter.Game;

// HamMeter's own directory of the entities in the current zone. Only the capture
// thread writes and reads it (through the combat and entity parsers).
public sealed class EntityRegistry : IEntityDirectory
{
    private const int MaxPlayers = 2_000;
    private static readonly TimeSpan UnnamedLifetime = TimeSpan.FromMinutes(10);

    private readonly Dictionary<int, Entry> m_players = new();
    private readonly Dictionary<int, int> m_summons = new();
    private readonly Dictionary<int, int> m_mobCodes = new();
    private string? m_userName;

    public event Action<int, int>? SummonRegistered;

    public int? UserId { get; private set; }

    // ----- Written by the entity packets --------------------------------------------

    public void SetName(int entityId, string name, bool isUser)
    {
        if (m_players.TryGetValue(entityId, out Entry? e) && e.Name is not null && e.Name != name)
        {
            // The id now belongs to someone else (reused after a zone change).
            m_players.Remove(entityId);
            e = null;
        }

        e ??= this.Add(entityId);
        e.Name = name;
        m_summons.Remove(entityId);

        if (isUser)
        {
            m_userName = name;
            this.UserId = entityId;
        }

        e.IsUser = name == m_userName;
    }

    public void RegisterSummon(int summonId, int ownerId)
    {
        if (summonId == ownerId || m_summons.TryGetValue(summonId, out int existing) && existing == ownerId)
        {
            return;
        }

        m_summons[summonId] = ownerId;
        this.SummonRegistered?.Invoke(summonId, ownerId);
    }

    public void SetMobCode(int entityId, int mobCode) => m_mobCodes[entityId] = mobCode;

    public int? MobCode(int entityId) => m_mobCodes.TryGetValue(entityId, out int code) ? code : null;

    // ----- IEntityDirectory -----------------------------------------------------------

    public int? SummonOwner(int entityId) => m_summons.TryGetValue(entityId, out int owner) ? owner : null;

    public bool IsSummon(int entityId) => m_summons.ContainsKey(entityId);

    public KnownPlayer? Player(int entityId) =>
        m_players.TryGetValue(entityId, out Entry? e) ? e.ToKnown() : null;

    public KnownPlayer AddPlayer(int entityId, long classId)
    {
        if (!m_players.TryGetValue(entityId, out Entry? e))
        {
            e = this.Add(entityId);
        }

        if (e.ClassId == 0)
        {
            e.ClassId = classId;
        }

        return e.ToKnown();
    }

    // Monster names come from the monster list by the mob code of the spawn packet.
    public string TargetName(int entityId) => this.Npc(entityId)?.Name ?? string.Empty;

    public bool IsBoss(int entityId) => this.Npc(entityId)?.IsBoss == true;

    private NpcInfo? Npc(int entityId) => this.MobCode(entityId) is int code ? NpcData.Get(code) : null;

    private Entry Add(int entityId)
    {
        if (m_players.Count >= MaxPlayers)
        {
            this.DropStaleUnnamed();
        }

        var e = new Entry(entityId);
        m_players[entityId] = e;
        return e;
    }

    private void DropStaleUnnamed()
    {
        DateTime cutoff = DateTime.Now - UnnamedLifetime;
        foreach (int id in m_players.Where(kv => kv.Value.Name is null && kv.Value.Created < cutoff).Select(kv => kv.Key).ToList())
        {
            m_players.Remove(id);
        }
    }

    private sealed class Entry(int id)
    {
        public int Id { get; } = id;

        public DateTime Created { get; } = DateTime.Now;

        public string? Name { get; set; }

        public long ClassId { get; set; }

        public bool IsUser { get; set; }

        public KnownPlayer ToKnown() => new(this.Id, this.Name, this.ClassId, this.IsUser);
    }
}
