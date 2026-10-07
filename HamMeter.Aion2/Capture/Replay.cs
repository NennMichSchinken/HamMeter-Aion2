using System.Text;
using HamMeter.Combat;
using HamMeter.Game;
using HamMeter.Protocol;
using Microsoft.Extensions.Logging;

namespace HamMeter.Capture;

// Development tool (HamMeter.exe --replay <file.hmrec>): runs a packet recording through
// HamMeter's reader in recorded time and writes <file>.report.txt with every fight, its
// per-skill totals and how often each opcode occurred.
public static class Replay
{
    public static void Run(string path)
    {
        var report = new StringBuilder();
        report.AppendLine($"HamMeter replay of {Path.GetFileName(path)}").AppendLine();

        using ILoggerFactory logs = LoggerFactory.Create(b => b
            .SetMinimumLevel(LogLevel.Information)
            .AddProvider(new FileLoggerProvider(path + ".log", LogLevel.Information)));

        var tracker = new EncounterTracker();
        using PacketEngine engine = PacketEngine.Offline(tracker, logs);

        DateTime now = DateTime.MinValue;
        engine.Parser.Clock = () => now;
        var own = new List<(EncounterSnapshot Fight, string? Skills)>();
        tracker.Finished += e => own.Add((e, engine.SkillLog?.Drain()));

        // Timeline of the user's targets: first hit and death (04 8D) per entity.
        var targets = new Dictionary<int, (DateTime FirstHit, DateTime LastHit, long Damage, DateTime? Died)>();
        engine.Router.On(Opcodes.Hit, p => NoteHit(p, engine, targets, now));
        engine.Router.On(Opcodes.Death, p =>
        {
            int id = (int)PacketReader.Body(p).ReadVarInt();
            Deaths[id] = now;
            if (targets.TryGetValue(id, out var t))
            {
                targets[id] = t with { Died = now };
            }
        });

        // Every change of the party list (02 97): which bit packing read it, or the packet
        // when none did (§6.6).
        var parties = new List<string>();
        string? lastParty = null;
        engine.Router.On(Opcodes.PartyList, p =>
        {
            string party = PartyPacket.Read(p) is var (members, packing)
                ? $"{packing}, dungeon {PartyDungeon(p)}: {(members.Count == 0 ? "no party" : string.Join(", ", members.Select(m => $"{m.Name} ({m.CharacterId})")))}"
                : $"NOT READABLE ({p.Length} bytes): {Convert.ToHexString(p)}";
            if (party != lastParty)
            {
                lastParty = party;
                parties.Add($"  {now:HH:mm:ss} {party}");
            }
        });

        // Every summon and its owner (§6.7); an owner can be a summon itself.
        var summons = new List<string>();
        string Who(int id) => engine.Entities.Player(id) is { } p ? $"{id} {p.Name ?? "(no name)"} [{ClassInfo.KeyFromId(p.ClassId)}]" : $"{id}";
        engine.Entities.SummonRegistered += (summon, owner) => summons.Add(
            $"  {now:HH:mm:ss} summon {summon} -> owner {Who(owner)}{(engine.Entities.SummonOwner(owner) is int top ? $", itself a summon of {Who(top)}" : string.Empty)}");

        // Zone changes and the first monster of each dungeon (monster list), to find the
        // signal for "entered a dungeon": the meter should start fresh there.
        var zones = new List<string>();
        int? zoneUser = null;
        var dungeonsSeen = new HashSet<int>();
        engine.Router.On(Opcodes.OwnCharacter, p => zones.Add($"  {now:HH:mm:ss} 33 36 own character, entity {PacketReader.Body(p).ReadVarInt()}"));
        engine.Router.On(Opcodes.UserState, p =>
        {
            int id = (int)PacketReader.Body(p).ReadVarInt();
            if (id != zoneUser)
            {
                zones.Add($"  {now:HH:mm:ss} 4A 36 user entity {id}{(zoneUser is int old ? $" (was {old})" : string.Empty)}");
                zoneUser = id;
            }
        });
        engine.Router.On(Opcodes.Spawn, p =>
        {
            int id = (int)PacketReader.Body(p).ReadVarInt();
            if (engine.Entities.MobCode(id) is int mc && NpcData.DungeonOf(mc) is int dungeon && dungeonsSeen.Add(dungeon))
            {
                zones.Add($"  {now:HH:mm:ss} first monster of dungeon {dungeon}: {NpcData.Get(mc)?.Name} (entity {id})");
            }
        });

        // Every known dungeon id (u32 or varint) anywhere in a packet: which opcode names the zone.
        var dungeonIds = NpcData.DungeonIds.Select(d => (uint)d).ToHashSet();
        var dungeonHits = new Dictionary<(ushort Op, int At, string Kind), (int Count, DateTime First, DateTime Last, HashSet<uint> Ids)>();

        // First body varint per packet, to find packets that are only ever about the user.
        var firstIds = new List<(ushort Op, uint Id, DateTime Time)>();

        // HAMMETER_FIND=<text>: which opcodes carry this text (UTF-8), e.g. a character name.
        byte[]? find = Environment.GetEnvironmentVariable("HAMMETER_FIND") is { Length: > 0 } f ? System.Text.Encoding.UTF8.GetBytes(f) : null;
        var found = new List<string>();

        // HAMMETER_WINDOW=HH:mm:ss-HH:mm:ss: every hit and tick in that time span.
        var window = new List<string>();
        if (Environment.GetEnvironmentVariable("HAMMETER_WINDOW") is { Length: > 0 } span && span.Split('-') is [var from, var to])
        {
            TimeSpan a = TimeSpan.Parse(from), b = TimeSpan.Parse(to);
            engine.Router.On(Opcodes.Hit, p =>
            {
                if (now.TimeOfDay < a || now.TimeOfDay > b)
                {
                    return;
                }

                PacketReader r = PacketReader.Body(p);
                int target = (int)r.ReadVarInt();
                uint layout = r.ReadVarInt() & 0x0F;
                r.ReadVarInt();
                int actor = (int)r.ReadVarInt();
                int skill = (int)r.ReadU32();
                r.ReadU8();
                r.ReadVarInt();
                r.Skip(layout == 4 ? 8 : 11);
                r.ReadVarInt();
                window.Add($"  {now:HH:mm:ss.f} hit  actor {actor} -> {target} skill {skill} amount {r.ReadVarInt()}");
            });
            engine.Router.On(Opcodes.Tick, p =>
            {
                if (now.TimeOfDay < a || now.TimeOfDay > b)
                {
                    return;
                }

                PacketReader r = PacketReader.Body(p);
                int target = (int)r.ReadVarInt();
                byte type = r.ReadU8();
                int actor = (int)r.ReadVarInt();
                r.ReadVarInt();
                int skill = (int)(r.ReadU32() / 100);
                window.Add($"  {now:HH:mm:ss.f} tick actor {actor} -> {target} type {type:X2} skill {skill} amount {r.ReadVarInt()}");
            });
        }

        double clockBefore = 0;
        DateTime? idleSince = null;
        var idleRuns = new List<string>();

        var opcodes = new Dictionary<ushort, int>();
        var expanded = new List<byte[]>();
        int records = 0;
        foreach ((DateTimeOffset time, byte[] packet) in Read(path, report))
        {
            records++;
            now = time.LocalDateTime;
            tracker.Tick(now);

            expanded.Clear();
            BundleDecoder.Expand(packet, expanded);
            foreach (byte[] p in expanded)
            {
                if (Opcodes.TryRead(p, out ushort op))
                {
                    opcodes[op] = opcodes.GetValueOrDefault(op) + 1;
                    int at = find is null ? -1 : p.AsSpan().IndexOf(find);
                    if (at >= 0)
                    {
                        found.Add($"  {now:HH:mm:ss} {op & 0xFF:X2} {op >> 8:X2} at byte {at} of {p.Length}: {Convert.ToHexString(p.AsSpan(0, Math.Min(p.Length, at + find!.Length + 8)))}");
                    }

                    FindDungeonIds(p, op, now, dungeonIds, dungeonHits);
                    try
                    {
                        firstIds.Add((op, PacketReader.Body(p).ReadVarInt(), now));
                    }
                    catch (PacketFormatException)
                    {
                    }
                }
            }

            engine.Stream.Dispatch(packet);

            // Fight clock running while nobody hit anything for 3 s: DPS sinks there.
            if (tracker.CurrentAt(now) is { Active: true } live)
            {
                DateTime lastHit = targets.Count == 0 ? DateTime.MinValue : targets.Values.Max(t => t.LastHit);
                bool idle = (now - lastHit).TotalSeconds > 3;
                if (idle && live.Seconds > clockBefore + 0.2)
                {
                    idleSince ??= now;
                }
                else if (!idle || live.Seconds <= clockBefore + 0.2)
                {
                    if (idleSince is { } s && (now - s).TotalSeconds >= 2)
                    {
                        idleRuns.Add($"  {s:HH:mm:ss} - {now:HH:mm:ss} ({(now - s).TotalSeconds:0}s), last hit {lastHit:HH:mm:ss}, engaged: {string.Join(", ", tracker.Engaged().Select(e => engine.Entities.MobCode(e) is int mc ? $"{e} ({NpcData.Get(mc)?.Name})" : $"{e} (no spawn seen{(Deaths.ContainsKey(e) ? ", died" : string.Empty)})"))}");
                    }

                    idleSince = null;
                }

                clockBefore = live.Seconds;
            }
        }

        tracker.Tick(now.AddHours(1)); // finish the last fight

        report.AppendLine($"Framed packets: {records:N0}");
        report.AppendLine($"Own character: {(engine.Entities.UserId is int id ? $"entity {id}" : "not seen")}");
        report.AppendLine($"Fights: {own.Count}").AppendLine();
        report.AppendLine("Party lists (02 97):");
        parties.ForEach(l => report.AppendLine(l));
        report.AppendLine();
        report.AppendLine("Summons (41 36):");
        summons.ForEach(l => report.AppendLine(l));
        report.AppendLine();
        report.AppendLine("Zone changes:");
        zones.ForEach(l => report.AppendLine(l));
        report.AppendLine();
        report.AppendLine("Known dungeon ids in packets (opcode | byte | as | count | first | last | ids):");
        foreach (var ((op, at, kind), h) in dungeonHits.OrderBy(kv => kv.Value.First).Take(60))
        {
            report.AppendLine($"  {op & 0xFF:X2} {op >> 8:X2} | {at} | {kind} | {h.Count} | {h.First:HH:mm:ss} | {h.Last:HH:mm:ss} | {string.Join(", ", h.Ids.Take(5))}");
        }

        report.AppendLine();

        for (int i = 0; i < own.Count; i++)
        {
            (EncounterSnapshot fight, string? skills) = own[i];
            report.AppendLine($"== Fight {i + 1}: {fight.Title}{(fight.IsBoss ? " (boss)" : string.Empty)}, {fight.Duration} ==");
            Totals(report, fight);
            if (skills is not null)
            {
                report.AppendLine(skills);
            }

            report.AppendLine();
        }

        report.AppendLine("Targets of the user (first hit | last hit | damage | death):");
        foreach ((int entity, var t) in targets.OrderBy(kv => kv.Value.FirstHit))
        {
            string died = t.Died is { } d ? $"died {d:HH:mm:ss} (+{(d - t.LastHit).TotalSeconds:0.0}s after last hit)" : "no death seen";
            string mob = engine.Entities.MobCode(entity) is int code ? $" mob {code}" : string.Empty;
            report.AppendLine($"  entity {entity}{mob}: {t.FirstHit:HH:mm:ss} | {t.LastHit:HH:mm:ss} | {t.Damage:N0} | {died}");
        }

        report.AppendLine();
        if (find is not null)
        {
            report.AppendLine($"Packets containing \"{Environment.GetEnvironmentVariable("HAMMETER_FIND")}\":");
            found.ForEach(l => report.AppendLine(l));
            report.AppendLine();
        }

        report.AppendLine("Fight clock running while nobody hit anything:");
        idleRuns.ForEach(r => report.AppendLine(r));
        if (window.Count > 0)
        {
            report.AppendLine().AppendLine("Hits and ticks in HAMMETER_WINDOW:");
            window.ForEach(w => report.AppendLine(w));
        }

        report.AppendLine();
        report.AppendLine("Non-players that hit the user (first | last | hits | damage | skill | death):");
        foreach ((int attacker, var a) in Attackers.OrderBy(kv => kv.Value.First))
        {
            string died = Deaths.TryGetValue(attacker, out DateTime d) ? $"died {d:HH:mm:ss}" : "NO DEATH SEEN";
            report.AppendLine($"  entity {attacker} ({a.Code}): {a.First:HH:mm:ss} | {a.Last:HH:mm:ss} | {a.Hits} | {a.Damage:N0} | {a.Skill} | {died}");
        }

        report.AppendLine();
        report.AppendLine("Hits by the user on players:");
        Unusual.ForEach(u => report.AppendLine(u));
        report.AppendLine();
        if (engine.Entities.UserId is int user)
        {
            // Which opcodes name the user first, and how often they name other players.
            var players = firstIds.Select(f => (int)f.Id).Where(i => i != user && engine.Entities.Player(i) is not null).ToHashSet();
            report.AppendLine($"Opcodes whose first field is the user ({user}) vs. another player:");
            foreach (var g in firstIds.GroupBy(f => f.Op))
            {
                int mine = g.Count(f => f.Id == (uint)user);
                int others = g.Count(f => players.Contains((int)f.Id));
                if (mine > 0)
                {
                    string first = string.Join(" ", g.Where(f => f.Id == (uint)user).Select(f => f.Time.ToString("HH:mm:ss")).Take(8));
                    report.AppendLine($"  {g.Key & 0xFF:X2} {g.Key >> 8:X2}: user {mine} (first {first}), other players {others}, total {g.Count()}");
                }
            }

            report.AppendLine();
        }

        report.AppendLine("Opcodes (wire order: count):");
        foreach ((ushort op, int count) in opcodes.OrderByDescending(kv => kv.Value))
        {
            report.AppendLine($"  {op & 0xFF:X2} {op >> 8:X2}: {count:N0}");
        }

        File.WriteAllText(path + ".report.txt", report.ToString());
    }

