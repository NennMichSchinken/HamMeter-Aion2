using System.Numerics;
using ImGuiNET;

namespace HamMeter.UI;

// Central design tokens for the modern UI. Change a value here once and every
// dialog / dropdown that uses these helpers updates automatically.
internal static class Theme
{
    // Shared palette (matches the settings window).
    public static readonly Vector4 WindowBg = Hex(0x16161A);
    public static readonly Vector4 Frame = Hex(0x22222A);
    public static readonly Vector4 FrameHover = Hex(0x2C2C36);
    public static readonly Vector4 FrameActive = Hex(0x33333E);
    public static readonly Vector4 Text = Hex(0xFFFFF2);
    public static readonly Vector4 Muted = Hex(0x8E8E93);
    public static readonly Vector4 Border = Hex(0x2C2C2E);
    public static readonly Vector4 Track = Hex(0x3A3A42);
    public static readonly Vector4 Accent = Hex(0x0A84FF);
    public static readonly Vector4 AccentHover = Hex(0x409CFF);
    public static readonly Vector4 AccentActive = Hex(0x0066CC);
    public static readonly Vector4 Success = Hex(0x32D74B);
    public static readonly Vector4 Danger = Hex(0xFF453A);

    // Rounding tokens: large surfaces (dialogs) round more than small menus (dropdowns).
    public const float DialogRounding = 10f;
    public const float DropdownRounding = 6f;

    private static Vector4 Hex(uint rgb) => new(
        ((rgb >> 16) & 0xFF) / 255f,
        ((rgb >> 8) & 0xFF) / 255f,
        (rgb & 0xFF) / 255f,
        1f);

    // Confirmation dialog: rounded, bordered, dark buttons that turn blue on hover.
    public static void PushDialog()
    {
        ImGui.PushStyleColor(ImGuiCol.PopupBg, WindowBg);
        ImGui.PushStyleColor(ImGuiCol.Text, Text);
        ImGui.PushStyleColor(ImGuiCol.Border, Border);
        ImGui.PushStyleColor(ImGuiCol.Button, Frame);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, AccentHover);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, AccentActive);
        ImGui.PushStyleVar(ImGuiStyleVar.PopupRounding, DialogRounding);
        ImGui.PushStyleVar(ImGuiStyleVar.PopupBorderSize, 1f);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 6f);
    }

    public static void PopDialog()
    {
        ImGui.PopStyleVar(3);
        ImGui.PopStyleColor(6);
    }

    // Dropdown / combo menu: same colours, less rounding, highlight in frame tones.
    public static void PushDropdown()
    {
        ImGui.PushStyleColor(ImGuiCol.PopupBg, WindowBg);
        ImGui.PushStyleColor(ImGuiCol.Text, Text);
        ImGui.PushStyleColor(ImGuiCol.Border, Border);
        ImGui.PushStyleColor(ImGuiCol.Header, Frame);
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered, FrameHover);
        ImGui.PushStyleColor(ImGuiCol.HeaderActive, FrameActive);
        ImGui.PushStyleVar(ImGuiStyleVar.PopupRounding, DropdownRounding);
        ImGui.PushStyleVar(ImGuiStyleVar.PopupBorderSize, 1f);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 6f);
    }

    public static void PopDropdown()
    {
        ImGui.PopStyleVar(3);
        ImGui.PopStyleColor(6);
    }
}
