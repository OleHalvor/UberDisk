using UberDisk.Core.Model;
using UberDisk.Core.Scanning;

namespace UberDisk.Core.Drives;

public enum ScanState { NotScanned, Queued, Scanning, Done, Stopped }

/// <summary>Everything UberDisk knows about one drive this session: its last finished tree and any scan in progress.</summary>
public sealed class DriveSession
{
    internal DriveSession(DriveMeta meta) => Meta = meta;

    public DriveMeta Meta { get; internal set; }
    public ScanState State { get; internal set; }

    /// <summary>The tree to show: the running scan's tree while scanning, otherwise the last result.</summary>
    public DirNode? Tree { get; internal set; }
    public DateTime? ScannedAt { get; internal set; }
    public TimeSpan LastScanDuration { get; internal set; }
    public ScanJob? Job { get; internal set; }

    /// <summary>The last complete tree, kept while a rescan runs so Stop can bring it back.</summary>
    internal DirNode? LastCompleteTree { get; set; }

    /// <summary>While a rescan runs: the last finished result, else null.</summary>
    public DirNode? PreviousTree => State == ScanState.Scanning ? LastCompleteTree : null;

    /// <summary>
    /// 0..1. A rescan goes by time against the last scan's duration: bytes arrive very unevenly (one big file
    /// can be a tenth of the drive), so they make a jerky progress bar and a poor estimate. A first scan has
    /// nothing better than scanned bytes against the drive's used space.
    /// </summary>
    public double Progress
    {
        get
        {
            if (State == ScanState.Done) return 1;
            if (TimedRescan is { } t) return Math.Clamp(t.elapsed / t.last, 0, 0.99);
            if (Tree == null || Meta.UsedBytes <= 0) return 0;
            return Math.Clamp((double)Tree.Size / Meta.UsedBytes, 0, 0.99);
        }
    }

    /// <summary>Rough time left. Null until there is enough to go on.</summary>
    public TimeSpan? Remaining
    {
        get
        {
            var job = Job;
            if (State != ScanState.Scanning || job == null) return null;
            if (TimedRescan is { } t) return TimeSpan.FromSeconds(Math.Max(0, t.last - t.elapsed));
            double p = Progress;
            double secs = job.Elapsed.TotalSeconds;
            if (p < 0.03 || secs < 0.5) return null;
            return TimeSpan.FromSeconds(secs / p * (1 - p));
        }
    }

    private (double elapsed, double last)? TimedRescan
    {
        get
        {
            var job = Job;
            if (State != ScanState.Scanning || job == null || LastScanDuration < TimeSpan.FromSeconds(0.5)) return null;
            return (job.Elapsed.TotalSeconds, LastScanDuration.TotalSeconds);
        }
    }
}

/// <summary>
/// Owns the per-drive cache and the scan queue.
/// <list type="bullet">
/// <item>Switching to a drive that has been scanned shows its cached tree; only Rescan scans again.</item>
/// <item>The drive being looked at scans in the foreground at full speed. Other drives scan one at a time in
/// the background, with Windows background priority, and pause while a foreground scan runs.</item>
/// <item>Rescan on the current drive jumps ahead of everything.</item>
/// </list>
/// Public methods are thread-safe. <see cref="Changed"/> can be raised on any thread, sometimes while an
/// internal lock is held: handlers must only post to the UI thread (BeginInvoke), never block on it.
/// </summary>
public sealed class ScanCoordinator
{
    private readonly Dictionary<string, DriveSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<DriveSession> _backgroundQueue = new();
    private readonly Func<DriveMeta, ScanOptions> _optionsFor;
    private readonly object _lock = new();

    /// <summary>The lock that guards tree changes (rescan of a folder, delete). Hold it briefly only.</summary>
    public object SyncRoot => _lock;

    private readonly Func<DriveMeta, DriveMeta>? _refresh;

    /// <param name="optionsFor">Scan options per drive (threads by drive type by default).</param>
    /// <param name="refreshMeta">
    /// Re-reads a drive's used and free space. Called when a scan starts and when it finishes, so progress
    /// and the "unscanned" slice use current numbers. Right after a clean-up the 15-second drive refresh is
    /// often stale, and the deleted amount would otherwise show up as unscanned space.
    /// </param>
    public ScanCoordinator(Func<DriveMeta, ScanOptions>? optionsFor = null, Func<DriveMeta, DriveMeta>? refreshMeta = null)
    {
        _optionsFor = optionsFor ?? (m => new ScanOptions { Threads = m.SuggestedThreads });
        _refresh = refreshMeta;
    }

