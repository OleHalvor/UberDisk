using UberDisk.Core.Drives;
using UberDisk.Core.Duplicates;
using UberDisk.Core.Model;
using UberDisk.Core.Scanning;

namespace UberDisk.Core.Tests;

/// <summary>
/// After a delete the chart must look as a fresh scan would. Nothing here calls the Recycle Bin: the tests do what
/// it does (take the node out of the tree, move the file into the temp folder's own "$Recycle.Bin").
/// </summary>
public class AfterDeleteTests
{
    private static ScanOptions Opts() => new() { Threads = 2, Reader = new PortableDirectoryReader(4096) };

    private static async Task<(ScanCoordinator co, DriveSession s)> Scanned(string root, DriveMeta meta)
    {
        var co = new ScanCoordinator(_ => Opts());
        co.UpdateDrives(new[] { meta });
        var s = co.Activate(root);
        for (int i = 0; i < 500 && s.State != ScanState.Done; i++) await Task.Delay(10);
        Assert.Equal(ScanState.Done, s.State);
        return (co, s);
    }

    private static void FakeRecycle(TempTree t, DirNode tree, string rel)
    {
        var node = (FileNode)Navigate(tree, rel);
        node.Parent!.RemoveChild(node);
        File.Move(Path.Combine(t.Root, rel), Path.Combine(t.Root, ScanCoordinator.RecycleBinName, "S-1-5-21-1", "$R" + Path.GetFileName(rel)));
    }

    private static Node Navigate(DirNode tree, string rel)
    {
        Node n = tree;
        foreach (var part in rel.Split('/'))
            n = ((DirNode)n).Children!.Single(c => c.Name.Equals(part, StringComparison.OrdinalIgnoreCase));
        return n;
    }

    [Fact]
    public async Task Deleted_file_moves_into_the_recycle_bin_folder_like_a_rescan_shows_it()
    {
        using var t = new TempTree()
            .File("big/movie.bin", 400 * 1024)
            .File("other/a.bin", 100 * 1024)
            .File("$Recycle.Bin/S-1-5-21-1/$Rold.bin", 8 * 1024);
        var (co, s) = await Scanned(t.Root, new DriveMeta(t.Root, t.Root, "T", MediaKind.Ssd, 1L << 40, 1L << 40, true));
        var tree = s.Tree!;
        long before = tree.Size;

        FakeRecycle(t, tree, "big/movie.bin");
        Assert.Equal(before - 400 * 1024, tree.Size); // what the chart showed before the fix
        await co.AfterDeleteAsync(t.Root);

        var fresh = ScanJob.ScanNow(t.Root, options: Opts());
        Assert.Same(tree, s.Tree);
        Assert.Equal(fresh.Size, tree.Size);
        Assert.Equal(before, tree.Size);
        Assert.Equal(fresh.FileCount, tree.FileCount);
        Assert.Equal(fresh.DirCount, tree.DirCount);
        Assert.Equal(408 * 1024, tree.FindChildDir("$Recycle.Bin")!.Size);
        Assert.Equal(0, tree.FindChildDir("big")!.Size);
        // Children stay sorted by size, as after a scan.
        var sizes = tree.Children!.Select(c => c.Size).ToList();
        Assert.Equal(sizes.OrderByDescending(x => x), sizes);
    }

    [Fact]
    public async Task Grey_unscanned_slice_is_redone_against_fresh_free_space()
    {
        using var t = new TempTree()
            .File("big/movie.bin", 400 * 1024)
            .Dir("$Recycle.Bin/S-1-5-21-1");
        long total = 100L << 30, free = 90L << 30;
        var co = new ScanCoordinator(_ => Opts(), refreshMeta: m => m with { FreeBytes = free });
        co.UpdateDrives(new[] { new DriveMeta(t.Root, t.Root, "T", MediaKind.Ssd, total, free, true) });
        var s = co.Activate(t.Root);
        for (int i = 0; i < 500 && s.State != ScanState.Done; i++) await Task.Delay(10);
        var tree = s.Tree!;
        Assert.Equal(10L << 30, tree.Size);

        // Recycled: used space unchanged, so the chart still adds up to it, with one grey slice.
        FakeRecycle(t, tree, "big/movie.bin");
        await co.AfterDeleteAsync(t.Root);
        Assert.Equal(10L << 30, tree.Size);
        Assert.Single(tree.Children!.OfType<DirNode>(), d => d.IsSynthetic);

        // Deleted for good (too big for the Recycle Bin): free space grows and the chart shrinks with it.
        free += 2L << 30;
        File.Delete(Path.Combine(t.Root, "$Recycle.Bin", "S-1-5-21-1", "$Rmovie.bin"));
        await co.AfterDeleteAsync(t.Root);
        Assert.Equal(8L << 30, tree.Size);
        var grey = Assert.Single(tree.Children!.OfType<DirNode>(), d => d.IsSynthetic);
        Assert.Equal((8L << 30) - tree.Children!.Where(c => c != grey).Sum(c => c.Size), grey.Size);
    }

    [Fact]
    public async Task Drive_without_a_recycle_bin_folder_is_left_alone()
    {
        using var t = new TempTree().File("x/a.bin", 4096).File("x/b.bin", 8192);
        var (co, s) = await Scanned(t.Root, new DriveMeta(t.Root, t.Root, "T", MediaKind.Ssd, 1L << 40, 1L << 40, true));
        var tree = s.Tree!;
        var b = (FileNode)Navigate(tree, "x/b.bin");
        b.Parent!.RemoveChild(b);
        await co.AfterDeleteAsync(t.Root);
        Assert.Equal(4096, tree.Size);
    }

    [Fact]
    public void Duplicate_search_skips_the_recycle_bin()
    {
        using var t = new TempTree();
        var data = new byte[64 * 1024];
        new Random(5).NextBytes(data);
        Directory.CreateDirectory(Path.Combine(t.Root, "keep"));
        Directory.CreateDirectory(Path.Combine(t.Root, "$Recycle.Bin", "S-1-5-21-1"));
        File.WriteAllBytes(Path.Combine(t.Root, "keep", "a.bin"), data);
        File.WriteAllBytes(Path.Combine(t.Root, "$Recycle.Bin", "S-1-5-21-1", "$Ra.bin"), data);
        var tree = ScanJob.ScanNow(t.Root, options: Opts());
        Assert.Empty(new DuplicateFinder(tree, new DuplicateOptions { MinSize = 32 * 1024, QuickChunk = 4096 }).Run().Groups);
    }

    [Fact]
    public void Hash_cache_save_replaces_the_file_whole()
    {
        using var t = new TempTree();
        var file = Path.Combine(t.Root, "hashes.tsv");
        var cache = new HashCache(file);
        cache.Put(@"C:\a.bin", 10, 20, "Q", "F");
        cache.Save();
        cache.Put(@"C:\b.bin", 11, 21, "Q2", null);
        cache.Save();
        Assert.False(File.Exists(file + ".tmp"));
        var again = new HashCache(file);
        Assert.Equal("F", again.Get(@"C:\a.bin", 10, 20)!.Full);
        Assert.Equal("Q2", again.Get(@"C:\b.bin", 11, 21)!.Quick);
    }
}
