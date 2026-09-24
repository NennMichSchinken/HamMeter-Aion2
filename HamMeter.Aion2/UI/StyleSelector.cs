using System.Globalization;
using System.Numerics;

using ImGuiNET;

namespace HamMeter.UI;

// "◀ [preview  Name  2 / 9] ▶" with the full list one click away on the face — the
// selector from WispUI's settings (ArrowSelector), redrawn in HamMeter's colours. The
// arrows step through the list; the preview is the point of the control, the name is
// the label for what you already see.
internal sealed class StyleSelector<T>
{
    private const float Height = 30f;
    private const float ArrowW = 26f;
    private const float RowHeight = 30f;
    private static readonly Vector2 FaceSwatch = new(56f, 14f);
    private static readonly Vector2 RowSwatch = new(84f, 16f);

    private readonly string m_id;
    private readonly IReadOnlyList<T> m_items;
    private readonly Func<T, string> m_label;
    private readonly Action<ImDrawListPtr, T, Vector2, Vector2> m_preview;
    private bool m_open;
    private bool m_openRequested;

    // Opens the list on the next draw, as if the face had been clicked (dev preview).
    public void RequestOpen() => m_openRequested = true;

    public StyleSelector(string id, IReadOnlyList<T> items, Func<T, string> label, Action<ImDrawListPtr, T, Vector2, Vector2> preview)
    {
        m_id = id;
        m_items = items;
        m_label = label;
        m_preview = preview;
    }

    // Draws at the cursor, filling `width`. Returns true when the selection changed.
    public bool Draw(ref int index, float width)
    {
        int count = m_items.Count;
        index = Math.Clamp(index, 0, count - 1);
        bool changed = false;

        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        Vector2 p = ImGui.GetCursorScreenPos();

        if (this.Arrow(dl, p, true, index > 0))
        {
            index--;
            changed = true;
        }

        if (this.Arrow(dl, new Vector2(p.X + width - ArrowW, p.Y), false, index < count - 1))
        {
            index++;
            changed = true;
        }

        Vector2 faceMin = new(p.X + ArrowW, p.Y);
        Vector2 faceMax = new(p.X + width - ArrowW, p.Y + Height);
        ImGui.SetCursorScreenPos(faceMin);
        bool faceClicked = ImGui.InvisibleButton($"##{m_id}_face", faceMax - faceMin);
        bool faceHovered = ImGui.IsItemHovered();
        if (faceHovered)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        }

        dl.AddRectFilled(faceMin, faceMax, Widgets.Col(faceHovered ? Theme.FrameHover : Theme.WindowBg));

        const float pad = 10f;
        float left = faceMin.X + pad;
        float swatchTop = MathF.Round(p.Y + ((Height - FaceSwatch.Y) * 0.5f));
        m_preview(dl, m_items[index], new Vector2(left, swatchTop), new Vector2(left + FaceSwatch.X, swatchTop + FaceSwatch.Y));
        left += FaceSwatch.X + pad;

        string counter = (index + 1).ToString(CultureInfo.InvariantCulture) + " / " + count.ToString(CultureInfo.InvariantCulture);
        float small = ImGui.GetFontSize() * 0.85f;
        float counterW = Widgets.TextWidth(counter, small);
        float right = faceMax.X - pad;
        dl.AddText(ImGui.GetFont(), small, new Vector2(right - counterW, p.Y + ((Height - small) * 0.5f)), Widgets.Col(Theme.Muted), counter);
        right -= counterW + pad;

        float textH = ImGui.GetTextLineHeight();
        dl.PushClipRect(new Vector2(left, faceMin.Y), new Vector2(right, faceMax.Y), true);
        dl.AddText(new Vector2(left, p.Y + ((Height - textH) * 0.5f)), Widgets.Col(Theme.Text), m_label(m_items[index]));
        dl.PopClipRect();

        // One outlined strip with hairlines where the arrows meet the face.
        dl.AddRect(p, new Vector2(p.X + width, p.Y + Height), Widgets.Col(Theme.Border), 6f, ImDrawFlags.RoundCornersAll, 1f);
        dl.AddLine(new Vector2(faceMin.X, p.Y), new Vector2(faceMin.X, p.Y + Height), Widgets.Col(Theme.Border));
        dl.AddLine(new Vector2(faceMax.X, p.Y), new Vector2(faceMax.X, p.Y + Height), Widgets.Col(Theme.Border));

        string popupId = $"##{m_id}_list";
        if (faceClicked || m_openRequested)
        {
            m_openRequested = false;
            m_open = true;
            ImGui.OpenPopup(popupId);
        }

