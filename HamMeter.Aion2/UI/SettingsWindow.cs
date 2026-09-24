using System.Numerics;
using ImGuiNET;

namespace HamMeter.UI;

public sealed class SettingsWindow
{
    private const ImGuiColorEditFlags Flags = ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.AlphaPreviewHalf;
    private const ImGuiColorEditFlags JobFlags = Flags | ImGuiColorEditFlags.NoTooltip;

    private static readonly string[] JobIndicatorItems = { "Off", "Text (TEM)", "Icon" };
    private static readonly string[] BarColorItems = { "By role", "By class" };

    // --- Modern theme palette ---
    private static Vector4 Hex(uint rgb, float a = 1f) =>
        new(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, a);

    private static readonly Vector4 ColWindowBg = Hex(0x16161A);
    private static readonly Vector4 ColFrame = Hex(0x22222A);
    private static readonly Vector4 ColFrameHover = Hex(0x2C2C36);
    private static readonly Vector4 ColFrameActive = Hex(0x33333E);
    private static readonly Vector4 ColText = Hex(0xFFFFF2);
    private static readonly Vector4 ColMuted = Hex(0x8E8E93);
    private static readonly Vector4 ColAccent = Hex(0x0A84FF);
    private static readonly Vector4 ColAccentHover = Hex(0x409CFF);
    private static readonly Vector4 ColAccentActive = Hex(0x0066CC);
    private static readonly Vector4 ColBorder = Hex(0x2C2C2E);
    private static readonly Vector4 ColTrack = Hex(0x3A3A42);

    private const float HeaderH = 44f;

    private readonly Config m_config;

    private bool m_testingOpen;
    private bool m_displayOpen;
    private bool m_headerOpen;
    private bool m_barsOpen;
    private bool m_colorsOpen;
    private bool m_appOpen;
    private bool m_jobResetOpen;
    private Vector2 m_winPos;
    private Vector2 m_winSize;
    private Vector2 m_anchorPos;
    private Vector2 m_anchorSize;
    private bool m_hasAnchor;
    private bool m_wasVisible;

    public bool Visible;

    // Raised when the user presses "Quit HamMeter".
    public event Action? QuitRequested;

    // Raised after any setting changed and was saved.
    public event Action? Changed;

    private readonly StyleSelector<BarStyle> m_barStyle;

    public SettingsWindow(Config config)
    {
        m_config = config;

        // The preview shows each texture in the accent colour; on the meter it takes the
        // class or role colour.
        m_barStyle = new StyleSelector<BarStyle>(
            "barstyle",
            BarStyles.All,
            s => s.Name,
            (dl, s, min, max) => BarStyles.Draw(dl, s, min, max, ImGui.GetColorU32(ColAccent), 3f));
    }

    // Development preview: settings open on the bar section, optionally with the texture list open.
    public void ShowBarsSection(bool openTextureList)
    {
        this.Visible = true;
        m_barsOpen = true;
        if (openTextureList)
        {
            m_barStyle.RequestOpen();
        }
    }

    // Called every frame by the meter so the settings window can dock beside it.
    public void SetMeterRect(Vector2 pos, Vector2 size)
    {
        m_anchorPos = pos;
        m_anchorSize = size;
        m_hasAnchor = true;
    }

