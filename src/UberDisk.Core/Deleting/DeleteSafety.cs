using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using UberDisk.Core.Model;

namespace UberDisk.Core.Deleting;

/// <summary>Why a delete is not allowed. <see cref="None"/> means it is.</summary>
public enum DeleteBlock
{
    None,
    /// <summary>The drive root itself.</summary>
    DriveRoot,
    /// <summary>A made-up segment, such as "Unscanned (system / no access)", or something inside one.</summary>
    Synthetic,
    /// <summary>The bucket that groups a folder's many small files: not one file on disk.</summary>
    Bundle,
    /// <summary>The node is no longer part of the tree on screen (the drive or folder was rescanned).</summary>
    NotInTree,
    /// <summary>A scan is running on this drive.</summary>
    ScanRunning,
    /// <summary>The path isn't a plain, absolute path UberDisk can hand to the Recycle Bin safely.</summary>
    UnsafePath,
    /// <summary>Over 259 characters: the Recycle Bin can't take it.</summary>
    PathTooLong,
    /// <summary>Nothing is at the path any more.</summary>
    Missing,
    /// <summary>A file became a folder or the other way round.</summary>
    KindChanged,
    /// <summary>The item at the path is not the one that was confirmed (it was replaced since).</summary>
    Replaced,
    /// <summary>The item is a junction or symbolic link.</summary>
    Link,
    /// <summary>The folder has a junction or symbolic link inside.</summary>
    ContainsLink,
    /// <summary>Part of the folder couldn't be read, so its contents couldn't all be checked.</summary>
    Unreadable,
    /// <summary>The path to delete is not the path the user confirmed.</summary>
    NotConfirmed,
    /// <summary>Not a fixed local drive (network, removable): Windows may delete for good there instead of recycling.</summary>
    NoRecycleBin,
}

/// <summary>
/// What will be deleted, worked out from a node when the user picks Delete and shown in the confirmation.
/// <see cref="Path"/> is the only thing ever handed to the Recycle Bin.
/// </summary>
public sealed record DeletePlan(
    Node Node,
    DirNode Tree,
    string Path,
    bool IsDirectory,
    long Size,
    long FileCount,
    long DirCount,
    string Identity);

/// <summary>
/// Every check that stands between a right-click and the Recycle Bin. Pure functions over the tree, plus
/// <see cref="CheckDisk"/>, which looks at the real item on disk without changing anything.
/// </summary>
public static class DeleteSafety
{
    public const int MaxPath = 259;

    public static string Explain(DeleteBlock b) => b switch
    {
        DeleteBlock.None => "",
        DeleteBlock.DriveRoot => "A whole drive can't be deleted.",
        DeleteBlock.Synthetic => "This segment isn't a real file or folder.",
        DeleteBlock.Bundle => "This segment groups many small files. Open the folder in Explorer to delete them.",
        DeleteBlock.NotInTree => "The chart changed (it was rescanned). Nothing was deleted; try again.",
        DeleteBlock.ScanRunning => "A scan is running on this drive. Wait for it to finish.",
        DeleteBlock.UnsafePath => "The path has characters or parts that Windows could read as a different item. Delete it in Explorer.",
        DeleteBlock.PathTooLong => "The path is longer than the Recycle Bin accepts (259 characters). Delete it in Explorer.",
        DeleteBlock.Missing => "It's no longer there. Nothing was deleted.",
        DeleteBlock.KindChanged => "A file became a folder or the other way round since the scan. Nothing was deleted; rescan first.",
        DeleteBlock.Replaced => "It was replaced by another item since you confirmed. Nothing was deleted.",
        DeleteBlock.Link => "It's a junction or symbolic link. Delete it in Explorer.",
        DeleteBlock.ContainsLink => "The folder has a junction or symbolic link inside, which could point elsewhere. Delete it in Explorer.",
        DeleteBlock.Unreadable => "Part of the folder couldn't be read, so UberDisk can't check everything inside. Nothing was deleted.",
        DeleteBlock.NotConfirmed => "The path doesn't match the one you confirmed. Nothing was deleted.",
        DeleteBlock.NoRecycleBin => "This isn't a fixed local drive, so Windows might delete it for good instead of using the Recycle Bin. Delete it in Explorer.",
        _ => b.ToString(),
    };

