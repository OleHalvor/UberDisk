using System.Globalization;

namespace UberDisk.Core.Viz;

public static class Format
{
    private static readonly string[] Units = { "B", "KB", "MB", "GB", "TB", "PB" };

    /// <summary>"872 GB", "1.43 TB", "86.0 GB", "512 KB". Three significant digits, binary units like Explorer.</summary>
    public static string Bytes(long bytes)
    {
        if (bytes < 1024) return bytes.ToString(CultureInfo.InvariantCulture) + " B";
        double v = bytes;
        int u = 0;
        while (v >= 1000 && u < Units.Length - 1) { v /= 1024; u++; }
        string num = v >= 100 ? v.ToString("0", CultureInfo.InvariantCulture)
                   : v >= 10 ? v.ToString("0.0", CultureInfo.InvariantCulture)
                   : v.ToString("0.00", CultureInfo.InvariantCulture);
        return num + " " + Units[u];
    }

    public static string Percent(double fraction) =>
        (fraction * 100) switch
        {
            >= 10 => (fraction * 100).ToString("0", CultureInfo.InvariantCulture) + " %",
            >= 0.1 => (fraction * 100).ToString("0.0", CultureInfo.InvariantCulture) + " %",
            > 0 => "< 0.1 %",
            _ => "0 %",
        };

    public static string Count(long n) => n.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>"just now", "2 min ago", "3 h ago", "yesterday", "3 days ago".</summary>
    public static string Ago(DateTime when, DateTime now)
    {
        var d = now - when;
        if (d.TotalSeconds < 45) return "just now";
        if (d.TotalMinutes < 60) return $"{Math.Max(1, (int)Math.Round(d.TotalMinutes))} min ago";
        if (d.TotalHours < 24) return $"{(int)d.TotalHours} h ago";
        if (d.TotalDays < 2) return "yesterday";
        return $"{(int)d.TotalDays} days ago";
    }

    /// <summary>"about 3 s left", "about 2 min left".</summary>
    public static string Remaining(TimeSpan t)
    {
        if (t.TotalSeconds < 60) return $"about {Math.Max(1, (int)Math.Ceiling(t.TotalSeconds))} s left";
        return $"about {(int)Math.Ceiling(t.TotalMinutes)} min left";
    }

    public static string Duration(TimeSpan t) =>
        t.TotalSeconds < 60 ? t.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s"
                            : $"{(int)t.TotalMinutes} min {t.Seconds} s";
}
