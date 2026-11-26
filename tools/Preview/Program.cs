// Usage: Preview <folder to scan> <out.png> [zoom path relative to root, '/'-separated] [hover ring] [hover angle deg]
using SkiaSharp;
using UberDisk.App;
using UberDisk.Core.Scanning;
using UberDisk.Core.Viz;

if (args[0] == "--anim") { AnimDemo.Run(args[1], args[2]); return; }
if (args[0] == "--dups") { DupDemo.Run(args[1], args[2], long.Parse(args[3])); return; }
if (args[0] == "--palettes") { PaletteDemo.Run(args[1]); return; }
if (args[0] == "--styles") { StyleDemo.Run(args[1], args[2]); return; }
var root = ScanJob.ScanNow(args[0], "C:\\");
var nav = new Navigator();
if (args.Length > 2 && args[2] != "") nav.Go(args[2].Split('/'));
var center = nav.Resolve(root);

var painter = new SunburstPainter { AnimationsEnabled = false };
if (Environment.GetEnvironmentVariable("UD_COLORS") is { Length: > 0 } uc) painter.Colors = Enum.Parse<SizeStyle>(uc, true);
if (Environment.GetEnvironmentVariable("UD_FREE") is { Length: > 0 } uf) painter.FreeFraction = double.Parse(uf, System.Globalization.CultureInfo.InvariantCulture);
if (Environment.GetEnvironmentVariable("UD_RINGS") is { Length: > 0 } ur) painter.RingStyle = Enum.Parse<RingStyle>(ur, true);
painter.Show(root, center);
painter.CenterText = center.Parent == null
    ? new CenterText(null, "C:", $"{Format.Bytes(root.Size)} used", "of 1 TB · 128 GB free")
    : new CenterText($"↑ Up to {center.Parent.Name}", center.Name, Format.Bytes(center.Size), "29.2 % of C:");

int W = args.Length > 5 ? int.Parse(args[5]) : 1190, H = args.Length > 6 ? int.Parse(args[6]) : 808; float scale = 1.5f;
var info = new SKImageInfo((int)(W * scale), (int)(H * scale));
using var surface = SKSurface.Create(info);
surface.Canvas.Scale(scale);
painter.Paint(surface.Canvas, W, H);
if (args.Length > 4)
{
    // Hover: pick a point on ring/angle, hit-test like the mouse would, paint again.
    double ring = double.Parse(args[3]), deg = double.Parse(args[4]) * Math.PI / 180;
    double r0 = (Math.Min(W, H) / 2.0 - 24) * 0.30, rw = ((Math.Min(W, H) / 2.0 - 24) - r0) / 7;
    double rr = r0 + (ring - 0.5) * rw;
    var (seg, _) = painter.HitTest(W / 2.0 + Math.Sin(deg) * rr, H / 2.0 - Math.Cos(deg) * rr);
    painter.Hover = seg;
    Console.WriteLine($"hover: {seg?.Node.FullPath}");
    painter.Paint(surface.Canvas, W, H);
}
using var img = surface.Snapshot();
using var data = img.Encode(SKEncodedImageFormat.Png, 90);
File.WriteAllBytes(args[1], data.ToArray());
Console.WriteLine($"{root.FileCount:N0} files, {Format.Bytes(root.Size)}");

