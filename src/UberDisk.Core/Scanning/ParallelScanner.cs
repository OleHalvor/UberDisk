using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using UberDisk.Core.Model;

namespace UberDisk.Core.Scanning;

public sealed class ScanOptions
{
    /// <summary>Worker threads. On SSDs many threads in flight is what makes the scan fast.</summary>
    public int Threads { get; init; } = Math.Clamp(Environment.ProcessorCount, 2, 16);

    /// <summary>
    /// How many files each folder keeps as separate segments (the largest ones). The rest are summed into
    /// one "N smaller files" segment, which keeps memory small on drives with millions of files.
    /// </summary>
    public int MaxFilesPerFolder { get; init; } = 48;

    public IDirectoryReader? Reader { get; init; }

    /// <summary>Folder names below the root to read first (the folder the user is zoomed into).</summary>
    public IReadOnlyList<string>? PriorityPath { get; init; }
}

/// <summary>
/// A running scan of one folder tree. Many worker threads take folders off a shared queue, list them and
/// queue their sub-folders. Sizes are added up the tree as each folder is read, so the tree can be drawn
/// live while the scan runs.
/// </summary>
public sealed class ScanJob
{
    private readonly string _rootPath;
    private readonly ScanOptions _options;
    private readonly IDirectoryReader _reader;
    private readonly ConcurrentStack<(DirNode node, string path, int prio)> _work = new();
    // Folders on the priority path and everything inside the zoomed folder. Workers always take these first,
    // so the folder being looked at is read before the rest of the drive (not just pushed last onto a shared
    // stack, where other threads' subfolders could pile on top of it).
    private readonly ConcurrentStack<(DirNode node, string path, int prio)> _hot = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly ManualResetEventSlim _running = new(true);
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource<ScanJob> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Stopwatch _clock = new();
    private int _pending;
    private int _activeWorkers;
    private volatile bool _background;

    public ScanJob(DirNode root, string rootPath, ScanOptions? options = null)
    {
        Root = root;
        _rootPath = rootPath;
        _options = options ?? new ScanOptions();
        _reader = _options.Reader ?? DirectoryReaders.CreateDefault();
    }

    public DirNode Root { get; }
    public Task<ScanJob> Completion => _done.Task;
    public bool IsCancelled => _cts.IsCancellationRequested;
    public TimeSpan Elapsed => _clock.Elapsed;
    public bool IsPaused => !_running.IsSet;

    private long _foldersRead;
    public long FoldersRead => Interlocked.Read(ref _foldersRead);

    /// <summary>
    /// Background mode puts the worker threads in Windows' background processing mode (low CPU and I/O
    /// priority), so a background scan doesn't make the machine feel slow. Can be switched while running:
    /// workers pick it up before each folder.
    /// </summary>
    public bool Background
    {
        get => _background;
        set => _background = value;
    }

    public void Start()
    {
        _clock.Start();
        Enqueue(Root, _rootPath, _options.PriorityPath is { Count: > 0 } ? 0 : -1);
        for (int i = 0; i < _options.Threads; i++)
        {
            var t = new Thread(Worker) { IsBackground = true, Name = $"UberDisk scan {i}" };
            t.Start();
        }
    }

    public void Cancel()
    {
        _cts.Cancel();
        _running.Set();
        _signal.Release(_options.Threads);
    }

    public void Pause() => _running.Reset();
    public void Resume() => _running.Set();

    /// <summary>Scans synchronously. Handy for tests and for one-off folder rescans.</summary>
    public static DirNode ScanNow(string rootPath, string? rootName = null, ScanOptions? options = null)
    {
        var job = new ScanJob(new DirNode(rootName ?? rootPath, null), rootPath, options);
        job.Start();
        job.Completion.GetAwaiter().GetResult();
        return job.Root;
    }

    private void Enqueue(DirNode node, string path, int prio = -1)
    {
        Interlocked.Increment(ref _pending);
        if (prio >= 0) _hot.Push((node, path, prio));
        else _work.Push((node, path, prio));
        _signal.Release();
    }

