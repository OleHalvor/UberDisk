using UberDisk.Core.Model;
using UberDisk.Core.Scanning;

namespace UberDisk.Core.Tests;

public sealed class TempTree : IDisposable
{
    public string Root { get; } = Directory.CreateTempSubdirectory("uberdisk-").FullName;

    public TempTree File(string rel, int bytes)
    {
        var p = Path.Combine(Root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        System.IO.File.WriteAllBytes(p, new byte[bytes]);
        return this;
    }

    public TempTree Dir(string rel)
    {
        Directory.CreateDirectory(Path.Combine(Root, rel));
        return this;
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, true); } catch { }
    }
}

public class ScannerTests
{
    private static ScanOptions Opts(int threads = 4, int keep = 48) =>
        new() { Threads = threads, MaxFilesPerFolder = keep, Reader = new PortableDirectoryReader(4096) };

    [Fact]
    public void Totals_sizes_files_and_folders_as_size_on_disk()
    {
        using var t = new TempTree()
            .File("a/one.bin", 1)          // 4 KB on disk
            .File("a/two.bin", 5000)       // 8 KB
            .File("a/b/three.bin", 10000)  // 12 KB
            .File("root.bin", 0)           // 0
            .Dir("empty");

        var root = ScanJob.ScanNow(t.Root, options: Opts());

        Assert.Equal(24 * 1024, root.Size);
        Assert.Equal(4, root.FileCount);
        Assert.Equal(3, root.DirCount); // a, a/b, empty
        var a = root.FindChildDir("a")!;
        Assert.Equal(24 * 1024, a.Size);
        Assert.Equal(3, a.FileCount);
        Assert.Equal(1, a.DirCount);
        Assert.Equal(12 * 1024, a.FindChildDir("b")!.Size);
        Assert.Equal(0, root.FindChildDir("empty")!.Size);
    }

    [Fact]
    public void Children_are_sorted_largest_first_when_done()
    {
        using var t = new TempTree()
            .File("small/x", 4096)
            .File("big/x", 40960)
            .File("mid.bin", 20000);

        var root = ScanJob.ScanNow(t.Root, options: Opts());

        Assert.True(root.Sorted);
        Assert.Equal(new[] { "big", "mid.bin", "small" }, root.Children!.Select(c => c.Name));
    }

    [Fact]
    public void Many_small_files_are_grouped_into_one_bucket()
    {
        using var t = new TempTree();
        for (int i = 0; i < 30; i++) t.File($"f{i:00}.bin", (i + 1) * 4096);

        var root = ScanJob.ScanNow(t.Root, options: Opts(keep: 10));

        var files = root.Children!.OfType<FileNode>().ToList();
        Assert.Equal(11, files.Count);
        var bucket = Assert.Single(files, f => f.IsAggregate);
        Assert.Equal(20, bucket.Count);
        Assert.Equal(Enumerable.Range(1, 20).Sum() * 4096L, bucket.Size);
        Assert.Contains(files, f => f.Name == "f29.bin"); // the biggest is kept by name
        Assert.Equal(30, root.FileCount);
        Assert.Equal(Enumerable.Range(1, 30).Sum() * 4096L, root.Size);
    }

    [Fact]
    public void Deep_wide_tree_scans_correctly_with_many_threads()
    {
        using var t = new TempTree();
        long expected = 0;
        for (int i = 0; i < 20; i++)
            for (int j = 0; j < 10; j++)
            {
                t.File($"d{i}/e{j}/f.bin", 4096 * (j + 1));
                expected += 4096 * (j + 1);
            }

        var root = ScanJob.ScanNow(t.Root, options: Opts(threads: 16));

        Assert.Equal(expected, root.Size);
        Assert.Equal(200, root.FileCount);
        Assert.Equal(220, root.DirCount);
        Assert.All(root.Children!.OfType<DirNode>(), d => Assert.True(d.HueSlot >= 0));
    }

