using UberDisk.Core.Model;
using UberDisk.Core.Scanning;
using UberDisk.Core.Viz;

namespace UberDisk.Core.Tests;

public class VizTests
{
    private static DirNode Sample(out TempTree t)
    {
        t = new TempTree()
            .File("big/a.bin", 4096 * 60)
            .File("big/sub/b.bin", 4096 * 20)
            .File("mid/c.bin", 4096 * 15)
            .File("tiny.bin", 4096 * 5);
        return ScanJob.ScanNow(t.Root, options: new ScanOptions { Reader = new PortableDirectoryReader() });
    }

    [Fact]
    public void Layout_starts_at_twelve_and_fills_the_circle_by_size()
    {
        var root = Sample(out var t);
        using var _ = t;
        var layout = SunburstLayout.Compute(root, 4, _ => 0);

        var ring1 = layout.Ring(1);
        Assert.Equal(new[] { "big", "mid", "tiny.bin" }, ring1.Select(s => s.Node.Name));
        Assert.Equal(0, ring1[0].Start, 9);
        Assert.Equal(Math.Tau * 80 / 100, ring1[0].Sweep, 9);
        Assert.Equal(Math.Tau, ring1[^1].End, 9);

        // big's children share big's arc.
        var ring2 = layout.Ring(2).Where(s => s.Node.Parent!.Name == "big").ToList();
        Assert.Equal(ring1[0].Sweep, ring2.Sum(s => s.Sweep), 9);
        Assert.Equal("a.bin", ring2[0].Node.Name);
    }

    [Fact]
    public void Layout_drops_segments_below_threshold()
    {
        var root = Sample(out var t);
        using var _ = t;
        var layout = SunburstLayout.Compute(root, 4, _ => Math.Tau * 0.10);
        Assert.Equal(new[] { "big", "mid" }, layout.Ring(1).Select(s => s.Node.Name));
    }

    [Fact]
    public void Hit_test_finds_segment_by_ring_and_angle()
    {
        var root = Sample(out var t);
        using var _ = t;
        var layout = SunburstLayout.Compute(root, 4, _ => 0);
        Assert.Equal("big", layout.HitTest(1, 0.1)!.Value.Node.Name);
        Assert.Equal("tiny.bin", layout.HitTest(1, Math.Tau - 0.01)!.Value.Node.Name);
        Assert.Null(layout.HitTest(9, 1));
    }

    [Theory]
    [InlineData(0, -1, 0)]              // straight up
    [InlineData(1, 0, Math.PI / 2)]     // right
    [InlineData(0, 1, Math.PI)]         // down
    [InlineData(-1, 0, Math.PI * 1.5)]  // left
    public void Angle_is_clockwise_from_twelve(double dx, double dy, double expected) =>
        Assert.Equal(expected, SunburstLayout.AngleOf(dx, dy), 9);

    [Fact]
    public void Navigator_survives_rescan_by_path_and_keeps_history()
    {
        var root = Sample(out var t);
        using var _ = t;
        var nav = new Navigator();
        var sub = root.FindChildDir("big")!.FindChildDir("sub")!;
        nav.ZoomTo(sub);
        Assert.Same(sub, nav.Resolve(root));

        var fresh = ScanJob.ScanNow(t.Root, options: new ScanOptions { Reader = new PortableDirectoryReader() });
        Assert.Equal("sub", nav.Resolve(fresh).Name);

        var partial = new DirNode("x", null); // rescan just started: nothing there yet
        Assert.Same(partial, nav.Resolve(partial));

        nav.Up();
        Assert.Equal(new[] { "big" }, nav.Path);
        nav.Back();
        Assert.Equal(new[] { "big", "sub" }, nav.Path);
        nav.Forward();
        Assert.Equal(new[] { "big" }, nav.Path);
    }

    [Theory]
    [InlineData(512, "512 B")]
    [InlineData(1536, "1.50 KB")]
    [InlineData(936302870528L, "872 GB")]
    [InlineData(1572301500000L, "1.43 TB")]
    public void Bytes_are_formatted_like_explorer(long bytes, string expected) =>
        Assert.Equal(expected, Format.Bytes(bytes));

    [Fact]
    public void Every_size_style_has_distinct_ends_and_fades_outwards()
    {
        foreach (var style in Enum.GetValues<SizeStyle>())
        {
            var small = Palette.BySize(style, 0, 1, 7);
            var big = Palette.BySize(style, 1, 1, 7);
            Assert.NotEqual(small, big);
            Assert.Equal(big, Palette.BySize(style, 5, 1, 7)); // clamped
            var outer = Palette.BySize(style, 1, 7, 7);
            Assert.True(Spread(outer) < Spread(big), $"{style}: outer rings should be less colourful");
        }
        static int Spread(Argb c) => Math.Max(c.R, Math.Max(c.G, c.B)) - Math.Min(c.R, Math.Min(c.G, c.B));
    }
}

public class LayoutOrderTests
{
    [Fact]
    public void Stable_layout_keeps_order_until_clearly_overtaken()
    {
        using var t = new TempTree().File("a.bin", 4096 * 10).File("b.bin", 4096 * 12).File("c.bin", 4096 * 30);
        var root = ScanJob.ScanNow(t.Root, options: new ScanOptions { Reader = new PortableDirectoryReader() });
        Node N(string name) => root.Children!.First(c => c.Name == name);

        Assert.Equal(new[] { "c.bin", "b.bin", "a.bin" },
            SunburstLayout.Compute(root, 2, _ => 0).Ring(1).Select(s => s.Node.Name));

        // Previously a, b, c. b is only 20 % bigger than a: stays behind it. c is 2.5x b: moves ahead of both.
        var prev = new Dictionary<Node, int>(ReferenceEqualityComparer.Instance) { [N("a.bin")] = 0, [N("b.bin")] = 1, [N("c.bin")] = 2 };
        int Rank(Node n) => prev.TryGetValue(n, out var r) ? r : int.MaxValue;
        var stable = SunburstLayout.Compute(root, 2, _ => 0, rankOf: Rank);
        Assert.Equal(new[] { "c.bin", "a.bin", "b.bin" }, stable.Ring(1).Select(s => s.Node.Name));
        Assert.Equal(Math.Tau, stable.Ring(1)[^1].End, 9);

        // Frozen (rescan): ranked siblings keep the last finished order however sizes compare.
        var frozen = SunburstLayout.Compute(root, 2, _ => 0, rankOf: Rank, frozen: true);
        Assert.Equal(new[] { "a.bin", "b.bin", "c.bin" }, frozen.Ring(1).Select(s => s.Node.Name));

        // A new sibling (no rank) still finds its place by size.
        prev.Remove(N("c.bin"));
        frozen = SunburstLayout.Compute(root, 2, _ => 0, rankOf: Rank, frozen: true);
        Assert.Equal(new[] { "c.bin", "a.bin", "b.bin" }, frozen.Ring(1).Select(s => s.Node.Name));
    }
}
