namespace HamMeter.Combat;

// A player as the parser resolved it at the time of the event.
public readonly record struct PlayerRef(int Id, string Name, string Job, bool IsUser);

// Turns parsed combat events into HamMeter encounters: a current fight, a history of
// finished fights and a combined "Overall". Packets arrive on the capture thread while
// the overlay reads on the render thread, so all state is guarded by one lock.
//
// Like combat in WoW: a fight lasts while enemies are engaged (hit by us or hitting us).
// Its clock pauses the moment the last of them dies, and the fight ends
// FightEndSeconds later unless the next pull comes first, so every pack is a fight of its
// own and walking never counts as fight time. An enemy nobody fought for QuietSeconds
// leaves the fight as if it died (adds that explode or despawn send no death). As a
// safety net, a fight also ends after IdleTimeoutSeconds without any damage.
//
// Bosses (monster list) always get a fight of their own: the first hit on a boss closes
// a running trash fight, and the fight ends as soon as its last boss dies.
//
// Only the user and the party are passed in (the parser drops everyone else), so players
// who are merely nearby never show up. "sure = false" marks a player who may be one of
// ours (no name yet while party members are unaccounted for): they count only on enemies
// of the running fight and never start a fight of their own.
//
// A dead enemy stays dead: its damage-over-time ticks (ours on it, its own on us) still
// count, but never bring it back into the fight and never start a fight of their own.
// A direct hit long after the death means a new monster got the same entity id.
public sealed class EncounterTracker
{
    private const int MaxHistory = 50;

    // A direct hit this soon after the death is a straggler, later it is a new monster.
    private const double RecentlyDeadSeconds = 10;

    // How long a death is remembered (longer than any damage-over-time lasts).
    private const double DeadMemorySeconds = 120;

    // Seconds without any hit on or from an enemy before it leaves the fight. Bosses stay.
    private const double QuietSeconds = 10;

    private readonly Lock m_sync = new();
    private readonly List<EncounterSnapshot> m_past = new();
    private readonly Dictionary<int, DateTime> m_dead = new();

    private Encounter? m_current;
    private EncounterSnapshot? m_lastFinished;
    private EncounterSnapshot? m_overall;
    private int m_overallCount = -1;
    private bool m_overallBosses;
    private int m_generation;

    // Seconds after the last engaged enemy died before the fight counts as over.
    public double FightEndSeconds { get; set; } = 5;

    // Seconds without any damage (dealt or taken) before a fight ends regardless.
    public double IdleTimeoutSeconds { get; set; } = 30;

    // tick: a damage-over-time tick (05 38) rather than a direct hit. skill and detail feed
    // the skill details (0 / none when unknown).
    public void DamageDone(DateTime time, PlayerRef source, long amount, int targetId, string targetName, bool targetIsBoss = false, bool tick = false, bool sure = true, int skill = 0, HitDetail detail = default)
    {
        EncounterSnapshot? finished = null;
        lock (m_sync)
        {
            if (!sure)
            {
                if (m_current is { Active: true } fight && fight.IsEnemy(targetId))
                {
                    fight.Get(source).Add(time - fight.Start, amount, skill, detail, heal: false, tick);
                    fight.AddTargetDamage(targetId, targetName, amount);
                    fight.Touch(targetId, time);
                }

                return;
            }

            bool dead = this.IsDead(targetId, time, tick);
            if (dead && m_current is not { Active: true })
            {
                return;
            }

            // Walking from the trash to the boss must not merge the two fights.
            if (targetIsBoss && !dead && m_current is { Active: true, HasBoss: false } trash)
            {
                finished = this.Finish(trash);
                m_current = null;
            }

            Encounter enc = this.BeginOrContinue(time);
            enc.Get(source).Add(time - enc.Start, amount, skill, detail, heal: false, tick);
            enc.AddTargetDamage(targetId, targetName, amount);
            if (!dead)
            {
                enc.Engage(targetId, time);
                if (targetIsBoss)
                {
                    enc.AddBoss(targetId);
                }
            }
        }

        this.Raise(finished);
    }