    private void Worker()
    {
        var entries = new List<RawEntry>(256);
        var files = new List<RawEntry>(256);
        bool inBackgroundMode = false;
        Interlocked.Increment(ref _activeWorkers);
        try
        {
            while (true)
            {
                _signal.Wait();
                if (_cts.IsCancellationRequested) break;
                _running.Wait();
                if (_cts.IsCancellationRequested) break;

                if (!_hot.TryPop(out var item) && !_work.TryPop(out item))
                {
                    if (Volatile.Read(ref _pending) == 0) break;
                    continue;
                }

                if (inBackgroundMode != _background)
                {
                    inBackgroundMode = _background;
                    ThreadPriority.SetBackgroundMode(inBackgroundMode);
                }

                try
                {
                    ReadFolder(item.node, item.path, item.prio, entries, files);
                }
                catch (Exception)
                {
                    item.node.Inaccessible = true;
                    item.node.PublishChildren(Array.Empty<Node>());
                }

                Interlocked.Increment(ref _foldersRead);
                if (Interlocked.Decrement(ref _pending) == 0)
                {
                    // Last folder done: wake every worker so they can exit.
                    _signal.Release(_options.Threads);
                    break;
                }
            }
        }
        finally
        {
            if (inBackgroundMode) ThreadPriority.SetBackgroundMode(false);
            if (Interlocked.Decrement(ref _activeWorkers) == 0) Finish();
        }
    }

    private void ReadFolder(DirNode node, string path, int prio, List<RawEntry> entries, List<RawEntry> files)
    {
        entries.Clear();
        NameArena.Reset();
        if (!_reader.TryRead(path, entries))
        {
            node.Inaccessible = true;
            node.PublishChildren(Array.Empty<Node>());
            return;
        }

        files.Clear();
        var children = new List<Node>(entries.Count);
        var subdirs = new List<(DirNode, string)>();
        long fileBytes = 0;
        foreach (var e in entries)
        {
            if (e.IsDirectory)
            {
                // Junctions and symlinked folders point somewhere already counted (or would loop). Skip them.
                if (e.IsReparsePoint) continue;
                var d = new DirNode(e.GetName(), node);
                children.Add(d);
                subdirs.Add((d, Path.Join(path, d.Name)));
            }
            else
            {
                files.Add(e);
                fileBytes += e.AllocatedSize;
            }
        }

        int keep = _options.MaxFilesPerFolder;
        if (files.Count <= keep)
        {
            foreach (var f in files) children.Add(new FileNode(f.GetName(), node, f.AllocatedSize));
        }
        else
        {
            files.Sort(static (a, b) => b.AllocatedSize.CompareTo(a.AllocatedSize));
            long rest = 0;
            for (int i = 0; i < files.Count; i++)
            {
                if (i < keep) children.Add(new FileNode(files[i].GetName(), node, files[i].AllocatedSize));
                else rest += files[i].AllocatedSize;
            }
            int restCount = files.Count - keep;
            children.Add(new FileNode($"{restCount:N0} smaller files", node, rest, restCount, aggregate: true));
        }

        node.PublishChildren(children.ToArray());
        node.AddUp(fileBytes, files.Count, subdirs.Count);

        // Priority: prio = depth along PriorityPath (the folder the user is zoomed into); prio = path length
        // means inside that folder, and its whole subtree stays hot. Hot folders are read before anything else,
        // so during a rescan the zoomed view comes back, and fills in, first.
        var path_ = _options.PriorityPath;
        int first = -1, childPrio = -1;
        if (prio >= 0 && path_ != null)
        {
            if (prio >= path_.Count) childPrio = path_.Count;
            else first = subdirs.FindIndex(x => string.Equals(x.Item1.Name, path_[prio], StringComparison.OrdinalIgnoreCase));
        }
        for (int i = 0; i < subdirs.Count; i++)
            Enqueue(subdirs[i].Item1, subdirs[i].Item2, i == first ? prio + 1 : childPrio);
    }

    private void Finish()
    {
        _clock.Stop();
        if (!_cts.IsCancellationRequested) Root.SortDeep();
        _done.TrySetResult(this);
    }
}

/// <summary>Windows background processing mode for the calling thread (low CPU and I/O priority).</summary>
internal static class ThreadPriority
{
    private const int THREAD_MODE_BACKGROUND_BEGIN = 0x00010000;
    private const int THREAD_MODE_BACKGROUND_END = 0x00020000;

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetThreadPriority(IntPtr thread, int priority);

    public static void SetBackgroundMode(bool on)
    {
        if (!OperatingSystem.IsWindows()) return;
        SetThreadPriority(GetCurrentThread(), on ? THREAD_MODE_BACKGROUND_BEGIN : THREAD_MODE_BACKGROUND_END);
    }
}
