using System.Security.Cryptography;
using UberDisk.Core.Deleting;
using UberDisk.Core.Model;
using UberDisk.Core.Scanning;

namespace UberDisk.Core.Tests;

/// <summary>
/// Stands in for the Recycle Bin in tests: moves the item into a separate temp "bin" and records every call.
/// Refuses anything outside the sandbox, so a bug can never reach real files.
/// </summary>
public sealed class SandboxRecycler : IRecycler, IDisposable
{
    private readonly string _sandbox;
    public string Bin { get; } = Directory.CreateTempSubdirectory("uberdisk-bin-").FullName;
    public List<string> Calls { get; } = new();
    public Action<string>? Instead { get; init; }

    public SandboxRecycler(string sandbox) => _sandbox = Path.GetFullPath(sandbox);

    public void Recycle(string path, bool isDirectory)
    {
        Calls.Add(path);
        Assert.StartsWith(_sandbox + Path.DirectorySeparatorChar, path);
        if (Instead != null) { Instead(path); return; }
        var dest = Path.Combine(Bin, Calls.Count + "-" + Path.GetFileName(path));
        if (isDirectory) Directory.Move(path, dest);
        else File.Move(path, dest);
    }

    public void Dispose()
    {
        try { Directory.Delete(Bin, true); } catch { }
    }
}

public class DeleteTests
{
    private static ScanOptions Opts(int keep = 48) =>
        new() { Threads = 2, MaxFilesPerFolder = keep, Reader = new PortableDirectoryReader(4096) };

    private static DirNode Scan(TempTree t, int keep = 48) => ScanJob.ScanNow(t.Root, options: Opts(keep));