static class AnimDemo
{
    // Renders a filmstrip: a live scan filling in (left half of frames), then a zoom into the biggest folder.
    public static void Run(string folder, string outPng)
    {
        const int W = 600, H = 420;
        var frames = new List<SKImage>();
        var painter = new SunburstPainter();
        var job = new ScanJob(new UberDisk.Core.Model.DirNode("C:\\", null), folder,
            new ScanOptions { Threads = 1, Reader = new SlowReader() });
        job.Start();
        void Frame()
        {
            using var surface = SKSurface.Create(new SKImageInfo(W, H));
            painter.Paint(surface.Canvas, W, H);
            frames.Add(surface.Snapshot());
        }
        painter.Show(job.Root, job.Root);
        painter.Live = true;
        for (int i = 0; i < 6; i++) { Thread.Sleep(120); painter.Fill = (i + 1) / 7.0; painter.Invalidate(); Frame(); }
        job.Completion.Wait();
        painter.Live = false;
        painter.Fill = 1;
        painter.Invalidate();
        for (int i = 0; i < 20; i++) { Thread.Sleep(30); Frame(); }
        var big = job.Root.Children!.OfType<UberDisk.Core.Model.DirNode>().First();
        painter.Show(job.Root, big);
        for (int i = 0; i < 4; i++) { Thread.Sleep(50); Frame(); }
        for (int i = 0; i < 20; i++) { Thread.Sleep(30); painter.Paint(SKSurface.Create(new SKImageInfo(W, H)).Canvas, W, H); }
        Frame();
        // 4 x 3 grid of picked frames
        int[] pick = { 0, 2, 4, 5, 7, 9, 25, 26, 27, 28, 29, frames.Count - 1 };
        using var sheet = SKSurface.Create(new SKImageInfo(W * 4 / 2, H * 3 / 2));
        for (int i = 0; i < pick.Length; i++)
            sheet.Canvas.DrawImage(frames[Math.Min(pick[i], frames.Count - 1)], SKRect.Create(i % 4 * W / 2, i / 4 * H / 2, W / 2, H / 2));
        using var data = sheet.Snapshot().Encode(SKEncodedImageFormat.Png, 90);
        File.WriteAllBytes(outPng, data.ToArray());
        Console.WriteLine($"{frames.Count} frames");
    }

    private sealed class SlowReader : IDirectoryReader
    {
        private readonly PortableDirectoryReader _inner = new();
        public bool TryRead(string path, List<RawEntry> sink) { Thread.SpinWait(200000); return _inner.TryRead(path, sink); }
    }
}

static class StyleDemo
{
    // One row per scan animation style: a simulated 3 s rescan of a finished tree, a frame every 0.3 s.
    public static void Run(string folder, string outPng)
    {
        const int W = 360, H = 300, Cols = 14;
        var root = ScanJob.ScanNow(folder, "C:\\");
        var styles = Enum.GetValues<ScanAnimation>();
        using var sheet = SKSurface.Create(new SKImageInfo(W * Cols / 2, H * styles.Length / 2));
        sheet.Canvas.Clear(new SKColor(0x17, 0x19, 0x1E));
        for (int row = 0; row < styles.Length; row++)
        {
            var painter = new SunburstPainter { Style = styles[row] };
            painter.Show(root, root);
            void Paint() { using var s = SKSurface.Create(new SKImageInfo(W, H)); painter.Paint(s.Canvas, W, H); }
            for (int i = 0; i < 20; i++) { Thread.Sleep(20); Paint(); } // settle, full circle
            var sw = System.Diagnostics.Stopwatch.StartNew();
            double next = 0;
            int col = 0;
            painter.Live = true;
            while (col < Cols)
            {
                double t = sw.Elapsed.TotalSeconds;
                bool scanning = t < 3.0;
                painter.Live = scanning;
                painter.Fill = scanning ? Math.Clamp(t / 3.0, 0.02, 0.97) : 1;
                painter.Invalidate();
                using var s = SKSurface.Create(new SKImageInfo(W, H));
                painter.Paint(s.Canvas, W, H);
                if (t >= next)
                {
                    using var img = s.Snapshot();
                    sheet.Canvas.DrawImage(img, SKRect.Create(col * W / 2, row * H / 2, W / 2, H / 2));
                    col++;
                    next += 0.3;
                }
                Thread.Sleep(15);
            }
        }
        using var data = sheet.Snapshot().Encode(SKEncodedImageFormat.Png, 90);
        File.WriteAllBytes(outPng, data.ToArray());
    }
}

