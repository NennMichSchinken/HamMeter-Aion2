using System.Globalization;
using System.Numerics;
using HamMeter.Combat;
using ImGuiNET;

namespace HamMeter.UI;

public sealed class MeterWindow
{
    // Precomputed "1." .. "24." so the render loop doesn't allocate a rank string per bar.
    private static readonly string[] RankLabels = BuildRankLabels();

    private static string[] BuildRankLabels()
    {
        string[] labels = new string[24];
        for (int i = 0; i < labels.Length; i++)
        {
            labels[i] = (i + 1) + ".";
        }

        return labels;
    }

    private readonly Config m_config;
    private readonly SettingsWindow m_settings;
    private readonly EncounterTracker m_tracker;
    private readonly Func<string, IntPtr> m_classIcon;
    private readonly Func<string?> m_status;
    private readonly Dictionary<int, float> m_animFractions = new();

    private bool m_visible = true;
    private Metric m_metric = Metric.DamageDone;
    private int m_view = -1; // -1 = Current, -2 = Overall, >=0 = past index

    // classIcon: texture handle for a class tag (IntPtr.Zero when not loaded).
    // status: a message shown instead of the bars when capture isn't running.
    public MeterWindow(Config config, SettingsWindow settings, EncounterTracker tracker, Func<string, IntPtr> classIcon, Func<string?> status)
    {
        m_config = config;
        m_settings = settings;
        m_tracker = tracker;
        m_classIcon = classIcon;
        m_status = status;
    }

    // Where the primary monitor starts in overlay coordinates; set by the overlay.
    public Vector2 PrimaryOrigin { get; set; }

    // Development preview: show the lock / resize corner without hovering.
    public bool PreviewCorner { get; set; }

    public bool Visible
    {
        get => m_visible;
        set => m_visible = value;
    }

    public void ClearAll()
    {
        m_tracker.Clear();
        m_animFractions.Clear();
        m_view = -1;
    }

    private EncounterSnapshot? GetDisplayedEvent()
    {
        if (m_config.TestMode)
        {
            return TestData.Build();
        }

        if (m_view == -2)
        {
            return m_tracker.GetOverall();
        }

        if (m_view >= 0 && m_view < m_tracker.PastCount)
        {
            return m_tracker.GetPast(m_view);
        }

        return m_tracker.Current;
    }

    public void Draw()
    {
        if (!m_visible)
        {
            return;
        }

        EncounterSnapshot? ev = this.GetDisplayedEvent();

        if (m_config.OnlyInCombat && !m_config.TestMode && (ev is null || !ev.Active) && !m_settings.Visible)
        {
            return;
        }

        Vector4 bg = m_config.BackgroundColor;
        bg.W = m_config.BackgroundOpacity;
        ImGui.PushStyleColor(ImGuiCol.WindowBg, new Vector4(0f, 0f, 0f, 0f));
        ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(0f, 0f, 0f, 0f));