    /// <summary>Every file and folder under the root, with a hash of each file's bytes.</summary>
    private static SortedDictionary<string, string> Snapshot(string root)
    {
        var map = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var e in new DirectoryInfo(root).EnumerateFileSystemInfos("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }))
        {
            var rel = Path.GetRelativePath(root, e.FullName);
            if (e.LinkTarget != null) map[rel] = "link:" + e.LinkTarget;
            else if (e is DirectoryInfo) map[rel] = "dir";
            else map[rel] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(e.FullName)));
        }
        return map;
    }

    /// <summary>The snapshot after deleting <paramref name="rel"/>: everything except that item and what's inside it.</summary>
    private static SortedDictionary<string, string> Without(SortedDictionary<string, string> snap, string rel)
    {
        var copy = new SortedDictionary<string, string>(snap, StringComparer.Ordinal);
        foreach (var k in snap.Keys)
            if (k == rel || k.StartsWith(rel + Path.DirectorySeparatorChar, StringComparison.Ordinal)) copy.Remove(k);
        return copy;
    }

    private static Node Find(DirNode root, string rel)
    {
        Node n = root;
        foreach (var part in rel.Split('/', '\\'))
            n = ((DirNode)n).Children!.First(c => c.Name == part);
        return n;
    }

    /// <summary>A folder link: a symbolic link, or on Windows without the symlink privilege a junction (mklink /J).</summary>
    private static void LinkDir(string link, string target)
    {
        try { Directory.CreateSymbolicLink(link, target); return; }
        catch (Exception) when (OperatingSystem.IsWindows()) { }
        var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
        p.WaitForExit();
        Assert.True(Directory.Exists(link), "couldn't create a junction for the test");
    }

    /// <summary>A file symbolic link. False on Windows without the symlink privilege (then that case is skipped).</summary>
    private static bool TryLinkFile(string link, string target)
    {
        try { File.CreateSymbolicLink(link, target); return true; }
        catch (Exception) when (OperatingSystem.IsWindows()) { return false; }
    }

    private static TempTree Sample() => new TempTree()
        .File("docs/report", 100)
        .File("docs/report.txt", 200)
        .File("docs/report.txt.bak", 300)
        .File("docs/report (1).txt", 400)
        .File("photos/a.jpg", 5000)
        .File("photos/sub/b.jpg", 7000)
        .File("photos 2018/c.jpg", 6000)
        .File("photos.zip", 9000)
        .File("keep.bin", 1234);

    // ---------------- What the menu offers ----------------

    [Fact]
    public void The_drive_root_can_never_be_deleted()
    {
        using var t = Sample();
        var root = Scan(t);
        Assert.Equal(DeleteBlock.DriveRoot, DeleteSafety.CheckTree(root, root, false));
        Assert.Null(DeleteSafety.Plan(root, root, false).plan);
    }

    [Fact]
    public void Synthetic_segments_and_anything_inside_them_are_refused()
    {
        using var t = Sample();
        var root = Scan(t);
        var unscanned = new DirNode("Unscanned (system / no access)", root) { IsSynthetic = true };
        unscanned.PublishChildren(Array.Empty<Node>());
        root.PublishChildren(root.Children!.Append(unscanned).ToArray());
        var inner = new FileNode("x", unscanned, 10);
        unscanned.PublishChildren(new Node[] { inner });

        Assert.Equal(DeleteBlock.Synthetic, DeleteSafety.CheckTree(unscanned, root, false));
        Assert.Equal(DeleteBlock.Synthetic, DeleteSafety.CheckTree(inner, root, false));
    }

    [Fact]
    public void The_bundle_of_small_files_is_refused()
    {
        using var t = new TempTree();
        for (int i = 0; i < 6; i++) t.File($"many/f{i}.bin", 100 + i);
        var root = Scan(t, keep: 2);
        var bundle = ((DirNode)Find(root, "many")).Children!.OfType<FileNode>().Single(f => f.IsAggregate);
        Assert.Equal(DeleteBlock.Bundle, DeleteSafety.CheckTree(bundle, root, false));
    }

    [Fact]
    public void A_bundle_holding_a_single_file_is_still_refused()
    {
        using var t = new TempTree();
        for (int i = 0; i < 3; i++) t.File($"few/f{i}.bin", 100 + i * 5000);
        var root = Scan(t, keep: 2);
        var bundle = ((DirNode)Find(root, "few")).Children!.OfType<FileNode>().Single(f => f.Name.EndsWith("smaller files"));
        Assert.True(bundle.IsAggregate);
        Assert.Equal(DeleteBlock.Bundle, DeleteSafety.CheckTree(bundle, root, false));
    }

    [Fact]
    public void Nothing_is_deletable_while_a_scan_runs()
    {
        using var t = Sample();
        var root = Scan(t);
        Assert.Equal(DeleteBlock.ScanRunning, DeleteSafety.CheckTree(Find(root, "keep.bin"), root, scanRunning: true));
    }

    [Fact]
    public void Real_files_and_folders_are_offered()
    {
        using var t = Sample();
        var root = Scan(t);
        Assert.Equal(DeleteBlock.None, DeleteSafety.CheckTree(Find(root, "keep.bin"), root, false));
        Assert.Equal(DeleteBlock.None, DeleteSafety.CheckTree(Find(root, "photos"), root, false));
        Assert.Equal(DeleteBlock.None, DeleteSafety.CheckTree(Find(root, "photos/sub/b.jpg"), root, false));
    }

    [Fact]
    public void A_node_from_an_older_scan_is_refused()
    {
        using var t = Sample();
        var old = Scan(t);
        var fresh = Scan(t);
        Assert.Equal(DeleteBlock.NotInTree, DeleteSafety.CheckTree(Find(old, "keep.bin"), fresh, false));
    }

    [Fact]
    public void A_node_from_a_folder_that_was_rescanned_is_refused()
    {
        using var t = Sample();
        var root = Scan(t);
        var photos = (DirNode)Find(root, "photos");
        var a = Find(root, "photos/a.jpg");
        root.ReplaceChild(photos, ScanJob.ScanNow(Path.Combine(t.Root, "photos"), "photos", Opts()));
        Assert.Equal(DeleteBlock.NotInTree, DeleteSafety.CheckTree(photos, root, false));
        Assert.Equal(DeleteBlock.NotInTree, DeleteSafety.CheckTree(a, root, false));
    }

    // ---------------- Paths ----------------

    [Theory]
    [InlineData("name.")]
    [InlineData("name ")]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("a*b")]
    [InlineData("a?b")]
    [InlineData("a\"b")]
    [InlineData("a|b")]
    [InlineData("a\tb")]
    public void Names_Windows_could_read_differently_are_refused(string name)
    {
        var root = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);
        Assert.Equal(DeleteBlock.UnsafePath, DeleteSafety.CheckPath(root + Path.DirectorySeparatorChar + name, root));
    }

    [Fact]
    public void Relative_UNC_device_and_outside_paths_are_refused()
    {
        var root = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);
        Assert.Equal(DeleteBlock.UnsafePath, DeleteSafety.CheckPath("relative/file", root));
        Assert.Equal(DeleteBlock.UnsafePath, DeleteSafety.CheckPath(@"\\server\share\file", root));
        Assert.Equal(DeleteBlock.UnsafePath, DeleteSafety.CheckPath(@"\\?\C:\file", root));
        Assert.Equal(DeleteBlock.UnsafePath, DeleteSafety.CheckPath(root + "-other" + Path.DirectorySeparatorChar + "f", root)); // same prefix, other folder
        Assert.Equal(DeleteBlock.UnsafePath, DeleteSafety.CheckPath(Path.Combine(root, "a", "..", "b"), root));
        Assert.Equal(DeleteBlock.DriveRoot, DeleteSafety.CheckPath(root, root));
        Assert.Equal(DeleteBlock.DriveRoot, DeleteSafety.CheckPath(root + Path.DirectorySeparatorChar, root));
    }

    [Fact]
    public void Paths_over_259_characters_are_refused()
    {
        var root = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);
        var longPath = Path.Combine(root, new string('a', 300));
        Assert.Equal(DeleteBlock.PathTooLong, DeleteSafety.CheckPath(longPath, root));
    }

    [Fact]
    public void Unicode_and_spaces_inside_names_are_fine()
    {
        var root = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);
        Assert.Equal(DeleteBlock.None, DeleteSafety.CheckPath(Path.Combine(root, "Ærø 写真 🎵", "my file (1).txt"), root));
    }

    // ---------------- The disk ----------------

    [Fact]
    public void A_file_gone_since_the_scan_is_refused()
    {
        using var t = Sample();
        var root = Scan(t);
        File.Delete(Path.Combine(t.Root, "keep.bin"));
        Assert.Equal(DeleteBlock.Missing, DeleteSafety.Plan(Find(root, "keep.bin"), root, false).block);
    }

    [Fact]
    public void A_file_that_became_a_folder_is_refused()
    {
        using var t = Sample();
        var root = Scan(t);
        var p = Path.Combine(t.Root, "keep.bin");
        File.Delete(p);
        Directory.CreateDirectory(p);
        Assert.Equal(DeleteBlock.KindChanged, DeleteSafety.Plan(Find(root, "keep.bin"), root, false).block);
    }

    [Fact]
    public void A_link_is_refused_and_its_target_is_untouched()
    {
        using var outside = new TempTree().File("precious.bin", 777);
        using var t = Sample();
        TryLinkFile(Path.Combine(t.Root, "link.bin"), Path.Combine(outside.Root, "precious.bin"));
        LinkDir(Path.Combine(t.Root, "linkdir"), outside.Root);
        var root = Scan(t);
        foreach (var name in new[] { "link.bin", "linkdir" })
        {
            var node = root.Children!.FirstOrDefault(c => c.Name == name);
            if (node == null) continue; // the scanner may skip links altogether, which is also safe
            var (plan, block) = DeleteSafety.Plan(node, root, false);
            Assert.Null(plan);
            Assert.True(block is DeleteBlock.Link or DeleteBlock.KindChanged, block.ToString());
        }
        Assert.True(File.Exists(Path.Combine(outside.Root, "precious.bin")));
    }

    [Fact]
    public void A_folder_with_a_link_anywhere_inside_is_refused()
    {
        using var outside = new TempTree().File("precious.bin", 777);
        using var t = Sample();
        LinkDir(Path.Combine(t.Root, "photos", "sub", "deep-link"), outside.Root);
        var root = Scan(t);
        var before = Snapshot(t.Root);
        using var bin = new SandboxRecycler(t.Root);

        var (plan, block) = DeleteSafety.Plan(Find(root, "photos"), root, false);
        Assert.Null(plan);
        Assert.Equal(DeleteBlock.ContainsLink, block);
        Assert.Empty(bin.Calls);
        Assert.Equal(before, Snapshot(t.Root));
        Assert.True(File.Exists(Path.Combine(outside.Root, "precious.bin")));
    }

    [Fact]
    public void A_folder_that_cannot_be_read_completely_is_refused()
    {
        if (OperatingSystem.IsWindows()) return; // covered by Windows ACLs; chmod is the portable way to test it
        using var t = Sample();
        var root = Scan(t);
        var locked = Path.Combine(t.Root, "photos", "sub");
        File.SetUnixFileMode(locked, UnixFileMode.None);
        try
        {
            bool readable;
            try { Directory.EnumerateFileSystemEntries(locked).ToList(); readable = true; }
            catch (UnauthorizedAccessException) { readable = false; }
            if (readable) return; // running as root: permissions don't apply, nothing to test
            Assert.Equal(DeleteBlock.Unreadable, DeleteSafety.Plan(Find(root, "photos"), root, false).block);
        }
        finally { File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
    }

    // ---------------- Running a confirmed delete ----------------

    [Fact]
    public void Deleting_a_file_removes_exactly_that_file_and_nothing_with_a_similar_name()
    {
        using var t = Sample();
        var root = Scan(t);
        var before = Snapshot(t.Root);
        using var bin = new SandboxRecycler(t.Root);
        var (plan, _) = DeleteSafety.Plan(Find(root, "docs/report.txt"), root, false);

        var r = new Deleter(bin).Execute(plan!, plan!.Path, root, false);

        Assert.Equal(DeleteOutcome.Deleted, r.Outcome);
        Assert.Equal(new[] { Path.Combine(t.Root, "docs", "report.txt") }, bin.Calls);
        Assert.Equal(Without(before, Path.Combine("docs", "report.txt")), Snapshot(t.Root));
    }

    [Fact]
    public void Deleting_a_folder_removes_exactly_that_folder_and_nothing_with_a_similar_name()
    {
        using var t = Sample();
        var root = Scan(t);
        var before = Snapshot(t.Root);
        using var bin = new SandboxRecycler(t.Root);
        var (plan, _) = DeleteSafety.Plan(Find(root, "photos"), root, false);

        var r = new Deleter(bin).Execute(plan!, plan!.Path, root, false);

        Assert.Equal(DeleteOutcome.Deleted, r.Outcome);
        Assert.Single(bin.Calls);
        var after = Snapshot(t.Root);
        Assert.Equal(Without(before, "photos"), after);
        Assert.Contains("photos 2018", after.Keys);
        Assert.Contains("photos.zip", after.Keys);
    }

    [Fact]
    public void Cancelling_changes_nothing()
    {
        using var t = Sample();
        var root = Scan(t);
        var before = Snapshot(t.Root);
        long size = root.Size;
        var (plan, _) = DeleteSafety.Plan(Find(root, "photos"), root, false);
        Assert.NotNull(plan); // the confirmation would show now; the user cancels, so Execute is never called
        Assert.Equal(before, Snapshot(t.Root));
        Assert.Equal(size, root.Size);
    }

    [Theory]
    [InlineData("other")]
    [InlineData("case")]
    [InlineData("slash")]
    [InlineData("empty")]
    public void A_confirmed_path_that_differs_in_any_way_is_refused(string kind)
    {
        using var t = Sample();
        var root = Scan(t);
        var before = Snapshot(t.Root);
        using var bin = new SandboxRecycler(t.Root);
        var (plan, _) = DeleteSafety.Plan(Find(root, "docs/report.txt"), root, false);
        string confirmed = kind switch
        {
            "other" => Path.Combine(t.Root, "docs", "report.txt.bak"),
            "case" => plan!.Path.ToUpperInvariant(),
            "slash" => plan!.Path + Path.DirectorySeparatorChar,
            _ => "",
        };

        var r = new Deleter(bin).Execute(plan!, confirmed, root, false);

        Assert.Equal(DeleteOutcome.Blocked, r.Outcome);
        Assert.Equal(DeleteBlock.NotConfirmed, r.Block);
        Assert.Empty(bin.Calls);
        Assert.Equal(before, Snapshot(t.Root));
    }

    [Fact]
    public void Hover_moving_to_another_segment_cannot_change_what_is_deleted()
    {
        // The menu is built for one node; the confirmation for another (say the mouse moved). Only a matching path deletes.
        using var t = Sample();
        var root = Scan(t);
        using var bin = new SandboxRecycler(t.Root);
        var (planA, _) = DeleteSafety.Plan(Find(root, "keep.bin"), root, false);
        var (planB, _) = DeleteSafety.Plan(Find(root, "photos.zip"), root, false);

        var r = new Deleter(bin).Execute(planA!, planB!.Path, root, false);

        Assert.Equal(DeleteBlock.NotConfirmed, r.Block);
        Assert.Empty(bin.Calls);
        Assert.True(File.Exists(planA!.Path) && File.Exists(planB.Path));
    }

    [Fact]
    public void An_item_replaced_after_the_confirmation_showed_is_refused()
    {
        using var t = Sample();
        var root = Scan(t);
        using var bin = new SandboxRecycler(t.Root);
        var (plan, _) = DeleteSafety.Plan(Find(root, "keep.bin"), root, false);
        File.Delete(plan!.Path);
        Thread.Sleep(30);
        File.WriteAllBytes(plan.Path, new byte[99]); // a different file under the same name
        File.SetCreationTimeUtc(plan.Path, DateTime.UtcNow.AddMinutes(1));
        var before = Snapshot(t.Root);

        var r = new Deleter(bin).Execute(plan, plan.Path, root, false);

        Assert.Equal(DeleteBlock.Replaced, r.Block);
        Assert.Empty(bin.Calls);
        Assert.Equal(before, Snapshot(t.Root));
    }

    [Fact]
    public void An_item_replaced_by_one_with_the_same_size_and_times_is_refused_on_Windows()
    {
        // Only the file ID can tell these apart (and only Windows has one here).
        if (!OperatingSystem.IsWindows()) return;
        using var t = Sample();
        var root = Scan(t);
        using var bin = new SandboxRecycler(t.Root);
        var (plan, _) = DeleteSafety.Plan(Find(root, "keep.bin"), root, false);
        var created = File.GetCreationTimeUtc(plan!.Path);
        var written = File.GetLastWriteTimeUtc(plan.Path);
        File.Delete(plan.Path);
        File.WriteAllBytes(plan.Path, new byte[1234]);
        File.SetCreationTimeUtc(plan.Path, created);
        File.SetLastWriteTimeUtc(plan.Path, written);

        var r = new Deleter(bin).Execute(plan, plan.Path, root, false);

        Assert.Equal(DeleteBlock.Replaced, r.Block);
        Assert.Empty(bin.Calls);
        Assert.True(File.Exists(plan.Path));
    }

    [Fact]
    public void Windows_file_identity_includes_the_volume_and_changes_when_a_file_is_recreated()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var t = new TempTree().File("x.bin", 10);
        var p = Path.Combine(t.Root, "x.bin");
        var ids = new HashSet<string>();
        for (int i = 0; i < 5; i++)
        {
            var id = DeleteSafety.Identity(new FileInfo(p));
            Assert.NotNull(id);
            Assert.DoesNotMatch("^f:00000000:", id); // volume serial read correctly
            ids.Add(id!);
            File.Delete(p);
            File.WriteAllBytes(p, new byte[10]);
        }
        Assert.Equal(5, ids.Count);
    }

    [Fact]
    public void Only_fixed_drives_are_offered_on_Windows()
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.True(DeleteSafety.HasRecycleBin(Path.GetTempPath()));
        Assert.False(DeleteSafety.HasRecycleBin(@"\\server\share\x"));
    }

    [Fact]
    public void A_folder_that_gained_a_link_after_the_confirmation_showed_is_refused()
    {
        using var outside = new TempTree().File("precious.bin", 777);
        using var t = Sample();
        var root = Scan(t);
        using var bin = new SandboxRecycler(t.Root);
        var (plan, _) = DeleteSafety.Plan(Find(root, "photos"), root, false);
        LinkDir(Path.Combine(t.Root, "photos", "late-link"), outside.Root);

        var r = new Deleter(bin).Execute(plan!, plan!.Path, root, false);

        Assert.Equal(DeleteBlock.ContainsLink, r.Block);
        Assert.Empty(bin.Calls);
    }

    [Fact]
    public void A_rescan_after_the_confirmation_showed_blocks_the_delete()
    {
        using var t = Sample();
        var root = Scan(t);
        using var bin = new SandboxRecycler(t.Root);
        var (plan, _) = DeleteSafety.Plan(Find(root, "keep.bin"), root, false);
        var fresh = Scan(t);

        Assert.Equal(DeleteBlock.NotInTree, new Deleter(bin).Execute(plan!, plan!.Path, fresh, false).Block);
        Assert.Equal(DeleteBlock.ScanRunning, new Deleter(bin).Execute(plan, plan.Path, root, scanRunning: true).Block);
        Assert.Empty(bin.Calls);
    }

    [Fact]
    public void The_tree_shrinks_by_exactly_what_was_deleted()
    {
        using var t = Sample();
        var root = Scan(t);
        using var bin = new SandboxRecycler(t.Root);
        var photos = (DirNode)Find(root, "photos");
        long rootSize = root.Size, files = root.FileCount, dirs = root.DirCount;
        long pSize = photos.Size, pFiles = photos.FileCount, pDirs = photos.DirCount;
        var (plan, _) = DeleteSafety.Plan(photos, root, false);

        new Deleter(bin).Execute(plan!, plan!.Path, root, false);

        Assert.DoesNotContain(photos, root.Children!);
        Assert.Equal(rootSize - pSize, root.Size);
        Assert.Equal(files - pFiles, root.FileCount);
        Assert.Equal(dirs - pDirs - 1, root.DirCount);
        var rescanned = Scan(t);
        Assert.Equal(rescanned.Size, root.Size);
        Assert.Equal(rescanned.FileCount, root.FileCount);
        Assert.Equal(rescanned.DirCount, root.DirCount);
    }

    [Fact]
    public void A_Recycle_Bin_failure_changes_neither_the_disk_nor_the_tree()
    {
        using var t = Sample();
        var root = Scan(t);
        var before = Snapshot(t.Root);
        long size = root.Size;
        using var bin = new SandboxRecycler(t.Root) { Instead = _ => throw new IOException("disk on fire") };
        var (plan, _) = DeleteSafety.Plan(Find(root, "keep.bin"), root, false);

        var r = new Deleter(bin).Execute(plan!, plan!.Path, root, false);

        Assert.Equal(DeleteOutcome.Failed, r.Outcome);
        Assert.Equal(before, Snapshot(t.Root));
        Assert.Equal(size, root.Size);
        Assert.Contains(plan.Node, root.Children!);
    }

    [Fact]
    public void A_delete_cancelled_in_the_Windows_prompt_leaves_the_tree_alone()
    {
        using var t = Sample();
        var root = Scan(t);
        long size = root.Size;
        using var bin = new SandboxRecycler(t.Root) { Instead = _ => { } }; // returns, but nothing was moved
        var (plan, _) = DeleteSafety.Plan(Find(root, "keep.bin"), root, false);

        var r = new Deleter(bin).Execute(plan!, plan!.Path, root, false);

        Assert.Equal(DeleteOutcome.StillThere, r.Outcome);
        Assert.Equal(size, root.Size);
        Assert.True(File.Exists(plan.Path));
    }

    [Fact]
    public void The_same_confirmation_can_only_delete_once()
    {
        using var t = Sample();
        var root = Scan(t);
        using var bin = new SandboxRecycler(t.Root);
        var (plan, _) = DeleteSafety.Plan(Find(root, "keep.bin"), root, false);
        var d = new Deleter(bin);
        Assert.Equal(DeleteOutcome.Deleted, d.Execute(plan!, plan!.Path, root, false).Outcome);
        File.WriteAllBytes(plan.Path, new byte[5]); // something new appears at the same path
        Assert.Equal(DeleteOutcome.Blocked, d.Execute(plan, plan.Path, root, false).Outcome);
        Assert.Single(bin.Calls);
        Assert.True(File.Exists(plan.Path));
    }

    [Fact]
    public void Unicode_names_delete_correctly()
    {
        using var t = new TempTree().File("Ærø 写真 🎵/fil ü.bin", 500).File("Ærø 写真/fil ü.bin", 600).File("Aero/fil u.bin", 700);
        var root = Scan(t);
        var before = Snapshot(t.Root);
        using var bin = new SandboxRecycler(t.Root);
        var (plan, _) = DeleteSafety.Plan(Find(root, "Ærø 写真 🎵"), root, false);

        Assert.Equal(DeleteOutcome.Deleted, new Deleter(bin).Execute(plan!, plan!.Path, root, false).Outcome);
        Assert.Equal(Without(before, "Ærø 写真 🎵"), Snapshot(t.Root));
    }

    [Fact]
    public void Random_trees_and_random_picks_only_ever_lose_the_picked_item()
    {
        var rnd = new Random(1234);
        string[] names = { "a", "a b", "a.b", "a.b.c", "ab", "A", "b", "photos", "photos 2018", "x (1)", "é", "日本" };
        for (int round = 0; round < 40; round++)
        {
            using var t = new TempTree();
            for (int i = 0; i < 25; i++)
            {
                var depth = rnd.Next(1, 4);
                var parts = Enumerable.Range(0, depth).Select(_ => names[rnd.Next(names.Length)]).ToArray();
                var rel = string.Join('/', parts) + (rnd.Next(2) == 0 ? ".bin" : "");
                try { t.File(rel, rnd.Next(1, 20000)); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            var root = Scan(t, keep: 3);
            var all = new List<Node>();
            var stack = new Stack<Node>(root.Children!);
            while (stack.Count > 0)
            {
                var n = stack.Pop();
                all.Add(n);
                if (n is DirNode d && d.Children != null) foreach (var c in d.Children) stack.Push(c);
            }
            for (int pick = 0; pick < 3 && all.Count > 0; pick++)
            {
                var node = all[rnd.Next(all.Count)];
                var before = Snapshot(t.Root);
                using var bin = new SandboxRecycler(t.Root);
                var (plan, block) = DeleteSafety.Plan(node, root, false);
                if (plan == null)
                {
                    Assert.True(node is FileNode { IsAggregate: true } || block == DeleteBlock.NotInTree, $"{node.FullPath}: {block}");
                    Assert.Equal(before, Snapshot(t.Root));
                    continue;
                }
                var r = new Deleter(bin).Execute(plan, plan.Path, root, false);
                Assert.Equal(DeleteOutcome.Deleted, r.Outcome);
                Assert.Equal(new[] { node.FullPath }, bin.Calls);
                Assert.Equal(Without(before, Path.GetRelativePath(t.Root, node.FullPath)), Snapshot(t.Root));
            }
        }
    }

    // ---------------- The real Recycle Bin ----------------

    [Fact]
    public void The_shell_recycler_refuses_unsafe_paths_before_calling_Windows()
    {
        var r = new ShellRecycler(IntPtr.Zero);
        Assert.Throws<ArgumentException>(() => r.Recycle("relative.txt", false));
        Assert.Throws<ArgumentException>(() => r.Recycle(Path.Combine(Path.GetTempPath(), "trailing."), false));
        Assert.Throws<ArgumentException>(() => r.Recycle(Path.Combine(Path.GetTempPath(), "*"), false));
        Assert.Throws<ArgumentException>(() => r.Recycle(Path.GetPathRoot(Path.GetTempPath())!, true));
    }

    [Fact]
    public void Windows_only_the_shell_recycler_moves_one_temp_file_to_the_Recycle_Bin_and_nothing_else()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var t = new TempTree().File("UberDisk-test-delete-me.tmp", 10).File("UberDisk-test-delete-me.tmp.keep", 10).File("UberDisk-test-delete-me", 10);
        var root = Scan(t);
        var before = Snapshot(t.Root);
        var (plan, block) = DeleteSafety.Plan(Find(root, "UberDisk-test-delete-me.tmp"), root, false);
        Assert.Equal(DeleteBlock.None, block);

        var r = new Deleter(new ShellRecycler(IntPtr.Zero)).Execute(plan!, plan!.Path, root, false);

        Assert.Equal(DeleteOutcome.Deleted, r.Outcome);
        Assert.Equal(Without(before, "UberDisk-test-delete-me.tmp"), Snapshot(t.Root));
    }
}
