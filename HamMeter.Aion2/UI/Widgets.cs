using System.Numerics;
using ImGuiNET;

namespace HamMeter.UI;

public enum ButtonKind
{
    Normal,
    Primary,
    Danger,
}

// Self-drawn controls shared by the meter, the settings window and the setup wizard,
// so every HamMeter surface uses exactly the same look.
internal static class Widgets
{
    // Custom checkbox: filled accent box with a white check when on, outlined when off.
    // An optional hint is drawn muted below the label.
    public static bool Checkbox(string label, ref bool value, string? hint = null)
    {
        const float box = 20f;
        const float gap = 8f;
        float h = MathF.Max(box, ImGui.GetFrameHeight());
        Vector2 ls = ImGui.CalcTextSize(label);
        float hintH = hint is null ? 0f : ImGui.GetTextLineHeight();

        Vector2 p = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton($"##cb_{label}", new Vector2(box + gap + ls.X, h + hintH));
        bool hovered = ImGui.IsItemHovered();
        bool changed = false;
        if (ImGui.IsItemClicked())
        {
            value = !value;
            changed = true;
        }

        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        float by = p.Y + ((h - box) * 0.5f);
        Vector2 b1 = new(p.X, by);
        Vector2 b2 = new(p.X + box, by + box);

        if (value)
        {
            dl.AddRectFilled(b1, b2, Col(hovered ? Theme.AccentHover : Theme.Accent), 4f);
            uint w = Col(new Vector4(1f, 1f, 1f, 1f));
            Vector2 a = new(p.X + (box * 0.26f), by + (box * 0.52f));
            Vector2 m = new(p.X + (box * 0.43f), by + (box * 0.70f));
            Vector2 e = new(p.X + (box * 0.76f), by + (box * 0.30f));
            dl.AddLine(a, m, w, 2f);
            dl.AddLine(m, e, w, 2f);
        }
        else
        {
            dl.AddRectFilled(b1, b2, Col(Theme.Frame), 4f);
            dl.AddRect(b1, b2, Col(hovered ? Theme.Accent : Theme.Border), 4f, ImDrawFlags.None, 1.5f);
        }

        dl.AddText(new Vector2(p.X + box + gap, p.Y + ((h - ls.Y) * 0.5f)), Col(Theme.Text), label);
        if (hint is not null)
        {
            dl.AddText(new Vector2(p.X + box + gap, p.Y + h - 2f), Col(Theme.Muted), hint);
        }

        return changed;
    }

    // Rounded button in the HamMeter style. Disabled buttons are drawn faded and never fire.
    public static bool Button(string label, ButtonKind kind = ButtonKind.Normal, bool enabled = true, float width = 0f, Icon? icon = null)
    {
        const float padX = 16f;
        const float padY = 8f;
        const float iconGap = 8f;
        Vector2 ts = ImGui.CalcTextSize(label);
        float iconSize = ts.Y;
        float contentW = ts.X + (icon is null ? 0f : iconSize + iconGap);
        Vector2 size = new(MathF.Max(width, contentW + (padX * 2f)), ts.Y + (padY * 2f));

        Vector2 p = ImGui.GetCursorScreenPos();
        bool clicked = ImGui.InvisibleButton($"##btn_{label}", size) && enabled;
        bool hovered = enabled && ImGui.IsItemHovered();
        bool held = enabled && ImGui.IsItemActive();

        Vector4 bg = kind switch
        {
            ButtonKind.Primary => held ? Theme.AccentActive : hovered ? Theme.AccentHover : Theme.Accent,
            ButtonKind.Danger => held ? Theme.Danger * 0.85f : hovered ? Theme.Danger : Theme.Frame,
            _ => held ? Theme.AccentActive : hovered ? Theme.AccentHover : Theme.Frame,
        };
        float alpha = enabled ? 1f : 0.35f;
        bg.W = alpha;

        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        dl.AddRectFilled(p, p + size, Col(bg), 6f);

        Vector4 fg = Theme.Text;
        fg.W = alpha;
        float x = p.X + ((size.X - contentW) * 0.5f);
        float y = p.Y + padY;
        if (icon is { } i)
        {
            Icons.Draw(dl, i, new Vector2(x, y), iconSize, Col(fg));
            x += iconSize + iconGap;
        }

        dl.AddText(new Vector2(x, y), Col(fg), label);
        return clicked;
    }

