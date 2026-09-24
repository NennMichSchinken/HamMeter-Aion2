using System.Text;
using HamMeter.Classic;
using HamMeter.Combat;
using HamMeter.Game;
using HamMeter.Protocol;
using Microsoft.Extensions.Logging;

namespace HamMeter.Capture;

// Development tool (HamMeter.exe --replay <file.hmrec>): runs a packet recording through
// HamMeter's own reader in recorded time and writes <file>.report.txt with every fight,
// its per-skill totals, a comparison with the classic reader and how often each opcode
// occurred.
public static class Replay
{
    public static void Run(string path)
    {
        var report = new StringBuilder();
        report.AppendLine($"HamMeter replay of {Path.GetFileName(path)}").AppendLine();

        // Kuroukihime's settings service writes into the working directory.
        Environment.CurrentDirectory = Path.GetTempPath();

        using ILoggerFactory logs = LoggerFactory.Create(b => b
            .SetMinimumLevel(LogLevel.Information)
            .AddProvider(new FileLoggerProvider(path + ".log", LogLevel.Information)));

        // ----- HamMeter's own reader -----
        var tracker = new EncounterTracker();
        using OwnEngine engine = OwnEngine.Offline(tracker, logs);

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

        // ----- Classic reader on the same packets -----
        var classicTracker = new EncounterTracker();
        var classic = new List<EncounterSnapshot>();
        classicTracker.Finished += classic.Add;
        ClassicReplay.Run(Read(path, new StringBuilder()), classicTracker);

        report.AppendLine($"Framed packets: {records:N0}");
        report.AppendLine($"Own character: {(engine.Entities.UserId is int id ? $"entity {id}" : "not seen")}");
        report.AppendLine($"Fights: HamMeter {own.Count}, classic {classic.Count}").AppendLine();

        for (int i = 0; i < Math.Max(own.Count, classic.Count); i++)
        {
            EncounterSnapshot? mine = i < own.Count ? own[i].Fight : null;
            EncounterSnapshot? theirs = i < classic.Count ? classic[i] : null;
            report.AppendLine($"== Fight {i + 1}: {(mine ?? theirs)!.Duration} ==");
            Compare(report, mine, theirs);
            if (i < own.Count && own[i].Skills is { } skills)
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

    private static readonly List<string> Unusual = new();

    private static readonly Dictionary<int, (string Code, DateTime First, DateTime Last, int Hits, long Damage, int Skill)> Attackers = new();

    private static readonly Dictionary<int, DateTime> Deaths = new();

    // 04 38 target, actor and amount (docs/protocol.md §5.1), for hits by the user.
    private static void NoteHit(
        byte[] packet,
        OwnEngine engine,
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

    private static void Compare(StringBuilder report, EncounterSnapshot? mine, EncounterSnapshot? theirs)
    {
        var ids = (mine?.Combatants.Select(c => c.Id) ?? []).Union(theirs?.Combatants.Select(c => c.Id) ?? []);
        foreach (int id in ids)
        {
            Combatant? a = mine?.Combatants.FirstOrDefault(c => c.Id == id);
            Combatant? b = theirs?.Combatants.FirstOrDefault(c => c.Id == id);
            Combatant any = (a ?? b)!;
            report.AppendLine($"  {any.Name}{(any.IsUser ? " (you)" : string.Empty)} [{any.Job}]");
            Row(report, "damage", a?.DamageTotal, b?.DamageTotal);
            Row(report, "healed", a?.HealedTotal, b?.HealedTotal);
            Row(report, "taken", a?.DamageTaken, b?.DamageTaken);
            Row(report, "healing taken", a?.HealingTaken, b?.HealingTaken);
            Row(report, "deaths", a?.DeathCount, b?.DeathCount);
        }
    }

    private static void Row(StringBuilder report, string what, double? mine, double? theirs)
    {
        string verdict = mine == theirs ? "same" : $"DIFFERS by {(mine ?? 0) - (theirs ?? 0):N0}";
        report.AppendLine($"    {what,-14} HamMeter {mine?.ToString("N0") ?? "-",10}   classic {theirs?.ToString("N0") ?? "-",10}   {verdict}");
    }
}
