using System.Buffers.Binary;
using System.Numerics;

namespace HamMeter.Game;

// The directory of the entities in the current zone. Only the capture
// thread writes and reads it (through the combat and entity parsers).
public sealed class EntityRegistry : IEntityDirectory
{
    private const int MaxPlayers = 2_000;
    private const int MaxPositions = 10_000;
    private const float SamePlace = 3f; // game units; a member's two positions differed by 2
    private static readonly TimeSpan UnnamedLifetime = TimeSpan.FromMinutes(10);

    private readonly Dictionary<int, Entry> m_players = new();
    private readonly Dictionary<int, int> m_summons = new();
    private readonly Dictionary<int, int> m_mobCodes = new();
    private readonly Dictionary<int, uint> m_characterIds = new();
    private Dictionary<uint, string> m_party = new();
    private readonly Dictionary<uint, Member> m_members = new(); // 1C 92, by character id
    private readonly Dictionary<int, Vector3> m_positions = new();
    private string? m_userName;
    private bool? m_partyIncomplete; // cached; names, links and the party reset it

    public event Action<int, int>? SummonRegistered;

    // (entityId, characterId, how): a party member from 1C 92 was tied to its entity.
    public event Action<int, uint, string>? MemberLinked;

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
        m_partyIncomplete = null;
    }

    // The user's entity, known before (or without) a name packet.
    public void SetUser(int entityId)
    {
        m_partyIncomplete = null;
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

    // 02 97: the current party. Replaces the previous one, and members known from 1C 92
    // that are not on it have left; an empty list = no party.
    public void SetParty(IReadOnlyList<PartyMember> members)
    {
        m_party = members.GroupBy(m => m.CharacterId).ToDictionary(g => g.Key, g => g.First().Name);
        foreach (uint left in m_members.Keys.Where(id => !m_party.ContainsKey(id)).ToList())
        {
            m_members.Remove(left);
        }

        m_partyIncomplete = null;
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
        m_partyIncomplete = null;
        this.NameFromParty(entityId, characterId);
    }

    // 1C 92: a party member other than the user, with the zone and position it is in. The
    // game sends it without a 02 97 after HamMeter started (open world, 2026-10-08), for
    // members near and far; it has no name and no entity id. The appearance (45 36) links
    // a member coming into view, the position one that was in view before HamMeter started.
    public void SetMember(ulong databaseId, uint zone, Vector3 position)
    {
        var member = new Member(databaseId, zone);
        if (!m_members.TryGetValue(member.CharacterId, out Member? old) || old != member)
        {
            m_members[member.CharacterId] = member;
            m_partyIncomplete = null;
        }

        if (!m_characterIds.ContainsValue(member.CharacterId))
        {
            this.LinkByPosition(member.CharacterId, position);
        }
    }

    // 1A 37 / 1B 37: where an entity is (players and monsters).
    public void SetPosition(int entityId, Vector3 position)
    {
        if (m_positions.Count >= MaxPositions && !m_positions.ContainsKey(entityId))
        {
            m_positions.Clear(); // entities of earlier zones; whoever is around moves again
        }

        m_positions[entityId] = position;
    }

    // The member's 1C 92 carries the same coordinates as the latest 1A 37 / 1B 37 of its
    // entity (2026-10-08: equal to the last bit). Only an entity right there that can be a
    // player and is no one else's yet is taken.
    private void LinkByPosition(uint characterId, Vector3 position)
    {
        int? found = null;
        float best = SamePlace * SamePlace;
        foreach ((int entityId, Vector3 at) in m_positions)
        {
            float distance = Vector3.DistanceSquared(at, position);
            if (distance <= best && entityId != this.UserId && !this.IsSummon(entityId)
                && this.Npc(entityId) is null && !m_characterIds.ContainsKey(entityId))
            {
                best = distance;
                found = entityId;
            }
        }

        if (found is int entity)
        {
            this.LinkCharacter(entity, characterId);
            this.MemberLinked?.Invoke(entity, characterId, "position");
        }
    }

    // 45 36 of another player: it carries the player's database id, so a party member known
    // from 1C 92 is linked to the entity that appeared. Any other player loses a link the
    // entity id had before (ids are reused).
    public void LinkByAppearance(int entityId, ReadOnlySpan<byte> packet)
    {
        Span<byte> id = stackalloc byte[8];
        foreach (Member member in m_members.Values)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(id, member.DatabaseId);
            if (packet.IndexOf(id) >= 0)
            {
                this.LinkCharacter(entityId, member.CharacterId);
                this.MemberLinked?.Invoke(entityId, member.CharacterId, "appearance");
                return;
            }
        }

        if (m_characterIds.Remove(entityId))
        {
            m_partyIncomplete = null;
        }
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

    // Spawned as a monster from the monster list. That beats a player entry for the same id
    // (an id reused after a zone change, or a monster that used a player-like skill code);
    // only the user and the party stay players whatever an old mob code says.
    public bool IsMonster(int entityId) =>
        this.Npc(entityId) is not null && entityId != this.UserId && !this.InParty(entityId);

    // Whether party members in a dungeon are still unaccounted for: fewer players tied to
    // one (by link or name) than the party has besides the user. Names come with the packet
    // of a player appearing, so after HamMeter starts inside a dungeon some never do
    // (2026-10-07). In the open world members are often far away while strangers without
    // a name are around, so there it is never assumed (2026-10-08).
    public bool PartyIncomplete => m_partyIncomplete ??= this.CountPartyIncomplete();

    private bool CountPartyIncomplete()
    {
        if (!m_members.Values.Any(m => DungeonZones.Value.Contains(m.Zone)))
        {
            return false;
        }

        HashSet<uint> linked = m_characterIds.Where(kv => kv.Key != this.UserId).Select(kv => kv.Value).ToHashSet();
        HashSet<string> named = m_players.Values.Where(e => !e.IsUser && e.Name is not null).Select(e => e.Name!).ToHashSet();
        int found = m_party.Count(kv => kv.Value != m_userName && (linked.Contains(kv.Key) || named.Contains(kv.Value)));
        bool listMissing = found < m_party.Count - 1; // one of the 02 97 list is the user
        return listMissing || m_members.Keys.Any(id => !m_party.ContainsKey(id) && !linked.Contains(id));
    }

    private static readonly Lazy<HashSet<uint>> DungeonZones = new(() => NpcData.DungeonIds.Select(id => (uint)id).ToHashSet());

    // By the character link, or by name when the link packet was missed (§4).
    public bool InParty(int entityId) =>
        (m_characterIds.TryGetValue(entityId, out uint characterId) && (m_party.ContainsKey(characterId) || m_members.ContainsKey(characterId)))
        || (m_party.Count > 0 && m_players.TryGetValue(entityId, out Entry? e) && e.Name is not null && m_party.ContainsValue(e.Name));

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

    // Database id: low 32 bits the character id, top 16 bits the server (§6.4).
    private sealed record Member(ulong DatabaseId, uint Zone)
    {
        public uint CharacterId => (uint)this.DatabaseId;
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