static class DupDemo
{
    public static void Run(string folder, string outPng, long minSize)
    {
        const int W = 1000, H = 800;
        var root = ScanJob.ScanNow(folder);
        var result = new UberDisk.Core.Duplicates.DuplicateFinder(root, new UberDisk.Core.Duplicates.DuplicateOptions { MinSize = minSize, QuickChunk = 64 * 1024 }).Run();
        Console.WriteLine($"{result.Groups.Count} groups, {result.Wasted} wasted");
        var painter = new SunburstPainter { AnimationsEnabled = false, Duplicates = result, DuplicateMode = Environment.GetEnvironmentVariable("UD_DUPMODE") != "0" };
        painter.Show(root, root);
        using var s = SKSurface.Create(new SKImageInfo(W, H));
        painter.Paint(s.Canvas, W, H);
        // Hover the first copy of the first group, wherever it was drawn.
        for (double r = 80; r < 380 && painter.Hover == null; r += 4)
            for (double a = 0; a < Math.Tau && painter.Hover == null; a += 0.002)
            {
                var (seg, _) = painter.HitTest(W / 2 + Math.Sin(a) * r, H / 2 - Math.Cos(a) * r);
                if (seg is { } sg && painter.GroupOf(sg.Node) == result.Groups[0]) painter.Hover = sg;
            }
        painter.SelectedGroup = result.Groups.Count > 1 ? result.Groups[1] : null;
        painter.Paint(s.Canvas, W, H);
        using var data = s.Snapshot().Encode(SKEncodedImageFormat.Png, 90);
        File.WriteAllBytes(outPng, data.ToArray());
    }
}

namespace UberDisk.App
{
    // The app's Demo switch lives in WPF code; the preview always counts as a demo (no order files written).
    internal static class Demo { public static bool On => true; }
}

static class PaletteDemo
{
    // One row per size style on the made-up demo C: drive: the whole drive, one folder zoomed in, and the gradient.
    public static void Run(string outPng)
    {
        const int W = 620, H = 520, Strip = 46;
        var root = ScanJob.ScanNow("C:\\", "C:\\", new ScanOptions { Threads = 8, Reader = UberDisk.Core.Demo.DemoDisk.Reader });
        var big = root.Children!.OfType<UberDisk.Core.Model.DirNode>().Where(d => !d.IsSynthetic).OrderByDescending(d => d.Size).First();
        var styles = Enum.GetValues<SizeStyle>();
        using var sheet = SKSurface.Create(new SKImageInfo(W * 2, (H + Strip) * styles.Length));
        sheet.Canvas.Clear(new SKColor(0x16, 0x18, 0x1D));
        using var font = new SKFont(SKTypeface.Default, 22);
        using var text = new SKPaint { Color = new SKColor(0xF2, 0xF3, 0xF5), IsAntialias = true };
        for (int row = 0; row < styles.Length; row++)
        {
            float y0 = row * (H + Strip);
            for (int col = 0; col < 2; col++)
            {
                var center = col == 0 ? root : big;
                var painter = new SunburstPainter { AnimationsEnabled = false, Colors = styles[row], RingStyle = RingStyle.Thinning };
                painter.Show(root, center);
                painter.CenterText = new CenterText(col == 0 ? null : "↑ Up to C:", center.Name.TrimEnd('\\'), Format.Bytes(center.Size), "");
                using var s = SKSurface.Create(new SKImageInfo(W, H));
                painter.Paint(s.Canvas, W, H);
                using var img = s.Snapshot();
                sheet.Canvas.DrawImage(img, col * W, y0 + Strip);
            }
            sheet.Canvas.DrawText(styles[row].ToString(), 24, y0 + 32, font, text);
            // Gradient strip: small on the left, big on the right, inner ring colours.
            for (int x = 0; x < 400; x++)
            {
                double t = x / 399.0;
                var c = Palette.BySize(styles[row], t * t, 1, 7);
                using var p = new SKPaint { Color = new SKColor(c.R, c.G, c.B) };
                sheet.Canvas.DrawRect(180 + x, y0 + 14, 1, 22, p);
            }
        }
        using var data = sheet.Snapshot().Encode(SKEncodedImageFormat.Png, 90);
        File.WriteAllBytes(outPng, data.ToArray());
    }
}