    // A hit of this skill that did no damage (missed or resisted): counts on the skill only,
    // in a running fight.
    public void Missed(PlayerRef source, int skill)
    {
        lock (m_sync)
        {
            if (m_current is { Active: true } enc && enc.Has(source.Id))
            {
                enc.Get(source).Miss(skill);
            }
        }
    }

    // The HP an enemy has left (00 8D). Only bosses of the running fight are kept, for the chart.
    public void EnemyHp(DateTime time, int entityId, long hp)
    {
        lock (m_sync)
        {
            if (m_current is { Active: true } enc)
            {
                enc.BossHp(time, entityId, hp);
            }
        }
    }

    // attackerId: the enemy that hit, when known (keeps the clock running while it lives).
    // detail: whether the hit was blocked or parried (direct hits only).
    public void DamageTaken(DateTime time, PlayerRef target, long amount, int? attackerId = null, bool tick = false, bool sure = true, HitDetail detail = default)
    {
        lock (m_sync)
        {
            if (!sure)
            {
                if (m_current is { Active: true } fight && attackerId is int enemy && fight.IsEnemy(enemy))
                {
                    fight.Get(target).Taken(amount, tick, detail);
                    fight.Touch(enemy, time);
                }

                return;
            }

            bool dead = attackerId is int attacker && this.IsDead(attacker, time, tick);
            if (dead && m_current is not { Active: true })
            {
                return;
            }

            Encounter enc = this.BeginOrContinue(time);
            enc.Get(target).Taken(amount, tick, detail);
            if (!dead)
            {
                enc.Engage(attackerId, time);
            }
        }
    }

    // Whether an action on or by this enemy comes after its death: any tick, or a direct
    // hit right after it. A direct hit later on is a new monster with the same id.
    private bool IsDead(int enemyId, DateTime time, bool tick)
    {
        if (!m_dead.TryGetValue(enemyId, out DateTime died))
        {
            return false;
        }

        double since = (time - died).TotalSeconds;
        if (since < RecentlyDeadSeconds || (tick && since < DeadMemorySeconds))
        {
            return true;
        }

        if (!tick)
        {
            m_dead.Remove(enemyId);
        }

        return false;
    }

    // A non-player entity died. When it was the last engaged enemy the clock pauses; when
    // it was the last boss of a boss fight, the fight is over.
    public void EnemyDied(DateTime time, int entityId)
    {
        EncounterSnapshot? finished = null;
        lock (m_sync)
        {
            if (m_dead.Count >= 512)
            {
                foreach (int old in m_dead.Where(d => (time - d.Value).TotalSeconds >= DeadMemorySeconds).Select(d => d.Key).ToList())
                {
                    m_dead.Remove(old);
                }
            }

            m_dead[entityId] = time;
            if (m_current is { Active: true } enc)
            {
                enc.Disengage(entityId, time);
                if (enc.BossDied(entityId))
                {
                    finished = this.Finish(enc);
                }
            }
        }

        this.Raise(finished);
    }

    // Healing never starts a fight on its own (regen and top-ups between pulls). healer and
    // target are null when they are not one of ours: a stranger's heal on us still counts
    // as healing taken, without the stranger showing up. Someone only maybe ours counts
    // once they are in the fight.
    public void Healing(DateTime time, PlayerRef? healer, PlayerRef? target, long amount, bool healerSure = true, bool targetSure = true, int skill = 0, bool crit = false, bool tick = false)
    {
        lock (m_sync)
        {
            if (m_current is not { Active: true } enc)
            {
                return;
            }

            if (healer is { } h && (healerSure || enc.Has(h.Id)))
            {
                enc.Get(h).Add(time - enc.Start, amount, skill, new HitDetail { Crit = crit }, heal: true, tick);
            }

            if (target is { } t && (targetSure || enc.Has(t.Id)))
            {
                enc.Get(t).HealingTaken += amount;
            }
        }
    }

    public void Death(DateTime time, PlayerRef player, bool sure = true)
    {
        lock (m_sync)
        {
            if (m_current is { Active: true } enc && (sure || enc.Has(player.Id)))
            {
                enc.Get(player).Deaths++;
            }
        }
    }

