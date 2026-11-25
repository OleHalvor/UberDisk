using UberDisk.Core.Model;

namespace UberDisk.Core.Viz;

/// <summary>A colour as 0xAARRGGBB, so Core needs no graphics library.</summary>
public readonly record struct Argb(uint Value)
{
    public byte A => (byte)(Value >> 24);
    public byte R => (byte)(Value >> 16);
    public byte G => (byte)(Value >> 8);
    public byte B => (byte)Value;

    public static Argb FromRgb(double r, double g, double b) =>
        new(0xFF000000u | (Clamp(r) << 16) | (Clamp(g) << 8) | Clamp(b));

    private static uint Clamp(double v) => (uint)Math.Clamp((int)Math.Round(v * 255), 0, 255);

    public Argb WithAlpha(byte a) => new((Value & 0x00FFFFFFu) | ((uint)a << 24));
}

/// <summary>
/// Mockup A palette: each top-level folder of the drive owns a hue and keeps it at every zoom level. Rings
/// get lighter with depth. Files are drawn desaturated, so folders stand out.
/// </summary>
public static class Palette
{
    /// <summary>Hues in degrees: red, orange, blue, purple, green, teal, gold, pink, sky, lime, violet, coral.</summary>
    public static readonly double[] Hues = { 356, 24, 212, 268, 140, 182, 44, 326, 196, 88, 248, 8 };

    private static readonly Argb SyntheticColor = Argb.FromRgb(0.32, 0.34, 0.38);

    /// <param name="node">The node to colour.</param>
    /// <param name="absDepth">Depth below the drive root (top-level folders are 1).</param>
    public static Argb For(Node node, int absDepth)
    {
        if (node is DirNode { IsSynthetic: true }) return SyntheticColor;

        var top = node.TopLevel;
        int slot = top is DirNode td && td.HueSlot >= 0 ? td.HueSlot : StableSlot(top?.Name ?? node.Name);
        double hue = Hues[slot % Hues.Length];
        // Cycle through slightly different shades once the base hues have all been used.
        double lift = slot / Hues.Length % 3 * 0.06;

        int d = Math.Max(1, absDepth);
        double light = Math.Min(0.78, 0.36 + lift + (d - 1) * 0.055);

        if (node is FileNode f)
        {
            double sat = f.IsAggregate ? 0.06 : 0.16;
            return Hsl(hue, sat, Math.Min(0.74, light + 0.04));
        }
        double s = Math.Max(0.38, 0.66 - (d - 1) * 0.04);
        return Hsl(hue, s, light);
    }

    /// <summary>Colour for the biggest items (OverDisk's size colouring runs from this to <see cref="SmallColor"/>).</summary>
    public static readonly Argb BigColor = Argb.FromRgb(0.89, 0.25, 0.20);
    /// <summary>Colour for the smallest items.</summary>
    public static readonly Argb SmallColor = Argb.FromRgb(0.96, 0.84, 0.30);

    /// <summary>
    /// OverDisk's size colouring: <paramref name="t"/> (size against the biggest sibling, 0..1) blends linearly from
    /// the small colour to the big one, and colour fades toward grey ring by ring (ring 1 is full colour).
    /// OverDisk fades to almost grey at the last ring; here it keeps at least 35 % so the outer rings stay readable.
    /// </summary>
    public static Argb BySize(double t, int ring, int rings)
    {
        t = Math.Clamp(t, 0, 1);
        double r = (SmallColor.R + (BigColor.R - SmallColor.R) * t) / 255.0;
        double g = (SmallColor.G + (BigColor.G - SmallColor.G) * t) / 255.0;
        double b = (SmallColor.B + (BigColor.B - SmallColor.B) * t) / 255.0;
        double keep = Math.Max(0.35, (double)(rings - (ring - 1)) / rings);
        double grey = 0.299 * r + 0.587 * g + 0.114 * b;
        return Argb.FromRgb(grey + (r - grey) * keep, grey + (g - grey) * keep, grey + (b - grey) * keep);
    }

    /// <summary>Fallback slot from the name (top-level files at the root, or nodes before their slot is set).</summary>
    public static int StableSlot(string name)
    {
        uint h = 2166136261;
        foreach (char c in name.ToUpperInvariant()) { h ^= c; h *= 16777619; }
        return (int)(h % (uint)Hues.Length);
    }

    public static Argb Hsl(double h, double s, double l)
    {
        h = ((h % 360) + 360) % 360 / 360.0;
        if (s <= 0) return Argb.FromRgb(l, l, l);
        double q = l < 0.5 ? l * (1 + s) : l + s - l * s;
        double p = 2 * l - q;
        return Argb.FromRgb(Hue(p, q, h + 1.0 / 3), Hue(p, q, h), Hue(p, q, h - 1.0 / 3));
    }

    private static double Hue(double p, double q, double t)
    {
        if (t < 0) t += 1;
        if (t > 1) t -= 1;
        if (t < 1.0 / 6) return p + (q - p) * 6 * t;
        if (t < 1.0 / 2) return q;
        if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
        return p;
    }
}