    // Selectable card with an icon, a title and a muted description (radio behaviour).
    public static bool OptionCard(string id, Icon icon, string title, string? badge, string description, bool selected, float width)
    {
        const float pad = 14f;
        const float iconSize = 22f;
        float textX = pad + iconSize + 12f;
        float wrap = width - textX - pad;

        Vector2 descSize = ImGui.CalcTextSize(description, wrap);
        float lineH = ImGui.GetTextLineHeight();
        Vector2 size = new(width, pad + lineH + 4f + descSize.Y + pad);

        Vector2 p = ImGui.GetCursorScreenPos();
        bool clicked = ImGui.InvisibleButton($"##opt_{id}", size);
        bool hovered = ImGui.IsItemHovered();

        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        dl.AddRectFilled(p, p + size, Col(hovered && !selected ? Theme.FrameHover : Theme.Frame), 8f);
        dl.AddRect(p, p + size, Col(selected ? Theme.Accent : Theme.Border), 8f, ImDrawFlags.None, 1.5f);

        Icons.Draw(dl, icon, new Vector2(p.X + pad, p.Y + pad), iconSize, Col(selected ? Theme.Accent : Theme.Muted));
        dl.AddText(new Vector2(p.X + textX, p.Y + pad), Col(Theme.Text), title);

        if (badge is not null)
        {
            float bx = p.X + textX + ImGui.CalcTextSize(title).X + 10f;
            Vector2 bs = ImGui.CalcTextSize(badge);
            float scale = 0.8f;
            Vector2 b1 = new(bx, p.Y + pad + 1f);
            Vector2 b2 = new(bx + (bs.X * scale) + 14f, b1.Y + (bs.Y * scale) + 4f);
            dl.AddRectFilled(b1, b2, Col(new Vector4(Theme.Accent.X, Theme.Accent.Y, Theme.Accent.Z, 0.18f)), 6f);
            dl.AddText(ImGui.GetFont(), ImGui.GetFontSize() * scale, new Vector2(b1.X + 7f, b1.Y + 2f), Col(Theme.AccentHover), badge);
        }

        dl.AddText(ImGui.GetFont(), ImGui.GetFontSize(), new Vector2(p.X + textX, p.Y + pad + lineH + 4f), Col(Theme.Muted), description, wrap);
        return clicked;
    }

    // Inline text link: muted lead-in text followed by an accent link (underlined on hover).
    public static bool Link(string lead, string link)
    {
        if (lead.Length > 0)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, Theme.Muted);
            ImGui.TextUnformatted(lead);
            ImGui.PopStyleColor();
            ImGui.SameLine(0f, 5f);
        }

        Vector2 p = ImGui.GetCursorScreenPos();
        Vector2 size = ImGui.CalcTextSize(link);
        bool clicked = ImGui.InvisibleButton($"##link_{link}", size);
        bool hovered = ImGui.IsItemHovered();
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        uint col = Col(hovered ? Theme.AccentHover : Theme.Accent);
        dl.AddText(p, col, link);
        if (hovered)
        {
            dl.AddLine(new Vector2(p.X, p.Y + size.Y), new Vector2(p.X + size.X, p.Y + size.Y), col, 1f);
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        }

        return clicked;
    }

    // Rounded pill (version badge, "Patch notes"). Clickable when an id is given.
    public static bool Pill(string id, string text, Vector4 background, Vector4 foreground, Icon? icon = null, bool dot = false, float scale = 0.85f)
    {
        float size = ImGui.GetFontSize() * scale;
        float textW = TextWidth(text, size);
        float lead = (icon is null ? 0f : size + 5f) + (dot ? 11f : 0f);
        Vector2 p = ImGui.GetCursorScreenPos();
        Vector2 box = new(textW + lead + 20f, size + 8f);

        bool clicked = ImGui.InvisibleButton($"##pill_{id}", box);
        bool hovered = ImGui.IsItemHovered();
        if (hovered)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        }

        Vector4 bg = hovered ? new Vector4(MathF.Min(1f, background.X + 0.06f), MathF.Min(1f, background.Y + 0.06f), MathF.Min(1f, background.Z + 0.06f), MathF.Max(background.W, 0.3f)) : background;
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        dl.AddRectFilled(p, p + box, Col(bg), box.Y * 0.5f);

        float x = p.X + 10f;
        float cy = p.Y + (box.Y * 0.5f);
        if (dot)
        {
            dl.AddCircleFilled(new Vector2(x + 3f, cy), 3f, Col(foreground));
            x += 11f;
        }

        if (icon is { } i)
        {
            Icons.Draw(dl, i, new Vector2(x, cy - (size * 0.5f)), size, Col(foreground));
            x += size + 5f;
        }

        dl.AddText(ImGui.GetFont(), size, new Vector2(x, cy - (size * 0.5f)), Col(hovered ? Theme.Text : foreground), text);
        return clicked;
    }

    // Tooltip in the HamMeter style (dark, rounded, bordered) instead of ImGui's default grey.
    public static void Tooltip(string text)
    {
        ImGui.PushStyleColor(ImGuiCol.PopupBg, Theme.WindowBg);
        ImGui.PushStyleColor(ImGuiCol.Border, Theme.Border);
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.Text);
        ImGui.PushStyleVar(ImGuiStyleVar.PopupRounding, 6f);
        ImGui.PushStyleVar(ImGuiStyleVar.PopupBorderSize, 1f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(10f, 6f));
        ImGui.BeginTooltip();
        ImGui.TextUnformatted(text);
        ImGui.EndTooltip();
        ImGui.PopStyleVar(3);
        ImGui.PopStyleColor(3);
    }

    // Muted, word-wrapped paragraph at the cursor.
    public static void Hint(string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.Muted);
        ImGui.PushTextWrapPos(0f);
        ImGui.TextUnformatted(text);
        ImGui.PopTextWrapPos();
        ImGui.PopStyleColor();
    }

    // Text at a custom size (the font is scaled from its base size).
    public static void Heading(string text, float size)
    {
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        Vector2 p = ImGui.GetCursorScreenPos();
        dl.AddText(ImGui.GetFont(), size, p, Col(Theme.Text), text);
        ImGui.Dummy(new Vector2(TextWidth(text, size), size));
    }

    public static float TextWidth(string s, float size)
    {
        float baseSize = ImGui.GetFontSize();
        return baseSize > 0 ? ImGui.CalcTextSize(s).X * (size / baseSize) : ImGui.CalcTextSize(s).X;
    }

    public static uint Col(Vector4 v) => ImGui.ColorConvertFloat4ToU32(v);
}