    // A summon's owner became known after it already dealt damage: fold it in.
    public void MergeSummon(int summonId, PlayerRef owner)
    {
        lock (m_sync)
        {
            m_current?.Merge(summonId, owner);
        }
    }

    // Raised (outside the lock) when a fight ends.
    public event Action<EncounterSnapshot>? Finished;

    // Called every frame: finishes the current fight once combat went quiet.
    public void Tick(DateTime now)
    {
        EncounterSnapshot? finished = null;
        lock (m_sync)
        {
            m_current?.DropQuiet(now, QuietSeconds);
            if (m_current is { Active: true } enc
                && ((enc.AllDeadSince is { } dead && (now - dead).TotalSeconds > this.FightEndSeconds)
                    || (now - enc.LastCombat).TotalSeconds > this.IdleTimeoutSeconds))
            {
                finished = this.Finish(enc);
            }
        }

        this.Raise(finished);
    }

    private void Raise(EncounterSnapshot? finished)
    {
        if (finished is not null)
        {
            this.Finished?.Invoke(finished);
        }
    }

    public EncounterSnapshot? Current => this.CurrentAt(DateTime.Now);

    // The current fight with skills and the per-second values (the details view); costs
    // more than Current, so only while the details are shown.
    public EncounterSnapshot? CurrentDetailed
    {
        get
        {
            lock (m_sync)
            {
                return m_current?.Active == true ? m_current.Snapshot(DateTime.Now, details: true) : m_lastFinished;
            }
        }
    }

    // Enemies that keep the current fight's clock running (for --replay).
    internal int[] Engaged()
    {
        lock (m_sync)
        {
            return m_current is { Active: true } enc ? enc.EngagedIds() : [];
        }
    }

    // The current fight as of a given time (a replay runs in recorded time).
    public EncounterSnapshot? CurrentAt(DateTime now)
    {
        lock (m_sync)
        {
            return m_current?.Active == true ? m_current.Snapshot(now, details: false) : m_lastFinished;
        }
    }

    public bool InCombat
    {
        get
        {
            lock (m_sync)
            {
                return m_current?.Active == true;
            }
        }
    }

    public List<EncounterSnapshot> SnapshotPast()
    {
        lock (m_sync)
        {
            return new List<EncounterSnapshot>(m_past);
        }
    }

    public EncounterSnapshot? GetPast(int index)
    {
        lock (m_sync)
        {
            return index >= 0 && index < m_past.Count ? m_past[index] : null;
        }
    }

    public int PastCount
    {
        get
        {
            lock (m_sync)
            {
                return m_past.Count;
            }
        }
    }

    // bossesOnly: only the boss fights, the numbers people compare (the default setting).
    // Null when there is nothing to sum yet.
    public EncounterSnapshot? GetOverall(bool bossesOnly = false)
    {
        lock (m_sync)
        {
            if (m_past.Count == 0)
            {
                return bossesOnly ? null : this.Current;
            }

            if (m_overall is null || m_overallCount != m_past.Count || m_overallBosses != bossesOnly)
            {
                List<EncounterSnapshot> fights = bossesOnly ? m_past.Where(f => f.IsBoss).ToList() : m_past;
                m_overall = fights.Count == 0 ? null : EncounterSnapshot.BuildOverall(fights, bossesOnly ? "Overall (bosses)" : "Overall");
                m_overallCount = m_past.Count;
                m_overallBosses = bossesOnly;
            }

            return m_overall;
        }
    }

    // Counts the clears, so a view into the history notices one it did not start itself
    // (a new dungeon run clears on the capture thread).
    public int Generation => Volatile.Read(ref m_generation);

    public void Clear()
    {
        lock (m_sync)
        {
            m_generation++;
            m_current = null;
            m_lastFinished = null;
            m_past.Clear();
            m_dead.Clear();
            m_overall = null;
            m_overallCount = -1;
        }
    }

    private Encounter BeginOrContinue(DateTime time)
    {
        if (m_current is { Active: true } enc)
        {
            return enc;
        }

        m_current = new Encounter(time);
        return m_current;
    }

