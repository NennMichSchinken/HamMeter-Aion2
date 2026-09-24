namespace HamMeter.Game;

// A player as far as the packets told us. Name is null until a name packet arrived.
public readonly record struct KnownPlayer(int Id, string? Name, long ClassId, bool IsUser);

// Who is who in the current zone: players, summons and mobs by entity id
// (docs/protocol.md §4). Filled by the entity packets, read by the combat parser.
public interface IEntityDirectory
{
    // (summonId, ownerId), raised when a summon's owner becomes known.
    event Action<int, int>? SummonRegistered;

    int? SummonOwner(int entityId);

    bool IsSummon(int entityId);

    KnownPlayer? Player(int entityId);

    // A player seen through combat: known by the class in its skill codes, maybe no name yet.
    KnownPlayer AddPlayer(int entityId, long classId);

    string TargetName(int entityId);

    // Whether the entity is a boss according to the monster list.
    bool IsBoss(int entityId);
}

// What skill codes mean (docs/protocol.md §7).
public interface ISkillRules
{
    // The class a player skill belongs to, or null when the code is not a class skill.
    long? ClassOf(int skillCode);

    bool IsTheostone(int skillCode);

    bool IsHealing(int skillCode);

    // Whether a damage tick (05 38) of this player skill counts as damage done.
    bool CountsAsTickDamage(int skillCode);
}
