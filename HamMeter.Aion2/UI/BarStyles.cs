using System.Numerics;
using ImGuiNET;

namespace HamMeter.UI;

// One bar style: a name and how to paint it. Ported from WispUI (same author, MIT), which
// explains the layering in detail:
//   Base  - greyscale, MULTIPLIED by the bar colour, so it can only make the bar darker.
//   Light - white with its shape in the alpha channel, drawn over the base and tinted
//           with a lightened bar colour. The only way to get brighter than the colour,
//           which is what makes the glow styles glow in the class colour.
// The pictures are greyscale on purpose: one file takes every class and role colour.
public readonly record struct BarStyle(string Name, string? Texture = null, string? Light = null, float LitAmount = 0f, float LightStrength = 0f);

internal static class BarStyles
{
    // WispUI's tuned values (picked on real bars in four job colours).
    private const float GlowLit = 0.35f;
    private const float GlowStrength = 0.68f;

    // The aurora's peaks are thin curtains, so the same numbers read weaker there.
    private const float AuroraLit = 0.45f;
    private const float AuroraStrength = 0.88f;

    public const string DefaultName = "Smooth";

    public static readonly BarStyle[] All =
    {
        // Drawn, not painted; also the fallback while a texture is not loaded yet.
        new("Flat"),

        new("Smooth", "smooth.png"),
        new("Gradient", "gradient.png"),
        new("Bevel", "bevel.png"),
        new("Sheen", "sheen.png"),

        // The glow family: one quiet base, four light layers over it.
        new("Glow", "body.png", "lit-core.png", GlowLit, GlowStrength),
        new("Glow top", "body.png", "lit-top.png", GlowLit, GlowStrength),
        new("Glow bottom", "body.png", "lit-bottom.png", GlowLit, GlowStrength),
        new("Aurora", "body.png", "aurora.png", AuroraLit, AuroraStrength),
    };

    // Resolves a texture file (Assets/Bars, embedded) to a GPU handle; set by the overlay,
    // which owns the renderer. Zero means "not available", and the bar is drawn flat.
    public static Func<string, IntPtr> Textures { get; set; } = _ => IntPtr.Zero;

    // Position in the list by name. A name that no longer exists (a style removed in an
    // update, a hand-edited config) falls back to the default instead of failing.
    public static int IndexOf(string? name)
    {
        for (int i = 0; i < All.Length; i++)
        {
            if (string.Equals(All[i].Name, name, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return IndexOf(DefaultName);
    }

    public static BarStyle Get(string? name) => All[IndexOf(name)];

    // Paints a style into a rectangle in the given colour (alpha = bar opacity).
    //
    // uvRight: how much of the texture's width the rectangle shows. A meter bar filled to
    // 60% passes 0.6, so it shows the left 60% of the picture instead of the whole picture
    // squeezed into a shorter box: the pattern (the aurora's curtains) stays where it is
    // whatever the value, and the bar is still one rounded shape with round ends on both
    // sides. (Clipping a full-width bar instead cut the right end off square.)
    public static void Draw(ImDrawListPtr dl, BarStyle style, Vector2 min, Vector2 max, uint colour, float radius, float uvRight = 1f)
    {
        IntPtr tex = style.Texture is null ? IntPtr.Zero : Textures(style.Texture);
        if (tex == IntPtr.Zero)
        {
            dl.AddRectFilled(min, max, colour, radius, ImDrawFlags.RoundCornersAll);
            return;
        }

        Vector2 uv1 = new(Math.Clamp(uvRight, 0f, 1f), 1f);
        Paint(dl, tex, min, max, uv1, colour, radius);

        // Light layer: tinted with the lit tone, carrying the bar's own opacity times the
        // style's strength, so a half-transparent bar glows half as hard.
        if (style.Light is not null && Textures(style.Light) is var lit && lit != IntPtr.Zero)
        {
            uint tint = Alpha(Lit(colour, style.LitAmount), Opacity(colour) * style.LightStrength);
            Paint(dl, lit, min, max, uv1, tint, radius);
        }
    }

    private static void Paint(ImDrawListPtr dl, IntPtr tex, Vector2 min, Vector2 max, Vector2 uv1, uint tint, float radius)
    {
        if (radius > 0f)
        {
            dl.AddImageRounded(tex, min, max, Vector2.Zero, uv1, tint, radius, ImDrawFlags.RoundCornersAll);
        }
        else
        {
            dl.AddImage(tex, min, max, Vector2.Zero, uv1, tint);
        }
    }

    // The colour carried toward white by `amount`, opacity unchanged (ImGui colours are ABGR).
    private static uint Lit(uint colour, float amount)
    {
        static uint Mix(uint c, int shift, float t)
        {
            float v = (c >> shift) & 0xFF;
            return (uint)MathF.Round(v + ((255f - v) * t)) << shift;
        }

        return (colour & 0xFF000000u) | Mix(colour, 0, amount) | Mix(colour, 8, amount) | Mix(colour, 16, amount);
    }

    private static float Opacity(uint colour) => ((colour >> 24) & 0xFFu) / 255f;

    private static uint Alpha(uint colour, float alpha) =>
        (colour & 0x00FFFFFFu) | ((uint)Math.Clamp(alpha * 255f, 0f, 255f) << 24);
}