    private EncounterSnapshot Finish(Encounter enc)
    {
        enc.Active = false;
        enc.Pause(enc.LastCombat);
        EncounterSnapshot snap = enc.Snapshot(enc.LastCombat, details: true);
        m_lastFinished = snap;
        if (snap.Combatants.Any(c => c.DamageTotal > 0))
        {
            m_past.Add(snap);
            while (m_past.Count > MaxHistory)
            {
                m_past.RemoveAt(0);
            }
        }

        return snap;
    }

    private sealed class Totals
    {
        // The chart covers an hour (the longest dungeon run); later seconds add to the last.
        private const int MaxSeconds = 3600;

        private readonly Dictionary<(int Skill, bool Heal), Skill> m_skills = new();
        private readonly List<float> m_damage = new();
        private readonly List<float> m_heal = new();

        public PlayerRef Player;
        public long Damage;
        public long DamageTaken;
        public long Healed;
        public long HealingTaken;
        public int Deaths;
        public int HitsTaken;
        public int Blocks;
        public int Parries;

        public void Add(TimeSpan at, long amount, int skill, HitDetail d, bool heal, bool tick)
        {
            if (heal)
            {
                this.Healed += amount;
            }
            else
            {
                this.Damage += amount;
            }

            List<float> line = heal ? m_heal : m_damage;
            int second = Math.Clamp((int)at.TotalSeconds, 0, MaxSeconds - 1);
            while (line.Count <= second)
            {
                line.Add(0f);
            }

            line[second] += amount;

            Skill s = this.SkillOf(skill, heal);
            s.Amount += amount;
            s.MaxHit = Math.Max(s.MaxHit, amount);
            if (tick)
            {
                s.Ticks++;
                return;
            }

            s.Hits++;
            s.Crits += d.Crit ? 1 : 0;
            s.Backs += d.Back ? 1 : 0;
            s.Fronts += d.Front ? 1 : 0;
            s.Doubles += d.Double ? 1 : 0;
            s.Perfects += d.Perfect ? 1 : 0;
            s.Multis += d.Multi ? 1 : 0;
        }

        public void Miss(int skill)
        {
            Skill s = this.SkillOf(skill, heal: false);
            s.Hits++;
            s.Misses++;
        }

        public void Taken(long amount, bool tick, HitDetail d)
        {
            this.DamageTaken += amount;
            if (!tick)
            {
                this.HitsTaken++;
                this.Blocks += d.Block ? 1 : 0;
                this.Parries += d.Parry ? 1 : 0;
            }
        }

        // A summon's numbers, folded into its owner.
        public void Absorb(Totals summon)
        {
            this.Damage += summon.Damage;
            this.Healed += summon.Healed;
            foreach ((var key, Skill s) in summon.m_skills)
            {
                this.SkillOf(key.Skill, key.Heal).Absorb(s);
            }

            AddLine(m_damage, summon.m_damage);
            AddLine(m_heal, summon.m_heal);
        }

        public List<SkillTotals> Skills() => m_skills
            .Select(kv => new SkillTotals(kv.Key.Skill, kv.Key.Heal, kv.Value.Amount, kv.Value.Hits, kv.Value.MaxHit, kv.Value.Crits, kv.Value.Backs, kv.Value.Fronts, kv.Value.Doubles, kv.Value.Perfects, kv.Value.Multis, kv.Value.Misses, kv.Value.Ticks))
            .ToList();

        public float[] DamageLine() => m_damage.ToArray();

        public float[] HealLine() => m_heal.ToArray();

        private Skill SkillOf(int skill, bool heal)
        {
            if (!m_skills.TryGetValue((skill, heal), out Skill? s))
            {
                m_skills[(skill, heal)] = s = new Skill();
            }

            return s;
        }

        private static void AddLine(List<float> into, List<float> from)
        {
            while (into.Count < from.Count)
            {
                into.Add(0f);
            }

            for (int i = 0; i < from.Count; i++)
            {
                into[i] += from[i];
            }
        }

        private sealed class Skill
        {
            public long Amount;
            public int Hits;
            public long MaxHit;
            public int Crits;
            public int Backs;
            public int Fronts;
            public int Doubles;
            public int Perfects;
            public int Multis;
            public int Misses;
            public int Ticks;

