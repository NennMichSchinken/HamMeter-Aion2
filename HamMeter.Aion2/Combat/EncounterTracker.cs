namespace HamMeter.Combat;

// A player as the parser resolved it at the time of the event.
public readonly record struct PlayerRef(int Id, string Name, string Job, bool IsUser);

// Turns parsed combat events into HamMeter encounters: a current fight, a history of
// finished fights and a combined "Overall". Packets arrive on the capture thread while
// the overlay reads on the render thread, so all state is guarded by one lock.
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
    public double CombatTimeoutSeconds { get; set; } = 10;

    public void DamageDone(DateTime time, PlayerRef source, long amount, int targetId, string targetName)
    {
        lock (m_sync)
        {
            Encounter enc = this.BeginOrContinue(time);
            enc.Get(source).Damage += amount;
            enc.AddTargetDamage(targetId, targetName, amount);
            enc.LastCombat = time;
        }
    }

    public void DamageTaken(DateTime time, PlayerRef target, long amount)
    {
        lock (m_sync)
        {
            Encounter enc = this.BeginOrContinue(time);
            enc.Get(target).DamageTaken += amount;
            enc.LastCombat = time;
        }
    }

    // Healing never starts a fight on its own (regen and top-ups between pulls).
    public void Healing(DateTime time, PlayerRef healer, PlayerRef? target, long amount)
    {
        lock (m_sync)
        {
            if (m_current is not { Active: true } enc)
            {
                return;
            }

            enc.Get(healer).Healed += amount;
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
            if (m_current is not { Active: true } enc)
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

    // Called every frame: finishes the current fight once combat went quiet.
    public void Tick(DateTime now)
    {
        lock (m_sync)
        {
            if (m_current is { Active: true } enc
                && (now - enc.LastCombat).TotalSeconds > this.CombatTimeoutSeconds)
            {
                this.Finish(enc);
            }
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

    private void Finish(Encounter enc)
    {
        enc.Active = false;
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

        public DateTime Start { get; } = start;
        public DateTime LastCombat { get; set; } = start;
        public bool Active { get; set; } = true;

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
            double seconds = Math.Max(0, ((this.Active ? now : this.LastCombat) - this.Start).TotalSeconds);
            double rate = Math.Max(1, seconds);

            string title = m_targets.Count == 0
                ? "Encounter"
                : m_targets.Values.MaxBy(t => t.Damage).Name;

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
