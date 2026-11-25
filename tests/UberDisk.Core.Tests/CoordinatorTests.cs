using UberDisk.Core.Drives;
using UberDisk.Core.Scanning;

namespace UberDisk.Core.Tests;

public class CoordinatorTests
{
    private static DriveMeta Meta(string root) =>
        new(root, root, "Test", MediaKind.Ssd, 1L << 40, 1L << 40, true);

    private static ScanCoordinator NewCoordinator(int delayMs = 0) =>
        new(_ => new ScanOptions { Threads = 4, Reader = new SlowReader(delayMs) });

    /// <summary>Makes each folder read take a while, so tests can catch scans mid-flight.</summary>
    private sealed class SlowReader(int delayMs) : IDirectoryReader
    {
        private readonly PortableDirectoryReader _inner = new();
        public bool TryRead(string path, List<RawEntry> sink)
        {
            if (delayMs > 0) Thread.Sleep(delayMs);
            return _inner.TryRead(path, sink);
        }
    }

    private static async Task WaitFor(Func<bool> cond)
    {
        for (int i = 0; i < 500 && !cond(); i++) await Task.Delay(10);
        Assert.True(cond());
    }

    [Fact]
    public async Task Active_drive_scans_first_then_others_in_background_and_results_are_cached()
    {
        using var a = new TempTree().File("x/1.bin", 4096);
        using var b = new TempTree().File("y/2.bin", 8192);
        using var c = new TempTree().File("z/3.bin", 4096);
        var co = NewCoordinator();
        co.UpdateDrives(new[] { Meta(a.Root), Meta(b.Root), Meta(c.Root) });

        var sa = co.Activate(a.Root);
        Assert.Contains(sa.State, new[] { ScanState.Scanning, ScanState.Done }); // tiny trees can finish at once
        co.QueueAllInBackground();

        await WaitFor(() => co.Sessions.All(s => s.State == ScanState.Done));
        Assert.Equal(8192, co.Get(b.Root).Tree!.Size);

        // Switching to a scanned drive reuses the cached tree; no new scan.
        var tree = co.Get(b.Root).Tree;
        var sb = co.Activate(b.Root);
        Assert.Equal(ScanState.Done, sb.State);
        Assert.Same(tree, sb.Tree);
    }

    [Fact]
    public async Task Rescan_replaces_tree_and_picks_up_changes()
    {
        using var a = new TempTree().File("x/1.bin", 4096);
        var co = NewCoordinator();
        co.UpdateDrives(new[] { Meta(a.Root) });
        var s = co.Activate(a.Root);
        await WaitFor(() => s.State == ScanState.Done);
        var first = s.Tree;

        File.Delete(Path.Combine(a.Root, "x", "1.bin"));
        co.Rescan(a.Root);
        await WaitFor(() => s.State == ScanState.Done && s.Tree != first);
        Assert.Equal(0, s.Tree!.Size);
    }

    [Fact]
    public async Task Background_scan_pauses_while_foreground_scans()
    {
        using var a = new TempTree();
        for (int i = 0; i < 200; i++) a.File($"d{i}/f.bin", 4096);
        using var b = new TempTree().File("y/2.bin", 8192);
        var co = NewCoordinator(delayMs: 5);
        co.UpdateDrives(new[] { Meta(a.Root), Meta(b.Root) });

        co.QueueAllInBackground();           // one starts in background (drive order), the other waits
        var running = co.Sessions.Single(s => s.State == ScanState.Scanning);
        var queued = co.Sessions.Single(s => s.State == ScanState.Queued);
        Assert.True(running.Job!.Background);
        var bg = running.Job;

        var sb = co.Activate(queued.Meta.Root); // the queued one jumps ahead in the foreground
        Assert.Equal(ScanState.Scanning, sb.State);
        Assert.False(sb.Job!.Background);
        Assert.True(bg.IsPaused || bg.Completion.IsCompleted);

        await WaitFor(() => co.Sessions.All(s => s.State == ScanState.Done));
        Assert.Equal(200 * 4096L, co.Get(a.Root).Tree!.Size);
        Assert.Equal(8192, co.Get(b.Root).Tree!.Size);
    }

    [Fact]
    public async Task Stopping_a_rescan_restores_the_previous_result()
    {
        using var a = new TempTree();
        for (int i = 0; i < 50; i++) a.File($"d{i}/f.bin", 4096);
        var co = NewCoordinator(delayMs: 20);
        co.UpdateDrives(new[] { Meta(a.Root) });
        var s = co.Activate(a.Root);
        await WaitFor(() => s.State == ScanState.Done);
        var complete = s.Tree;

        co.Rescan(a.Root);
        s.Job!.Pause();
        co.Stop(a.Root);

        Assert.Equal(ScanState.Done, s.State);
        Assert.Same(complete, s.Tree);
    }
}

public class UnseenSpaceTests
{
    [Fact]
    public async Task Space_the_scan_cannot_see_is_shown_as_one_grey_segment()
    {
        using var a = new TempTree().File("x/1.bin", 4096);
        var co = new ScanCoordinator(_ => new ScanOptions { Reader = new PortableDirectoryReader() });
        co.UpdateDrives(new[] { new DriveMeta(a.Root, a.Root, "T", MediaKind.Ssd, 100L << 30, 90L << 30, true) });
        var s = co.Activate(a.Root);
        for (int i = 0; i < 500 && s.State != ScanState.Done; i++) await Task.Delay(10);
        var unseen = s.Tree!.Children!.OfType<UberDisk.Core.Model.DirNode>().Single(d => d.IsSynthetic);
        Assert.Equal((10L << 30) - 4096, unseen.Size);
        Assert.Equal(10L << 30, s.Tree.Size);
    }
}