        if (m_open)
        {
            // Below the field, or above it when the monitor has no room left underneath.
            float listH = this.ListHeight;
            (float monitorTop, float monitorBottom) = PopupPlacement.MonitorSpan(p);
            float top = PopupPlacement.ListTop(p.Y, p.Y + Height, listH, monitorTop, monitorBottom);
            changed |= this.DrawList(popupId, ref index, new Vector2(p.X, top), width);
        }

        ImGui.SetCursorScreenPos(p);
        ImGui.Dummy(new Vector2(width, Height));
        return changed;
    }

    private const float ListPadding = 6f;
    private const float RowSpacing = 2f;

    // Full height of the open list: every row, the gaps between them, padding and border.
    private float ListHeight => (m_items.Count * RowHeight) + ((m_items.Count - 1) * RowSpacing) + (ListPadding * 2f) + 2f;

    private bool Arrow(ImDrawListPtr dl, Vector2 pos, bool left, bool enabled)
    {
        ImGui.SetCursorScreenPos(pos);
        bool clicked = ImGui.InvisibleButton($"##{m_id}_{(left ? "prev" : "next")}", new Vector2(ArrowW, Height)) && enabled;
        bool hovered = enabled && ImGui.IsItemHovered();
        if (hovered)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        }

        dl.AddRectFilled(pos, pos + new Vector2(ArrowW, Height), Widgets.Col(hovered ? Theme.FrameHover : Theme.Frame), 6f,
            left ? ImDrawFlags.RoundCornersLeft : ImDrawFlags.RoundCornersRight);

        // Drawn triangle rather than a glyph, so it never depends on the font.
        uint ink = Widgets.Col(!enabled ? Theme.Track : hovered ? Theme.AccentHover : Theme.Text);
        float cx = MathF.Round(pos.X + (ArrowW * 0.5f));
        float cy = MathF.Round(pos.Y + (Height * 0.5f));
        const float g = 5f;
        if (left)
        {
            dl.AddTriangleFilled(new Vector2(cx - (g * 0.6f), cy), new Vector2(cx + (g * 0.6f), cy - g), new Vector2(cx + (g * 0.6f), cy + g), ink);
        }
        else
        {
            dl.AddTriangleFilled(new Vector2(cx + (g * 0.6f), cy), new Vector2(cx - (g * 0.6f), cy + g), new Vector2(cx - (g * 0.6f), cy - g), ink);
        }

        return clicked;
    }

    // The full list with a preview per row, as a popup so it is not clipped by the
    // scrolling settings body and closes on a click outside or Escape.
    private bool DrawList(string popupId, ref int index, Vector2 pos, float width)
    {
        bool changed = false;
        ImGui.SetNextWindowPos(pos);
        ImGui.SetNextWindowSize(new Vector2(width, 0f));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(ListPadding, ListPadding));
        ImGui.PushStyleVar(ImGuiStyleVar.PopupRounding, 6f);
        ImGui.PushStyleVar(ImGuiStyleVar.PopupBorderSize, 1f);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(0f, RowSpacing));
        ImGui.PushStyleColor(ImGuiCol.PopupBg, Theme.WindowBg);
        ImGui.PushStyleColor(ImGuiCol.Border, Theme.Border);

        if (ImGui.BeginPopup(popupId))
        {
            ImDrawListPtr dl = ImGui.GetWindowDrawList();
            for (int i = 0; i < m_items.Count; i++)
            {
                Vector2 min = ImGui.GetCursorScreenPos();
                float w = ImGui.GetContentRegionAvail().X;
                Vector2 max = new(min.X + w, min.Y + RowHeight);

                ImGui.PushID(i);
                bool clicked = ImGui.InvisibleButton("##row", new Vector2(w, RowHeight));
                bool hovered = ImGui.IsItemHovered();
                ImGui.PopID();

                bool selected = i == index;
                if (selected || hovered)
                {
                    dl.AddRectFilled(min, max, Widgets.Col(selected ? Theme.Frame : Theme.FrameHover), 4f);
                }

                float left = min.X + 8f;
                float top = MathF.Round(min.Y + ((RowHeight - RowSwatch.Y) * 0.5f));
                m_preview(dl, m_items[i], new Vector2(left, top), new Vector2(left + RowSwatch.X, top + RowSwatch.Y));
                left += RowSwatch.X + 10f;

                Vector4 ink = selected ? Theme.AccentHover : hovered ? Theme.Text : Theme.Muted;
                dl.AddText(new Vector2(left, min.Y + ((RowHeight - ImGui.GetTextLineHeight()) * 0.5f)), Widgets.Col(ink), m_label(m_items[i]));

                if (clicked)
                {
                    index = i;
                    changed = true;
                    m_open = false;
                    ImGui.CloseCurrentPopup();
                }
            }

            ImGui.EndPopup();
        }
        else
        {
            m_open = false; // closed by a click outside or Escape
        }

        ImGui.PopStyleColor(2);
        ImGui.PopStyleVar(4);
        return changed;
    }
}
