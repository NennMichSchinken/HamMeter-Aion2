using System.Globalization;
using System.Numerics;
using HamMeter.Combat;
using HamMeter.Game;
using ImGuiNET;

namespace HamMeter.UI;

// The skill details of one player in the fight the meter shows: hit rates, the skills and
// a chart of DPS or healing over the fight, with the party and the boss's HP. Opened from
// the "Details" button of an expanded bar; a click on another bar or on a name at the top
// switches the player. Everything scales with the bar names' text size: the body text is
// as big as the names on the bars.
public sealed class DetailWindow
{
    // Sizes at a body text of 14 px; multiplied by the scale (see S).
    private const float BaseText = 14f;
    private const float Width = 640f;
    private const float RowH = 26f;
    private const int VisibleRows = 6;
    private const float ChartH = 150f;

    private static readonly Vector4 PlotBg = new(0.106f, 0.106f, 0.129f, 1f);
    private static readonly Vector4 BossLine = new(1f, 0.271f, 0.227f, 0.85f);

    private readonly Config m_config;
    private readonly Func<string, IntPtr> m_classIcon;
    private readonly Func<string, Vector4> m_classColor;

    private Vector2 m_anchorPos;
    private Vector2 m_anchorSize;
    private bool m_hasAnchor;
    private bool m_heal;
    private bool m_showParty = true;
    private float m_scale = 1f;

    // classColor: the bar colour of a class, as the meter draws it.
    public DetailWindow(Config config, Func<string, IntPtr> classIcon, Func<string, Vector4> classColor)
    {
        m_config = config;
        m_classIcon = classIcon;
        m_classColor = classColor;
    }

    public bool Visible { get; private set; }

    public int PlayerId { get; private set; }

    public void Show(int playerId)
    {
        this.PlayerId = playerId;
        this.Visible = true;
    }

    public void Close() => this.Visible = false;

    // Where the meter is, so the window first opens beside it.
    public void SetAnchor(Vector2 pos, Vector2 size)
    {
        m_anchorPos = pos;
        m_anchorSize = size;
        m_hasAnchor = true;
    }

