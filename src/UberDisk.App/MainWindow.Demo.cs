using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using UberDisk.Core.Demo;
using UberDisk.Core.Drives;
using UberDisk.Core.Model;
using UberDisk.Core.Scanning;

namespace UberDisk.App;

/// <summary>
/// <c>UberDisk.exe --demo</c> shows made-up drives (see <see cref="DemoDisk"/>) instead of the PC's, for screenshots
/// that show nobody's files. Nothing is read from the real disks, nothing is deleted or opened, and no settings or
/// caches are written. <c>--demo --shots &lt;folder&gt;</c> also saves the README screenshots there and closes.
/// </summary>
internal static class Demo
{
    public static readonly bool On;
    public static readonly string? ShotsDir;

    static Demo()
    {
        var args = Environment.GetCommandLineArgs();
        On = args.Any(a => a.Equals("--demo", StringComparison.OrdinalIgnoreCase));
        int i = Array.FindIndex(args, a => a.Equals("--shots", StringComparison.OrdinalIgnoreCase));
        if (On && i >= 0 && i + 1 < args.Length) ShotsDir = args[i + 1];
    }

    public static IReadOnlyList<DriveMeta> ListDrives() => On ? DemoDisk.Drives : DriveMeta.List();

    public static ScanCoordinator NewCoordinator() => On
        ? new ScanCoordinator(_ => new ScanOptions { Threads = 8, Reader = DemoDisk.Reader })
        : new ScanCoordinator(refreshMeta: DriveMeta.Refresh);
}

public partial class MainWindow
{
    /// <summary>Takes the README screenshots: rendered from the window itself, so nothing else on screen gets in.</summary>
    private async Task TakeDemoShotsAsync(string dir)
    {
        Directory.CreateDirectory(dir);
        Chart.IsHitTestVisible = false; // the real mouse must not move the hover card

        // 1. C: half scanned.
        await Until(() => ActiveSession is { State: ScanState.Scanning } s && s.Progress >= 0.5);
        Save(dir, "scanning.png");

        // 2. Whole drive, every drive scanned, hovering the biggest folder.
        await Until(() => _scans.Sessions.All(s => s.State == ScanState.Done));
        await Settle();
        HoverNode("Users");
        await Settle(300);
        Save(dir, "drive.png");

        // 3. Zoomed into the user folder, hovering a video folder.
        HoverNode(null);
        Nav!.Go(new[] { "Users", "demo" });
        await Settle();
        HoverNode("Videos");
        await Settle(300);
        Save(dir, "zoom.png");

        // 4. Duplicates view on the drive, hovering a copied video.
        HoverNode(null);
        Nav!.Go(Array.Empty<string>());
        SetDuplicateMode(true);
        await Settle();
        HoverNode("DJI_0042.MP4");
        await Settle(300);
        Save(dir, "duplicates.png");

        // 5. The games drive.
        HoverNode(null);
        SetDuplicateMode(false);
        Activate("D:\\");
        await Settle();
        Save(dir, "games-drive.png");

        Close();
    }

    private static async Task Until(Func<bool> done)
    {
        while (!done()) await Task.Delay(30);
    }

    /// <summary>Waits for animations to finish.</summary>
    private async Task Settle(int extraMs = 0)
    {
        await Task.Delay(400);
        while (Chart.Animating) await Task.Delay(50);
        await Task.Delay(200 + extraMs);
    }

    /// <summary>Puts the hover on the segment with this name, as if the mouse were there (null clears it).</summary>
    private void HoverNode(string? name)
    {
        if (name == null) { Chart.DemoHover(null); return; }
        // Scan the chart on a grid, then take the hit nearest the hits' centre (a ring segment bends, so its
        // centre of mass can fall outside it).
        var hits = new List<Point>();
        for (double y = 0; y < Chart.ActualHeight; y += 3)
            for (double x = 0; x < Chart.ActualWidth; x += 3)
                if (Chart.SegmentAt(new Point(x, y)) is { } seg && seg.Node.Name == name) hits.Add(new Point(x, y));
        if (hits.Count == 0) return;
        double cx = hits.Average(p => p.X), cy = hits.Average(p => p.Y);
        Chart.DemoHover(hits.MinBy(p => (p.X - cx) * (p.X - cx) + (p.Y - cy) * (p.Y - cy)));
    }
    private void Save(string dir, string file)
    {
        UpdateLayout();
        var root = (FrameworkElement)Content;
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        int pw = (int)Math.Round(root.ActualWidth * scale), ph = (int)Math.Round(root.ActualHeight * scale);
        var rtb = new RenderTargetBitmap(pw, ph, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        // The window's background sits behind the content; without it the top bar and footer come out transparent.
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            var size = new Rect(0, 0, root.ActualWidth, root.ActualHeight);
            dc.DrawRectangle(Background, null, size);
            dc.DrawRectangle(new VisualBrush(root) { Stretch = Stretch.None, ViewboxUnits = BrushMappingMode.Absolute, Viewbox = size, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, size);
        }
        rtb.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(Path.Combine(dir, file));
        enc.Save(fs);
    }
}