    // 02 97 header (§6.4): party key, party name, party size, then the dungeon id.
    private static string PartyDungeon(byte[] packet)
    {
        try
        {
            PacketReader r = PacketReader.Body(packet);
            r.ReadU32();
            r.Skip(r.ReadU8());
            r.ReadU8();
            return r.ReadU32().ToString();
        }
        catch (PacketFormatException)
        {
            return "?";
        }
    }

    private static void FindDungeonIds(
        byte[] packet,
        ushort op,
        DateTime now,
        HashSet<uint> ids,
        Dictionary<(ushort Op, int At, string Kind), (int Count, DateTime First, DateTime Last, HashSet<uint> Ids)> hits)
    {
        for (int at = 0; at < packet.Length; at++)
        {
            ReadOnlySpan<byte> rest = packet.AsSpan(at);
            if (rest.Length >= 4)
            {
                Note(BitConverter.ToUInt32(rest), "u32");
            }

            if (VarInt.TryRead(rest, out uint value, out _))
            {
                Note(value, "varint");
            }

            void Note(uint value, string kind)
            {
                if (!ids.Contains(value))
                {
                    return;
                }

                var key = (op, at, kind);
                hits[key] = hits.TryGetValue(key, out var h)
                    ? (h.Count + 1, h.First, now, h.Ids)
                    : (1, now, now, new HashSet<uint>());
                hits[key].Ids.Add(value);
            }
        }
    }