    /// <summary>Checks that only need the tree. Cheap: used to decide whether the menu offers Delete at all.</summary>
    public static DeleteBlock CheckTree(Node node, DirNode? tree, bool scanRunning)
    {
        if (tree == null) return DeleteBlock.NotInTree;
        if (node.Parent == null) return node == tree ? DeleteBlock.DriveRoot : DeleteBlock.NotInTree;
        if (node is FileNode { IsAggregate: true }) return DeleteBlock.Bundle;
        for (Node? n = node; n != null; n = n.Parent)
            if (n is DirNode { IsSynthetic: true }) return DeleteBlock.Synthetic;
        if (!IsAttached(node, tree)) return DeleteBlock.NotInTree;
        if (scanRunning) return DeleteBlock.ScanRunning;
        var pathBlock = CheckPath(node.FullPath, tree.FullPath);
        if (pathBlock != DeleteBlock.None) return pathBlock;
        return HasRecycleBin(tree.FullPath) ? DeleteBlock.None : DeleteBlock.NoRecycleBin;
    }

    /// <summary>
    /// On Windows only fixed local drives are allowed: network shares and removable drives often have no Recycle
    /// Bin, and the shell may then delete for good. Elsewhere (tests) always true.
    /// </summary>
    public static bool HasRecycleBin(string anyPathOnDrive)
    {
        if (!OperatingSystem.IsWindows()) return true;
        try
        {
            var root = System.IO.Path.GetPathRoot(anyPathOnDrive);
            return !string.IsNullOrEmpty(root) && new DriveInfo(root).DriveType == DriveType.Fixed;
        }
        catch (Exception) { return false; }
    }

    /// <summary>True when every step from the node up to <paramref name="tree"/> is still a current child of its parent.</summary>
    public static bool IsAttached(Node node, DirNode tree)
    {
        Node n = node;
        while (n.Parent is { } p)
        {
            var ch = p.Children;
            if (ch == null || Array.IndexOf(ch, n) < 0) return false;
            n = p;
        }
        return n == tree;
    }

    /// <summary>
    /// The path must be absolute, under the drive root, without "." or ".." parts, wildcards or names that Windows
    /// trims (trailing dot or space), so the shell can't read it as a different item.
    /// </summary>
    public static DeleteBlock CheckPath(string path, string root)
    {
        if (string.IsNullOrEmpty(path) || path.Length > MaxPath) return path.Length > MaxPath ? DeleteBlock.PathTooLong : DeleteBlock.UnsafePath;
        if (path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal)) return DeleteBlock.UnsafePath;
        if (!System.IO.Path.IsPathFullyQualified(path)) return DeleteBlock.UnsafePath;
        if (path.IndexOfAny(new[] { '*', '?', '"', '<', '>', '|', '\0' }) >= 0) return DeleteBlock.UnsafePath;
        if (path.Any(char.IsControl)) return DeleteBlock.UnsafePath;