    public void Draw(EncounterSnapshot? fight)
    {
        // Waits for the meter's position, so the first opening lands beside it.
        if (!this.Visible || !m_hasAnchor || fight is null || fight.Combatants.Count == 0)
        {
            return;
        }

        m_scale = Math.Clamp(m_config.LeftTextSize / BaseText, 0.8f, 2f);
        Combatant me = fight.Combatants.FirstOrDefault(c => c.Id == this.PlayerId) ?? fight.Combatants.OrderByDescending(c => c.DamageTotal).First();
        this.PlayerId = me.Id;

        float width = this.S(Width);
        Vector2 disp = ImGui.GetIO().DisplaySize;
        float x = m_anchorPos.X + m_anchorSize.X + 8f;
        if (x + width + 32f > disp.X)
        {
            x = m_anchorPos.X - 8f - width - 32f;
        }

        ImGui.SetNextWindowPos(new Vector2(Math.Max(0f, x), Math.Max(0f, m_anchorPos.Y)), ImGuiCond.FirstUseEver);
        ImGui.PushStyleColor(ImGuiCol.WindowBg, Theme.WindowBg);
        ImGui.PushStyleColor(ImGuiCol.Border, Theme.Border);
        ImGui.PushStyleColor(ImGuiCol.ChildBg, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarBg, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrab, Theme.Border);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrabHovered, Theme.FrameHover);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrabActive, Theme.Accent);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 12f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(this.S(16f), this.S(14f)));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(0f, this.S(12f)));
        ImGui.PushStyleVar(ImGuiStyleVar.ScrollbarSize, 8f);
        ImGui.PushStyleVar(ImGuiStyleVar.ScrollbarRounding, 6f);

        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoCollapse
            | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;
        if (ImGui.Begin("Details###ham_details", flags))
        {
            this.DrawHeader(fight, me);
            this.DrawTabs(fight);
            this.DrawRates(SkillDetails.Rates(me));
            this.DrawSkills(me);
            this.DrawChart(fight, me);

            if (ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows) && ImGui.IsKeyPressed(ImGuiKey.Escape))
            {
                this.Close();
            }
        }

        ImGui.End();
        ImGui.PopStyleVar(6);
        ImGui.PopStyleColor(7);
    }

    // Class icon, name, the fight, the totals and the close button.
    private void DrawHeader(EncounterSnapshot fight, Combatant me)
    {
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        Vector2 p = ImGui.GetCursorScreenPos();
        float width = this.S(Width);
        float icon = this.S(38f);

        IntPtr tex = m_classIcon(me.Job);
        if (tex != IntPtr.Zero)
        {
            dl.AddImage(tex, p, p + new Vector2(icon, icon));
        }
        else
        {
            dl.AddRectFilled(p, p + new Vector2(icon, icon), Col(m_classColor(me.Job)), 8f);
        }

        float tx = p.X + icon + this.S(10f);
        float big = this.S(19f), small = this.S(14f);
        this.Text(dl, new Vector2(tx, p.Y - this.S(1f)), me.Name, big, Col(Theme.Text));
        string role = (ClassInfo.IsKnown(me.Job) ? ClassInfo.FullName(me.Job) : "Unknown class") + (me.IsUser ? " · you" : string.Empty);
        this.Text(dl, new Vector2(tx + this.TextW(me.Name, big) + this.S(8f), p.Y + this.S(3f)), role, small, Col(Theme.Muted));
        this.Text(dl, new Vector2(tx, p.Y + this.S(22f)), $"{fight.Title}{(fight.IsBoss ? " (boss)" : string.Empty)} · {fight.Duration}", small, Col(Theme.Muted));

        // Close button, top right.
        float cb = this.S(24f);
        Vector2 closeMin = new(p.X + width - cb, p.Y);
        ImGui.SetCursorScreenPos(closeMin);
        bool closeClicked = ImGui.InvisibleButton("##close", new Vector2(cb, cb));
        bool closeHovered = ImGui.IsItemHovered();
        if (closeHovered)
        {
            dl.AddRectFilled(closeMin, closeMin + new Vector2(cb, cb), Col(Theme.FrameHover), 6f);
        }

        Icons.Draw(dl, Icon.Close, closeMin + new Vector2(cb * 0.18f, cb * 0.18f), cb * 0.64f, Col(closeHovered ? Theme.Text : Theme.Muted));
        if (closeClicked)
        {
            this.Close();
        }

        // Totals, right-aligned before the close button.
        float right = p.X + width - cb - this.S(16f);
        float label = this.S(12f), value = this.S(19f);
        (string label, string value)[] stats = m_heal
            ? [("Healing", Fmt(me.HealedTotal)), ("HPS", Fmt(me.Hps))]
            : [("Damage", Fmt(me.DamageTotal)), ("DPS", Fmt(me.Dps))];
        for (int i = stats.Length - 1; i >= 0; i--)
        {
            float w = MathF.Max(this.TextW(stats[i].value, value), this.TextW(stats[i].label, label));
            this.Text(dl, new Vector2(right - this.TextW(stats[i].label, label), p.Y), stats[i].label, label, Col(Theme.Muted));
            this.Text(dl, new Vector2(right - this.TextW(stats[i].value, value), p.Y + this.S(16f)), stats[i].value, value, Col(Theme.Text));
            right -= w + this.S(22f);
        }

        ImGui.SetCursorScreenPos(p);
        ImGui.Dummy(new Vector2(width, icon));
    }

    // Every player of the fight; a click shows that player.
    private void DrawTabs(EncounterSnapshot fight)
    {
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        Vector2 start = ImGui.GetCursorScreenPos();
        Vector2 at = start;
        float width = this.S(Width);
        float h = this.S(30f), name = this.S(BaseText), amountSize = this.S(13f);
        IEnumerable<Combatant> players = fight.Combatants
            .Where(c => (m_heal ? c.HealedTotal : c.DamageTotal) > 0 || c.Id == this.PlayerId)
            .OrderByDescending(c => m_heal ? c.HealedTotal : c.DamageTotal);
        foreach (Combatant c in players)
        {
            string amount = Fmt(m_heal ? c.HealedTotal : c.DamageTotal);
            float w = this.S(26f) + this.TextW(c.Name, name) + this.S(6f) + this.TextW(amount, amountSize) + this.S(12f);
            if (at.X + w > start.X + width)
            {
                at = new Vector2(start.X, at.Y + h + this.S(6f));
            }

            ImGui.SetCursorScreenPos(at);
            if (ImGui.InvisibleButton($"##tab{c.Id}", new Vector2(w, h)))
            {
                this.PlayerId = c.Id;
            }

            bool selected = c.Id == this.PlayerId;
            bool hovered = ImGui.IsItemHovered();
            dl.AddRectFilled(at, at + new Vector2(w, h), Col(selected ? Theme.FrameActive : hovered ? Theme.Frame : Vector4.Zero), 7f);
            dl.AddRect(at, at + new Vector2(w, h), Col(selected ? Theme.Accent : Theme.Border), 7f, ImDrawFlags.RoundCornersAll, 1f);
            dl.AddCircleFilled(at + new Vector2(this.S(15f), h / 2f), this.S(4f), Col(m_classColor(c.Job)));
            float tx = at.X + this.S(26f);
            this.Text(dl, new Vector2(tx, at.Y + ((h - name) / 2f)), c.Name, name, Col(Theme.Text));
            this.Text(dl, new Vector2(tx + this.TextW(c.Name, name) + this.S(6f), at.Y + ((h - amountSize) / 2f)), amount, amountSize, Col(Theme.Muted));
            at.X += w + this.S(6f);
        }

        ImGui.SetCursorScreenPos(start);
        ImGui.Dummy(new Vector2(width, at.Y - start.Y + h));
    }

    // The hit rates as tiles, two rows of four.
    private void DrawRates(HitRates r)
    {
        (string, float?)[] tiles =
        [
            ("Crit", r.Crit), ("Back attack", r.Back), ("Front attack", r.Front), ("Double", r.Double),
            ("Perfect", r.Perfect), ("Missed", r.Missed), ("Parry", r.Parry), ("Block", r.Block),
        ];
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        Vector2 p = ImGui.GetCursorScreenPos();
        float gap = this.S(8f), h = this.S(50f), label = this.S(12f), value = this.S(17f);
        float w = (this.S(Width) - (3f * gap)) / 4f;
        for (int i = 0; i < tiles.Length; i++)
        {
            Vector2 min = p + new Vector2((i % 4) * (w + gap), (i / 4) * (h + gap));
            dl.AddRectFilled(min, min + new Vector2(w, h), Col(Theme.Frame), 8f);
            this.Text(dl, min + new Vector2(this.S(10f), this.S(7f)), tiles[i].Item1, label, Col(Theme.Muted));
            float? v = tiles[i].Item2;
            this.Text(dl, min + new Vector2(this.S(10f), this.S(24f)), Percent(v), value, Col(v is null ? Theme.Muted : Theme.Text));
        }

        ImGui.Dummy(new Vector2(this.S(Width), (2f * h) + gap));
    }

    // The skills, biggest first, about six lines; the rest scrolls.
    private void DrawSkills(Combatant me)
    {
        string language = m_config.SkillLanguage ?? SkillNames.DefaultLanguage();
        List<SkillRow> rows = SkillDetails.Rows(me, m_heal, language);
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        float width = this.S(Width), rowH = this.S(RowH), body = this.S(BaseText), head = this.S(11.5f);
        float cols = width - this.S(14f);

        // Column x positions as fractions of `cols`, numbers right-aligned at them. Titles and
        // rows share `cols`, the width left of the list's scrollbar, so they line up.
        const float barStart = 0.31f, barEnd = 0.52f, value = 0.62f, share = 0.71f, hits = 0.785f, crit = 0.86f, back = 0.93f, avg = 1f;
        Vector2 p = ImGui.GetCursorScreenPos();
        uint muted = Col(Theme.Muted);
        this.Text(dl, new Vector2(p.X + this.S(8f), p.Y), "SKILL", head, muted);
        this.Text(dl, new Vector2(p.X + (barStart * cols), p.Y), m_heal ? "HEALING" : "DAMAGE", head, muted);
        this.RightText(dl, p, share, "SHARE", head, muted, cols);
        this.RightText(dl, p, hits, "HITS", head, muted, cols);
        this.RightText(dl, p, crit, "CRIT", head, muted, cols);
        this.RightText(dl, p, back, "BACK", head, muted, cols);
        this.RightText(dl, p, avg, "AVG", head, muted, cols);
        ImGui.Dummy(new Vector2(width, head + this.S(2f)));

        Vector2 listSize = new(width, Math.Max(1, Math.Min(rows.Count, VisibleRows)) * rowH);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, Vector2.Zero);
        if (ImGui.BeginChild("##skills", listSize))
        {
            ImDrawListPtr cl = ImGui.GetWindowDrawList();
            float w = ImGui.GetContentRegionAvail().X;
            long top = rows.Count > 0 ? Math.Max(1, rows[0].Amount) : 1;
            Vector4 color = m_classColor(me.Job);
            if (rows.Count == 0)
            {
                ImGui.TextDisabled(m_heal ? "No healing in this fight." : "No damage in this fight.");
            }

            for (int i = 0; i < rows.Count; i++)
            {
                SkillRow r = rows[i];
                Vector2 at = ImGui.GetCursorScreenPos();
                ImGui.Dummy(new Vector2(w, rowH));
                bool hovered = ImGui.IsItemHovered();
                if (hovered || i == 0)
                {
                    cl.AddRectFilled(at, at + new Vector2(w, rowH - 2f), Col(hovered ? Theme.Frame : new Vector4(0.122f, 0.122f, 0.153f, 1f)), 6f);
                }

                float ty = at.Y + ((rowH - 2f - body) / 2f);
                this.Text(cl, new Vector2(at.X + this.S(8f), ty), this.Fit(r.Name, (barStart * cols) - this.S(16f), body), body, Col(Theme.Text));
                float barH = this.S(8f);
                Vector2 barMin = new(at.X + (barStart * cols), at.Y + ((rowH - 2f - barH) / 2f));
                float barW = (barEnd - barStart) * cols;
                cl.AddRectFilled(barMin, barMin + new Vector2(barW, barH), Col(Theme.Track), 4f);
                cl.AddRectFilled(barMin, barMin + new Vector2(barW * r.Amount / top, barH), Col(color), 4f);
                Vector2 row = new(at.X, ty);
                this.RightText(cl, row, value, Fmt(r.Amount), body, Col(Theme.Text), cols);
                this.RightText(cl, row, share, Percent(r.Share, 1), body, muted, cols);
                this.RightText(cl, row, hits, (r.Hits + r.Ticks).ToString(CultureInfo.InvariantCulture), body, Col(Theme.Text), cols);
                this.RightText(cl, row, crit, Percent(r.Crit), body, Col(Theme.Text), cols);
                this.RightText(cl, row, back, Percent(r.Back), body, Col(Theme.Text), cols);
                int count = r.Hits + r.Ticks;
                this.RightText(cl, row, avg, count > 0 ? Fmt(r.Amount / count) : "—", body, muted, cols);
                if (hovered)
                {
                    Widgets.Tooltip($"Biggest hit {Fmt(r.MaxHit)}" + (r.Ticks > 0 ? $"\n{r.Ticks} ticks over time" : string.Empty));
                }
            }
        }

        ImGui.EndChild();
        ImGui.PopStyleVar();
    }

    // DPS (or healing) per second over the fight, smoothed over 5 s: this player as a solid
    // line, the party dashed, the boss's HP dotted on its own scale.
    private void DrawChart(EncounterSnapshot fight, Combatant me)
    {
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        Vector2 p = ImGui.GetCursorScreenPos();
        float width = this.S(Width), chartH = this.S(ChartH), small = this.S(12f), head = this.S(11.5f);
        float segW = this.S(56f), segH = this.S(28f);

        // Controls: DPS / Heal, show party.
        this.Text(dl, new Vector2(p.X, p.Y + ((segH - head) / 2f)), "OVER THE FIGHT", head, Col(Theme.Muted));
        float partyW = this.S(120f);
        float segX = p.X + width - partyW - (2f * segW) - this.S(14f);
        if (this.Segment("##dps", "DPS", !m_heal, new Vector2(segX, p.Y), segW, segH, dl))
        {
            m_heal = false;
        }

        if (this.Segment("##heal", "Heal", m_heal, new Vector2(segX + segW + this.S(4f), p.Y), segW, segH, dl))
        {
            m_heal = true;
        }

        float box = this.S(14f);
        Vector2 boxAt = new(p.X + width - partyW + this.S(6f), p.Y + ((segH - box) / 2f));
        ImGui.SetCursorScreenPos(new Vector2(boxAt.X, p.Y));
        if (ImGui.InvisibleButton("##party", new Vector2(partyW - this.S(6f), segH)))
        {
            m_showParty = !m_showParty;
        }

        dl.AddRect(boxAt, boxAt + new Vector2(box, box), Col(Theme.Muted), 3f, ImDrawFlags.RoundCornersAll, 1f);
        if (m_showParty)
        {
            dl.AddRectFilled(boxAt + new Vector2(2f, 2f), boxAt + new Vector2(box - 2f, box - 2f), Col(Theme.Accent), 2f);
        }

        float body = this.S(BaseText);
        this.Text(dl, new Vector2(boxAt.X + box + this.S(7f), p.Y + ((segH - body) / 2f)), "Show party", body, Col(Theme.Text));
        ImGui.SetCursorScreenPos(p);
        ImGui.Dummy(new Vector2(width, segH));

        // The lines.
        var shown = fight.Combatants
            .Where(c => c.Id == me.Id || m_showParty)
            .Select(c => (c, line: m_heal ? c.HealPerSecond : c.DamagePerSecond))
            .Where(s => s.line.Count > 0)
            .ToList();
        float axis = this.S(48f);
        Vector2 plot = ImGui.GetCursorScreenPos() + new Vector2(axis, 0f);
        float plotW = width - axis;
        dl.AddRectFilled(plot, plot + new Vector2(plotW, chartH), Col(PlotBg), 6f);
        if (shown.Count == 0)
        {
            string empty = fight.Title.StartsWith("Overall", StringComparison.Ordinal) ? "No chart for Overall: pick a fight." : "Nothing to draw yet.";
            this.Text(dl, plot + new Vector2((plotW - this.TextW(empty, body)) / 2f, (chartH - body) / 2f), empty, body, Col(Theme.Muted));
            ImGui.Dummy(new Vector2(width, chartH + 4f));
            return;
        }

        int length = Math.Max(2, Math.Max(shown.Max(s => s.line.Count), fight.BossHp.Count));
        var smooth = shown.Select(s => (s.c, ys: SkillDetails.Smooth(s.line, length))).ToList();
        float max = Math.Max(1f, smooth.Max(s => s.ys.Max()));
        dl.AddLine(plot + new Vector2(0f, chartH / 2f), plot + new Vector2(plotW, chartH / 2f), Col(Theme.Border), 1f);
        uint muted = Col(Theme.Muted);
        this.RightAt(dl, new Vector2(plot.X - this.S(6f), plot.Y + 2f), Fmt(max), small, muted);
        this.RightAt(dl, new Vector2(plot.X - this.S(6f), plot.Y + ((chartH - small) / 2f)), Fmt(max / 2f), small, muted);
        this.RightAt(dl, new Vector2(plot.X - this.S(6f), plot.Y + chartH - small - 2f), "0", small, muted);

        Vector2 Point(int i, float v, float top) => new(
            plot.X + (plotW * i / (length - 1)),
            plot.Y + chartH - 4f - (v / top * (chartH - 10f)));

        if (fight.BossHp.Count > 1)
        {
            Vector2[] boss = fight.BossHp.Select((v, i) => Point(i, v, 1f)).ToArray();
            Dashed(dl, boss, Col(BossLine), this.S(1.6f), this.S(2f), this.S(4f));
        }

        foreach (var (c, ys) in smooth.OrderBy(s => s.c.Id == me.Id))
        {
            Vector2[] pts = ys.Select((v, i) => Point(i, v, max)).ToArray();
            uint col = Col(m_classColor(c.Job));
            if (c.Id == me.Id)
            {
                dl.AddPolyline(ref pts[0], pts.Length, col, ImDrawFlags.None, this.S(2.6f));
            }
            else
            {
                Dashed(dl, pts, col, this.S(1.6f), this.S(6f), this.S(4f));
            }
        }

        ImGui.Dummy(new Vector2(width, chartH + 2f));

        // Time and legend.
        Vector2 l = ImGui.GetCursorScreenPos();
        this.Text(dl, new Vector2(plot.X, l.Y), "0:00", small, muted);
        string end = EncounterSnapshot.FormatDuration(length - 1);
        this.Text(dl, new Vector2(plot.X + plotW - this.TextW(end, small), l.Y), end, small, muted);
        float lx = plot.X + this.TextW("0:00", small) + this.S(24f);
        float mid = l.Y + (small / 2f);
        foreach (var (c, _) in smooth.OrderByDescending(s => s.c.Id == me.Id))
        {
            dl.AddRectFilled(new Vector2(lx, mid - 1.5f), new Vector2(lx + this.S(14f), mid + 1.5f), Col(m_classColor(c.Job)), 1f);
            this.Text(dl, new Vector2(lx + this.S(19f), l.Y), c.Name, small, Col(Theme.Text));
            lx += this.S(19f) + this.TextW(c.Name, small) + this.S(14f);
        }

        if (fight.BossHp.Count > 1)
        {
            Dashed(dl, [new Vector2(lx, mid), new Vector2(lx + this.S(14f), mid)], Col(BossLine), 2f, 2f, 3f);
            this.Text(dl, new Vector2(lx + this.S(19f), l.Y), "Boss HP", small, Col(BossLine));
        }

        ImGui.Dummy(new Vector2(width, small + 2f));
    }

    private bool Segment(string id, string label, bool on, Vector2 at, float w, float h, ImDrawListPtr dl)
    {
        ImGui.SetCursorScreenPos(at);
        bool clicked = ImGui.InvisibleButton(id, new Vector2(w, h));
        bool hovered = ImGui.IsItemHovered();
        dl.AddRectFilled(at, at + new Vector2(w, h), Col(on ? Theme.Accent : hovered ? Theme.FrameHover : Theme.Frame), 6f);
        float size = this.S(BaseText);
        this.Text(dl, at + new Vector2((w - this.TextW(label, size)) / 2f, (h - size) / 2f), label, size, Col(Theme.Text));
        return clicked;
    }

    // A polyline drawn as dashes: `dash` px on, `gap` px off along its length. Every step
    // ends either the segment or the current dash/gap, so it always finishes (a version
    // that kept a running phase with % stalled on float rounding at uneven sizes and froze
    // the overlay).
    internal static void Dashed(ImDrawListPtr dl, Vector2[] pts, uint col, float thickness, float dash, float gap)
    {
        foreach ((Vector2 from, Vector2 to) in DashSegments(pts, dash, gap))
        {
            dl.AddLine(from, to, col, thickness);
        }
    }

    internal static List<(Vector2 From, Vector2 To)> DashSegments(Vector2[] pts, float dash, float gap)
    {
        var dashes = new List<(Vector2, Vector2)>();
        dash = Math.Max(0.5f, dash);
        gap = Math.Max(0.5f, gap);
        bool on = true;
        float left = dash;
        for (int i = 1; i < pts.Length; i++)
        {
            Vector2 a = pts[i - 1];
            Vector2 b = pts[i];
            float len = Vector2.Distance(a, b);
            float t = 0f;
            while (len - t > 0.01f)
            {
                float step = Math.Min(left, len - t);
                if (on)
                {
                    dashes.Add((Vector2.Lerp(a, b, t / len), Vector2.Lerp(a, b, (t + step) / len)));
                }

                t += step;
                left -= step;
                if (left <= 0.01f)
                {
                    on = !on;
                    left = on ? dash : gap;
                }
            }
        }

        return dashes;
    }

    // A size at body text 14 px, scaled to the bar names' text size.
    private float S(float v) => v * m_scale;

    private void RightText(ImDrawListPtr dl, Vector2 row, float at, string s, float size, uint col, float width) =>
        this.Text(dl, new Vector2(row.X + (at * width) - this.TextW(s, size), row.Y), s, size, col);

    private void RightAt(ImDrawListPtr dl, Vector2 rightTop, string s, float size, uint col) =>
        this.Text(dl, new Vector2(rightTop.X - this.TextW(s, size), rightTop.Y), s, size, col);

    private static string Percent(float? v, int decimals = 0) =>
        v is float f ? (f * 100f).ToString(decimals == 0 ? "0" : "0.0", CultureInfo.InvariantCulture) + "%" : "—";

    private static string Fmt(float v) =>
        v >= 1_000_000 ? (v / 1_000_000f).ToString("0.0", CultureInfo.InvariantCulture) + "M"
        : v >= 1000 ? (v / 1000f).ToString("0.0", CultureInfo.InvariantCulture) + "K"
        : v.ToString("0", CultureInfo.InvariantCulture);

    private void Text(ImDrawListPtr dl, Vector2 pos, string s, float size, uint col) =>
        dl.AddText(ImGui.GetFont(), size, pos, col, s);

    private float TextW(string s, float size)
    {
        float baseSize = ImGui.GetFontSize();
        return baseSize > 0 ? ImGui.CalcTextSize(s).X * (size / baseSize) : ImGui.CalcTextSize(s).X;
    }

    private string Fit(string s, float room, float size)
    {
        if (this.TextW(s, size) <= room)
        {
            return s;
        }

        int n = s.Length;
        while (n > 0 && this.TextW(s[..n].TrimEnd() + "...", size) > room)
        {
            n--;
        }

        return s[..n].TrimEnd() + "...";
    }

    private static uint Col(Vector4 v) => ImGui.ColorConvertFloat4ToU32(v);
}
