using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Numerics;
using System.Runtime.InteropServices;
using ClickableTransparentOverlay;
using HamMeter.UI;
using ImGuiNET;

namespace HamMeter.Wizard;

// The pages of one wizard (setup or uninstall). The window draws the HamMeter chrome
// around them: header, step list, footer buttons.
internal abstract class WizardFlow
{
    public abstract string Subtitle { get; }

    public abstract IReadOnlyList<string> Steps { get; }

    public abstract int Current { get; }

    public virtual bool CanGoBack => this.Current > 0;

    public virtual bool CanClose => true;

    public abstract string NextLabel { get; }

    public abstract bool NextEnabled { get; }

    public virtual ButtonKind NextKind => ButtonKind.Primary;

    // Set by the window; a flow calls it to end the wizard.
    public Action Close { get; set; } = () => { };

    public abstract void DrawPage(float width);

    public abstract void Back();

    public abstract void Next();
}

// A small, centred, rounded window in the HamMeter design (not a full-screen overlay).
internal sealed class WizardWindow : Overlay
{
    private const int Width = 660;
    private const int Height = 560;
    private const float HeaderH = 44f;
    private const float FooterH = 60f;
    private const float StepsW = 170f;
    private const int FontSize = 18;

    private readonly WizardFlow m_flow;
    private Point m_dragWindowStart;
    private Point m_dragCursorStart;

    public WizardWindow(WizardFlow flow)
        : base("HamMeter", true, Width, Height)
    {
        m_flow = flow;
        m_flow.Close = this.Close;
    }

    protected override unsafe Task PostInitialized()
    {
        this.VSync = true;
        ImGui.GetIO().NativePtr->IniFilename = null; // never write imgui.ini (setup runs from Downloads)

        string fonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        string? font = new[] { Path.Combine(fonts, "msjh.ttc"), Path.Combine(fonts, "segoeui.ttf") }.FirstOrDefault(File.Exists);
        if (font is not null)
        {
            // Latin + Latin-1 (umlauts), general punctuation (…, –, “ ”) and arrows (→).
            ushort[] ranges = [0x0020, 0x00FF, 0x2000, 0x206F, 0x2190, 0x21FF, 0];
            this.ReplaceFont(font, FontSize, ranges);
        }

        this.Position = new Point(
            (GetSystemMetrics(SmCxScreen) - Width) / 2,
            (GetSystemMetrics(SmCyScreen) - Height) / 2);
        ShowInTaskbar(this.window.Handle);
        return Task.CompletedTask;
    }