    private void RefreshMeta(DriveSession s)
    {
        if (_refresh == null) return;
        try { s.Meta = _refresh(s.Meta); } catch { /* keep the old numbers */ }
    }

    /// <summary>Raised when a scan starts, finishes or is stopped.</summary>
    public event Action<DriveSession>? Changed;

    public IReadOnlyCollection<DriveSession> Sessions => _sessions.Values;
    public DriveSession? Foreground { get; private set; }

    public DriveSession Get(string root) => _sessions[root];

    /// <summary>Adds new drives and refreshes free space. Drives that disappeared are dropped unless scanning.</summary>
    public void UpdateDrives(IEnumerable<DriveMeta> drives)
    {
        lock (_lock)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in drives)
            {
                seen.Add(m.Root);
                if (_sessions.TryGetValue(m.Root, out var s)) s.Meta = m;
                else _sessions[m.Root] = new DriveSession(m);
            }
            foreach (var key in _sessions.Keys.Where(k => !seen.Contains(k)).ToList())
            {
                var s = _sessions[key];
                if (s.State != ScanState.Scanning) { _sessions.Remove(key); _backgroundQueue.Remove(s); }
            }
        }
    }

    /// <summary>
    /// Makes <paramref name="root"/> the drive being looked at. Scans it if it has never been scanned; if it is
    /// queued or scanning in the background, promotes it to the foreground. A cached tree is reused as is.
    /// </summary>
    public DriveSession Activate(string root)
    {
        lock (_lock)
        {
            var s = _sessions[root];
            var previous = Foreground;
            Foreground = s;
            if (previous != null && previous != s && previous.State == ScanState.Scanning && previous.Job != null)
                previous.Job.Background = true;

            switch (s.State)
            {
                case ScanState.NotScanned:
                case ScanState.Queued:
                    _backgroundQueue.Remove(s);
                    StartLocked(s, background: false);
                    break;
                case ScanState.Scanning:
                    s.Job!.Background = false;
                    s.Job.Resume();
                    break;
            }
            Pump();
            return s;
        }
    }

    /// <summary>Rescans a drive now, ahead of any background work. Restarts it if it is already scanning.</summary>
    public void Rescan(string root, IReadOnlyList<string>? priorityPath = null)
    {
        lock (_lock)
        {
            var s = _sessions[root];
            if (s.State == ScanState.Scanning) StopLocked(s, keepTree: false);
            _backgroundQueue.Remove(s);
            StartLocked(s, background: s != Foreground, priorityPath);
            Pump();
        }
    }

    /// <summary>Stops a running scan. The partial tree stays visible; a previous complete result is restored.</summary>
    public void Stop(string root)
    {
        lock (_lock)
        {
            var s = _sessions[root];
            if (s.State == ScanState.Scanning) StopLocked(s, keepTree: true);
            Pump();
        }
    }

    /// <summary>Queues every auto-scannable drive that has no result yet, in drive letter order.</summary>
    public void QueueAllInBackground()
    {
        lock (_lock)
        {
            foreach (var s in _sessions.Values.OrderBy(s => s.Meta.Root, StringComparer.OrdinalIgnoreCase))
            {
                if (s.State == ScanState.NotScanned && s.Meta.AutoScan && !_backgroundQueue.Contains(s))
                {
                    s.State = ScanState.Queued;
                    _backgroundQueue.Add(s);
                }
            }
            Pump();
        }
    }

    /// <summary>Rescans a single folder of a finished tree and splices the result in.</summary>
    public async Task RescanFolderAsync(string root, DirNode folder)
    {
        DriveSession s;
        lock (_lock) s = _sessions[root];
        if (folder.Parent == null) { Rescan(root); return; }
        var parent = folder.Parent;
        var opts = _optionsFor(s.Meta);
        var path = folder.FullPath;
        var fresh = await Task.Run(() => ScanJob.ScanNow(path, folder.Name, opts));
        lock (_lock)
        {
            if (s.Tree != null && IsInTree(parent, s.Tree) && parent.Children?.Contains(folder) == true)
                parent.ReplaceChild(folder, fresh);
        }
        Changed?.Invoke(s);
    }

    private static bool IsInTree(DirNode node, DirNode root)
    {
        DirNode? n = node;
        while (n.Parent != null) n = n.Parent;
        return n == root;
    }

    private void StartLocked(DriveSession s, bool background, IReadOnlyList<string>? priorityPath = null)
    {
        RefreshMeta(s);
        var root = new DirNode(s.Meta.Root, null);
        var options = _optionsFor(s.Meta);
        var hues = HueSlotsOf(s.Tree) ?? HueSlotsOf(s.LastCompleteTree);
        if (hues != null || priorityPath != null)
            options = new ScanOptions
            {
                Threads = options.Threads,
                MaxFilesPerFolder = options.MaxFilesPerFolder,
                Reader = options.Reader,
                HueSlots = hues ?? options.HueSlots,
                PriorityPath = priorityPath ?? options.PriorityPath,
            };
        var job = new ScanJob(root, s.Meta.Root, options) { Background = background };
        if (s.State == ScanState.Done) s.LastCompleteTree = s.Tree;
        s.Job = job;
        s.Tree = root;
        s.State = ScanState.Scanning;
        job.Completion.ContinueWith(_ => OnFinished(s, job), TaskScheduler.Default);
        job.Start();
        Changed?.Invoke(s);
    }

    private static Dictionary<string, int>? HueSlotsOf(DirNode? tree)
    {
        if (tree?.Children == null) return null;
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in tree.Children)
            if (c is DirNode { IsSynthetic: false, HueSlot: >= 0 } d) map[d.Name] = d.HueSlot;
        return map.Count > 0 ? map : null;
    }

    private void StopLocked(DriveSession s, bool keepTree)
    {
        var job = s.Job;
        if (job == null) return;
        s.Job = null;
        job.Cancel();
        if (keepTree && s.LastCompleteTree != null)
        {
            // Stopping a rescan brings back the previous full result rather than a half-built tree.
            s.Tree = s.LastCompleteTree;
            s.State = ScanState.Done;
        }
        else
        {
            s.State = ScanState.Stopped;
            if (!keepTree) s.Tree = null;
        }
        Changed?.Invoke(s);
    }

    private void OnFinished(DriveSession s, ScanJob job)
    {
        lock (_lock)
        {
            if (s.Job != job) return; // superseded by a newer scan or stopped
            s.Job = null;
            if (job.IsCancelled)
            {
                s.State = ScanState.Stopped;
            }
            else
            {
                RefreshMeta(s);
                AddUnseenSpace(s);
                s.ScannedAt = DateTime.Now;
                s.LastScanDuration = job.Elapsed;
                s.LastCompleteTree = null;
                s.State = ScanState.Done;
            }
            Pump();
        }
        Changed?.Invoke(s);
        // The tree this scan replaced is garbage now, and it is big (tens of MB per drive) but sits in gen 2,
        // which the GC rarely visits on its own. Ask for a background collection so the memory goes back
        // straight away instead of piling up over a few rescans. Non-blocking: the UI does not pause.
        if (s.State == ScanState.Done)
            GC.Collect(2, GCCollectionMode.Forced, blocking: false);
    }

    /// <summary>
    /// Space the drive reports as used that the scan could not see (folders without access, NTFS metadata,
    /// shadow copies, ...) is shown as one grey segment, so the chart adds up to the drive's used space.
    /// </summary>
    private static void AddUnseenSpace(DriveSession s)
    {
        var tree = s.Tree;
        if (tree?.Children == null) return;
        long unseen = s.Meta.UsedBytes - tree.Size;
        if (unseen < Math.Max(1L << 30, s.Meta.UsedBytes / 100)) return; // ignore under 1 GB or 1 %
        var node = new DirNode("Unscanned (system / no access)", tree) { IsSynthetic = true, Sorted = true };
        node._size = unseen;
        node.PublishChildren(Array.Empty<Node>());
        var copy = tree.Children.Append(node).ToArray();
        Array.Sort(copy, BySizeDesc.Instance);
        tree.PublishChildren(copy);
        Interlocked.Add(ref tree._size, unseen);
    }

    /// <summary>Starts the next background scan when nothing else runs; pauses background work during a foreground scan.</summary>
    private void Pump()
    {
        var running = _sessions.Values.Where(x => x.State == ScanState.Scanning && x.Job != null).ToList();
        bool foregroundBusy = running.Any(x => !x.Job!.Background);

        foreach (var r in running.Where(x => x.Job!.Background))
        {
            if (foregroundBusy) r.Job!.Pause();
            else r.Job!.Resume();
        }

        if (foregroundBusy || running.Count > 0) return;
        while (_backgroundQueue.Count > 0)
        {
            var next = _backgroundQueue[0];
            _backgroundQueue.RemoveAt(0);
            if (next.State != ScanState.Queued) continue;
            StartLocked(next, background: next != Foreground);
            return;
        }
    }

    internal IReadOnlyList<DriveSession> BackgroundQueue => _backgroundQueue;
}