            public void Absorb(Skill o)
            {
                this.Amount += o.Amount;
                this.Hits += o.Hits;
                this.MaxHit = Math.Max(this.MaxHit, o.MaxHit);
                this.Crits += o.Crits;
                this.Backs += o.Backs;
                this.Fronts += o.Fronts;
                this.Doubles += o.Doubles;
                this.Perfects += o.Perfects;
                this.Multis += o.Multis;
                this.Misses += o.Misses;
                this.Ticks += o.Ticks;
            }
        }
    }

    private sealed class Encounter(DateTime start)
    {
        private readonly Dictionary<int, Totals> m_players = new();
        private readonly Dictionary<int, (string Name, long Damage)> m_targets = new();
        private readonly Dictionary<int, DateTime> m_engaged = new(); // enemy -> last hit on or from it
        private readonly HashSet<int> m_enemies = new();              // every enemy of this fight
        private readonly HashSet<int> m_bosses = new();
        private readonly HashSet<int> m_deadBosses = new();
        private readonly Dictionary<int, (long Full, List<float> Left)> m_bossHp = new(); // per second
        private double m_pausedSeconds;
        private DateTime? m_runningSince = start;

        // When the fight began: second 0 of its chart.
        public DateTime Start { get; } = start;

        public DateTime LastCombat { get; private set; } = start;
        public bool Active { get; set; } = true;

        public bool HasBoss => m_bosses.Count > 0;

        public int[] EngagedIds() => m_engaged.Keys.ToArray();

        public bool IsEnemy(int entityId) => m_enemies.Contains(entityId);

        public bool Has(int playerId) => m_players.ContainsKey(playerId);

        public void AddBoss(int entityId) => m_bosses.Add(entityId);

        // True when this was the last living boss of the fight.
        public bool BossDied(int entityId)
        {
            if (!m_bosses.Contains(entityId))
            {
                return false;
            }

            m_deadBosses.Add(entityId);
            return m_deadBosses.Count == m_bosses.Count;
        }

        // Combat time: running while enemies are engaged, paused in between pulls.
        public double Seconds(DateTime now) =>
            m_pausedSeconds + (m_runningSince is { } since ? Math.Max(0, (now - since).TotalSeconds) : 0);

        // Set while every engaged enemy is dead: the fight ends a little later.
        public DateTime? AllDeadSince { get; private set; }

        // The tracker never engages a dead enemy again: it would never die a second time and
        // the clock would run on.
        public void Engage(int? enemyId, DateTime time)
        {
            if (enemyId is int id)
            {
                m_engaged[id] = m_engaged.TryGetValue(id, out DateTime last) && last > time ? last : time;
                m_enemies.Add(id);
            }

            m_runningSince ??= time;
            this.AllDeadSince = null;
            if (time > this.LastCombat)
            {
                this.LastCombat = time;
            }
        }

        // Someone who may be one of ours fights an engaged enemy: it is not quiet.
        public void Touch(int enemyId, DateTime time)
        {
            if (m_engaged.ContainsKey(enemyId))
            {
                this.Engage(enemyId, time);
            }
        }

        // Enemies nobody hit and that hit nobody for `seconds` leave the fight; when that was
        // the last one, the clock stops at the last action and the fight ends.
        public void DropQuiet(DateTime now, double seconds)
        {
            if (!this.Active || m_engaged.Count == 0)
            {
                return;
            }

            List<int>? quiet = null;
            foreach ((int id, DateTime last) in m_engaged)
            {
                if ((now - last).TotalSeconds > seconds && !m_bosses.Contains(id))
                {
                    (quiet ??= new()).Add(id);
                }
            }

            if (quiet is null)
            {
                return;
            }

            quiet.ForEach(id => m_engaged.Remove(id));
            if (m_engaged.Count == 0)
            {
                this.Pause(this.LastCombat);
                this.AllDeadSince = this.LastCombat;
            }
        }

        public void Disengage(int enemyId, DateTime time)
        {
            if (m_engaged.Remove(enemyId) && m_engaged.Count == 0)
            {
                this.Pause(time);
                this.AllDeadSince = time;
            }
        }