    [Fact]
    public void Junctions_and_symlinked_folders_are_not_followed()
    {
        using var t = new TempTree().File("real/data.bin", 40960);
        string link = Path.Combine(t.Root, "link"), target = Path.Combine(t.Root, "real");
        if (OperatingSystem.IsWindows())
        {
            // A junction (like C:\Documents and Settings) needs no special privilege, unlike a symlink.
            var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
                { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!;
            p.WaitForExit();
            Assert.True(Directory.Exists(link), "could not create the test junction");
        }
        else
        {
            Directory.CreateSymbolicLink(link, target);
        }

        var root = ScanJob.ScanNow(t.Root, options: Opts());

        Assert.Equal(40960, root.Size);
        Assert.Null(root.FindChildDir("link"));
    }

    [Fact]
    public void Full_path_is_rebuilt_from_parents()
    {
        using var t = new TempTree().File("a/b/c.bin", 1);
        var root = ScanJob.ScanNow(t.Root, options: Opts());
        var c = root.FindChildDir("a")!.FindChildDir("b")!.Children!.Single();
        Assert.Equal(Path.Combine(t.Root, "a", "b", "c.bin"), c.FullPath);
    }

    [Fact]
    public void Cancel_stops_and_completes()
    {
        using var t = new TempTree();
        for (int i = 0; i < 50; i++) t.File($"d{i}/f.bin", 1);
        var job = new ScanJob(new DirNode(t.Root, null), t.Root, Opts());
        job.Pause();
        job.Start();
        job.Cancel();
        Assert.True(job.Completion.Wait(5000));
        Assert.True(job.IsCancelled);
    }

    [Fact]
    public void Replacing_a_rescanned_folder_fixes_ancestor_totals()
    {
        using var t = new TempTree().File("a/b/x.bin", 4096).File("a/y.bin", 4096);
        var root = ScanJob.ScanNow(t.Root, options: Opts());
        var a = root.FindChildDir("a")!;
        var b = a.FindChildDir("b")!;

        System.IO.File.WriteAllBytes(Path.Combine(t.Root, "a", "b", "z.bin"), new byte[8192]);
        var fresh = ScanJob.ScanNow(b.FullPath, "b", Opts());
        a.ReplaceChild(b, fresh);

        Assert.Equal(16384, a.Size);
        Assert.Equal(16384, root.Size);
        Assert.Equal(3, root.FileCount);
        Assert.Same(fresh, a.FindChildDir("b"));
        Assert.Same(a, fresh.Parent);
    }
}

public class PriorityAndRefreshTests
{
    private sealed class RecordingReader : IDirectoryReader
    {
        private readonly PortableDirectoryReader _inner = new();
        public readonly List<string> Order = new();
        public bool TryRead(string path, List<RawEntry> sink)
        {
            lock (Order) Order.Add(path);
            return _inner.TryRead(path, sink);
        }
    }

    [Fact]
    public void Folder_on_the_priority_path_is_read_first()
    {
        using var t = new TempTree();
        foreach (var top in new[] { "a", "b", "c", "d", "zoomed" })
            for (int i = 0; i < 3; i++) t.File($"{top}/s{i}/f.bin", 1);
        t.File("zoomed/s1/deep/f.bin", 1);
        var reader = new RecordingReader();
        ScanJob.ScanNow(t.Root, options: new ScanOptions { Threads = 1, Reader = reader, PriorityPath = new[] { "zoomed", "s1" } });
        Assert.Equal(Path.Combine(t.Root, "zoomed"), reader.Order[1]);
        Assert.Equal(Path.Combine(t.Root, "zoomed", "s1"), reader.Order[2]);
    }

    [Fact]
    public async Task Used_space_is_refreshed_when_a_scan_finishes()
    {
        using var t = new TempTree().File("x/1.bin", 4096);
        // The drive list says 50 GB used (stale: a clean-up just freed it); the refresh says 4 KB.
        var stale = new UberDisk.Core.Drives.DriveMeta(t.Root, t.Root, "T", UberDisk.Core.Drives.MediaKind.Ssd, 100L << 30, 50L << 30, true);
        var co = new UberDisk.Core.Drives.ScanCoordinator(_ => new ScanOptions { Reader = new PortableDirectoryReader() },
            m => m with { FreeBytes = m.TotalBytes - 4096 });
        co.UpdateDrives(new[] { stale });
        var s = co.Activate(t.Root);
        for (int i = 0; i < 500 && s.State != UberDisk.Core.Drives.ScanState.Done; i++) await Task.Delay(10);
        Assert.Equal(4096, s.Meta.UsedBytes);
        Assert.DoesNotContain(s.Tree!.Children!, c => c is DirNode { IsSynthetic: true });
    }
}
