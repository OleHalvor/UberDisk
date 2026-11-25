using System.Runtime.InteropServices;
using UberDisk.Core.Model;

namespace UberDisk.Core.Deleting;

/// <summary>Moves one item to the Recycle Bin. Gets only absolute paths that passed <see cref="DeleteSafety"/>.</summary>
public interface IRecycler
{
    /// <summary>Returns normally when the shell reported success; throws otherwise. Must touch nothing but <paramref name="path"/>.</summary>
    void Recycle(string path, bool isDirectory);
}

public enum DeleteOutcome { Deleted, Blocked, Failed, StillThere }

public sealed record DeleteResult(DeleteOutcome Outcome, DeleteBlock Block = DeleteBlock.None, string? Error = null)
{
    public string Message => Outcome switch
    {
        DeleteOutcome.Deleted => "Moved to the Recycle Bin.",
        DeleteOutcome.Blocked => DeleteSafety.Explain(Block),
        DeleteOutcome.StillThere => "It's still there (the delete was cancelled or only partly done). The chart wasn't changed; rescan the folder to see what's left.",
        _ => "Couldn't delete: " + Error,
    };
}

/// <summary>
/// Runs a confirmed delete: one last check, one Recycle Bin call for exactly the confirmed path, a check that it is
/// gone, then the segment is taken out of the tree.
/// </summary>
public sealed class Deleter
{
    private readonly IRecycler _recycler;
    public Deleter(IRecycler recycler) => _recycler = recycler;

    /// <param name="confirmedPath">The path shown in the confirmation the user accepted.</param>
    /// <param name="currentTree">The tree on screen now for the plan's drive.</param>
    /// <param name="treeLock">Held while the tree is changed (the scan coordinator's lock), or null.</param>
    public DeleteResult Execute(DeletePlan plan, string confirmedPath, DirNode? currentTree, bool scanRunning, object? treeLock = null)
    {
        var block = DeleteSafety.Recheck(plan, confirmedPath, currentTree, scanRunning);
        if (block != DeleteBlock.None) return new DeleteResult(DeleteOutcome.Blocked, block);

        try { _recycler.Recycle(plan.Path, plan.IsDirectory); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new DeleteResult(DeleteOutcome.Failed, Error: ex.Message);
        }

        if (File.Exists(plan.Path) || Directory.Exists(plan.Path))
            return new DeleteResult(DeleteOutcome.StillThere);

        if (treeLock != null) { lock (treeLock) Detach(plan); }
        else Detach(plan);
        return new DeleteResult(DeleteOutcome.Deleted);
    }

    private static void Detach(DeletePlan plan)
    {
        if (plan.Node.Parent is { } parent && DeleteSafety.IsAttached(plan.Node, plan.Tree))
            parent.RemoveChild(plan.Node);
    }
}

/// <summary>
/// The Windows shell's Recycle Bin (SHFileOperation, FO_DELETE with FOF_ALLOWUNDO). If an item is too big for the
/// Recycle Bin, Windows asks before deleting it for good (FOF_WANTNUKEWARNING) instead of doing it silently.
/// </summary>
public sealed class ShellRecycler : IRecycler
{
    private readonly IntPtr _owner;
    public ShellRecycler(IntPtr ownerWindow) => _owner = ownerWindow;

    public void Recycle(string path, bool isDirectory)
    {
        if (!Path.IsPathFullyQualified(path) || DeleteSafety.CheckPath(path, Path.GetPathRoot(path)!) != DeleteBlock.None)
            throw new ArgumentException("Refusing an unsafe path.", nameof(path));
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The Recycle Bin is Windows only.");

        const uint FO_DELETE = 3;
        const ushort FOF_NOCONFIRMATION = 0x0010, FOF_ALLOWUNDO = 0x0040, FOF_NOERRORUI = 0x0400,
            FOF_WANTNUKEWARNING = 0x4000;
        var op = new SHFILEOPSTRUCTW
        {
            hwnd = _owner,
            wFunc = FO_DELETE,
            pFrom = path + "\0\0", // exactly one item: double-null-terminated list
            pTo = null,
            fFlags = (ushort)(FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_WANTNUKEWARNING | FOF_NOERRORUI),
        };
        int rc = SHFileOperationW(ref op);
        if (op.fAnyOperationsAborted) throw new OperationCanceledException("Cancelled.");
        if (rc != 0) throw new IOException($"Windows couldn't move it to the Recycle Bin (error 0x{rc:X}).");
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCTW
    {
        public IntPtr hwnd;
        public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperationW(ref SHFILEOPSTRUCTW op);
}
