using System.Globalization;
using System.Numerics;
using HamMeter.Combat;
using HamMeter.Game;
using ImGuiNET;

namespace HamMeter.UI;

// The skill details of one player in the fight the meter shows: hit rates, the skills and
// a chart of DPS or healing over the fight, with the party and the boss's HP. Opened from
// the "Details" button of an expanded bar; a click on another bar or on a name at the top
// switches the player.
public sealed class DetailWindow
{
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

        Combatant me = fight.Combatants.FirstOrDefault(c => c.Id == this.PlayerId) ?? fight.Combatants.OrderByDescending(c => c.DamageTotal).First();
        this.PlayerId = me.Id;

        Vector2 disp = ImGui.GetIO().DisplaySize;
        float x = m_anchorPos.X + m_anchorSize.X + 8f;
        if (x + Width + 32f > disp.X)
        {
            x = m_anchorPos.X - 8f - Width - 32f;
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
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(16f, 14f));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(0f, 12f));
        ImGui.PushStyleVar(ImGuiStyleVar.ScrollbarSize, 8f);
        ImGui.PushStyleVar(ImGuiStyleVar.ScrollbarRounding, 6f);

        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoCollapse
            | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;
        if (ImGui.Begin("Details###ham_details", flags))
        {
            this.DrawHeader(fight, me);
            this.DrawTabs(fight);
            DrawRates(SkillDetails.Rates(me));
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
        const float h = 40f;

        IntPtr icon = m_classIcon(me.Job);
        if (icon != IntPtr.Zero)
        {
            dl.AddImage(icon, p + new Vector2(0f, 2f), p + new Vector2(36f, 38f));
        }
        else
        {
            dl.AddRectFilled(p + new Vector2(0f, 2f), p + new Vector2(36f, 38f), Col(m_classColor(me.Job)), 8f);
        }

        float tx = p.X + 46f;
        Text(dl, new Vector2(tx, p.Y), me.Name, 19f, Col(Theme.Text));
        string role = (ClassInfo.IsKnown(me.Job) ? ClassInfo.FullName(me.Job) : "Unknown class") + (me.IsUser ? " · you" : string.Empty);
        Text(dl, new Vector2(tx + TextW(me.Name, 19f) + 8f, p.Y + 4f), role, 14f, Col(Theme.Muted));
        Text(dl, new Vector2(tx, p.Y + 23f), $"{fight.Title}{(fight.IsBoss ? " (boss)" : string.Empty)} · {fight.Duration}", 14f, Col(Theme.Muted));

        // Close button, top right.
        Vector2 closeMin = new(p.X + Width - 24f, p.Y + 2f);
        ImGui.SetCursorScreenPos(closeMin);
        bool closeClicked = ImGui.InvisibleButton("##close", new Vector2(24f, 24f));
        bool closeHovered = ImGui.IsItemHovered();
        if (closeHovered)
        {
            dl.AddRectFilled(closeMin, closeMin + new Vector2(24f, 24f), Col(Theme.FrameHover), 6f);
        }

        Icons.Draw(dl, Icon.Close, closeMin + new Vector2(4f, 4f), 16f, Col(closeHovered ? Theme.Text : Theme.Muted));
        if (closeClicked)
        {
            this.Close();
        }

        // Totals, right-aligned before the close button.
        float right = p.X + Width - 40f;
        (string label, string value)[] stats = m_heal
            ? [("Healing", Fmt(me.HealedTotal)), ("HPS", Fmt(me.Hps))]
            : [("Damage", Fmt(me.DamageTotal)), ("DPS", Fmt(me.Dps))];
        for (int i = stats.Length - 1; i >= 0; i--)
        {
            float w = MathF.Max(TextW(stats[i].value, 18f), TextW(stats[i].label, 12f));
            Text(dl, new Vector2(right - TextW(stats[i].label, 12f), p.Y + 1f), stats[i].label, 12f, Col(Theme.Muted));
            Text(dl, new Vector2(right - TextW(stats[i].value, 18f), p.Y + 17f), stats[i].value, 18f, Col(Theme.Text));
            right -= w + 22f;
        }

        ImGui.SetCursorScreenPos(p);
        ImGui.Dummy(new Vector2(Width, h));
    }

    // Every player of the fight; a click shows that player.
    private void DrawTabs(EncounterSnapshot fight)
    {
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        Vector2 start = ImGui.GetCursorScreenPos();
        Vector2 at = start;
        const float h = 28f;
        IEnumerable<Combatant> players = fight.Combatants
            .Where(c => (m_heal ? c.HealedTotal : c.DamageTotal) > 0 || c.Id == this.PlayerId)
            .OrderByDescending(c => m_heal ? c.HealedTotal : c.DamageTotal);
        foreach (Combatant c in players)
        {
            string amount = Fmt(m_heal ? c.HealedTotal : c.DamageTotal);
            float w = 12f + 8f + 6f + TextW(c.Name, 14f) + 6f + TextW(amount, 13f) + 12f;
            if (at.X + w > start.X + Width)
            {
                at = new Vector2(start.X, at.Y + h + 6f);
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
            dl.AddCircleFilled(at + new Vector2(16f, h / 2f), 4f, Col(m_classColor(c.Job)));
            float tx = at.X + 26f;
            Text(dl, new Vector2(tx, at.Y + 6f), c.Name, 14f, Col(Theme.Text));
            Text(dl, new Vector2(tx + TextW(c.Name, 14f) + 6f, at.Y + 7f), amount, 13f, Col(Theme.Muted));
            at.X += w + 6f;
        }

        ImGui.SetCursorScreenPos(start);
        ImGui.Dummy(new Vector2(Width, at.Y - start.Y + h));
    }

    // The hit rates as tiles, two rows of four.
    private static void DrawRates(HitRates r)
    {
        (string, float?)[] tiles =
        [
            ("Crit", r.Crit), ("Back attack", r.Back), ("Front attack", r.Front), ("Double", r.Double),
            ("Perfect", r.Perfect), ("Missed", r.Missed), ("Parry", r.Parry), ("Block", r.Block),
        ];
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        Vector2 p = ImGui.GetCursorScreenPos();
        const float gap = 8f;
        const float h = 46f;
        float w = (Width - (3f * gap)) / 4f;
        for (int i = 0; i < tiles.Length; i++)
        {
            Vector2 min = p + new Vector2((i % 4) * (w + gap), (i / 4) * (h + gap));
            dl.AddRectFilled(min, min + new Vector2(w, h), Col(Theme.Frame), 8f);
            Text(dl, min + new Vector2(10f, 6f), tiles[i].Item1, 12f, Col(Theme.Muted));
            float? v = tiles[i].Item2;
            Text(dl, min + new Vector2(10f, 22f), Percent(v), 16f, Col(v is null ? Theme.Muted : Theme.Text));
        }

        ImGui.Dummy(new Vector2(Width, (2f * h) + gap));
    }

    // The skills, biggest first, about six lines; the rest scrolls.
    private void DrawSkills(Combatant me)
    {
        string language = m_config.SkillLanguage ?? SkillNames.DefaultLanguage();
        List<SkillRow> rows = SkillDetails.Rows(me, m_heal, language);
        ImDrawListPtr dl = ImGui.GetWindowDrawList();

        // Column x positions as fractions of the width; numbers are right-aligned at them.
        const float barStart = 0.31f, barEnd = 0.54f, value = 0.64f, share = 0.73f, hits = 0.81f, crit = 0.88f, back = 0.94f, avg = 1f;
        Vector2 p = ImGui.GetCursorScreenPos();
        uint muted = Col(Theme.Muted);
        Text(dl, p, "SKILL", 11f, muted);
        Text(dl, new Vector2(p.X + (barStart * Width), p.Y), m_heal ? "HEALING" : "DAMAGE", 11f, muted);
        RightText(dl, p, share, "SHARE", 11f, muted);
        RightText(dl, p, hits, "HITS", 11f, muted);
        RightText(dl, p, crit, "CRIT", 11f, muted);
        RightText(dl, p, back, "BACK", 11f, muted);
        RightText(dl, p, avg - 0.012f, "AVG", 11f, muted);
        ImGui.Dummy(new Vector2(Width, 14f));

        Vector2 listSize = new(Width, Math.Max(1, Math.Min(rows.Count, VisibleRows)) * RowH);
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
                ImGui.Dummy(new Vector2(w, RowH));
                bool hovered = ImGui.IsItemHovered();
                if (hovered || i == 0)
                {
                    cl.AddRectFilled(at, at + new Vector2(w, RowH - 2f), Col(hovered ? Theme.Frame : new Vector4(0.122f, 0.122f, 0.153f, 1f)), 6f);
                }

                float ty = at.Y + ((RowH - 2f - 14f) / 2f);
                Text(cl, new Vector2(at.X + 8f, ty), Fit(r.Name, (barStart * w) - 16f, 14f), 14f, Col(Theme.Text));
                Vector2 barMin = new(at.X + (barStart * w), at.Y + 9f);
                float barW = (barEnd - barStart) * w;
                cl.AddRectFilled(barMin, barMin + new Vector2(barW, 7f), Col(Theme.Track), 4f);
                cl.AddRectFilled(barMin, barMin + new Vector2(barW * r.Amount / top, 7f), Col(color), 4f);
                RightText(cl, new Vector2(at.X, ty), value, Fmt(r.Amount), 14f, Col(Theme.Text), w);
                RightText(cl, new Vector2(at.X, ty), share, Percent(r.Share, 1), 14f, muted, w);
                RightText(cl, new Vector2(at.X, ty), hits, (r.Hits + r.Ticks).ToString(CultureInfo.InvariantCulture), 14f, Col(Theme.Text), w);
                RightText(cl, new Vector2(at.X, ty), crit, Percent(r.Crit), 14f, Col(Theme.Text), w);
                RightText(cl, new Vector2(at.X, ty), back, Percent(r.Back), 14f, Col(Theme.Text), w);
                int count = r.Hits + r.Ticks;
                RightText(cl, new Vector2(at.X, ty), avg - 0.012f, count > 0 ? Fmt(r.Amount / count) : "—", 14f, muted, w);
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

        // Controls: DPS / Heal, show party.
        Text(dl, new Vector2(p.X, p.Y + 6f), "OVER THE FIGHT", 11f, Col(Theme.Muted));
        float segX = p.X + Width - 230f;
        if (Segment("##dps", "DPS", !m_heal, new Vector2(segX, p.Y), dl))
        {
            m_heal = false;
        }

        if (Segment("##heal", "Heal", m_heal, new Vector2(segX + 56f, p.Y), dl))
        {
            m_heal = true;
        }

        Vector2 boxAt = new(segX + 130f, p.Y + 6f);
        ImGui.SetCursorScreenPos(new Vector2(boxAt.X, p.Y));
        if (ImGui.InvisibleButton("##party", new Vector2(100f, 26f)))
        {
            m_showParty = !m_showParty;
        }

        dl.AddRect(boxAt, boxAt + new Vector2(13f, 13f), Col(Theme.Muted), 3f, ImDrawFlags.RoundCornersAll, 1f);
        if (m_showParty)
        {
            dl.AddRectFilled(boxAt + new Vector2(2f, 2f), boxAt + new Vector2(11f, 11f), Col(Theme.Accent), 2f);
        }

        Text(dl, boxAt + new Vector2(19f, -1f), "Show party", 13f, Col(Theme.Text));
        ImGui.SetCursorScreenPos(p);
        ImGui.Dummy(new Vector2(Width, 26f));

        // The lines.
        var shown = fight.Combatants
            .Where(c => c.Id == me.Id || m_showParty)
            .Select(c => (c, line: m_heal ? c.HealPerSecond : c.DamagePerSecond))
            .Where(s => s.line.Count > 0)
            .ToList();
        Vector2 plot = ImGui.GetCursorScreenPos() + new Vector2(44f, 0f);
        float plotW = Width - 44f;
        dl.AddRectFilled(plot, plot + new Vector2(plotW, ChartH), Col(PlotBg), 6f);
        if (shown.Count == 0)
        {
            string empty = fight.Title.StartsWith("Overall", StringComparison.Ordinal) ? "No chart for Overall: pick a fight." : "Nothing to draw yet.";
            Text(dl, plot + new Vector2((plotW - TextW(empty, 13f)) / 2f, (ChartH / 2f) - 8f), empty, 13f, Col(Theme.Muted));
            ImGui.Dummy(new Vector2(Width, ChartH + 4f));
            return;
        }

        int length = Math.Max(2, Math.Max(shown.Max(s => s.line.Count), fight.BossHp.Count));
        var smooth = shown.Select(s => (s.c, ys: SkillDetails.Smooth(s.line, length))).ToList();
        float max = Math.Max(1f, smooth.Max(s => s.ys.Max()));
        dl.AddLine(plot + new Vector2(0f, ChartH / 2f), plot + new Vector2(plotW, ChartH / 2f), Col(Theme.Border), 1f);
        uint muted = Col(Theme.Muted);
        RightAt(dl, new Vector2(plot.X - 6f, plot.Y + 2f), Fmt(max), 11f, muted);
        RightAt(dl, new Vector2(plot.X - 6f, plot.Y + (ChartH / 2f) - 6f), Fmt(max / 2f), 11f, muted);
        RightAt(dl, new Vector2(plot.X - 6f, plot.Y + ChartH - 14f), "0", 11f, muted);

        Vector2 Point(int i, float v, float top) => new(
            plot.X + (plotW * i / (length - 1)),
            plot.Y + ChartH - 4f - (v / top * (ChartH - 10f)));

        if (fight.BossHp.Count > 1)
        {
            Vector2[] boss = fight.BossHp.Select((v, i) => Point(i, v, 1f)).ToArray();
            Dashed(dl, boss, Col(BossLine), 1.5f, 2f, 4f);
        }

        foreach (var (c, ys) in smooth.OrderBy(s => s.c.Id == me.Id))
        {
            Vector2[] pts = ys.Select((v, i) => Point(i, v, max)).ToArray();
            uint col = Col(m_classColor(c.Job));
            if (c.Id == me.Id)
            {
                dl.AddPolyline(ref pts[0], pts.Length, col, ImDrawFlags.None, 2.5f);
            }
            else
            {
                Dashed(dl, pts, col, 1.5f, 6f, 4f);
            }
        }

        ImGui.Dummy(new Vector2(Width, ChartH + 2f));

        // Time and legend.
        Vector2 l = ImGui.GetCursorScreenPos();
        Text(dl, new Vector2(plot.X, l.Y), "0:00", 12f, muted);
        string end = EncounterSnapshot.FormatDuration(length - 1);
        Text(dl, new Vector2(plot.X + plotW - TextW(end, 12f), l.Y), end, 12f, muted);
        float lx = plot.X + 60f;
        foreach (var (c, _) in smooth.OrderByDescending(s => s.c.Id == me.Id))
        {
            dl.AddRectFilled(new Vector2(lx, l.Y + 7f), new Vector2(lx + 14f, l.Y + 10f), Col(m_classColor(c.Job)), 1f);
            Text(dl, new Vector2(lx + 19f, l.Y), c.Name, 12f, Col(Theme.Text));
            lx += 19f + TextW(c.Name, 12f) + 14f;
        }

        if (fight.BossHp.Count > 1)
        {
            Dashed(dl, [new Vector2(lx, l.Y + 8f), new Vector2(lx + 14f, l.Y + 8f)], Col(BossLine), 2f, 2f, 3f);
            Text(dl, new Vector2(lx + 19f, l.Y), "Boss HP", 12f, Col(BossLine));
        }

        ImGui.Dummy(new Vector2(Width, 16f));
    }

    private static bool Segment(string id, string label, bool on, Vector2 at, ImDrawListPtr dl)
    {
        ImGui.SetCursorScreenPos(at);
        bool clicked = ImGui.InvisibleButton(id, new Vector2(52f, 26f));
        bool hovered = ImGui.IsItemHovered();
        dl.AddRectFilled(at, at + new Vector2(52f, 26f), Col(on ? Theme.Accent : hovered ? Theme.FrameHover : Theme.Frame), 6f);
        Text(dl, at + new Vector2((52f - TextW(label, 13f)) / 2f, 5f), label, 13f, Col(Theme.Text));
        return clicked;
    }

    // A polyline drawn as dashes: `dash` px on, `gap` px off along its length.
    private static void Dashed(ImDrawListPtr dl, Vector2[] pts, uint col, float thickness, float dash, float gap)
    {
        float phase = 0f;
        for (int i = 1; i < pts.Length; i++)
        {
            Vector2 a = pts[i - 1];
            Vector2 b = pts[i];
            float len = Vector2.Distance(a, b);
            float t = 0f;
            while (t < len)
            {
                float period = dash + gap;
                float inPeriod = phase % period;
                float step = inPeriod < dash ? Math.Min(dash - inPeriod, len - t) : Math.Min(period - inPeriod, len - t);
                if (inPeriod < dash && len > 0f)
                {
                    dl.AddLine(Vector2.Lerp(a, b, t / len), Vector2.Lerp(a, b, (t + step) / len), col, thickness);
                }

                t += step;
                phase += step;
            }
        }
    }

    private static void RightText(ImDrawListPtr dl, Vector2 row, float at, string s, float size, uint col, float width = Width) =>
        Text(dl, new Vector2(row.X + (at * width) - TextW(s, size) - 8f, row.Y), s, size, col);

    private static void RightAt(ImDrawListPtr dl, Vector2 rightTop, string s, float size, uint col) =>
        Text(dl, new Vector2(rightTop.X - TextW(s, size), rightTop.Y), s, size, col);

    private static string Percent(float? v, int decimals = 0) =>
        v is float f ? (f * 100f).ToString(decimals == 0 ? "0" : "0.0", CultureInfo.InvariantCulture) + "%" : "—";

    private static string Fmt(float v) =>
        v >= 1_000_000 ? (v / 1_000_000f).ToString("0.0", CultureInfo.InvariantCulture) + "M"
        : v >= 1000 ? (v / 1000f).ToString("0.0", CultureInfo.InvariantCulture) + "K"
        : v.ToString("0", CultureInfo.InvariantCulture);

    private static void Text(ImDrawListPtr dl, Vector2 pos, string s, float size, uint col) =>
        dl.AddText(ImGui.GetFont(), size, pos, col, s);

    private static float TextW(string s, float size)
    {
        float baseSize = ImGui.GetFontSize();
        return baseSize > 0 ? ImGui.CalcTextSize(s).X * (size / baseSize) : ImGui.CalcTextSize(s).X;
    }

    private static string Fit(string s, float room, float size)
    {
        if (TextW(s, size) <= room)
        {
            return s;
        }

        int n = s.Length;
        while (n > 0 && TextW(s[..n].TrimEnd() + "...", size) > room)
        {
            n--;
        }

        return s[..n].TrimEnd() + "...";
    }

    private static uint Col(Vector4 v) => ImGui.ColorConvertFloat4ToU32(v);
}