    public void Draw()
    {
        if (!Visible)
        {
            m_wasVisible = false;
            return;
        }

        // Wait until the meter has reported where it is (it draws after this window), so
        // the first opening docks beside it instead of landing on top of it.
        if (!m_hasAnchor)
        {
            return;
        }

        // When the window first opens, dock it next to the meter, top-aligned. It can
        // be moved freely afterwards; reopening snaps it back beside the meter.
        bool justOpened = !m_wasVisible;
        m_wasVisible = true;
        if (justOpened && m_hasAnchor)
        {
            const float gap = 8f;
            float sw = m_winSize.X > 1f ? m_winSize.X : 470f;
            Vector2 disp = ImGui.GetIO().DisplaySize;
            float x = m_anchorPos.X + m_anchorSize.X + gap;
            if (x + sw > disp.X)
            {
                x = m_anchorPos.X - gap - sw; // no room on the right → left side
            }

            float y = m_anchorPos.Y;
            x = Math.Clamp(x, 0f, MathF.Max(0f, disp.X - sw));
            y = Math.Clamp(y, 0f, MathF.Max(0f, disp.Y - 120f));
            ImGui.SetNextWindowPos(new Vector2(x, y), ImGuiCond.Always);
        }

        int nCol = PushColors();
        int nVar = PushVars();

        ImGui.SetNextWindowSize(new Vector2(470f, 470f), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(new Vector2(440f, 330f), new Vector2(900f, 1600f));
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoCollapse
            | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;

        if (!ImGui.Begin("HamMeter##settings", ref Visible, flags))
        {
            ImGui.End();
            ImGui.PopStyleVar(nVar);
            ImGui.PopStyleColor(nCol);
            return;
        }

        m_winPos = ImGui.GetWindowPos();
        m_winSize = ImGui.GetWindowSize();

        // Self-drawn window background so all four corners are rounded identically.
        ImGui.GetWindowDrawList().AddRectFilled(
            m_winPos,
            new Vector2(m_winPos.X + m_winSize.X, m_winPos.Y + m_winSize.Y),
            ImGui.GetColorU32(ColWindowBg),
            10f,
            ImDrawFlags.RoundCornersAll);

        // Self-drawn rounded border (all four corners) so it matches the meter.
        ImGui.GetWindowDrawList().AddRect(
            m_winPos,
            new Vector2(m_winPos.X + m_winSize.X, m_winPos.Y + m_winSize.Y),
            ImGui.GetColorU32(ColBorder),
            10f,
            ImDrawFlags.RoundCornersAll,
            1f);

        this.DrawHeader();

        bool changed = false;
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(16f, 16f));
        ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(0f, 0f, 0f, 0f));
        ImGuiWindowFlags bodyFlags = m_jobResetOpen ? ImGuiWindowFlags.NoScrollWithMouse : ImGuiWindowFlags.None;
        if (ImGui.BeginChild("##body", new Vector2(0f, -24f), ImGuiChildFlags.Borders, bodyFlags))
        {
            if (this.Bar("Testing", ref m_testingOpen))
            {
                ImGui.Dummy(new Vector2(0f, 4f));
                changed |= CheckboxRow("Test mode (show fake bars)", ref m_config.TestMode);
                ImGui.Indent(24f);
                ImGui.TextDisabled("Turn on to see bars without being in combat.");
                ImGui.Unindent(24f);
                ImGui.Dummy(new Vector2(0f, 4f));
            }

            if (this.Bar("Display", ref m_displayOpen))
            {
                ImGui.Dummy(new Vector2(0f, 4f));
                changed |= CheckboxRow("Only show in combat", ref m_config.OnlyInCombat);
                changed |= CheckboxRow("Confirm before reset", ref m_config.ConfirmReset);
                ImGui.Dummy(new Vector2(0f, 6f));
                changed |= SliderWhole("Combat timeout (s)", ref m_config.CombatTimeout, 3f, 30f);
                ImGui.Indent(24f);
                ImGui.TextDisabled("A fight ends after this many seconds without damage.");
                ImGui.Unindent(24f);
                ImGui.Dummy(new Vector2(0f, 6f));
                changed |= Slider("Background opacity", ref m_config.BackgroundOpacity, 0f, 1f);
                changed |= ColorRow("Background color", ref m_config.BackgroundColor);
                ImGui.Dummy(new Vector2(0f, 4f));
            }

            if (this.Bar("Header", ref m_headerOpen))
            {
                ImGui.Dummy(new Vector2(0f, 4f));
                changed |= SliderWhole("Header height", ref m_config.HeaderHeight, 24f, 56f);
                changed |= Slider("Header opacity", ref m_config.HeaderOpacity, 0f, 1f);
                changed |= ColorRow("Header color", ref m_config.HeaderColor);
                changed |= DragI("Header text size", ref m_config.TopTextSize, 8, 30);
                changed |= SliderWhole("Icon size", ref m_config.IconSize, 10f, 40f);
                changed |= SliderWhole("Icon spacing", ref m_config.IconSpacing, 0f, 20f);
                ImGui.Dummy(new Vector2(0f, 4f));
            }

            if (this.Bar("Bars", ref m_barsOpen))
            {
                ImGui.Dummy(new Vector2(0f, 4f));
                changed |= this.BarStyleRow();
                changed |= SliderWhole("Bar height", ref m_config.BarHeight, 14f, 48f);
                changed |= SliderWhole("Bar spacing", ref m_config.BarSpacing, 0f, 12f);
                changed |= Slider("Bar opacity", ref m_config.BarOpacity, 0f, 1f);
                changed |= ColorRow("Bar background", ref m_config.BarTrackColor);
                ImGui.Dummy(new Vector2(0f, 6f));
                changed |= CheckboxRow("Show rank numbers", ref m_config.ShowRankNumbers);
                changed |= CheckboxRow("Rounded bar corners", ref m_config.RoundedBars);
                changed |= CheckboxRow("Smooth bar animation", ref m_config.SmoothBars);
                ImGui.Dummy(new Vector2(0f, 6f));
                changed |= Combo("Class indicator", ref m_config.JobIndicator, JobIndicatorItems);
                changed |= Combo("Bar color", ref m_config.BarColorMode, BarColorItems);

                SubHeading("Bar text");
                changed |= DragI("Left text size", ref m_config.LeftTextSize, 8, 28);
                changed |= DragI("Right text size", ref m_config.RightTextSize, 8, 28);
                ImGui.Dummy(new Vector2(0f, 6f));
                changed |= CheckboxRow("Short numbers (137K)", ref m_config.ShortNumbers);
                ImGui.Dummy(new Vector2(0f, 4f));
            }

            if (this.Bar("Colors", ref m_colorsOpen))
            {
                ImGui.Dummy(new Vector2(0f, 4f));
                SubHeading("Role colors");
                changed |= ColorRow("Tank", ref m_config.TankColor);
                changed |= ColorRow("Healer", ref m_config.HealerColor);
                changed |= ColorRow("DPS", ref m_config.DpsColor);

                SubHeading("Class colors");
                ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(8f, 0f));
                ImGui.Dummy(new Vector2(0f, 2f));
                ImGui.TextDisabled("Used when 'Bar color' is 'By class'. Hover a swatch for the class name.");
                ImGui.Dummy(new Vector2(0f, 8f));
                ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(12f, 8f));
                if (ImGui.Button("Reset class colors to default"))
                {
                    if (m_config.ConfirmReset)
                    {
                        ImGui.OpenPopup("jobreset_confirm");
                        m_jobResetOpen = true;
                    }
                    else
                    {
                        m_config.ResetJobColors();
                        changed = true;
                    }
                }

