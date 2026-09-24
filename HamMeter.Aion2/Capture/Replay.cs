using System.Text;
using HamMeter.Classic;
using HamMeter.Combat;
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
            if (targets.TryGetValue(id, out var t))
            {
                targets[id] = t with { Died = now };
            }
        });

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
                }
            }

            engine.Stream.Dispatch(packet);
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
        report.AppendLine("Hits by the user on players:");
        Unusual.ForEach(u => report.AppendLine(u));
        report.AppendLine();
        report.AppendLine("Opcodes (wire order: count):");
        foreach ((ushort op, int count) in opcodes.OrderByDescending(kv => kv.Value))
        {
            report.AppendLine($"  {op & 0xFF:X2} {op >> 8:X2}: {count:N0}");
        }

        File.WriteAllText(path + ".report.txt", report.ToString());
    }

    private static readonly List<string> Unusual = new();

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
