using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using UberDisk.Core.Model;
using UberDisk.Core.Scanning;

namespace UberDisk.Core.Duplicates;

/// <summary>Identical files: same length and the same SHA-256 of the whole content.</summary>
public sealed class DuplicateGroup
{
    public DuplicateGroup(string name, long length, long allocated, IReadOnlyList<string> paths)
    {
        Name = name;
        Length = length;
        Allocated = allocated;
        Paths = paths;
    }

    /// <summary>File name of the first copy (copies can be named differently).</summary>
    public string Name { get; }
    /// <summary>File length in bytes.</summary>
    public long Length { get; }
    /// <summary>Size on disk of one copy.</summary>
    public long Allocated { get; }
    /// <summary>Full paths of the copies, sorted.</summary>
    public IReadOnlyList<string> Paths { get; }
    /// <summary>Space freed by keeping only one copy.</summary>
    public long Wasted => Allocated * (Paths.Count - 1);
    /// <summary>Position in the result, 0 = most space wasted. Picks the group's colour.</summary>
    public int Index { get; internal set; }
}

public sealed class DuplicateResult
{
    public DuplicateResult(string root, long minSize, IReadOnlyList<DuplicateGroup> groups)
    {
        Root = root;
        MinSize = minSize;
        Groups = groups;
        foreach (var g in groups)
        {
            Wasted += g.Wasted;
            Files += g.Paths.Count;
            foreach (var p in g.Paths) _byPath[p] = g;
        }
    }

    public string Root { get; }
    public long MinSize { get; }
    /// <summary>Sorted by space wasted, most first.</summary>
    public IReadOnlyList<DuplicateGroup> Groups { get; }
    public long Wasted { get; }
    public int Files { get; }

    private readonly Dictionary<string, DuplicateGroup> _byPath = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The group a file belongs to, by full path, or null.</summary>
    public DuplicateGroup? GroupOf(string path) => _byPath.GetValueOrDefault(path);
    public IEnumerable<string> AllPaths => _byPath.Keys;
}

public sealed class DuplicateOptions
{
    /// <summary>Files smaller than this are ignored. Duplicates are only worth acting on when they are big.</summary>
    public long MinSize { get; init; } = 500L * 1024 * 1024;
    /// <summary>Bytes read from the start and from the end for the quick hash.</summary>
    public int QuickChunk { get; init; } = 4 * 1024 * 1024;
    /// <summary>Hashes from earlier runs. Files whose length and last-write time are unchanged aren't read again.</summary>
    public HashCache? Cache { get; init; }
    /// <summary>While this returns true (a drive scan is running), hashing waits, so it never slows a scan.</summary>
    public Func<bool>? Busy { get; init; }
}

/// <summary>
/// Finds identical big files in a scanned tree. Read-only. Steps, each one only for files still in the running:
/// <list type="number">
/// <item>Files from the tree at or above the minimum size, grouped by size on disk, then by exact length.</item>
/// <item>Hard links to the same file are counted once (they don't take space twice).</item>
/// <item>Quick hash of the first and last few MB.</item>
/// <item>Full SHA-256 of the whole file.</item>
/// </list>
/// Runs on one background-priority thread. Candidates come from the tree; a folder whose big files didn't all fit
/// in the tree (it keeps the biggest few per folder) is listed from disk.
/// </summary>
public sealed class DuplicateFinder
{
    private readonly DirNode _root;
    private readonly DuplicateOptions _options;
    private readonly CancellationTokenSource _cts = new();
    private long _bytesToRead, _bytesRead, _bytesHashed;
    private volatile bool _done;

    /// <summary>Bytes actually read from disk so far (cached files aren't read).</summary>
    public long BytesHashed => Interlocked.Read(ref _bytesHashed);

    public DuplicateFinder(DirNode root, DuplicateOptions? options = null)
    {
        _root = root;
        _options = options ?? new DuplicateOptions();
    }

    public DirNode Root => _root;
    public long MinSize => _options.MinSize;

    /// <summary>0..1, by bytes to read in the current step (quick hashes, then full hashes).</summary>
    public double Progress
    {
        get
        {
            if (_done) return 1;
            long total = Interlocked.Read(ref _bytesToRead);
            return total <= 0 ? 0 : Math.Clamp((double)Interlocked.Read(ref _bytesRead) / total, 0, 1);
        }
    }

    public void Cancel() => _cts.Cancel();
    public bool IsCancelled => _cts.IsCancellationRequested;

    /// <summary>Runs on its own background-priority thread. The task fails with OperationCanceledException on Cancel.</summary>
    public Task<DuplicateResult> RunAsync()
    {
        var tcs = new TaskCompletionSource<DuplicateResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var t = new Thread(() =>
        {
            UberDisk.Core.Scanning.ThreadPriority.SetBackgroundMode(true);
            try { tcs.SetResult(Run()); }
            catch (OperationCanceledException) { tcs.SetCanceled(); }
            catch (Exception ex) { tcs.SetException(ex); }
        })
        { IsBackground = true, Name = "UberDisk duplicates" };
        t.Start();
        return tcs.Task;
    }