        // ImGui's own resize triangle stays functional but invisible; DrawCorner shows
        // WispUI's three diagonals there instead, only while the pointer is on the meter.
        ImGui.PushStyleColor(ImGuiCol.ResizeGrip, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.ResizeGripHovered, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.ResizeGripActive, Vector4.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 10f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(0f, 0f));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(0f, m_config.BarSpacing));

        // First start: top-left area of the primary monitor (the overlay spans all monitors,
        // so its own origin can sit on a screen to the left).
        ImGui.SetNextWindowPos(this.PrimaryOrigin + new Vector2(60f, 200f), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new Vector2(360f, 300f), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(new Vector2(340f, 110f), new Vector2(2000f, 2000f));
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoScrollbar
            | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoCollapse;
        if (m_config.Locked)
        {
            flags |= ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize;
        }

        // Title is hidden (NoTitleBar). The "###main" suffix fixes the ImGui id to a
        // stable value regardless of the visible label, so the saved position sticks.
        if (ImGui.Begin("HamMeter###main", flags))
        {
            Vector2 winPos = ImGui.GetWindowPos();
            Vector2 winSize = ImGui.GetWindowSize();
            m_settings.SetMeterRect(winPos, winSize);

            // Check this in the main-window scope: the popup id is created here, and
            // a child window has a different id seed (so IsPopupOpen would miss it).
            bool resetOpen = ImGui.IsPopupOpen("reset_confirm");

            // Self-drawn rounded background so all four corners match the settings look.
            ImGui.GetWindowDrawList().AddRectFilled(
                winPos,
                new Vector2(winPos.X + winSize.X, winPos.Y + winSize.Y),
                Col(bg),
                10f,
                ImDrawFlags.RoundCornersAll);

            // Matching rounded border (same colour as the settings window).
            ImGui.GetWindowDrawList().AddRect(
                winPos,
                new Vector2(winPos.X + winSize.X, winPos.Y + winSize.Y),
                Col(new Vector4(0.173f, 0.173f, 0.18f, 1f)),
                10f,
                ImDrawFlags.RoundCornersAll,
                1f);

            this.DrawHeader(ev);

            // Bars live in a scrollable child so the header stays fixed and the
            // list can scroll when there are more players than fit the window.
            const float pad = 6f;
            float headerH = m_config.HeaderHeight;
            ImGui.SetCursorScreenPos(new Vector2(winPos.X + pad, winPos.Y + headerH + m_config.BarSpacing));
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(0f, 0f));

            // Slim scrollbar in the HamMeter palette for lists longer than the window.
            ImGui.PushStyleVar(ImGuiStyleVar.ScrollbarSize, 8f);
            ImGui.PushStyleVar(ImGuiStyleVar.ScrollbarRounding, 6f);
            ImGui.PushStyleColor(ImGuiCol.ScrollbarBg, Vector4.Zero);
            ImGui.PushStyleColor(ImGuiCol.ScrollbarGrab, Theme.Border);
            ImGui.PushStyleColor(ImGuiCol.ScrollbarGrabHovered, Theme.FrameHover);
            ImGui.PushStyleColor(ImGuiCol.ScrollbarGrabActive, Theme.Accent);

            float barsH = Math.Max(1f, winSize.Y - headerH - m_config.BarSpacing - pad);
            if (ImGui.BeginChild("##bars", new Vector2(winSize.X - (pad * 2f), barsH)))
            {
                this.DrawBars(ev);

                // When the reset confirmation is open, dim the whole meter window
                // (header + bars) so the popup looks like it sits on top.
                if (resetOpen)
                {
                    ImDrawListPtr ddl = ImGui.GetWindowDrawList();
                    ddl.PushClipRectFullScreen();
                    ddl.AddRectFilled(winPos, new Vector2(winPos.X + winSize.X, winPos.Y + winSize.Y), Col(new Vector4(0f, 0f, 0f, 0.5f)), 10f, ImDrawFlags.RoundCornersAll);
                    ddl.PopClipRect();
                }
            }

            ImGui.EndChild();
            ImGui.PopStyleColor(4);
            ImGui.PopStyleVar(3);
            this.DrawCorner(winPos, winSize);
            this.DrawPopups();
        }

        ImGui.End();
        ImGui.PopStyleVar(4);
        ImGui.PopStyleColor(5);
    }

    // The resize corner and the lock beside it, like WispUI's combat tracker: both only
    // while the pointer is on the meter, so neither is in view during a fight. A locked
    // meter (no moving, no resizing) shows the lock alone, which is the way back.
    private void DrawCorner(Vector2 winPos, Vector2 winSize)
    {
        bool overMeter = this.PreviewCorner
            || ImGui.IsWindowHovered(ImGuiHoveredFlags.RootAndChildWindows | ImGuiHoveredFlags.AllowWhenBlockedByActiveItem);
        if (!overMeter || ImGui.IsPopupOpen(string.Empty, ImGuiPopupFlags.AnyPopupId))
        {
            return;
        }

        const float size = 16f;
        const float edge = 5f;
        Vector2 max = winPos + winSize;
        Vector2 gripMin = new(max.X - edge - size, max.Y - edge - size);
        bool locked = m_config.Locked;
        Vector2 lockMin = locked ? gripMin : new Vector2(gripMin.X - size - 8f, gripMin.Y);

        // Drawn above the bars (they live in a child window that paints over this one), on a
        // small chip so the icons stay readable over a bar that reaches the bottom row.
        ImDrawListPtr dl = ImGui.GetForegroundDrawList();
        Vector2 chipMin = lockMin - new Vector2(5f, 4f);
        Vector2 chipMax = gripMin + new Vector2(size + 4f, size + 4f);
        dl.AddRectFilled(chipMin, chipMax, Col(new Vector4(Theme.WindowBg.X, Theme.WindowBg.Y, Theme.WindowBg.Z, 0.92f)), 6f);
        dl.AddRect(chipMin, chipMax, Col(Theme.Border), 6f, ImDrawFlags.RoundCornersAll, 1f);

        bool lockHovered = ImGui.IsMouseHoveringRect(lockMin, lockMin + new Vector2(size, size), false);
        if (lockHovered)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            Widgets.Tooltip(locked ? "Locked. Click to unlock." : "Click to lock in place.");
            if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                m_config.Locked = !locked;
                m_config.Save();
            }
        }

        Icons.Draw(dl, locked ? Icon.Lock : Icon.LockOpen, lockMin, size, Col(lockHovered ? Theme.AccentHover : Theme.Muted));
        if (locked)
        {
            return;
        }

        // Three short diagonals, the usual sign for "pull here" (ImGui's grip is underneath).
        bool gripHovered = ImGui.IsMouseHoveringRect(gripMin, max, false);
        uint ink = Col(gripHovered ? Theme.AccentHover : Theme.Muted);
        Vector2 corner = gripMin + new Vector2(size, size);
        for (int i = 1; i <= 3; i++)
        {
            float reach = size * i / 3f;
            dl.AddLine(new Vector2(corner.X - reach, corner.Y), new Vector2(corner.X, corner.Y - reach), ink, 1.5f);
        }
    }

    private void DrawHeader(EncounterSnapshot? ev)
    {
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        Vector2 wp = ImGui.GetWindowPos();
        float width = ImGui.GetWindowSize().X;
        float h = m_config.HeaderHeight;

        Vector4 headerColor = m_config.HeaderColor;
        headerColor.W = m_config.HeaderOpacity;
        dl.AddRectFilled(wp, new Vector2(wp.X + width, wp.Y + h), Col(headerColor), 10f, ImDrawFlags.RoundCornersTop);

        string viewLabel = m_view == -2 ? "Overall" : m_view >= 0 ? "History" : "Current";
        string duration = ev?.Duration ?? "00:00";
        string main = $"{Metrics.Name(m_metric)}  -  {viewLabel}   ";
        string paren = $"({duration})";
        float ts = m_config.TopTextSize;
        float ty = wp.Y + ((h - ts) / 2f);
        float tx = wp.X + 12f;
        uint white = Col(new Vector4(1f, 1f, 1f, 1f));
        uint muted = Col(new Vector4(0.557f, 0.557f, 0.576f, 1f));
        this.Text(dl, new Vector2(tx, ty), main, ts, white);
        this.Text(dl, new Vector2(tx + this.TextW(main, ts), ty), paren, ts, muted);

        // Icon row, right-aligned. Drawn from the right, so the visible left-to-right
        // order is: Reset, History, Metric, Settings.
        float bsize = m_config.IconSize;
        float gap = m_config.IconSpacing;
        float iconY = wp.Y + ((h - bsize) / 2f);
        float x = wp.X + width - bsize - 10f;

        if (this.IconButton("ham_cfg", Icon.Settings, new Vector2(x, iconY), bsize))
        {
            m_settings.Visible = !m_settings.Visible;
        }

        x -= bsize + gap;
        if (this.IconButton("ham_metric", Icon.Exchange, new Vector2(x, iconY), bsize))
        {
            ImGui.OpenPopup("metric_popup");
        }

        x -= bsize + gap;
        if (this.IconButton("ham_hist", Icon.ClipboardList, new Vector2(x, iconY), bsize))
        {
            ImGui.OpenPopup("hist_popup");
        }

        x -= bsize + gap;
        if (this.IconButton("ham_clear", Icon.Refresh, new Vector2(x, iconY), bsize))
        {
            if (m_config.ConfirmReset)
            {
                ImGui.OpenPopup("reset_confirm");
            }
            else
            {
                this.ClearAll();
            }
        }
    }

    private void DrawBars(EncounterSnapshot? ev)
    {
        if (ev is null || ev.Combatants.Count == 0)
        {
            // Capture problems only replace the bars while there is nothing to show.
            ImGui.TextWrapped(m_status() ?? "Waiting for combat data...");
            return;
        }

        List<Combatant> sorted = ev.Combatants
            .Where(c => Metrics.Value(c, m_metric) > 0)
            .OrderByDescending(c => Metrics.Value(c, m_metric))
            .ToList();

        if (sorted.Count == 0)
        {
            return;
        }

        float max = Math.Max(1f, Metrics.Value(sorted[0], m_metric));
        int rank = 1;
        float width = ImGui.GetContentRegionAvail().X;
        foreach (Combatant c in sorted)
        {
            this.DrawBar(c, max, rank, width);
            rank++;
        }
    }

    private void DrawBar(Combatant c, float max, int rank, float width)
    {
        float value = Metrics.Value(c, m_metric);
        float target = max > 0 ? Math.Clamp(value / max, 0f, 1f) : 0f;
        float fraction = this.AnimateFraction(c.Id, target);

        float rowH = m_config.BarHeight;
        float barH = rowH - 2f;

        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        Vector2 pos = ImGui.GetCursorScreenPos();
        if (width < 1f)
        {
            width = 1f;
        }

        bool known = ClassInfo.IsKnown(c.Job);
        Vector4 baseCol = this.BarColor(c.Job, known);
        Vector4 fill = baseCol;
        fill.W = m_config.BarOpacity;
        Vector4 trackCol = m_config.BarTrackColor;
        trackCol.W *= m_config.BarOpacity;
        uint track = Col(trackCol);
        float round = m_config.RoundedBars ? 6f : 0f;
        dl.AddRectFilled(pos, new Vector2(pos.X + width, pos.Y + barH), track, round);

        // Filled portion in the chosen texture (WispUI bar styles), rounded at both ends
        // like the track. It shows the matching slice of the texture rather than the whole
        // texture squeezed, so the pattern does not shift with the value.
        float fillW = MathF.Round(width * fraction);
        if (fillW > 0.5f)
        {
            BarStyles.Draw(dl, BarStyles.Get(m_config.BarStyle), pos, new Vector2(pos.X + fillW, pos.Y + barH), Col(fill), round, fillW / width);
        }

        uint white = Col(new Vector4(1f, 1f, 1f, 1f));
        float leftSize = m_config.LeftTextSize;
        float rightSize = m_config.RightTextSize;
        float textY = pos.Y + ((barH - leftSize) / 2f);
        float x = pos.X + 6f;

        if (m_config.ShowRankNumbers)
        {
            string r = rank >= 1 && rank <= RankLabels.Length ? RankLabels[rank - 1] : rank + ".";
            this.Text(dl, new Vector2(x, textY), r, leftSize, white);
            x += this.TextW(r, leftSize) + 5f;
        }

        x = this.DrawJobIndicator(dl, c.Job, baseCol, pos, x, barH, leftSize, known);

        this.Text(dl, new Vector2(x, textY), c.Name, leftSize, white);

        string right = this.FormatValue(c, value);
        float rw = this.TextW(right, rightSize);
        this.Text(dl, new Vector2(pos.X + width - rw - 6f, pos.Y + ((barH - rightSize) / 2f)), right, rightSize, white);

        ImGui.Dummy(new Vector2(width, rowH));
    }

    private float DrawJobIndicator(ImDrawListPtr dl, string job, Vector4 baseCol, Vector2 pos, float x, float barH, float leftSize, bool known)
    {
        // No indicator when it's off or the class isn't known yet — just the name.
        if (m_config.JobIndicator == 0 || string.IsNullOrEmpty(job) || !known)
        {
            return x;
        }

        float boxTop = pos.Y + 2f;
        float boxBottom = pos.Y + barH - 2f;
        float boxH = boxBottom - boxTop;

        // Icon mode: the official class icon as it is, falls back to the text tag if it
        // isn't loaded.
        if (m_config.JobIndicator == 2)
        {
            IntPtr icon = m_classIcon(job);
            if (icon != IntPtr.Zero)
            {
                dl.AddImage(icon, new Vector2(x, boxTop), new Vector2(x + boxH, boxBottom));
                return x + boxH + 5f;
            }
        }

        // Text tag — a frosted "glass" chip. It floats over the coloured bar: a
        // translucent tint lets the bar shimmer through, a thin white frost veil lifts it
        // so it reads lighter than the bar, and a top light edge + soft bottom shadow give
        // depth. No real backdrop blur is possible in an ImGui draw list.
        float tagW = this.TextW("WWW", leftSize - 1f) + 6f;
        float tagRound = 3f;
        Vector2 ta = new(x, boxTop);
        Vector2 tb = new(x + tagW, boxBottom);

        Vector4 tint = baseCol;
        tint.W = 0.34f;
        dl.AddRectFilled(ta, tb, Col(tint), tagRound);
        dl.AddRectFilled(ta, tb, Col(new Vector4(1f, 1f, 1f, 0.10f)), tagRound);

        // Top light edge + bottom shadow. Inset horizontally by the corner radius so the
        // square gradient overlays don't poke past the rounded corners.
        float gx = ta.X + tagRound;
        float gxr = tb.X - tagRound;
        float mid = ta.Y + (boxH * 0.5f);
        uint clearW = Col(new Vector4(1f, 1f, 1f, 0f));
        uint sheenW = Col(new Vector4(1f, 1f, 1f, 0.22f));
        dl.AddRectFilledMultiColor(new Vector2(gx, ta.Y + 1f), new Vector2(gxr, mid), sheenW, sheenW, clearW, clearW);
        uint clearB = Col(new Vector4(0f, 0f, 0f, 0f));
        uint darkB = Col(new Vector4(0f, 0f, 0f, 0.12f));
        dl.AddRectFilledMultiColor(new Vector2(gx, mid), new Vector2(gxr, tb.Y - 1f), clearB, clearB, darkB, darkB);

        // Adaptive accents: dark/saturated classes get a softly dimmed light edge + white
        // label; light classes get a gently lightened dark edge + a deep label drawn from
        // their own hue. Threshold on the class colour's perceived luminance.
        float tagLum = (baseCol.X * 0.2126f) + (baseCol.Y * 0.7152f) + (baseCol.Z * 0.0722f);
        bool darkJob = tagLum < 0.5f;
        Vector4 borderCol = darkJob
            ? new Vector4(1f, 1f, 1f, 0.20f)
            : new Vector4(0f, 0f, 0f, 0.26f);
        dl.AddRect(ta, tb, Col(borderCol), tagRound, ImDrawFlags.RoundCornersAll, 1f);

        Vector4 textCol;
        if (darkJob)
        {
            textCol = new Vector4(1f, 0.996f, 0.949f, 1f);
        }
        else
        {
            float f = tagLum > 0.001f ? (0.40f / tagLum) : 1f;
            textCol = new Vector4(Math.Min(1f, baseCol.X * f), Math.Min(1f, baseCol.Y * f), Math.Min(1f, baseCol.Z * f), 1f);
        }

        string label = job.ToUpperInvariant();
        float lw = this.TextW(label, leftSize - 1f);
        float lx = x + ((tagW - lw) / 2f);
        this.Text(dl, new Vector2(lx, pos.Y + ((barH - (leftSize - 1f)) / 2f)), label, leftSize - 1f, Col(textCol), false);
        return x + tagW + 5f;
    }

    private float AnimateFraction(int id, float target)
    {
        if (!m_config.SmoothBars)
        {
            m_animFractions[id] = target;
            return target;
        }

        float current = m_animFractions.TryGetValue(id, out float v) ? v : target;
        float dt = ImGui.GetIO().DeltaTime;
        float step = Math.Clamp(dt * 10f, 0f, 1f);
        current += (target - current) * step;
        m_animFractions[id] = current;
        return current;
    }

    private string FormatValue(Combatant c, float value)
    {
        if (Metrics.IsCount(m_metric))
        {
            return ((int)value).ToString();
        }

        string main = this.Fmt(value);
        if (Metrics.HasRate(m_metric))
        {
            return $"{main}  ({this.Fmt(Metrics.Rate(c, m_metric))})";
        }

        return main;
    }

    private void DrawPopups()
    {
        // Uniform minimum width for both dropdowns, based on the widest metric label,
        // so the short History popup never looks narrower than the Metric popup.
        float minW = 120f;
        foreach (Metric m in Metrics.All)
        {
            minW = MathF.Max(minW, ImGui.CalcTextSize(Metrics.Name(m)).X + 36f);
        }

        Vector2 sizeMin = new(minW, 0f);
        Vector2 sizeMax = new(float.MaxValue, float.MaxValue);

        Theme.PushDropdown();

        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(12f, 10f));
        ImGui.SetNextWindowSizeConstraints(sizeMin, sizeMax);
        if (ImGui.BeginPopup("metric_popup"))
        {
            ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(8f, 10f));
            foreach (Metric m in Metrics.All)
            {
                if (ImGui.Selectable(Metrics.Name(m), m == m_metric))
                {
                    m_metric = m;
                }
            }

            ImGui.PopStyleVar();
            ImGui.EndPopup();
        }

        ImGui.PopStyleVar();

        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(12f, 10f));
        ImGui.SetNextWindowSizeConstraints(sizeMin, sizeMax);
        if (ImGui.BeginPopup("hist_popup"))
        {
            ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(8f, 10f));
            if (ImGui.Selectable("Current", m_view == -1))
            {
                m_view = -1;
            }

            if (ImGui.Selectable("Overall", m_view == -2))
            {
                m_view = -2;
            }

            List<EncounterSnapshot> past = m_tracker.SnapshotPast();
            if (past.Count > 0)
            {
                ImGui.Separator();

                // Boss fights get a crown in front; the column stays for all entries so
                // the titles line up.
                float line = ImGui.GetTextLineHeight();
                string gap = new(' ', (int)MathF.Ceiling((line + 6f) / MathF.Max(1f, ImGui.CalcTextSize(" ").X)));
                ImDrawListPtr pdl = ImGui.GetWindowDrawList();
                for (int i = past.Count - 1; i >= 0; i--)
                {
                    Vector2 at = ImGui.GetCursorScreenPos();
                    string label = $"{gap}{past[i].Title} ({past[i].Duration})##{i}";
                    if (ImGui.Selectable(label, m_view == i))
                    {
                        m_view = i;
                    }

                    if (past[i].IsBoss)
                    {
                        Icons.Draw(pdl, Icon.Crown, at, line, Col(new Vector4(0.95f, 0.76f, 0.30f, 1f)));
                    }
                }
            }

            ImGui.PopStyleVar();
            ImGui.EndPopup();
        }

        ImGui.PopStyleVar();

        // Done with the dropdowns.
        Theme.PopDropdown();

        // Confirmation dialog for the reset button.
        Theme.PushDialog();
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(14f, 12f));

        Vector2 wpos = ImGui.GetWindowPos();
        Vector2 wsize = ImGui.GetWindowSize();
        ImGui.SetNextWindowPos(
            new Vector2(wpos.X + (wsize.X * 0.5f), wpos.Y + (wsize.Y * 0.5f)),
            ImGuiCond.Appearing,
            new Vector2(0.5f, 0.5f));
        if (ImGui.BeginPopup("reset_confirm", ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoMove))
        {
            ImGui.TextUnformatted("Reset all combat data?");
            ImGui.Dummy(new Vector2(0f, 6f));
            ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(12f, 8f));
            if (ImGui.Button("Reset", new Vector2(100f, 0f)))
            {
                this.ClearAll();
                ImGui.CloseCurrentPopup();
            }

            ImGui.SameLine(0f, 16f);
            if (ImGui.Button("Cancel", new Vector2(100f, 0f)))
            {
                ImGui.CloseCurrentPopup();
            }

            ImGui.PopStyleVar();
            ImGui.EndPopup();
        }

        ImGui.PopStyleVar();
        Theme.PopDialog();
    }

    private bool IconButton(string id, Icon icon, Vector2 pos, float size)
    {
        ImGui.SetCursorScreenPos(pos);
        bool clicked = ImGui.InvisibleButton(id, new Vector2(size, size));
        bool hovered = ImGui.IsItemHovered();

        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        if (hovered)
        {
            dl.AddRectFilled(pos, new Vector2(pos.X + size, pos.Y + size), Col(new Vector4(1f, 1f, 1f, 0.15f)), 6f);
        }

        // Inset slightly: Lucide glyphs fill the whole 24x24 box, FontAwesome ones don't.
        float inset = size * 0.08f;
        Vector2 gpos = new(pos.X + inset, pos.Y + inset);
        float gsize = size - (inset * 2f);
        Icons.Draw(dl, icon, new Vector2(gpos.X + 1f, gpos.Y + 1f), gsize, Col(new Vector4(0f, 0f, 0f, 0.9f)));
        Icons.Draw(dl, icon, gpos, gsize, Col(new Vector4(1f, 1f, 1f, 1f)));
        return clicked;
    }

    private Vector4 BarColor(string job, bool known)
    {
        // Entries without a known class get a neutral grey.
        if (!known)
        {
            return new Vector4(0.55f, 0.55f, 0.6f, 1f);
        }

        // All class dictionaries are case-insensitive, so the raw tag works.
        if (m_config.BarColorMode == 1)
        {
            if (m_config.JobColors.TryGetValue(job, out Vector4 jc))
            {
                jc.W = 1f;
                return jc;
            }
        }

        Vector4 c = ClassInfo.Tanks.Contains(job) ? m_config.TankColor
            : ClassInfo.Healers.Contains(job) ? m_config.HealerColor
            : m_config.DpsColor;
        c.W = 1f;
        return c;
    }

    private string Fmt(float v)
    {
        if (m_config.ShortNumbers)
        {
            if (v >= 1_000_000)
            {
                return (v / 1_000_000f).ToString("0.0", CultureInfo.InvariantCulture) + "M";
            }

            if (v >= 1000)
            {
                return (v / 1000f).ToString("0.0", CultureInfo.InvariantCulture) + "K";
            }

            return v.ToString("0", CultureInfo.InvariantCulture);
        }

        return ((long)v).ToString("N0", CultureInfo.InvariantCulture);
    }

    private void Text(ImDrawListPtr dl, Vector2 pos, string s, float size, uint col, bool shadow = true)
    {
        ImFontPtr font = ImGui.GetFont();
        if (shadow)
        {
            dl.AddText(font, size, new Vector2(pos.X + 1f, pos.Y + 1f), Col(new Vector4(0f, 0f, 0f, 0.9f)), s);
        }

        dl.AddText(font, size, pos, col, s);
    }

    private float TextW(string s, float size)
    {
        float baseSize = ImGui.GetFontSize();
        return baseSize > 0 ? ImGui.CalcTextSize(s).X * (size / baseSize) : ImGui.CalcTextSize(s).X;
    }

    private static uint Col(Vector4 v) => ImGui.ColorConvertFloat4ToU32(v);
}
