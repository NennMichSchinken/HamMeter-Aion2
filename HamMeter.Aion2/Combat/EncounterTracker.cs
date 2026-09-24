namespace HamMeter.Combat;

// A player as the parser resolved it at the time of the event.
public readonly record struct PlayerRef(int Id, string Name, string Job, bool IsUser);

// Turns parsed combat events into HamMeter encounters: a current fight, a history of
// finished fights and a combined "Overall". Packets arrive on the capture thread while
// the overlay reads on the render thread, so all state is guarded by one lock.
//
// A fight ends after CombatTimeoutSeconds without damage, so pulls in quick succession
// (farming) stay one fight. Its clock only runs while enemies are engaged: once every
// enemy that was hit or hit back has died, the clock pauses until the next pull, so
// walking between mobs does not lower DPS.
//
// Bosses (monster list) always get a fight of their own: the first hit on a boss closes
// a running trash fight, and the fight ends as soon as its last boss dies.
//
// "ours" marks the user (and later the party). Only they start and keep fights going;
// players who are merely nearby count only on enemies we fight as well.
public sealed class EncounterTracker
{
    private const int MaxHistory = 50;

    private readonly Lock m_sync = new();
    private readonly List<EncounterSnapshot> m_past = new();

    private Encounter? m_current;
    private EncounterSnapshot? m_lastFinished;
    private EncounterSnapshot? m_overall;
    private int m_overallCount = -1;

    // Seconds without any damage (dealt or taken) before a fight counts as over.
    public double CombatTimeoutSeconds { get; set; } = 30;

    public void DamageDone(DateTime time, PlayerRef source, long amount, int targetId, string targetName, bool targetIsBoss = false, bool ours = true)
    {
        EncounterSnapshot? finished = null;
        lock (m_sync)
        {
            if (!ours)
            {
                // Someone nearby: only on an enemy of the running fight.
                if (m_current is { Active: true } fight && fight.IsOurTarget(targetId))
                {
                    fight.Get(source).Damage += amount;
                    fight.AddTargetDamage(targetId, targetName, amount);
                }

                return;
            }

            // Walking from the trash to the boss must not merge the two fights.
            if (targetIsBoss && m_current is { Active: true, HasBoss: false } trash)
            {
                finished = this.Finish(trash);
                m_current = null;
            }

            Encounter enc = this.BeginOrContinue(time);
            enc.Get(source).Damage += amount;
            enc.AddTargetDamage(targetId, targetName, amount);
            enc.Engage(targetId, time);
            enc.AddOurTarget(targetId);
            if (targetIsBoss)
            {
                enc.AddBoss(targetId);
            }
        }

        this.Raise(finished);
    }

    // attackerId: the enemy that hit, when known (keeps the clock running while it lives).
    public void DamageTaken(DateTime time, PlayerRef target, long amount, int? attackerId = null, bool ours = true)
    {
        lock (m_sync)
        {
            if (!ours)
            {
                return;
            }

            Encounter enc = this.BeginOrContinue(time);
            if (attackerId is int attacker)
            {
                enc.AddOurTarget(attacker);
            }

            enc.Get(target).DamageTaken += amount;
            enc.Engage(attackerId, time);
        }
    }

    // A non-player entity died. When it was the last engaged enemy the clock pauses; when
    // it was the last boss of a boss fight, the fight is over.
    public void EnemyDied(DateTime time, int entityId)
    {
        EncounterSnapshot? finished = null;
        lock (m_sync)
        {
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

    // Healing never starts a fight on its own (regen and top-ups between pulls). Players
    // nearby only show up when they heal us, or were already in the fight.
    public void Healing(DateTime time, PlayerRef healer, PlayerRef? target, long amount, bool healerOurs = true, bool targetOurs = true)
    {
        lock (m_sync)
        {
            if (m_current is not { Active: true } enc || (!healerOurs && !targetOurs))
            {
                if (m_current is { Active: true } fight && fight.Has(healer.Id) && target is { } known && fight.Has(known.Id))
                {
                    fight.Get(healer).Healed += amount;
                    fight.Get(known).HealingTaken += amount;
                }

                return;
            }

            enc.Get(healer).Healed += amount;
            if (target is { } t && (targetOurs || enc.Has(t.Id)))
            {
                enc.Get(t).HealingTaken += amount;
            }
        }
    }

    public void Death(DateTime time, PlayerRef player, bool ours = true)
    {
        lock (m_sync)
        {
            if (m_current is not { Active: true } enc || (!ours && !enc.Has(player.Id)))
            {
                return;
            }

            enc.Get(player).Deaths++;
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
                && (now - enc.LastCombat).TotalSeconds > this.CombatTimeoutSeconds)
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

    public EncounterSnapshot? Current
    {
        get
        {
            lock (m_sync)
            {
                return m_current?.Active == true ? m_current.Snapshot(DateTime.Now) : m_lastFinished;
            }
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

        // Enemies we hit or that hit us; nearby players only count on these.
        private readonly HashSet<int> m_ourTargets = new();

        public void AddOurTarget(int entityId) => m_ourTargets.Add(entityId);

        public bool IsOurTarget(int entityId) => m_ourTargets.Contains(entityId);

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

        public void Engage(int? enemyId, DateTime time)
        {
            if (enemyId is int id)
            {
                m_engaged.Add(id);
            }

            m_runningSince ??= time;
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