    /// <summary>Synchronous version, for tests.</summary>
    public DuplicateResult Run()
    {
        var ct = _cts.Token;
        string rootPath = _root.FullPath;

        // 1. Candidates from the tree. A folder keeps only its biggest files as separate items, so when even the
        // smallest of those is over the minimum and the rest are bundled, the folder itself is listed from disk.
        var big = new List<(string path, string name, long allocated)>();
        var stack = new Stack<DirNode>();
        stack.Push(_root);
        while (stack.Count > 0)
        {
            var d = stack.Pop();
            var children = d.Children;
            if (children == null) continue;
            bool bundled = false;
            long smallest = long.MaxValue;
            int before = big.Count;
            foreach (var c in children)
            {
                if (c is DirNode sub) { if (!sub.IsSynthetic) stack.Push(sub); }
                else if (c is FileNode { IsAggregate: true }) bundled = true;
                else if (c is FileNode f)
                {
                    smallest = Math.Min(smallest, f.Size);
                    if (f.Size >= _options.MinSize) big.Add((f.FullPath, f.Name, f.Size));
                }
            }
            if (bundled && smallest != long.MaxValue && smallest >= _options.MinSize)
            {
                ct.ThrowIfCancellationRequested();
                var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int i = before; i < big.Count; i++) known.Add(big[i].name);
                try
                {
                    foreach (var fi in new DirectoryInfo(d.FullPath).EnumerateFiles())
                        if (fi.Length >= _options.MinSize && known.Add(fi.Name))
                            big.Add((fi.FullName, fi.Name, (fi.Length + 4095) / 4096 * 4096)); // not in the tree: size on disk estimated from the length
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        // Exact length decides (size on disk can differ for identical files).
        var candidates = new List<Candidate>();
        foreach (var (path, name, allocated) in big)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) continue;
                candidates.Add(new Candidate(path, name, info.Length, allocated, info.LastWriteTimeUtc.Ticks, null));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        candidates = candidates.GroupBy(c => c.Length).Where(g => g.Count() >= 2).SelectMany(g => g)
            .Select(c => new Candidate(c.Path, c.Name, c.Length, c.Allocated, c.Ticks, FileId(c.Path))).ToList();

        // Same length; hard links to one file count once.
        var groups = candidates
            .GroupBy(c => c.Length)
            .Select(g => g.GroupBy(c => c.Id ?? c.Path, StringComparer.OrdinalIgnoreCase).Select(x => x.First()).ToList())
            .Where(g => g.Count >= 2)
            .ToList();

        var cache = _options.Cache;
        long quick = 0;
        foreach (var g in groups)
            foreach (var c in g)
                if (cache?.Get(c.Path, c.Length, c.Ticks)?.Quick == null) quick += QuickBytes(c.Length);
        Interlocked.Exchange(ref _bytesToRead, quick);

        // 2. Quick hash.
        var afterQuick = new List<List<Candidate>>();
        foreach (var g in groups)
        {
            foreach (var c in g)
            {
                var hit = cache?.Get(c.Path, c.Length, c.Ticks);
                c.Quick = hit?.Quick ?? TryHash(c, quickOnly: true, ct);
                if (hit?.Quick == null && c.Quick != null) cache?.Put(c.Path, c.Length, c.Ticks, c.Quick, null);
            }
            afterQuick.AddRange(g.Where(c => c.Quick != null).GroupBy(c => c.Quick!).Select(x => x.ToList()).Where(x => x.Count >= 2));
        }

        // 3. Full hash (files up to two quick chunks long are already fully read).
        long full = 0;
        foreach (var g in afterQuick)
            foreach (var c in g)
                if (c.Length > 2L * _options.QuickChunk && cache?.Get(c.Path, c.Length, c.Ticks)?.Full == null) full += c.Length;
        Interlocked.Exchange(ref _bytesRead, 0);
        Interlocked.Exchange(ref _bytesToRead, full);

        var result = new List<DuplicateGroup>();
        foreach (var g in afterQuick)
        {
            foreach (var c in g)
            {
                if (c.Length <= 2L * _options.QuickChunk) { c.Full = c.Quick; continue; }
                var hit = cache?.Get(c.Path, c.Length, c.Ticks);
                c.Full = hit?.Full ?? TryHash(c, quickOnly: false, ct);
                if (hit?.Full == null && c.Full != null) cache?.Put(c.Path, c.Length, c.Ticks, c.Quick, c.Full);
            }
            foreach (var same in g.Where(c => c.Full != null).GroupBy(c => c.Full!))
            {
                var copies = same.OrderBy(c => c.Path, StringComparer.OrdinalIgnoreCase).ToList();
                if (copies.Count < 2) continue;
                result.Add(new DuplicateGroup(copies[0].Name, copies[0].Length, copies.Max(c => c.Allocated),
                    copies.Select(c => c.Path).ToList()));
            }
        }

        result.Sort((a, b) => b.Wasted.CompareTo(a.Wasted));
        for (int i = 0; i < result.Count; i++) result[i].Index = i;
        _done = true;
        return new DuplicateResult(rootPath, _options.MinSize, result);
    }

    private long QuickBytes(long length) => Math.Min(length, 2L * _options.QuickChunk);

    private sealed class Candidate(string path, string name, long length, long allocated, long ticks, string? id)
    {
        public string Path { get; } = path;
        public string Name { get; } = name;
        public long Length { get; } = length;
        public long Allocated { get; } = allocated;
        public long Ticks { get; } = ticks;
        public string? Id { get; } = id;
        public string? Quick { get; set; }
        public string? Full { get; set; }
    }

    /// <summary>Quick: SHA-256 of length + first and last chunk. Full: SHA-256 of the whole file. Null if unreadable.</summary>
    private string? TryHash(Candidate c, bool quickOnly, CancellationToken ct)
    {
        try
        {
            using var fs = new FileStream(c.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                1, FileOptions.SequentialScan);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[1024 * 1024];
            if (quickOnly)
            {
                hash.AppendData(BitConverter.GetBytes(c.Length));
                long chunk = _options.QuickChunk;
                if (c.Length <= 2 * chunk) ReadInto(fs, hash, buffer, c.Length, ct);
                else
                {
                    ReadInto(fs, hash, buffer, chunk, ct);
                    fs.Seek(-chunk, SeekOrigin.End);
                    ReadInto(fs, hash, buffer, chunk, ct);
                }
            }
            else ReadInto(fs, hash, buffer, c.Length, ct);
            return Convert.ToHexString(hash.GetHashAndReset());
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private void ReadInto(FileStream fs, IncrementalHash hash, byte[] buffer, long count, CancellationToken ct)
    {
        while (count > 0)
        {
            ct.ThrowIfCancellationRequested();
            while (_options.Busy?.Invoke() == true)
            {
                ct.ThrowIfCancellationRequested();
                Thread.Sleep(100);
            }
            int n = fs.Read(buffer, 0, (int)Math.Min(buffer.Length, count));
            if (n <= 0) throw new IOException("File got shorter while reading.");
            hash.AppendData(buffer, 0, n);
            count -= n;
            Interlocked.Add(ref _bytesRead, n);
            Interlocked.Add(ref _bytesHashed, n);
        }
    }

    // ---------- Hard links: same volume serial + file index = same file ----------

    private static string? FileId(string path)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var h = CreateFileW(@"\\?\" + path, 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
            if (h.IsInvalid) return null;
            if (!GetFileInformationByHandle(h, out var info)) return null;
            return $"{info.VolumeSerialNumber:X8}:{info.FileIndexHigh:X8}{info.FileIndexLow:X8}";
        }
        catch { return null; }
    }

    // Pack = 4: Windows aligns the FILETIME fields to 4 bytes; C#'s default (8 for long) would shift the later fields.
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct BY_HANDLE_FILE_INFORMATION
    {
        public uint FileAttributes;
        public long CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security,
        uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle h, out BY_HANDLE_FILE_INFORMATION info);
}

/// <summary>
/// Hashes by path, valid while the file's length and last-write time are unchanged. Kept in memory and saved to
/// a small tab-separated file, so a rescan (or the next start) only reads files that changed.
/// </summary>
public sealed class HashCache
{
    public sealed record Entry(long Length, long Ticks, string? Quick, string? Full);

    private readonly Dictionary<string, Entry> _map = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private readonly string? _file;
    private bool _dirty;

    public HashCache(string? file = null)
    {
        _file = file;
        if (file == null || !File.Exists(file)) return;
        try
        {
            foreach (var line in File.ReadLines(file))
            {
                var p = line.Split('\t');
                if (p.Length != 5 || !long.TryParse(p[1], out var len) || !long.TryParse(p[2], out var ticks)) continue;
                _map[p[0]] = new Entry(len, ticks, p[3].Length > 0 ? p[3] : null, p[4].Length > 0 ? p[4] : null);
            }
        }
        catch { _map.Clear(); }
    }

    public Entry? Get(string path, long length, long ticks)
    {
        lock (_lock)
            return _map.TryGetValue(path, out var e) && e.Length == length && e.Ticks == ticks ? e : null;
    }

    public void Put(string path, long length, long ticks, string? quick, string? full)
    {
        lock (_lock)
        {
            _map[path] = new Entry(length, ticks, quick, full);
            _dirty = true;
        }
    }

    public void Save()
    {
        if (_file == null) return;
        lock (_lock)
        {
            if (!_dirty) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
                File.WriteAllLines(_file, _map.Select(kv => $"{kv.Key}\t{kv.Value.Length}\t{kv.Value.Ticks}\t{kv.Value.Quick}\t{kv.Value.Full}"));
                _dirty = false;
            }
            catch { /* the cache is only an optimisation */ }
        }
    }
}