    protected override void Render()
    {
        ImGui.SetNextWindowPos(Vector2.Zero);
        ImGui.SetNextWindowSize(new Vector2(Width, Height));
        ImGui.PushStyleColor(ImGuiCol.WindowBg, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.ChildBg, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.Text);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarBg, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrab, Theme.Border);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrabHovered, Theme.FrameHover);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrabActive, Theme.Accent);
        ImGui.PushStyleVar(ImGuiStyleVar.ScrollbarRounding, 6f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(8f, 10f));

        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove
            | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoCollapse
            | ImGuiWindowFlags.NoSavedSettings;

        if (ImGui.Begin("##wizard", flags))
        {
            ImDrawListPtr dl = ImGui.GetWindowDrawList();
            Vector2 size = new(Width, Height);
            dl.AddRectFilled(Vector2.Zero, size, Widgets.Col(Theme.WindowBg), 10f);
            dl.AddRect(Vector2.Zero, size, Widgets.Col(Theme.Border), 10f, ImDrawFlags.RoundCornersAll, 1f);

            this.DrawHeader(dl);
            this.DrawSteps(dl);

            // Page content.
            const float pad = 20f;
            ImGui.SetCursorPos(new Vector2(StepsW + pad, HeaderH + 18f));
            ImGui.BeginChild("##page", new Vector2(Width - StepsW - (pad * 2f), Height - HeaderH - FooterH - 24f));
            ImGui.PushTextWrapPos(0f);
            m_flow.DrawPage(ImGui.GetContentRegionAvail().X);
            ImGui.PopTextWrapPos();
            ImGui.EndChild();

            this.DrawFooter(dl);
        }

        ImGui.End();
        ImGui.PopStyleVar(4);
        ImGui.PopStyleColor(7);
    }

    private void DrawHeader(ImDrawListPtr dl)
    {
        dl.AddRectFilled(Vector2.Zero, new Vector2(Width, HeaderH), Widgets.Col(Theme.Frame), 10f, ImDrawFlags.RoundCornersTop);

        ImFontPtr font = ImGui.GetFont();
        const float titleSize = 20f;
        const float subSize = 15f;
        float titleW = Widgets.TextWidth("HamMeter", titleSize);
        dl.AddText(font, titleSize, new Vector2(16f, (HeaderH - titleSize) * 0.5f), Widgets.Col(Theme.Text), "HamMeter");
        dl.AddText(font, subSize, new Vector2(16f + titleW + 10f, (HeaderH - subSize) * 0.5f + 1f), Widgets.Col(Theme.Muted), m_flow.Subtitle);

        // Drag the window by its header (everything left of the close button).
        const float bs = 26f;
        ImGui.SetCursorPos(Vector2.Zero);
        ImGui.InvisibleButton("##drag", new Vector2(Width - bs - 24f, HeaderH));
        if (ImGui.IsItemActivated())
        {
            GetCursorPos(out m_dragCursorStart);
            m_dragWindowStart = this.Position;
        }
        else if (ImGui.IsItemActive())
        {
            GetCursorPos(out Point now);
            this.Position = new Point(
                m_dragWindowStart.X + (now.X - m_dragCursorStart.X),
                m_dragWindowStart.Y + (now.Y - m_dragCursorStart.Y));
        }

        if (!m_flow.CanClose)
        {
            return;
        }

        Vector2 bpos = new(Width - bs - 12f, (HeaderH - bs) * 0.5f);
        ImGui.SetCursorPos(bpos);
        if (ImGui.InvisibleButton("##close", new Vector2(bs, bs)))
        {
            this.Close();
        }

        const float glyph = 16f;
        uint xcol = Widgets.Col(ImGui.IsItemHovered() ? Theme.Text : Theme.Muted);
        Icons.Draw(dl, Icon.Close, new Vector2(bpos.X + ((bs - glyph) * 0.5f), bpos.Y + ((bs - glyph) * 0.5f)), glyph, xcol);
    }

    private void DrawSteps(ImDrawListPtr dl)
    {
        float y = HeaderH + 16f;
        const float rowH = 32f;
        for (int i = 0; i < m_flow.Steps.Count; i++)
        {
            bool current = i == m_flow.Current;
            bool done = i < m_flow.Current;
            if (current)
            {
                dl.AddRectFilled(new Vector2(16f, y), new Vector2(StepsW, y + rowH), Widgets.Col(Theme.Frame), 6f, ImDrawFlags.RoundCornersLeft);
            }

            Vector4 dot = done ? Theme.Success : current ? Theme.Accent : Theme.Track;
            dl.AddCircleFilled(new Vector2(30f, y + (rowH * 0.5f)), 3.5f, Widgets.Col(dot));

            float ts = ImGui.GetFontSize() * 0.9f;
            dl.AddText(ImGui.GetFont(), ts, new Vector2(42f, y + ((rowH - ts) * 0.5f)), Widgets.Col(current || done ? Theme.Text : Theme.Muted), m_flow.Steps[i]);
            y += rowH + 2f;
        }
    }

    private void DrawFooter(ImDrawListPtr dl)
    {
        float top = Height - FooterH;
        dl.AddLine(new Vector2(1f, top), new Vector2(Width - 1f, top), Widgets.Col(Theme.Border), 1f);

        // Easter egg, same as the settings window.
        const string footer = "Princess Donut is watching you!";
        const float fsize = 12f;
        dl.AddText(ImGui.GetFont(), fsize, new Vector2(16f, top + ((FooterH - fsize) * 0.5f)),
            Widgets.Col(new Vector4(Theme.Muted.X, Theme.Muted.Y, Theme.Muted.Z, 0.45f)), footer);

        // Buttons, right-aligned: [Back] [Next].
        const float btnW = 110f;
        float btnH = ImGui.GetTextLineHeight() + 16f;
        float y = top + ((FooterH - btnH) * 0.5f);
        ImGui.SetCursorPos(new Vector2(Width - 16f - btnW, y));
        if (Widgets.Button(m_flow.NextLabel, m_flow.NextKind, m_flow.NextEnabled, btnW))
        {
            m_flow.Next();
        }

        if (m_flow.CanGoBack)
        {
            ImGui.SetCursorPos(new Vector2(Width - 16f - (btnW * 2f) - 10f, y));
            if (Widgets.Button(Strings.Back, ButtonKind.Normal, true, btnW))
            {
                m_flow.Back();
            }
        }
    }

    // The overlay library creates a topmost tool window (no taskbar button). A wizard
    // should behave like a normal app window: taskbar button, not always on top.
    private static void ShowInTaskbar(IntPtr hwnd)
    {
        const int gwlExStyle = -20;
        const long wsExToolWindow = 0x00000080;
        const long wsExAppWindow = 0x00040000;
        const long wsExTopmost = 0x00000008;
        const uint swpNoSize = 0x1, swpNoMove = 0x2, swpNoActivate = 0x10, swpFrameChanged = 0x20;

        ShowWindow(hwnd, 0);

        // Use the exe's HamMeter icon for the taskbar and Alt+Tab.
        IntPtr icon = ExtractIcon(IntPtr.Zero, Environment.ProcessPath ?? string.Empty, 0);
        if (icon.ToInt64() > 1)
        {
            SendMessage(hwnd, 0x0080, new IntPtr(1), icon); // WM_SETICON, ICON_BIG
            SendMessage(hwnd, 0x0080, IntPtr.Zero, icon);   // WM_SETICON, ICON_SMALL
        }

        long style = GetWindowLongPtr(hwnd, gwlExStyle).ToInt64();
        style = (style & ~wsExToolWindow & ~wsExTopmost) | wsExAppWindow;
        SetWindowLongPtr(hwnd, gwlExStyle, new IntPtr(style));
        SetWindowPos(hwnd, new IntPtr(-2), 0, 0, 0, 0, swpNoSize | swpNoMove | swpNoActivate | swpFrameChanged);
        ShowWindow(hwnd, 5);
        SetForegroundWindow(hwnd);
    }

    private const int SmCxScreen = 0;
    private const int SmCyScreen = 1;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int cmd);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr ExtractIcon(IntPtr instance, string file, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
}

// Small pieces shared by the setup and uninstall pages.
internal static class WizardHelpers
{
    internal static void RunElevated(string file, string arguments) =>
        Process.Start(new ProcessStartInfo(file, arguments) { UseShellExecute = true, Verb = "runas" })?.Dispose();

    internal static void InfoBox(string text, float width)
    {
        const float pad = 10f;
        const float iconSize = 16f;
        float wrap = width - (pad * 3f) - iconSize;
        Vector2 ts = ImGui.CalcTextSize(text, wrap);
        Vector2 p = ImGui.GetCursorScreenPos();
        Vector2 size = new(width, ts.Y + (pad * 2f));

        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        dl.AddRectFilled(p, p + size, Widgets.Col(Theme.Frame), 6f);
        Icons.Draw(dl, Icon.Info, new Vector2(p.X + pad, p.Y + pad), iconSize, Widgets.Col(Theme.Muted));
        dl.AddText(ImGui.GetFont(), ImGui.GetFontSize(), new Vector2(p.X + (pad * 2f) + iconSize, p.Y + pad), Widgets.Col(Theme.Muted), text, wrap);
        ImGui.Dummy(size);
    }
}