        public void Pause(DateTime time)
        {
            if (m_runningSince is { } since)
            {
                m_pausedSeconds += Math.Max(0, (time - since).TotalSeconds);
                m_runningSince = null;
            }
        }

        public Totals Get(PlayerRef p)
        {
            if (!m_players.TryGetValue(p.Id, out Totals? t))
            {
                t = new Totals();
                m_players[p.Id] = t;
            }

            // Names arrive late (only on teleport/zone entry), so keep the newest one.
            t.Player = p with { Job = string.IsNullOrEmpty(p.Job) ? t.Player.Job ?? string.Empty : p.Job };
            return t;
        }

        public void AddTargetDamage(int targetId, string name, long amount)
        {
            m_targets.TryGetValue(targetId, out var e);
            m_targets[targetId] = (string.IsNullOrEmpty(name) ? e.Name : name, e.Damage + amount);
        }

        public void Merge(int summonId, PlayerRef owner)
        {
            if (!m_players.Remove(summonId, out Totals? s))
            {
                return;
            }

            this.Get(owner).Absorb(s);
        }

        // A boss's HP left, as a fraction of the most it was seen with (HamMeter may start
        // after the pull), kept per second; seconds without news repeat the last value.
        public void BossHp(DateTime time, int entityId, long hp)
        {
            if (!m_bosses.Contains(entityId) || hp < 0)
            {
                return;
            }

            if (!m_bossHp.TryGetValue(entityId, out var b))
            {
                b = (hp, new List<float>());
            }

            long full = Math.Max(b.Full, hp);
            int second = Math.Clamp((int)(time - this.Start).TotalSeconds, 0, 3599);
            float last = b.Left.Count > 0 ? b.Left[^1] : 1f;
            while (b.Left.Count < second)
            {
                b.Left.Add(last);
            }

            float left = full > 0 ? (float)hp / full : 0f;
            if (b.Left.Count == second)
            {
                b.Left.Add(left);
            }
            else
            {
                b.Left[second] = left;
            }

            m_bossHp[entityId] = (full, b.Left);
        }

        public EncounterSnapshot Snapshot(DateTime now, bool details)
        {
            double seconds = this.Seconds(now);
            double rate = Math.Max(1, seconds);

            // A boss names the fight, otherwise the target that took the most damage.
            var named = m_targets.Where(t => !string.IsNullOrEmpty(t.Value.Name)).ToList();
            var bosses = named.Where(t => m_bosses.Contains(t.Key)).ToList();
            string title = (bosses.Count > 0 ? bosses : named)
                .OrderByDescending(t => t.Value.Damage)
                .Select(t => t.Value.Name)
                .FirstOrDefault() ?? string.Empty;

            // The chart shows the boss that took the most damage.
            IReadOnlyList<float> bossHp = [];
            if (details && m_bossHp.Count > 0)
            {
                int main = m_bossHp.Keys.OrderByDescending(id => m_targets.TryGetValue(id, out var t) ? t.Damage : 0).First();
                bossHp = m_bossHp[main].Left.ToArray();
            }

            return new EncounterSnapshot
            {
                Title = string.IsNullOrEmpty(title) ? "Encounter" : title,
                IsBoss = this.HasBoss,
                Seconds = seconds,
                Active = this.Active,
                BossHp = bossHp,
                Combatants = m_players.Values.Select(t => new Combatant
                {
                    Id = t.Player.Id,
                    Name = t.Player.Name,
                    Job = t.Player.Job,
                    IsUser = t.Player.IsUser,
                    DamageTotal = t.Damage,
                    DamageTaken = t.DamageTaken,
                    HealedTotal = t.Healed,
                    HealingTaken = t.HealingTaken,
                    DeathCount = t.Deaths,
                    Dps = (float)(t.Damage / rate),
                    Hps = (float)(t.Healed / rate),
                    HitsTaken = t.HitsTaken,
                    Blocks = t.Blocks,
                    Parries = t.Parries,
                    Skills = details ? t.Skills() : [],
                    DamagePerSecond = details ? t.DamageLine() : [],
                    HealPerSecond = details ? t.HealLine() : [],
                }).ToList(),
            };
        }
    }
}
