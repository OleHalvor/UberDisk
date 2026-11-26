
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

/// <summary>The size gradients the chart can use. Colour always means size; this only picks the look.</summary>
public enum SizeStyle
{
    /// <summary>Pale gold for small items, through amber and vermilion, to deep crimson for the biggest.</summary>
    Ember,
    /// <summary>Pale aqua, through teal and blue, to indigo.</summary>
    Ocean,
    /// <summary>Deep blue, through teal and green, to bright yellow: the biggest items glow.</summary>
    Aurora,
    /// <summary>Peach, through coral and rose, to plum.</summary>
    Sunset,
}

/// <summary>
/// Size colouring: an item's size against its biggest sibling picks a colour on a gradient, and colour fades
/// ring by ring so the inner rings lead. Gradients are blended in OKLab, so the steps look even.
/// </summary>
public static class Palette
{
    private static readonly Argb SyntheticColor = Argb.FromRgb(0.32, 0.34, 0.38);

    /// <summary>Colour for the grey "other files" / unseen-space segments.</summary>
    public static Argb Synthetic => SyntheticColor;

    // Stops from smallest to biggest, as 0xRRGGBB.
    private static readonly Dictionary<SizeStyle, Lab[]> Stops = new()
    {
        [SizeStyle.Ember] = Ramp(0xF7E2A1, 0xF4A24F, 0xE0563E, 0xA8234F),
        [SizeStyle.Ocean] = Ramp(0xC4ECE6, 0x5FC4C6, 0x2F88C8, 0x4A3CA6),
        [SizeStyle.Aurora] = Ramp(0x31688F, 0x2A9D8F, 0x7ACB5A, 0xF2E15B),
        [SizeStyle.Sunset] = Ramp(0xFFD3A3, 0xFB8B76, 0xD8508A, 0x7B3B9E),
    };

    /// <param name="style">Which gradient.</param>
    /// <param name="t">Size against the biggest sibling, 0..1.</param>
    /// <param name="ring">Ring the item is drawn in, 1 = innermost.</param>
    /// <param name="rings">Number of rings.</param>
    public static Argb BySize(SizeStyle style, double t, int ring, int rings)
    {
        // Square root spreads the many small items over more of the gradient than a straight line would.
        t = Math.Sqrt(Math.Clamp(t, 0, 1));
        var stops = Stops.TryGetValue(style, out var st) ? st : Stops[SizeStyle.Ember];
        double x = t * (stops.Length - 1);
        int i = Math.Min((int)x, stops.Length - 2);
        var c = Lab.Lerp(stops[i], stops[i + 1], x - i);
        // Outer rings lose chroma and drift a little towards mid grey, never below 40 % colour.
        double keep = Math.Max(0.4, 1 - (ring - 1) * 0.6 / Math.Max(1, rings - 1));
        c = new Lab(c.L + (0.62 - c.L) * (1 - keep) * 0.5, c.A * keep, c.B * keep);
        return c.ToArgb();
    }

    private static Lab[] Ramp(params int[] rgb) => rgb.Select(Lab.FromRgb).ToArray();

    /// <summary>A colour in OKLab.</summary>
    private readonly record struct Lab(double L, double A, double B)
    {
        public static Lab Lerp(Lab x, Lab y, double t) =>
            new(x.L + (y.L - x.L) * t, x.A + (y.A - x.A) * t, x.B + (y.B - x.B) * t);

        public static Lab FromRgb(int rgb)
        {
            double r = Lin((rgb >> 16 & 0xFF) / 255.0), g = Lin((rgb >> 8 & 0xFF) / 255.0), b = Lin((rgb & 0xFF) / 255.0);
            double l = Math.Cbrt(0.4122214708 * r + 0.5363325363 * g + 0.0514459929 * b);
            double m = Math.Cbrt(0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * b);
            double s = Math.Cbrt(0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * b);
            return new(0.2104542553 * l + 0.7936177850 * m - 0.0040720468 * s,
                1.9779984951 * l - 2.4285922050 * m + 0.4505937099 * s,
                0.0259040371 * l + 0.7827717662 * m - 0.8086757660 * s);
        }

        public Argb ToArgb()
        {
            double l = L + 0.3963377774 * A + 0.2158037573 * B;
            double m = L - 0.1055613458 * A - 0.0638541728 * B;
            double s = L - 0.0894841775 * A - 1.2914855480 * B;
            l *= l * l; m *= m * m; s *= s * s;
            return Argb.FromRgb(
                Gam(4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s),
                Gam(-1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s),
                Gam(-0.0041960863 * l - 0.7034186147 * m + 1.7076147010 * s));
        }

        private static double Lin(double c) => c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        private static double Gam(double c) => c <= 0.0031308 ? 12.92 * c : 1.055 * Math.Pow(Math.Max(0, c), 1 / 2.4) - 0.055;
    }
}