                ImGui.PopStyleVar();

                // Confirmation popup (non-modal so ImGui doesn't dim the whole
                // screen). Scrolling is blocked via the body flag, and the window
                // is dimmed locally at the end of the body.
                ImGui.SetNextWindowPos(
                    new Vector2(m_winPos.X + (m_winSize.X * 0.5f), m_winPos.Y + (m_winSize.Y * 0.5f)),
                    ImGuiCond.Appearing,
                    new Vector2(0.5f, 0.5f));

                Theme.PushDialog();
                ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(14f, 12f));
                if (ImGui.BeginPopup("jobreset_confirm", ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoMove))
                {
                    ImGui.TextUnformatted("Reset all class colors to default?");
                    ImGui.Dummy(new Vector2(0f, 6f));
                    ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(12f, 8f));
                    if (ImGui.Button("Reset", new Vector2(100f, 0f)))
                    {
                        m_config.ResetJobColors();
                        changed = true;
                        m_jobResetOpen = false;
                        ImGui.CloseCurrentPopup();
                    }

                    ImGui.SameLine(0f, 16f);
                    if (ImGui.Button("Cancel", new Vector2(100f, 0f)))
                    {
                        m_jobResetOpen = false;
                        ImGui.CloseCurrentPopup();
                    }

                    ImGui.PopStyleVar();
                    ImGui.EndPopup();
                }

                // Keep the flag in sync if the popup was dismissed by clicking away.
                if (m_jobResetOpen && !ImGui.IsPopupOpen("jobreset_confirm"))
                {
                    m_jobResetOpen = false;
                }

                ImGui.PopStyleVar();
                Theme.PopDialog();
                ImGui.Dummy(new Vector2(0f, 10f));
                ImGui.PopStyleVar();

                changed |= this.DrawJobColors();
                ImGui.Dummy(new Vector2(0f, 4f));
            }

            if (this.Bar("Data & App", ref m_appOpen))
            {
                ImGui.Dummy(new Vector2(0f, 4f));
                changed |= CheckboxRow("Record packets (for analysis)", ref m_config.RecordPackets);
                ImGui.Indent(24f);
                ImGui.TextDisabled("Saves the game traffic so a fight can be replayed and checked.");
                ImGui.TextDisabled("Encrypted, readable only by your Windows account.");
                ImGui.TextDisabled($"Turns off on restart, deleted after {Capture.PacketRecorder.RetentionDays} days.");
                ImGui.Unindent(24f);
                ImGui.Dummy(new Vector2(0f, 8f));
                ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(12f, 8f));
                if (ImGui.Button("Quit HamMeter"))
                {
                    this.QuitRequested?.Invoke();
                }

                ImGui.PopStyleVar();
                ImGui.Dummy(new Vector2(0f, 4f));
            }

            ImGui.Dummy(new Vector2(0f, 8f));

            // Dim the whole settings window while the class-reset popup is open. Drawn
            // last (on the body's draw list, full-screen clip) so it covers all
            // content and the header, but stays below the popup window.
            if (m_jobResetOpen)
            {
                ImDrawListPtr ddl = ImGui.GetWindowDrawList();
                ddl.PushClipRectFullScreen();
                ddl.AddRectFilled(
                    m_winPos,
                    new Vector2(m_winPos.X + m_winSize.X, m_winPos.Y + m_winSize.Y),
                    ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.5f)),
                    10f,
                    ImDrawFlags.RoundCornersAll);
                ddl.PopClipRect();
            }
        }

        ImGui.EndChild();
        ImGui.PopStyleColor();
        ImGui.PopStyleVar();

        // Easter-egg footer: pinned to the bottom, centered, deliberately faint.
        {
            ImDrawListPtr fdl = ImGui.GetWindowDrawList();
            ImFontPtr ffont = ImGui.GetFont();
            const string footer = "Princess Donut is watching you!";
            const float fsize = 12f;
            float baseFont = ImGui.GetFontSize();
            float scale = baseFont > 0f ? fsize / baseFont : 1f;
            float fw = ImGui.CalcTextSize(footer).X * scale;
            float fx = m_winPos.X + ((m_winSize.X - fw) * 0.5f);
            float fy = m_winPos.Y + m_winSize.Y - 8f - fsize;
            uint fcol = ImGui.GetColorU32(new Vector4(ColMuted.X, ColMuted.Y, ColMuted.Z, 0.45f));
            fdl.AddText(ffont, fsize, new Vector2(fx, fy), fcol, footer);
        }

        if (changed)
        {
            m_config.Save();
            this.Changed?.Invoke();
        }

        ImGui.End();
        ImGui.PopStyleVar(nVar);
        ImGui.PopStyleColor(nCol);
    }

    private static int PushColors()
    {
        (ImGuiCol Slot, Vector4 Color)[] cols =
        {
            (ImGuiCol.WindowBg, new Vector4(0f, 0f, 0f, 0f)),
            (ImGuiCol.ChildBg, new Vector4(0f, 0f, 0f, 0f)),
            (ImGuiCol.PopupBg, ColWindowBg),
            (ImGuiCol.Text, ColText),
            (ImGuiCol.TextDisabled, ColMuted),
            (ImGuiCol.Border, ColBorder),
            (ImGuiCol.Separator, ColBorder),
            (ImGuiCol.FrameBg, ColFrame),
            (ImGuiCol.FrameBgHovered, ColFrameHover),
            (ImGuiCol.FrameBgActive, ColFrameActive),
            (ImGuiCol.CheckMark, ColAccent),
            (ImGuiCol.SliderGrab, ColAccent),
            (ImGuiCol.SliderGrabActive, ColAccentActive),
            (ImGuiCol.Button, ColFrame),
            (ImGuiCol.ButtonHovered, ColAccentHover),
            (ImGuiCol.ButtonActive, ColAccentActive),
            (ImGuiCol.Header, ColFrame),
            (ImGuiCol.HeaderHovered, ColFrameHover),
            (ImGuiCol.HeaderActive, ColFrameActive),
            (ImGuiCol.ScrollbarBg, new Vector4(0f, 0f, 0f, 0f)),
            (ImGuiCol.ScrollbarGrab, ColBorder),
            (ImGuiCol.ScrollbarGrabHovered, ColFrameHover),
            (ImGuiCol.ScrollbarGrabActive, ColAccent),
            (ImGuiCol.ModalWindowDimBg, new Vector4(0f, 0f, 0f, 0f)),
        };

        foreach ((ImGuiCol slot, Vector4 color) in cols)
        {
            ImGui.PushStyleColor(slot, color);
        }

        return cols.Length;
    }

    private static int PushVars()
    {
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 10f);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 6f);
        ImGui.PushStyleVar(ImGuiStyleVar.GrabRounding, 6f);
        ImGui.PushStyleVar(ImGuiStyleVar.ScrollbarRounding, 6f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(8f, 12f));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemInnerSpacing, new Vector2(8f, 4f));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(0f, 0f));
        return 9;
    }

    private void DrawHeader()
    {
        Vector2 wp = ImGui.GetWindowPos();
        float ww = ImGui.GetWindowWidth();
        ImDrawListPtr dl = ImGui.GetWindowDrawList();

        // Header background, rounded only at the top to match the window corners.
        dl.AddRectFilled(wp, new Vector2(wp.X + ww, wp.Y + HeaderH), ImGui.GetColorU32(ColFrame), 10f, ImDrawFlags.RoundCornersTop);

        ImFontPtr font = ImGui.GetFont();
        float baseSize = ImGui.GetFontSize();
        const float titleSize = 20f;
        const float subSize = 15f;

        Vector2 ts = ImGui.CalcTextSize("HamMeter");
        float titleW = ts.X * (titleSize / baseSize);
        float titleH = ts.Y * (titleSize / baseSize);
        float subH = ts.Y * (subSize / baseSize);

        float tx = wp.X + 16f;
        dl.AddText(font, titleSize, new Vector2(tx, wp.Y + ((HeaderH - titleH) * 0.5f)), ImGui.GetColorU32(ColText), "HamMeter");
        dl.AddText(font, subSize, new Vector2(tx + titleW + 10f, wp.Y + ((HeaderH - subH) * 0.5f)), ImGui.GetColorU32(ColMuted), "Settings");

        // Custom close button (no boxy default frame).
        const float bs = 26f;
        Vector2 bpos = new(wp.X + ww - bs - 12f, wp.Y + ((HeaderH - bs) * 0.5f));
        ImGui.SetCursorScreenPos(bpos);
        ImGui.InvisibleButton("##close", new Vector2(bs, bs));
        bool hov = ImGui.IsItemHovered();
        if (ImGui.IsItemClicked())
        {
            Visible = false;
        }

        uint xcol = ImGui.GetColorU32(hov ? ColText : ColMuted);
        const float glyph = 16f;
        Icons.Draw(dl, Icon.Close, new Vector2(bpos.X + ((bs - glyph) * 0.5f), bpos.Y + ((bs - glyph) * 0.5f)), glyph, xcol);

        ImGui.SetCursorPos(new Vector2(0f, HeaderH));
    }

    private static bool CheckboxRow(string label, ref bool value) => Widgets.Checkbox(label, ref value);

    // 40/60 row like the others: "Bar texture" left, the texture selector right.
    private bool BarStyleRow()
    {
        float startX = ImGui.GetCursorPosX();
        float full = ImGui.GetContentRegionAvail().X;
        float labelW = full * 0.4f;
        float rowY = ImGui.GetCursorPosY();
        ImGui.SetCursorPosY(rowY + ((30f - ImGui.GetTextLineHeight()) * 0.5f));
        ImGui.TextDisabled("Bar texture");
        ImGui.SetCursorPos(new Vector2(startX + labelW, rowY));

        int index = BarStyles.IndexOf(m_config.BarStyle);
        if (m_barStyle.Draw(ref index, full - labelW))
        {
            m_config.BarStyle = BarStyles.All[index].Name;
            return true;
        }

        return false;
    }

    private static void Section(string label)
    {
        ImGui.Dummy(new Vector2(0f, 2f));
        ImGui.TextDisabled(label.ToUpperInvariant());
        ImGui.SameLine();

        Vector2 c = ImGui.GetCursorScreenPos();
        float lineY = c.Y + (ImGui.GetTextLineHeight() * 0.5f);
        float x1 = c.X + 6f;
        float x2 = ImGui.GetWindowPos().X + ImGui.GetWindowSize().X - 16f;
        if (x2 > x1)
        {
            ImGui.GetWindowDrawList().AddLine(new Vector2(x1, lineY), new Vector2(x2, lineY), ImGui.GetColorU32(ColBorder), 1f);
        }

        ImGui.NewLine();
        ImGui.Dummy(new Vector2(0f, 2f));
    }

    // 40/60 row: muted label left, control fills the right column.
    private static void RowLabel(string label, out float startX, out float full, out float labelW)
    {
        startX = ImGui.GetCursorPosX();
        full = ImGui.GetContentRegionAvail().X;
        labelW = full * 0.4f;
        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled(label);
        ImGui.SameLine();
        ImGui.SetCursorPosX(startX + labelW);
        ImGui.SetNextItemWidth(full - labelW);
    }

    private static bool Slider(string label, ref float value, float min, float max)
    {
        float frac = (value - min) / (max - min);
        if (SliderRow(label, ref frac, value.ToString("0.0")))
        {
            float nv = MathF.Round((min + (frac * (max - min))) * 10f) / 10f;
            if (Math.Abs(nv - value) > 0.0001f)
            {
                value = nv;
                return true;
            }
        }

        return false;
    }

    private static bool DragI(string label, ref int value, int min, int max)
    {
        float frac = (float)(value - min) / (max - min);
        if (SliderRow(label, ref frac, value.ToString()))
        {
            int nv = Math.Clamp((int)MathF.Round(min + (frac * (max - min))), min, max);
            if (nv != value)
            {
                value = nv;
                return true;
            }
        }

        return false;
    }

    // Like Slider but snaps to whole numbers (for pixel-like values such as icon size).
    private static bool SliderWhole(string label, ref float value, float min, float max)
    {
        float frac = (value - min) / (max - min);
        if (SliderRow(label, ref frac, value.ToString("0")))
        {
            float nv = MathF.Round(min + (frac * (max - min)));
            if (Math.Abs(nv - value) > 0.001f)
            {
                value = nv;
                return true;
            }
        }

        return false;
    }

    // Custom slider: muted label left, value next to it, filled track + round grab right.
    private static bool SliderRow(string label, ref float frac, string valueText)
    {
        float startX = ImGui.GetCursorPosX();
        float full = ImGui.GetContentRegionAvail().X;
        float labelW = full * 0.4f;

        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled(label);
        ImGui.SameLine();
        ImGui.SetCursorPosX(startX + labelW);

        float h = ImGui.GetFrameHeight();
        Vector2 fieldPos = ImGui.GetCursorScreenPos();
        const float valueColW = 46f;
        const float gap = 10f;
        float trackW = MathF.Max(24f, full - labelW - valueColW - gap);

        ImDrawListPtr dl = ImGui.GetWindowDrawList();

        // Value sits left of the track (between label and track), like the mockup.
        Vector2 tsz = ImGui.CalcTextSize(valueText);
        dl.AddText(new Vector2(fieldPos.X, fieldPos.Y + ((h - tsz.Y) * 0.5f)), ImGui.GetColorU32(ColText), valueText);

        float trackX = fieldPos.X + valueColW + gap;
        const float grabR = 7f;
        ImGui.SetCursorScreenPos(new Vector2(trackX - grabR, fieldPos.Y));
        ImGui.InvisibleButton($"##{label}", new Vector2(trackW + (grabR * 2f), h));
        bool active = ImGui.IsItemActive();
        bool hot = active || ImGui.IsItemHovered();

        bool changed = false;
        if (active)
        {
            float mx = ImGui.GetIO().MousePos.X;
            frac = Math.Clamp((mx - trackX) / trackW, 0f, 1f);
            changed = true;
        }

        const float trackH = 6f;
        float ty = fieldPos.Y + ((h - trackH) * 0.5f);
        dl.AddRectFilled(new Vector2(trackX, ty), new Vector2(trackX + trackW, ty + trackH), ImGui.GetColorU32(ColTrack), trackH * 0.5f);

        float fillW = trackW * Math.Clamp(frac, 0f, 1f);
        if (fillW > 0.5f)
        {
            dl.AddRectFilled(new Vector2(trackX, ty), new Vector2(trackX + fillW, ty + trackH), ImGui.GetColorU32(hot ? ColAccentHover : ColAccent), trackH * 0.5f);
        }

        float gx = trackX + fillW;
        float gy = fieldPos.Y + (h * 0.5f);
        dl.AddCircleFilled(new Vector2(gx, gy), 7f, ImGui.GetColorU32(hot ? ColAccentHover : ColAccent));
        dl.AddCircle(new Vector2(gx, gy), 7f, ImGui.GetColorU32(ColAccentActive), 0, 1.8f);

        return changed;
    }

    private static bool ColorRow(string label, ref Vector4 value)
    {
        float startX = ImGui.GetCursorPosX();
        float full = ImGui.GetContentRegionAvail().X;
        float labelW = full * 0.4f;
        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled(label);
        ImGui.SameLine();
        ImGui.SetCursorPosX(startX + labelW);
        return ImGui.ColorEdit4($"##{label}", ref value, Flags);
    }

    private static bool Combo(string label, ref int value, string[] items)
    {
        RowLabel(label, out _, out _, out _);
        bool changed = false;
        int current = value < 0 || value >= items.Length ? 0 : value;
        Theme.PushDropdown();
        if (ImGui.BeginCombo($"##{label}", items[current]))
        {
            for (int i = 0; i < items.Length; i++)
            {
                if (ImGui.Selectable(items[i], i == current))
                {
                    value = i;
                    changed = true;
                }
            }

            ImGui.EndCombo();
        }

        Theme.PopDropdown();
        return changed;
    }

    // A collapsible bar with a tight 8px gap above it.
    private bool Bar(string label, ref bool open)
    {
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(8f, 8f));
        bool result = this.CollapsingSection(label, ref open);
        ImGui.PopStyleVar();
        return result;
    }

    // A collapsible bar drawn in the same dark style as the window header.
    private bool CollapsingSection(string label, ref bool open)
    {
        float w = ImGui.GetContentRegionAvail().X;
        const float h = 32f;
        Vector2 p = ImGui.GetCursorScreenPos();

        ImGui.InvisibleButton($"##sec_{label}", new Vector2(w, h));
        bool hovered = ImGui.IsItemHovered();
        if (ImGui.IsItemClicked())
        {
            open = !open;
        }

        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        dl.AddRectFilled(p, new Vector2(p.X + w, p.Y + h), ImGui.GetColorU32(hovered ? ColFrameHover : ColFrame), 6f);

        uint icol = ImGui.GetColorU32(ColText);
        float cxx = p.X + 16f;
        float cyy = p.Y + (h * 0.5f);
        if (open)
        {
            dl.AddTriangleFilled(new Vector2(cxx - 5f, cyy - 3f), new Vector2(cxx + 5f, cyy - 3f), new Vector2(cxx, cyy + 4f), icol);
        }
        else
        {
            dl.AddTriangleFilled(new Vector2(cxx - 3f, cyy - 5f), new Vector2(cxx - 3f, cyy + 5f), new Vector2(cxx + 4f, cyy), icol);
        }

        Vector2 ls = ImGui.CalcTextSize(label);
        dl.AddText(new Vector2(p.X + 32f, cyy - (ls.Y * 0.5f)), icol, label);

        return open;
    }

    // A sub-heading inside a collapsible panel, styled like a section header.
    private static void SubHeading(string label)
    {
        Section(label);
    }

    private bool DrawJobColors()
    {
        bool changed = false;
        (string Role, string[] Jobs)[] groups = ClassInfo.Groups;
        float gap = 8f;
        float avail = ImGui.GetContentRegionAvail().X;
        float colW = (avail - gap) / 2f;
        const ImGuiWindowFlags cflags = ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;

        int i = 0;
        for (; i + 1 < groups.Length; i += 2)
        {
            int n = Math.Max(groups[i].Jobs.Length, groups[i + 1].Jobs.Length);
            float h = JobGroupHeight(n);

            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(0f, 0f));
            ImGui.BeginChild($"##jc{i}a", new Vector2(colW, h), ImGuiChildFlags.None, cflags);
            changed |= this.DrawJobGroup(groups[i].Role, groups[i].Jobs);
            ImGui.EndChild();

            ImGui.SameLine(0f, gap);

            ImGui.BeginChild($"##jc{i}b", new Vector2(colW, h), ImGuiChildFlags.None, cflags);
            changed |= this.DrawJobGroup(groups[i + 1].Role, groups[i + 1].Jobs);
            ImGui.EndChild();
            ImGui.PopStyleVar();
        }

        if (i < groups.Length)
        {
            changed |= this.DrawJobGroup(groups[i].Role, groups[i].Jobs);
        }

        return changed;
    }

    private static float JobGroupHeight(int n)
    {
        float spacing = ImGui.GetStyle().ItemSpacing.Y;
        return ImGui.GetTextLineHeightWithSpacing()
             + 4f + spacing
             + (n * ImGui.GetFrameHeightWithSpacing())
             + 4f;
    }

    private bool DrawJobGroup(string role, string[] jobs)
    {
        bool changed = false;
        ImGui.TextDisabled(role);

        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        Vector2 lp = ImGui.GetCursorScreenPos();
        float lw = ImGui.GetContentRegionAvail().X;
        dl.AddLine(new Vector2(lp.X, lp.Y + 1f), new Vector2(lp.X + lw, lp.Y + 1f), ImGui.GetColorU32(ColBorder), 1f);
        ImGui.Dummy(new Vector2(0f, 4f));

        foreach (string job in jobs)
        {
            Vector4 col = m_config.JobColors.TryGetValue(job, out Vector4 c) ? c : new Vector4(1f, 1f, 1f, 1f);
            if (ImGui.ColorEdit4(job, ref col, JobFlags))
            {
                m_config.JobColors[job] = col;
                changed = true;
            }

            if (ImGui.IsItemHovered())
            {
                int r = (int)MathF.Round(col.X * 255f);
                int g = (int)MathF.Round(col.Y * 255f);
                int b = (int)MathF.Round(col.Z * 255f);
                ImGui.SetTooltip($"{ClassInfo.FullName(job)}\n#{r:X2}{g:X2}{b:X2}");
            }
        }

        return changed;
    }
}
