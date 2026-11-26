using System.IO;
using UberDisk.Core.Viz;

namespace UberDisk.App;

/// <summary>The few user choices worth remembering, in %APPDATA%\UberDisk\settings.ini (key=value lines).</summary>
internal static class AppSettings
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UberDisk", "settings.ini");

    /// <summary>How scans of the whole drive are animated.</summary>
    public static ScanAnimation Animation { get; set; } = ScanAnimation.Spiral;

    /// <summary>Gradient for the size colouring.</summary>
    public static SizeStyle Colors { get; set; } = SizeStyle.Ember;

    /// <summary>Drive view: leave the free share of the disk empty, so slices are sized against the whole disk.</summary>
    public static bool ShowFree { get; set; } = true;

    /// <summary>Ring thickness.</summary>
    public static RingStyle Rings { get; set; } = RingStyle.Thinning;

    /// <summary>Smallest file the duplicate finder looks at.</summary>
    public static long DuplicateMinSize { get; set; } = 500L * 1024 * 1024;

    /// <summary>Last window position and size in WPF units, and whether it was maximized; null until the first close.</summary>
    public static WindowPlace? Window { get; set; }

    /// <summary>Look for a newer release on GitHub at startup.</summary>
    public static bool CheckUpdates { get; set; } = true;

    /// <summary>A release tag the user chose to skip, so the startup check stays quiet about it.</summary>
    public static string? SkipVersion { get; set; }

    /// <summary>Bumped when the defaults change, so older settings files take the new defaults once.</summary>
    private const int CurrentDefaults = 2;

    public static void Load()
    {
        if (Demo.On) return; // demo: the defaults
        try
        {
            if (!File.Exists(FilePath)) return;
            int defaults = 1;
            foreach (var line in File.ReadAllLines(FilePath))
            {
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                var key = line[..eq].Trim();
                var value = line[(eq + 1)..].Trim();
                if (key.Equals("duplicates-min-size", StringComparison.OrdinalIgnoreCase) && long.TryParse(value, out var min) && min > 0)
                    DuplicateMinSize = min;
                if (key.Equals("colors", StringComparison.OrdinalIgnoreCase) && Enum.TryParse<SizeStyle>(value, true, out var c)) Colors = c;
                if (key.Equals("defaults", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out var dv)) defaults = dv;
                if (key.Equals("free-space", StringComparison.OrdinalIgnoreCase) && bool.TryParse(value, out var fr)) ShowFree = fr;
                if (key.Equals("window", StringComparison.OrdinalIgnoreCase)) Window = WindowPlace.Parse(value) ?? Window;
                if (key.Equals("check-updates", StringComparison.OrdinalIgnoreCase) && bool.TryParse(value, out var cu)) CheckUpdates = cu;
                if (key.Equals("skip-version", StringComparison.OrdinalIgnoreCase)) SkipVersion = value.Length > 0 ? value : null;
                if (key.Equals("rings", StringComparison.OrdinalIgnoreCase) && Enum.TryParse<RingStyle>(value, true, out var r)) Rings = r;
                if (!key.Equals("animation", StringComparison.OrdinalIgnoreCase)) continue;
                if (Enum.TryParse<ScanAnimation>(value, ignoreCase: true, out var a)) Animation = a;
                else if (value.Equals("smooth", StringComparison.OrdinalIgnoreCase)) Animation = ScanAnimation.Sweep; // older setting
            }
            // Settings saved before the defaults changed (size colours, free space, thin rings, spiral).
            if (defaults < CurrentDefaults)
            {
                ShowFree = true;
                Rings = RingStyle.Thinning;
                Animation = ScanAnimation.Spiral;
            }
        }
        catch { /* a broken settings file just means defaults */ }
    }

    public static void Save()
    {
        if (Demo.On) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, $"animation={Animation.ToString().ToLowerInvariant()}\r\nduplicates-min-size={DuplicateMinSize}\r\n" +
                $"colors={Colors.ToString().ToLowerInvariant()}\r\nrings={Rings.ToString().ToLowerInvariant()}\r\nfree-space={ShowFree.ToString().ToLowerInvariant()}\r\n" +
                $"check-updates={CheckUpdates.ToString().ToLowerInvariant()}\r\n" + (SkipVersion is { } sv ? $"skip-version={sv}\r\n" : "") +
                (Window is { } w ? $"window={w}\r\n" : "") + $"defaults={CurrentDefaults}\r\n");
        }
        catch { /* not worth bothering the user about */ }
    }
}

/// <summary>Where the main window was: left, top, width and height in WPF units, then 1 if maximized.</summary>
internal sealed record WindowPlace(double Left, double Top, double Width, double Height, bool Maximized)
{
    private static readonly System.Globalization.CultureInfo Inv = System.Globalization.CultureInfo.InvariantCulture;

    public override string ToString() =>
        string.Join(",", new[] { Left, Top, Width, Height }.Select(v => Math.Round(v).ToString(Inv))) + (Maximized ? ",1" : ",0");

    public static WindowPlace? Parse(string value)
    {
        var p = value.Split(',');
        if (p.Length != 5) return null;
        var n = new double[4];
        for (int i = 0; i < 4; i++)
            if (!double.TryParse(p[i], System.Globalization.NumberStyles.Float, Inv, out n[i]) || !double.IsFinite(n[i])) return null;
        if (n[2] < 100 || n[3] < 100) return null;
        return new WindowPlace(n[0], n[1], n[2], n[3], p[4].Trim() == "1");
    }
}
