using System.Numerics;
using HamMeter.Update;
using ImGuiNET;

namespace HamMeter.UI;

// The update popup and "What's new", in the settings window's look: rounded dark panel,
// "HamMeter <subtitle>" header with a close cross.
internal sealed class UpdateWindows
{
    private const float HeaderH = 40f;

    private readonly UpdateController m_updates;

    public UpdateWindows(UpdateController updates)
    {
        m_updates = updates;
    }

    // Centre of the primary monitor in overlay coordinates; set by the overlay.
    public Vector2 PrimaryCenter { get; set; }

    public void Draw()
    {
        if (m_updates.PopupOpen && m_updates.Available is { } update)
        {
            this.DrawPopup(update);
        }

        if (m_updates.WhatsNewOpen)
        {
            this.DrawWhatsNew();
        }
    }

    // ----- Update popup ---------------------------------------------------------------

    private void DrawPopup(UpdateInfo update)
    {
        const float width = 460f;
        bool open = true;
        if (!BeginPanel("##update", "Update", new Vector2(width, 0f), this.PrimaryCenter, ref open, !m_updates.Busy))
        {
            EndPanel();
            return;
        }

        if (!open)
        {
            m_updates.Later();
        }

        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        float inner = width - 32f;

        // Title row: bell, text, optional "Important" tag.
        Vector2 p = ImGui.GetCursorScreenPos();
        float line = ImGui.GetTextLineHeight();
        Icons.Draw(dl, Icon.Bell, p, line, Widgets.Col(Theme.Accent));
        ImGui.SetCursorScreenPos(new Vector2(p.X + line + 8f, p.Y));
        ImGui.TextUnformatted("New version available");
        if (update.Important)
        {
            ImGui.SameLine(0f, 10f);
            Tag("Important", Theme.Danger);
        }

        ImGui.Dummy(new Vector2(0f, 4f));

        // Yours -> new, side by side.
        Release? mine = Update.Changelog.CurrentRelease;
        float cardW = (inner - 34f) * 0.5f;
        Vector2 c = ImGui.GetCursorScreenPos();
        VersionCard(dl, c, cardW, "Your version", Update.Changelog.Current, mine?.Date, false);
        // Arrow drawn, not typed: the meter's font has no arrow glyph.
        Vector2 a = new(c.X + cardW + 9f, c.Y + 30f);
        uint arrow = Widgets.Col(Theme.Muted);
        dl.AddLine(a, a + new Vector2(16f, 0f), arrow, 1.5f);
        dl.AddTriangleFilled(a + new Vector2(18f, 0f), a + new Vector2(12f, -4f), a + new Vector2(12f, 4f), arrow);
        VersionCard(dl, new Vector2(c.X + cardW + 34f, c.Y), cardW, "New", update.Version, update.Date, true);
        ImGui.Dummy(new Vector2(inner, 64f));

        if (update.Important)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.54f, 0.5f, 1f));
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + inner);
            ImGui.TextUnformatted("Recommended: without this update the numbers may be wrong.");
            ImGui.PopTextWrapPos();
            ImGui.PopStyleColor();
        }

        // Patch notes as a pill, not a list in the popup.
        if (Widgets.Pill("patchnotes", "Patch notes", Theme.Frame, Theme.Muted, Icon.Sparkles))
        {
            m_updates.WhatsNewOpen = true;
        }

        if (m_updates.PopupStatus is { } status)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, m_updates.PopupError ? Theme.Danger : Theme.Muted);
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + inner);
            ImGui.TextUnformatted(status);
            ImGui.PopTextWrapPos();
            ImGui.PopStyleColor();
        }

        ImGui.Dummy(new Vector2(0f, 4f));

        // Buttons, right-aligned: [Ignore this version] [Later] [Update now]
        bool idle = !m_updates.Busy;
        float wIgnore = ButtonWidth("Ignore this version");
        float wLater = ButtonWidth("Later");
        float wUpdate = ButtonWidth("Update now") + ImGui.GetTextLineHeight() + 8f;
        float x = ImGui.GetCursorPosX() + inner - (wIgnore + wLater + wUpdate + 16f);
        ImGui.SetCursorPosX(x);
        if (Widgets.Button("Ignore this version", ButtonKind.Normal, idle))
        {
            m_updates.Ignore();
        }

        ImGui.SameLine(0f, 8f);
        if (Widgets.Button("Later", ButtonKind.Normal, idle))
        {
            m_updates.Later();
        }

        ImGui.SameLine(0f, 8f);
        if (Widgets.Button("Update now", ButtonKind.Primary, idle, 0f, Icon.Download))
        {
            _ = m_updates.UpdateNowAsync();
        }

        EndPanel();
    }

    private static void VersionCard(ImDrawListPtr dl, Vector2 p, float w, string label, Version version, DateOnly? date, bool highlight)
    {
        Vector2 max = p + new Vector2(w, 60f);
        dl.AddRectFilled(p, max, Widgets.Col(Theme.Frame), 8f);
        if (highlight)
        {
            dl.AddRect(p, max, Widgets.Col(Theme.Accent), 8f, ImDrawFlags.RoundCornersAll, 1.5f);
        }

        float small = ImGui.GetFontSize() * 0.78f;
        dl.AddText(ImGui.GetFont(), small, p + new Vector2(10f, 6f), Widgets.Col(Theme.Muted), label);
        dl.AddText(ImGui.GetFont(), ImGui.GetFontSize() * 1.05f, p + new Vector2(10f, 20f), Widgets.Col(Theme.Text), "v" + version.ToString(3));
        dl.AddText(ImGui.GetFont(), small, p + new Vector2(10f, 42f), Widgets.Col(Theme.Muted), Update.Changelog.FormatDate(date));
    }

    // ----- What's new -----------------------------------------------------------------

    private void DrawWhatsNew()
    {
        const float width = 480f;
        bool open = true;
        Vector2 center = this.PrimaryCenter + new Vector2(40f, 30f);
        if (!BeginPanel("##whatsnew", "What's new", new Vector2(width, 440f), center, ref open, true))
        {
            EndPanel();
            return;
        }

        if (!open)
        {
            m_updates.WhatsNewOpen = false;
        }

        ImGui.PushStyleVar(ImGuiStyleVar.ScrollbarSize, 8f);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarBg, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrab, Theme.Border);
        if (ImGui.BeginChild("##notes", new Vector2(0f, 0f)))
        {
            float inner = ImGui.GetContentRegionAvail().X - 12f;
            bool first = true;

            if (m_updates.Available is { } update)
            {
                ReleaseBlock(new Release(update.Version, update.Date, update.Important, update.Notes), "available", Theme.Accent, inner);
                first = false;
            }

            foreach (Release r in Update.Changelog.Embedded)
            {
                if (!first)
                {
                    Vector2 s = ImGui.GetCursorScreenPos();
                    ImGui.GetWindowDrawList().AddLine(new Vector2(s.X, s.Y + 6f), new Vector2(s.X + inner, s.Y + 6f), Widgets.Col(Theme.Border));
                    ImGui.Dummy(new Vector2(0f, 12f));
                }

                bool installed = r.Version == Update.Changelog.Current;
                ReleaseBlock(r, installed ? "installed" : null, installed ? Theme.Accent : Theme.Frame, inner);
                first = false;
            }
        }

        ImGui.EndChild();
        ImGui.PopStyleColor(2);
        ImGui.PopStyleVar();
        EndPanel();
    }

    private static void ReleaseBlock(Release r, string? marker, Vector4 pillColor, float inner)
    {
        Vector4 pillBg = pillColor == Theme.Frame ? Theme.Frame : new Vector4(pillColor.X, pillColor.Y, pillColor.Z, 0.18f);
        Vector4 pillFg = pillColor == Theme.Frame ? Theme.Muted : Theme.AccentHover;
        Widgets.Pill("v" + r.Version.ToString(3), "v" + r.Version.ToString(3), pillBg, pillFg);
        ImGui.SameLine(0f, 8f);
        string meta = Update.Changelog.FormatDate(r.Date) + (marker is null ? string.Empty : " · " + marker);
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 2f);
        ImGui.TextDisabled(meta);
        if (r.Important)
        {
            ImGui.SameLine(0f, 8f);
            Tag("Important", Theme.Danger);
        }

        foreach (Note n in r.Notes)
        {
            NoteLine(n, inner);
        }
    }

    // "[New] text that wraps under itself"
    private static void NoteLine(Note note, float inner)
    {
        (string label, Vector4 col) = note.Kind switch
        {
            NoteKind.New => ("New", Theme.Accent),
            NoteKind.Improved => ("Improved", Theme.Success),
            _ => ("Fixed", Theme.Warning),
        };

        const float tagW = 74f;
        Vector2 p = ImGui.GetCursorScreenPos();
        ImGui.SetCursorScreenPos(p + new Vector2(0f, 2f));
        Tag(label, col);
        float wrap = inner - tagW;
        Vector2 ts = ImGui.CalcTextSize(note.Text, wrap);
        ImGui.GetWindowDrawList().AddText(ImGui.GetFont(), ImGui.GetFontSize(), new Vector2(p.X + tagW, p.Y + 2f), Widgets.Col(Theme.Text), note.Text, wrap);
        ImGui.SetCursorScreenPos(p);
        ImGui.Dummy(new Vector2(inner, MathF.Max(ts.Y, ImGui.GetTextLineHeight()) + 6f));
    }

    // Small coloured tag (New / Improved / Fixed / Important), not clickable.
    private static void Tag(string text, Vector4 color)
    {
        float size = ImGui.GetFontSize() * 0.78f;
        Vector2 p = ImGui.GetCursorScreenPos();
        Vector2 box = new(Widgets.TextWidth(text, size) + 12f, size + 5f);
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        dl.AddRectFilled(p, p + box, Widgets.Col(new Vector4(color.X, color.Y, color.Z, 0.16f)), 5f);
        dl.AddText(ImGui.GetFont(), size, p + new Vector2(6f, 2.5f), Widgets.Col(color), text);
        ImGui.Dummy(box);
    }

    private static float ButtonWidth(string label) => ImGui.CalcTextSize(label).X + 32f;

    // ----- Panel chrome ---------------------------------------------------------------

    private static bool BeginPanel(string id, string subtitle, Vector2 size, Vector2 center, ref bool open, bool closable)
    {
        // A height of 0 means "fixed width, height follows the content".
        bool autoHeight = size.Y <= 0f;
        ImGui.SetNextWindowPos(center, ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
        if (autoHeight)
        {
            ImGui.SetNextWindowSizeConstraints(new Vector2(size.X, 0f), new Vector2(size.X, 2000f));
        }
        else
        {
            ImGui.SetNextWindowSize(size, ImGuiCond.Appearing);
        }
        ImGui.PushStyleColor(ImGuiCol.WindowBg, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.ChildBg, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.Text);
        ImGui.PushStyleColor(ImGuiCol.TextDisabled, Theme.Muted);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 10f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(16f, 12f));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(8f, 8f));

        ImGuiWindowFlags flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoScrollbar
            | ImGuiWindowFlags.NoSavedSettings;
        if (autoHeight)
        {
            flags |= ImGuiWindowFlags.AlwaysAutoResize;
        }

        bool visible = ImGui.Begin("HamMeter" + id, flags);
        if (!visible)
        {
            return false;
        }

        Vector2 wp = ImGui.GetWindowPos();
        Vector2 ws = ImGui.GetWindowSize();
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        dl.AddRectFilled(wp, wp + ws, Widgets.Col(Theme.WindowBg), 10f);
        dl.AddRect(wp, wp + ws, Widgets.Col(Theme.Border), 10f, ImDrawFlags.RoundCornersAll, 1f);
        dl.AddRectFilled(wp, new Vector2(wp.X + ws.X, wp.Y + HeaderH), Widgets.Col(Theme.Frame), 10f, ImDrawFlags.RoundCornersTop);

        float titleW = Widgets.TextWidth("HamMeter", 18f);
        dl.AddText(ImGui.GetFont(), 18f, new Vector2(wp.X + 14f, wp.Y + 11f), Widgets.Col(Theme.Text), "HamMeter");
        dl.AddText(ImGui.GetFont(), 14f, new Vector2(wp.X + 24f + titleW, wp.Y + 14f), Widgets.Col(Theme.Muted), subtitle);

        if (closable)
        {
            const float bs = 24f;
            Vector2 bpos = new(wp.X + ws.X - bs - 10f, wp.Y + ((HeaderH - bs) * 0.5f));
            ImGui.SetCursorScreenPos(bpos);
            if (ImGui.InvisibleButton("##close", new Vector2(bs, bs)))
            {
                open = false;
            }

            uint xcol = Widgets.Col(ImGui.IsItemHovered() ? Theme.Text : Theme.Muted);
            Icons.Draw(dl, Icon.Close, bpos + new Vector2(5f, 5f), 14f, xcol);
        }

        ImGui.SetCursorScreenPos(new Vector2(wp.X + 16f, wp.Y + HeaderH + 12f));
        return true;
    }

    private static void EndPanel()
    {
        ImGui.End();
        ImGui.PopStyleVar(4);
        ImGui.PopStyleColor(4);
    }
}
