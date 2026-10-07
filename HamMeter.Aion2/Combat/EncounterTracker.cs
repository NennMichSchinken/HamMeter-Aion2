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
// own and walking never counts as fight time. As a safety net for a missing death, a
// fight also ends after IdleTimeoutSeconds without any damage.
//
// Bosses (monster list) always get a fight of their own: the first hit on a boss closes
// a running trash fight, and the fight ends as soon as its last boss dies.
//
// Only the user and the party are passed in (the parser drops everyone else), so players
// who are merely nearby never show up.
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

    private readonly Lock m_sync = new();
    private readonly List<EncounterSnapshot> m_past = new();
    private readonly Dictionary<int, DateTime> m_dead = new();

    private Encounter? m_current;
    private EncounterSnapshot? m_lastFinished;
    private EncounterSnapshot? m_overall;
    private int m_overallCount = -1;

    // Seconds after the last engaged enemy died before the fight counts as over.
    public double FightEndSeconds { get; set; } = 5;

    // Seconds without any damage (dealt or taken) before a fight ends regardless.
    public double IdleTimeoutSeconds { get; set; } = 30;

    // tick: a damage-over-time tick (05 38) rather than a direct hit.
    public void DamageDone(DateTime time, PlayerRef source, long amount, int targetId, string targetName, bool targetIsBoss = false, bool tick = false)
    {
        EncounterSnapshot? finished = null;
        lock (m_sync)
        {
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
            enc.Get(source).Damage += amount;
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

    // attackerId: the enemy that hit, when known (keeps the clock running while it lives).
    public void DamageTaken(DateTime time, PlayerRef target, long amount, int? attackerId = null, bool tick = false)
    {
        lock (m_sync)
        {
            bool dead = attackerId is int attacker && this.IsDead(attacker, time, tick);
            if (dead && m_current is not { Active: true })
            {
                return;
            }

            Encounter enc = this.BeginOrContinue(time);
            enc.Get(target).DamageTaken += amount;
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
    // as healing taken, without the stranger showing up.
    public void Healing(DateTime time, PlayerRef? healer, PlayerRef? target, long amount)
    {
        lock (m_sync)
        {
            if (m_current is not { Active: true } enc)
            {
                return;
            }

            if (healer is { } h)
            {
                enc.Get(h).Healed += amount;
            }

            if (target is { } t)
            {
                enc.Get(t).HealingTaken += amount;
            }
        }
    }

    public void Death(DateTime time, PlayerRef player)
    {
        lock (m_sync)
        {
            if (m_current is { Active: true } enc)
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
            return m_current?.Active == true ? m_current.Snapshot(now) : m_lastFinished;
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

    public EncounterSnapshot? GetOverall()
    {
        lock (m_sync)
        {
            if (m_past.Count == 0)
            {
                return this.Current;
            }

            if (m_overall is null || m_overallCount != m_past.Count)
            {
                m_overall = EncounterSnapshot.BuildOverall(m_past);
                m_overallCount = m_past.Count;
            }

            return m_overall;
        }
    }

    public void Clear()
    {
        lock (m_sync)
        {
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
        EncounterSnapshot snap = enc.Snapshot(enc.LastCombat);
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
        public PlayerRef Player;
        public long Damage;
        public long DamageTaken;
        public long Healed;
        public long HealingTaken;
        public int Deaths;
    }

    private sealed class Encounter(DateTime start)
    {
        private readonly Dictionary<int, Totals> m_players = new();
        private readonly Dictionary<int, (string Name, long Damage)> m_targets = new();
        private readonly HashSet<int> m_engaged = new();
        private readonly HashSet<int> m_bosses = new();
        private readonly HashSet<int> m_deadBosses = new();
        private double m_pausedSeconds;
        private DateTime? m_runningSince = start;

        public DateTime LastCombat { get; private set; } = start;
        public bool Active { get; set; } = true;

        public bool HasBoss => m_bosses.Count > 0;

        public int[] EngagedIds() => m_engaged.ToArray();

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
                m_engaged.Add(id);
            }

            m_runningSince ??= time;
            this.AllDeadSince = null;
            if (time > this.LastCombat)
            {
                this.LastCombat = time;
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

            Totals o = this.Get(owner);
            o.Damage += s.Damage;
            o.Healed += s.Healed;
        }

        public EncounterSnapshot Snapshot(DateTime now)
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

            return new EncounterSnapshot
            {
                Title = string.IsNullOrEmpty(title) ? "Encounter" : title,
                IsBoss = this.HasBoss,
                Seconds = seconds,
                Active = this.Active,
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
                }).ToList(),
            };
        }
    }
}
