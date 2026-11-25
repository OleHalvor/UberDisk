using UberDisk.Core.Model;

namespace UberDisk.Core.Viz;

/// <summary>
/// Where the chart is zoomed to, kept as folder names below the drive root rather than node references.
/// That way the view survives a rescan: the same path is looked up in the new tree, and while a rescan
/// is still filling in, the chart shows the deepest part of the path that exists so far.
/// Back/forward history works the same way.
/// </summary>
public sealed class Navigator
{
    private readonly List<string[]> _back = new();
    private readonly List<string[]> _forward = new();

    public IReadOnlyList<string> Path { get; private set; } = Array.Empty<string>();

    public bool CanGoBack => _back.Count > 0;
    public bool CanGoForward => _forward.Count > 0;
    public bool CanGoUp => Path.Count > 0;

    public event Action? Changed;

    /// <summary>The folder to show for <paramref name="root"/>: the deepest folder of the path that exists in it.</summary>
    public DirNode Resolve(DirNode root)
    {
        var d = root;
        foreach (var name in Path)
        {
            var next = d.FindChildDir(name);
            if (next == null) break;
            d = next;
        }
        return d;
    }

    public static string[] PathOf(DirNode node)
    {
        var parts = new List<string>();
        for (var n = node; n.Parent != null; n = n.Parent) parts.Add(n.Name);
        parts.Reverse();
        return parts.ToArray();
    }

    public void ZoomTo(DirNode node) => Go(PathOf(node));

    public void Go(string[] path)
    {
        if (path.SequenceEqual(Path, StringComparer.OrdinalIgnoreCase)) return;
        _back.Add(Path.ToArray());
        _forward.Clear();
        Path = path;
        Changed?.Invoke();
    }

    public void Up()
    {
        if (!CanGoUp) return;
        Go(Path.Take(Path.Count - 1).ToArray());
    }

    public void Back()
    {
        if (!CanGoBack) return;
        _forward.Add(Path.ToArray());
        Path = _back[^1];
        _back.RemoveAt(_back.Count - 1);
        Changed?.Invoke();
    }

    public void Forward()
    {
        if (!CanGoForward) return;
        _back.Add(Path.ToArray());
        Path = _forward[^1];
        _forward.RemoveAt(_forward.Count - 1);
        Changed?.Invoke();
    }

    /// <summary>Starts over at the drive root with empty history (used when switching drives).</summary>
    public void Reset(string[]? path = null)
    {
        _back.Clear();
        _forward.Clear();
        Path = path ?? Array.Empty<string>();
        Changed?.Invoke();
    }
}
