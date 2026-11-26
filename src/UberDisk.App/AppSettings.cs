using System.IO;

namespace UberDisk.App;

/// <summary>The few user choices worth remembering, in %APPDATA%\UberDisk\settings.ini (key=value lines).</summary>
internal static class AppSettings
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UberDisk", "settings.ini");

    /// <summary>How scans of the whole drive are animated.</summary>
    public static ScanAnimation Animation { get; set; } = ScanAnimation.Spiral;

    /// <summary>How the chart is coloured.</summary>
    public static ChartColors Colors { get; set; } = ChartColors.Size;

    /// <summary>Drive view: leave the free share of the disk empty, so slices are sized against the whole disk.</summary>
    public static bool ShowFree { get; set; } = true;

    /// <summary>Ring thickness.</summary>
    public static RingStyle Rings { get; set; } = RingStyle.Thinning;

    /// <summary>Smallest file the duplicate finder looks at.</summary>
    public static long DuplicateMinSize { get; set; } = 500L * 1024 * 1024;

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
                if (key.Equals("colors", StringComparison.OrdinalIgnoreCase) && Enum.TryParse<ChartColors>(value, true, out var c)) Colors = c;
                if (key.Equals("defaults", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out var dv)) defaults = dv;
                if (key.Equals("free-space", StringComparison.OrdinalIgnoreCase) && bool.TryParse(value, out var fr)) ShowFree = fr;
                if (key.Equals("rings", StringComparison.OrdinalIgnoreCase) && Enum.TryParse<RingStyle>(value, true, out var r)) Rings = r;
                if (!key.Equals("animation", StringComparison.OrdinalIgnoreCase)) continue;
                if (Enum.TryParse<ScanAnimation>(value, ignoreCase: true, out var a)) Animation = a;
                else if (value.Equals("smooth", StringComparison.OrdinalIgnoreCase)) Animation = ScanAnimation.Sweep; // older setting
            }
            // Settings saved before the defaults changed (size colours, free space, thin rings, spiral).
            if (defaults < CurrentDefaults)
            {
                Colors = ChartColors.Size;
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
                $"colors={Colors.ToString().ToLowerInvariant()}\r\nrings={Rings.ToString().ToLowerInvariant()}\r\nfree-space={ShowFree.ToString().ToLowerInvariant()}\r\ndefaults={CurrentDefaults}\r\n");
        }
        catch { /* not worth bothering the user about */ }
    }
}