        var rootFull = TrimSep(root);
        var pathFull = TrimSep(path);
        if (pathFull.Length <= rootFull.Length) return DeleteBlock.DriveRoot;
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!pathFull.StartsWith(rootFull, cmp)) return DeleteBlock.UnsafePath;
        char sep = pathFull[rootFull.Length];
        if (sep != System.IO.Path.DirectorySeparatorChar && sep != System.IO.Path.AltDirectorySeparatorChar
            && !(rootFull.EndsWith(System.IO.Path.DirectorySeparatorChar) || rootFull.EndsWith(':'))) return DeleteBlock.UnsafePath;

        var rest = pathFull[rootFull.Length..].Trim('\\', '/');
        if (rest.Length == 0) return DeleteBlock.DriveRoot;
        foreach (var part in rest.Split('\\', '/'))
        {
            if (part.Length == 0 || part == "." || part == "..") return DeleteBlock.UnsafePath;
            if (part.EndsWith('.') || part.EndsWith(' ')) return DeleteBlock.UnsafePath;
            if (OperatingSystem.IsWindows() && part.Contains(':')) return DeleteBlock.UnsafePath; // alternate data stream
        }
        return DeleteBlock.None;
    }

    private static string TrimSep(string p)
    {
        // Keep "C:\" and "/" whole; trim a trailing separator elsewhere.
        if (p.Length > 1 && (p.EndsWith('\\') || p.EndsWith('/')) && !(p.Length == 3 && p[1] == ':'))
            return p[..^1];
        return p;
    }

    /// <summary>Builds the plan for the confirmation: tree checks, then a read-only look at the disk.</summary>
    public static (DeletePlan? plan, DeleteBlock block) Plan(Node node, DirNode? tree, bool scanRunning)
    {
        var b = CheckTree(node, tree, scanRunning);
        if (b != DeleteBlock.None) return (null, b);
        var path = node.FullPath;
        var (disk, id) = CheckDisk(path, node.IsDirectory);
        if (disk != DeleteBlock.None) return (null, disk);
        var d = node as DirNode;
        return (new DeletePlan(node, tree!, path, node.IsDirectory, node.Size,
            d?.FileCount ?? 1, d?.DirCount ?? 0, id!), DeleteBlock.None);
    }

    /// <summary>
    /// Looks at the item on disk, changing nothing: it must exist, be the expected kind, not be a link, and (for a
    /// folder) hold no links anywhere inside. Returns an identity that changes if the item is replaced.
    /// </summary>
    public static (DeleteBlock block, string? identity) CheckDisk(string path, bool isDirectory)
    {
        FileSystemInfo info = isDirectory ? new DirectoryInfo(path) : new FileInfo(path);
        try
        {
            info.Refresh();
            if (!info.Exists)
            {
                // Present as the other kind?
                bool other = isDirectory ? File.Exists(path) : Directory.Exists(path);
                return (other ? DeleteBlock.KindChanged : DeleteBlock.Missing, null);
            }
            if (IsLink(info)) return (DeleteBlock.Link, null);
            if (isDirectory)
            {
                var inner = CheckContents((DirectoryInfo)info);
                if (inner != DeleteBlock.None) return (inner, null);
            }
            var identity = Identity(info);
            if (identity == null) return (DeleteBlock.Unreadable, null);
            return (DeleteBlock.None, identity);
        }
        catch (UnauthorizedAccessException) { return (DeleteBlock.Unreadable, null); }
        catch (IOException) { return (DeleteBlock.Unreadable, null); }
    }

    private static bool IsLink(FileSystemInfo info) =>
        info.LinkTarget != null || (info.Attributes.HasFlag(FileAttributes.ReparsePoint) && IsNameSurrogate(info.FullName));

    /// <summary>Walks the folder without following links. Any link inside, or any part that can't be read, blocks.</summary>
    private static DeleteBlock CheckContents(DirectoryInfo dir)
    {
        var opts = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = false,
            AttributesToSkip = 0,
            ReturnSpecialDirectories = false,
        };
        var stack = new Stack<DirectoryInfo>();
        stack.Push(dir);
        while (stack.Count > 0)
        {
            var d = stack.Pop();
            IEnumerable<FileSystemInfo> entries;
            try { entries = d.EnumerateFileSystemInfos("*", opts).ToList(); }
            catch (UnauthorizedAccessException) { return DeleteBlock.Unreadable; }
            catch (IOException) { return DeleteBlock.Unreadable; }
            foreach (var e in entries)
            {
                if (IsLink(e)) return DeleteBlock.ContainsLink;
                if (e is DirectoryInfo sub) stack.Push(sub);
            }
        }
        return DeleteBlock.None;
    }

    /// <summary>
    /// What identifies this exact item: on Windows the volume serial and file index (stable across renames, new
    /// for a re-created item); elsewhere, kind, creation and last-write times and length. Null on Windows when the
    /// file ID can't be read: then the delete is refused rather than relying on times and size.
    /// </summary>
    public static string? Identity(FileSystemInfo info)
    {
        string kind = info is DirectoryInfo ? "d" : "f";
        if (!OperatingSystem.IsWindows())
            return $"{kind}:{info.CreationTimeUtc.Ticks}:{info.LastWriteTimeUtc.Ticks}:{(info is FileInfo f ? f.Length : 0)}";
        var id = WindowsFileId(info.FullName);
        return id == null ? null : $"{kind}:{id}";
    }

    /// <summary>
    /// The last check, run right before the Recycle Bin call: the confirmed path is the plan's path, the node is
    /// still in the tree on screen, and the same item is still there.
    /// </summary>
    public static DeleteBlock Recheck(DeletePlan plan, string confirmedPath, DirNode? currentTree, bool scanRunning)
    {
        if (!string.Equals(confirmedPath, plan.Path, StringComparison.Ordinal)) return DeleteBlock.NotConfirmed;
        if (currentTree != plan.Tree) return DeleteBlock.NotInTree;
        var b = CheckTree(plan.Node, currentTree, scanRunning);
        if (b != DeleteBlock.None) return b;
        if (!string.Equals(plan.Node.FullPath, plan.Path, StringComparison.Ordinal)) return DeleteBlock.NotConfirmed;
        var (disk, id) = CheckDisk(plan.Path, plan.IsDirectory);
        if (disk != DeleteBlock.None) return disk;
        if (!string.Equals(id, plan.Identity, StringComparison.Ordinal)) return DeleteBlock.Replaced;
        return DeleteBlock.None;
    }

    // ---------- Windows details ----------

    private static bool IsNameSurrogate(string path)
    {
        // Junctions and symlinks are "name surrogates"; OneDrive placeholders and dedup files are not, and are safe
        // to recycle like normal files. LinkTarget already covers the common cases; this catches other surrogates.
        if (!OperatingSystem.IsWindows()) return true;
        var tag = ReparseTag(path);
        return tag == null || (tag.Value & 0x20000000) != 0;
    }

    private static uint? ReparseTag(string path)
    {
        var h = FindFirstFileW(@"\\?\" + path, out var data);
        if (h == new IntPtr(-1)) return null;
        FindClose(h);
        return data.dwReserved0;
    }

    private static string? WindowsFileId(string path)
    {
        try
        {
            // FILE_FLAG_BACKUP_SEMANTICS (folders) | FILE_FLAG_OPEN_REPARSE_POINT (never follow a link)
            using var h = CreateFileW(@"\\?\" + path, 0, 7, IntPtr.Zero, 3, 0x02000000 | 0x00200000, IntPtr.Zero);
            if (h.IsInvalid) return null;
            if (!GetFileInformationByHandle(h, out var info)) return null;
            return $"{info.VolumeSerialNumber:X8}:{info.FileIndexHigh:X8}{info.FileIndexLow:X8}";
        }
        catch { return null; }
    }

    // Pack = 4: the FILETIME fields are 4-byte aligned in Windows' layout; C#'s default would align the longs to 8
    // and shift every field after them.
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct BY_HANDLE_FILE_INFORMATION
    {
        public uint FileAttributes;
        public long CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 4)]
    private struct WIN32_FIND_DATAW
    {
        public uint dwFileAttributes;
        public long ftCreationTime, ftLastAccessTime, ftLastWriteTime;
        public uint nFileSizeHigh, nFileSizeLow, dwReserved0, dwReserved1;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string cFileName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] public string cAlternateFileName;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security,
        uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle h, out BY_HANDLE_FILE_INFORMATION info);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindFirstFileW(string name, out WIN32_FIND_DATAW data);

    [DllImport("kernel32.dll")]
    private static extern bool FindClose(IntPtr h);
}