    private static readonly List<string> Unusual = new();

    private static readonly Dictionary<int, (string Code, DateTime First, DateTime Last, int Hits, long Damage, int Skill)> Attackers = new();

    private static readonly Dictionary<int, DateTime> Deaths = new();

    // 04 38 target, actor and amount (docs/protocol.md §5.1), for hits by the user.
    private static void NoteHit(
        byte[] packet,
        PacketEngine engine,
        Dictionary<int, (DateTime FirstHit, DateTime LastHit, long Damage, DateTime? Died)> targets,
        DateTime now)
    {
        PacketReader r = PacketReader.Body(packet);
        int target = (int)r.ReadVarInt();
        int layout = (int)(r.ReadVarInt() & 0x0F);
        r.ReadVarInt();
        int actor = (int)r.ReadVarInt();
        int skill = (int)r.ReadU32();
        r.ReadU8();
        r.ReadVarInt();
        r.Skip(layout == 4 ? 8 : 11);
        r.ReadVarInt();
        long amount = r.ReadVarInt();

        // Non-players hitting the user: they keep the fight clock running until they die.
        if (engine.Entities.Player(target) is { IsUser: true } && engine.Entities.Player(actor) is null && !engine.Entities.IsSummon(actor))
        {
            string code = engine.Entities.MobCode(actor) is int mc ? $"mob {mc} {NpcData.Get(mc)?.Name}" : "no spawn seen";
            Attackers[actor] = Attackers.TryGetValue(actor, out var a)
                ? a with { Last = now, Hits = a.Hits + 1, Damage = a.Damage + amount }
                : (code, now, now, 1, amount, skill);
        }

        bool byUser = engine.Entities.Player(actor) is { IsUser: true } || engine.Entities.SummonOwner(actor) is int owner
            && engine.Entities.Player(owner) is { IsUser: true };
        bool fromUserId = engine.Entities.UserId is null && engine.Entities.Player(actor) is not null;
        if (actor == target || (!byUser && !fromUserId))
        {
            return;
        }

        if (engine.Entities.Player(target) is not null)
        {
            Unusual.Add($"  {now:HH:mm:ss} actor {actor} hit player {target} with skill {skill}: {amount:N0}");
            return;
        }

        if (engine.Entities.Player(actor) is null)
        {
            return; // a monster hitting someone, see Attackers
        }

        targets[target] = targets.TryGetValue(target, out var t)
            ? t with { LastHit = now, Damage = t.Damage + amount }
            : (now, now, amount, null);
    }

    // A recording that is still being written ends with an incomplete record.
    private static IEnumerable<(DateTimeOffset Time, byte[] Packet)> Read(string path, StringBuilder report)
    {
        using IEnumerator<(DateTimeOffset, byte[])> records = PacketRecorder.Read(path).GetEnumerator();
        while (true)
        {
            try
            {
                if (!records.MoveNext())
                {
                    yield break;
                }
            }
            catch (Exception ex)
            {
                report.AppendLine($"Recording ends early: {ex.Message}").AppendLine();
                yield break;
            }

            yield return records.Current;
        }
    }

    private static void Totals(StringBuilder report, EncounterSnapshot fight)
    {
        foreach (Combatant c in fight.Combatants)
        {
            report.AppendLine($"  {c.Name}{(c.IsUser ? " (you)" : string.Empty)} [{c.Job}]");
            report.AppendLine($"    damage {c.DamageTotal,12:N0}   healed {c.HealedTotal,12:N0}   taken {c.DamageTaken,12:N0}   healing taken {c.HealingTaken,12:N0}   deaths {c.DeathCount}");
        }
    }
}
