namespace UberDisk.Core.Scanning;

/// <summary>
/// One entry of a folder listing. Readers may leave <see cref="Name"/> null for files and put the
/// characters in this thread's <see cref="NameArena"/> instead: most file names are never needed (only
/// the largest files of each folder are kept by name), so not turning them into strings saves hundreds of
/// thousands of allocations per scan.
/// </summary>
public readonly record struct RawEntry(string? Name, bool IsDirectory, long AllocatedSize, bool IsReparsePoint,
    int NameStart = 0, int NameLength = 0)
{
    public string GetName() => Name ?? NameArena.Get(NameStart, NameLength);
}

/// <summary>Per-thread buffer of name characters for the folder being read. Reset before each folder.</summary>
public static class NameArena
{
    [ThreadStatic] private static char[]? t_chars;
    [ThreadStatic] private static int t_count;

    public static void Reset() => t_count = 0;

    public static unsafe int Add(char* chars, int length)
    {
        var buf = t_chars ??= new char[16 * 1024];
        if (t_count + length > buf.Length)
        {
            Array.Resize(ref buf, Math.Max(buf.Length * 2, t_count + length));
            t_chars = buf;
        }
        int start = t_count;
        new ReadOnlySpan<char>(chars, length).CopyTo(buf.AsSpan(start));
        t_count += length;
        return start;
    }

    public static string Get(int start, int length) => new(t_chars!, start, length);
}

/// <summary>Lists one folder. Implementations must be thread-safe (one instance is shared by all workers).</summary>
public interface IDirectoryReader
{
    /// <summary>
    /// Appends the entries of <paramref name="path"/> to <paramref name="sink"/>, without "." and "..".
    /// Returns false when the folder can't be opened (access denied, gone, ...).
    /// </summary>
    bool TryRead(string path, List<RawEntry> sink);
}

public static class DirectoryReaders
{
    /// <summary>The fastest reader for this OS: bulk NT directory queries on Windows, .NET enumeration elsewhere.</summary>
    public static IDirectoryReader CreateDefault() =>
        OperatingSystem.IsWindows() ? new Win32DirectoryReader() : new PortableDirectoryReader();
}
