namespace UberDisk.Core.Model;

/// <summary>
/// One segment in the tree: a folder, a single file, or the "other files" bucket of a folder.
/// Sizes are always allocated size (size on disk). Sizes and counts grow while a scan runs and are
/// updated with Interlocked from scanner threads, so the UI can read them at any time.
/// </summary>
public abstract class Node
{
    protected Node(string name, DirNode? parent)
    {
        Name = name;
        Parent = parent;
    }

    public string Name { get; }
    public DirNode? Parent { get; internal set; }

    internal long _size;
    /// <summary>Allocated bytes (size on disk), deep for folders.</summary>
    public long Size => Volatile.Read(ref _size);

    public abstract bool IsDirectory { get; }

    public int Depth
    {
        get
        {
            int d = 0;
            for (var p = Parent; p != null; p = p.Parent) d++;
            return d;
        }
    }

    /// <summary>Full Windows path, built on demand by walking up the parents (nodes don't store it, to save memory).</summary>
    public string FullPath
    {
        get
        {
            var parts = new Stack<string>();
            Node? n = this;
            while (n != null)
            {
                parts.Push(n.Name);
                n = n.Parent;
            }
            var root = parts.Pop();
            var path = root;
            foreach (var p in parts)
                path = System.IO.Path.Join(path, p);
            return path;
        }
    }

    /// <summary>The ancestor directly below the drive root (or this node if it is one). Used for colour.</summary>
    public Node? TopLevel
    {
        get
        {
            Node n = this;
            if (n.Parent == null) return null;
            while (n.Parent!.Parent != null) n = n.Parent;
            return n;
        }
    }
}

public sealed class FileNode : Node
{
    public FileNode(string name, DirNode parent, long size, int count = 1) : base(name, parent)
    {
        _size = size;
        Count = count;
    }

    /// <summary>1 for a single file. More than 1 for the bucket that groups a folder's many small files.</summary>
    public int Count { get; }
    public bool IsAggregate => Count > 1;
    public override bool IsDirectory => false;
}

public sealed class DirNode : Node
{
    public DirNode(string name, DirNode? parent) : base(name, parent) { }

    public override bool IsDirectory => true;

    private Node[]? _children;
    /// <summary>
    /// Sub-folders and files. Null until the folder has been read; published once as a complete array
    /// (replaced wholesale on "rescan this folder"), so readers never see a half-built list.
    /// </summary>
    public Node[]? Children => Volatile.Read(ref _children);

    internal void PublishChildren(Node[] children) => Volatile.Write(ref _children, children);

    internal long _files;
    internal long _dirs;
    /// <summary>Files in this folder and below.</summary>
    public long FileCount => Volatile.Read(ref _files);
    /// <summary>Folders below this folder (not counting itself).</summary>
    public long DirCount => Volatile.Read(ref _dirs);

    /// <summary>True when the folder could not be opened (access denied and similar).</summary>
    public bool Inaccessible { get; internal set; }

    /// <summary>True when the children are final and sorted by size, largest first.</summary>
    public bool Sorted { get; internal set; }

    /// <summary>Palette slot for top-level folders, assigned by the scanner. -1 elsewhere.</summary>
    public int HueSlot { get; internal set; } = -1;

    /// <summary>Synthetic node, such as "space the scan could not see". Not a real folder.</summary>
    public bool IsSynthetic { get; internal set; }

    /// <summary>Adds size and counts to this folder and every ancestor.</summary>
    internal void AddUp(long bytes, long files, long dirs)
    {
        for (DirNode? d = this; d != null; d = d.Parent)
        {
            if (bytes != 0) Interlocked.Add(ref d._size, bytes);
            if (files != 0) Interlocked.Add(ref d._files, files);
            if (dirs != 0) Interlocked.Add(ref d._dirs, dirs);
        }
    }

    public DirNode? FindChildDir(string name)
    {
        var ch = Children;
        if (ch == null) return null;
        foreach (var c in ch)
            if (c is DirNode d && string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase))
                return d;
        return null;
    }

    /// <summary>Sorts every folder's children by size, largest first. Call once the scan has finished.</summary>
    public void SortDeep()
    {
        var stack = new Stack<DirNode>();
        stack.Push(this);
        while (stack.Count > 0)
        {
            var d = stack.Pop();
            var ch = d.Children;
            if (ch == null) continue;
            var copy = (Node[])ch.Clone();
            Array.Sort(copy, BySizeDesc.Instance);
            d.PublishChildren(copy);
            d.Sorted = true;
            foreach (var c in copy)
                if (c is DirNode cd) stack.Push(cd);
        }
    }

    /// <summary>
    /// Replaces a child folder with a freshly scanned one (rescan of a single folder) and fixes the
    /// totals of every ancestor.
    /// </summary>
    public void ReplaceChild(DirNode oldChild, DirNode newChild)
    {
        var ch = Children ?? throw new InvalidOperationException("Folder has no children yet.");
        var copy = (Node[])ch.Clone();
        int i = Array.IndexOf(copy, oldChild);
        if (i < 0) throw new ArgumentException("Not a child of this folder.", nameof(oldChild));
        newChild.Parent = this;
        newChild.HueSlot = oldChild.HueSlot;
        copy[i] = newChild;
        Array.Sort(copy, BySizeDesc.Instance);
        PublishChildren(copy);
        AddUp(newChild.Size - oldChild.Size,
              newChild.FileCount - oldChild.FileCount,
              newChild.DirCount - oldChild.DirCount);
    }
}

public sealed class BySizeDesc : IComparer<Node>
{
    public static readonly BySizeDesc Instance = new();
    public int Compare(Node? x, Node? y)
    {
        int c = y!.Size.CompareTo(x!.Size);
        return c != 0 ? c : string.Compare(x.Name, y.Name, StringComparison.OrdinalIgnoreCase);
    }
}
