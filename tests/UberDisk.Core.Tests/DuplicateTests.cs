using UberDisk.Core.Duplicates;
using UberDisk.Core.Scanning;

namespace UberDisk.Core.Tests;

public class DuplicateTests
{
    private static void Write(string root, string rel, byte[] data)
    {
        var p = Path.Combine(root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllBytes(p, data);
    }

    private static byte[] Pattern(int length, int seed)
    {
        var b = new byte[length];
        new Random(seed).NextBytes(b);
        return b;
    }

    private static Model.DirNode Scan(string root) =>
        ScanJob.ScanNow(root, options: new ScanOptions { Threads = 2, Reader = new PortableDirectoryReader(4096) });

    [Fact]
    public void Finds_identical_files_and_ignores_lookalikes()
    {
        using var t = new TempTree();
        var a = Pattern(64 * 1024, 1);
        Write(t.Root, "one/a.bin", a);
        Write(t.Root, "two/a-copy.bin", a);
        Write(t.Root, "three/deep/a.bin", a);
        // Same length, same first and last 4 KB as a, different in the middle: passes the quick hash, not the full one.
        var lookalike = (byte[])a.Clone();
        lookalike[32 * 1024] ^= 0xFF;
        Write(t.Root, "four/lookalike.bin", lookalike);
        // Same size, different content.
        Write(t.Root, "five/other.bin", Pattern(64 * 1024, 2));
        // Identical but below the minimum size.
        var small = Pattern(8 * 1024, 3);
        Write(t.Root, "s1.bin", small);
        Write(t.Root, "s2.bin", small);

        var finder = new DuplicateFinder(Scan(t.Root), new DuplicateOptions { MinSize = 32 * 1024, QuickChunk = 4096 });
        var result = finder.Run();

        var g = Assert.Single(result.Groups);
        Assert.Equal(3, g.Paths.Count);
        Assert.All(g.Paths, p => Assert.DoesNotContain("lookalike", p));
        Assert.Equal(64 * 1024, g.Length);
        Assert.Equal(2 * g.Allocated, g.Wasted);
        Assert.Equal(g.Wasted, result.Wasted);
        Assert.Same(g, result.GroupOf(Path.Combine(t.Root, "two", "a-copy.bin")));
        Assert.Null(result.GroupOf(Path.Combine(t.Root, "four", "lookalike.bin")));
        Assert.Equal(1.0, finder.Progress);
    }

    [Fact]
    public void Groups_are_sorted_by_space_wasted()
    {
        using var t = new TempTree();
        var big = Pattern(128 * 1024, 4);
        var small = Pattern(48 * 1024, 5);
        Write(t.Root, "b1.bin", big);
        Write(t.Root, "b2.bin", big);
        Write(t.Root, "s1.bin", small);
        Write(t.Root, "s2.bin", small);
        Write(t.Root, "s3.bin", small);
        Write(t.Root, "s4.bin", small);

        var result = new DuplicateFinder(Scan(t.Root), new DuplicateOptions { MinSize = 32 * 1024, QuickChunk = 4096 }).Run();

        Assert.Equal(2, result.Groups.Count);
        Assert.True(result.Groups[0].Wasted >= result.Groups[1].Wasted);
        Assert.Equal(0, result.Groups[0].Index);
        Assert.Equal(4, result.Groups.Single(x => x.Name.StartsWith("s")).Paths.Count);
    }

    [Fact]
    public void Finds_copies_in_folders_with_more_big_files_than_the_tree_keeps()
    {
        using var t = new TempTree();
        var a = Pattern(40 * 1024, 7);
        Write(t.Root, "other/a.bin", a);
        // 6 bigger files plus the copy in one folder; the scan keeps only the 3 biggest files per folder here.
        for (int i = 0; i < 6; i++) Write(t.Root, $"crowded/big{i}.bin", Pattern(64 * 1024 + i * 4096, 100 + i));
        Write(t.Root, "crowded/a-copy.bin", a);

        var tree = ScanJob.ScanNow(t.Root, options: new ScanOptions { Threads = 2, Reader = new PortableDirectoryReader(4096), MaxFilesPerFolder = 3 });
        var result = new DuplicateFinder(tree, new DuplicateOptions { MinSize = 32 * 1024, QuickChunk = 4096 }).Run();

        var g = Assert.Single(result.Groups);
        Assert.Contains(Path.Combine(t.Root, "crowded", "a-copy.bin"), g.Paths);
    }

    [Fact]
    public void Cache_skips_reading_unchanged_files()
    {
        using var t = new TempTree();
        var a = Pattern(64 * 1024, 6);
        Write(t.Root, "x/a.bin", a);
        Write(t.Root, "y/a.bin", a);
        var cacheFile = Path.Combine(t.Root, "cache", "hashes.tsv");
        var options = new DuplicateOptions { MinSize = 32 * 1024, QuickChunk = 4096, Cache = new HashCache(cacheFile) };

        Assert.Single(new DuplicateFinder(Scan(t.Root), options).Run().Groups);
        options.Cache!.Save();

        // Second run from a fresh cache loaded from disk: nothing left to read.
        var again = new DuplicateFinder(Scan(t.Root), new DuplicateOptions { MinSize = 32 * 1024, QuickChunk = 4096, Cache = new HashCache(cacheFile) });
        Assert.Single(again.Run().Groups);
        Assert.Equal(0, again.BytesHashed); // nothing read from disk

        // A changed file is read again and drops out of the group.
        Write(t.Root, "y/a.bin", Pattern(64 * 1024, 7));
        Assert.Empty(new DuplicateFinder(Scan(t.Root), new DuplicateOptions { MinSize = 32 * 1024, QuickChunk = 4096, Cache = new HashCache(cacheFile) }).Run().Groups);
    }
}
