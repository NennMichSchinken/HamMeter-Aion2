namespace HamMeter.Game;

// The directory of the entities in the current zone. Only the capture
// thread writes and reads it (through the combat and entity parsers).
public sealed class EntityRegistry : IEntityDirectory
{
    private const int MaxPlayers = 2_000;
    private static readonly TimeSpan UnnamedLifetime = TimeSpan.FromMinutes(10);

    private readonly Dictionary<int, Entry> m_players = new();
    private readonly Dictionary<int, int> m_summons = new();
    private readonly Dictionary<int, int> m_mobCodes = new();
    private readonly Dictionary<int, uint> m_characterIds = new();
    private Dictionary<uint, string> m_party = new();
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
            this.SetUser(entityId);
        }

        e.IsUser = name == m_userName || entityId == this.UserId;
    }

    // The user's entity, known before (or without) a name packet.
    public void SetUser(int entityId)
    {
        if (this.UserId is int old && old != entityId && m_players.TryGetValue(old, out Entry? previous))
        {
            previous.IsUser = false;
        }

        this.UserId = entityId;
        if (!m_players.TryGetValue(entityId, out Entry? e))
        {
            e = this.Add(entityId);
        }

        e.IsUser = true;
        e.Name ??= m_userName; // a new entity id after a zone change: same character
        m_summons.Remove(entityId);
    }

    public bool UserKnown => this.UserId is not null;

    public void RegisterSummon(int summonId, int ownerId)
    {
        if (summonId == ownerId || m_summons.TryGetValue(summonId, out int existing) && existing == ownerId)
        {
            return;
        }

        m_summons[summonId] = ownerId;
        this.SummonRegistered?.Invoke(summonId, ownerId);
    }

    // 02 97: the current party. Replaces the previous one; an empty list = no party.
    public void SetParty(IReadOnlyList<PartyMember> members)
    {
        m_party = members.GroupBy(m => m.CharacterId).ToDictionary(g => g.Key, g => g.First().Name);
        foreach ((int entityId, uint characterId) in m_characterIds)
        {
            this.NameFromParty(entityId, characterId);
        }
    }

    // 20 36: which character an entity is, so party data reaches it.
    public void LinkCharacter(int entityId, uint characterId)
    {
        if (m_characterIds.Count >= MaxPlayers && !m_characterIds.ContainsKey(entityId))
        {
            m_characterIds.Clear(); // stale links from earlier zones; the party ones come again
        }

        m_characterIds[entityId] = characterId;
        this.NameFromParty(entityId, characterId);
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
            if (m_characterIds.TryGetValue(entityId, out uint characterId) && m_party.TryGetValue(characterId, out string? name))
            {
                e.Name = name;
            }
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

    // By the character link, or by name when the link packet was missed (§4).
    public bool InParty(int entityId) =>
        m_party.Count > 0
        && ((m_characterIds.TryGetValue(entityId, out uint characterId) && m_party.ContainsKey(characterId))
            || (m_players.TryGetValue(entityId, out Entry? e) && e.Name is not null && m_party.ContainsValue(e.Name)));

    // Party members are players; a linked one without a name yet gets it from the list.
    private void NameFromParty(int entityId, uint characterId)
    {
        if (m_party.TryGetValue(characterId, out string? name) && m_players.TryGetValue(entityId, out Entry? e) && e.Name is null)
        {
            this.SetName(entityId, name, isUser: false);
        }
    }

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
