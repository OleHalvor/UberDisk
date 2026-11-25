using UberDisk.Core.Model;

namespace UberDisk.Core.Viz;

/// <summary>One drawn arc. Angles are radians, measured clockwise from 12 o'clock.</summary>
public readonly record struct Segment(Node Node, int Ring, double Start, double Sweep)
{
    public double End => Start + Sweep;
}

/// <summary>
/// The multi-level pie from OverDisk (TPieView): the current folder is the centre disc and each ring
/// outwards is one level deeper. Children are placed by size, largest first, starting at 12 o'clock and
/// going clockwise, folders and files mixed. Anything narrower than the visibility threshold is skipped
/// (OverDisk's default was 3 px), which also keeps huge trees cheap to draw.
/// </summary>
public sealed class SunburstLayout
{
    private readonly List<Segment>[] _rings;

    private SunburstLayout(DirNode center, int ringCount)
    {
        Center = center;
        _rings = new List<Segment>[ringCount + 1];
        for (int i = 0; i < _rings.Length; i++) _rings[i] = new List<Segment>();
    }

    public DirNode Center { get; }
    public int RingCount => _rings.Length - 1;

    /// <summary>Segments of ring <paramref name="ring"/> (1 = innermost ring), in angle order.</summary>
    public IReadOnlyList<Segment> Ring(int ring) => _rings[ring];

    public IEnumerable<Segment> All => _rings.Skip(1).SelectMany(r => r);
    public int Count => _rings.Sum(r => r.Count);

    /// <param name="center">Folder shown in the centre disc.</param>
    /// <param name="ringCount">How many levels below the centre to draw.</param>
    /// <param name="minSweep">Smallest sweep (radians) worth drawing on a given ring, usually threshold px / ring radius.</param>
    /// <param name="maxSegments">Hard cap, so a pathological tree can't stall a frame.</param>
    /// <param name="totalSweep">How much of the circle the centre's children fill (radians). Less than a full turn
    /// while a scan is still running, so the chart sweeps round as the scan progresses.</param>
    /// <param name="rankOf">Optional, used while a scan runs so the chart doesn't shuffle: a segment's place in an earlier
    /// layout (int.MaxValue if it had none, see <see cref="PathRanks"/>). Siblings keep that order unless one has
    /// clearly outgrown the one before it, and segments already shown stay until they shrink well below the threshold.</param>
    /// <param name="mustShow">Optional: nodes laid out even when below the size threshold (duplicates and the
    /// folders that lead to them), so they can be drawn and pointed at.</param>
    /// <param name="frozen">With <paramref name="rankOf"/>: ranked siblings never swap (a rescan keeps the last finished
    /// scan's order); only new ones find their place by size.</param>
    public static SunburstLayout Compute(DirNode center, int ringCount, Func<int, double> minSweep, int maxSegments = 60_000,
        double totalSweep = Math.Tau, Func<Node, int>? rankOf = null, bool frozen = false, Func<Node, bool>? mustShow = null)
    {
        var layout = new SunburstLayout(center, ringCount);
        var thresholds = new double[ringCount + 1];
        for (int r = 1; r <= ringCount; r++) thresholds[r] = minSweep(r);

        int budget = maxSegments;
        // Breadth-first, so if the budget runs out it's the outer rings that lose detail.
        var queue = new Queue<(DirNode dir, int ring, double start, double sweep)>();
        queue.Enqueue((center, 1, 0, Math.Clamp(totalSweep, 0, Math.Tau)));
        while (queue.Count > 0)
        {
            var (dir, ring, start, sweep) = queue.Dequeue();
            if (ring > ringCount) continue;
            var children = dir.Children;
            if (children == null || children.Length == 0) continue;
            // Snapshot sizes: while a scan runs they keep growing on other threads.
            var sizes = new long[children.Length];
            long total = 0;
            for (int i = 0; i < children.Length; i++) total += sizes[i] = children[i].Size;
            if (!dir.Sorted || rankOf != null)
            {
                children = (Node[])children.Clone();
                if (!dir.Sorted) Array.Sort(sizes, children, DescendingLong.Instance);
            }
            total = Math.Max(total, dir.Size);
            if (total <= 0) continue;

            // Only the biggest few can ever be big enough to draw; those are the ones kept in a stable order.
            int head = rankOf != null ? Math.Min(children.Length, StableHead) : 0;
            var ranks = head > 0 ? Stabilize(children, sizes, head, rankOf!, frozen) : null;

            double a = start;
            double min = thresholds[ring];
            for (int i = 0; i < children.Length; i++)
            {
                var c = children[i];
                double s = sweep * sizes[i] / total;
                // Hysteresis: once on screen, a segment stays until it is well under the threshold.
                double m = ranks != null && i < head && ranks[i] != int.MaxValue ? min * 0.5 : min;
                if (s < m && !(mustShow != null && mustShow(c)))
                {
                    if (i < head || mustShow != null) continue; // the stable head isn't strictly sorted
                    break; // sorted by size: everything after is smaller still
                }
                if (--budget < 0) return layout;
                layout._rings[ring].Add(new Segment(c, ring, a, s));
                if (c is DirNode d && ring < ringCount)
                    queue.Enqueue((d, ring + 1, a, s));
                a += s;
            }
        }
        return layout;
    }

    private const int StableHead = 64;
    private const double OvertakeFactor = 1.3;

    /// <summary>
    /// Reorders the first <paramref name="head"/> children: earlier order first (new ones after, biggest first),
    /// then a child moves ahead of its neighbour only when it is 30 % bigger (with <paramref name="frozen"/>, only
    /// new children move). Returns the ranks in the new order. Size order comes back when the scan ends.
    /// </summary>
    private static int[] Stabilize(Node[] children, long[] sizes, int head, Func<Node, int> rankOf, bool frozen)
    {
        var idx = new int[head];
        var rank = new int[head];
        for (int i = 0; i < head; i++)
        {
            idx[i] = i;
            rank[i] = rankOf(children[i]);
        }
        // Stable sort by rank (ties keep size order).
        Array.Sort(idx, (x, y) => rank[x] != rank[y] ? rank[x].CompareTo(rank[y]) : x.CompareTo(y));
        var c2 = new Node[head];
        var s2 = new long[head];
        var r2 = new int[head];
        for (int i = 0; i < head; i++) { c2[i] = children[idx[i]]; s2[i] = sizes[idx[i]]; r2[i] = rank[idx[i]]; }
        for (int i = 1; i < head; i++)
        {
            if (frozen && r2[i] != int.MaxValue) continue;
            for (int j = i; j > 0 && s2[j] > s2[j - 1] * OvertakeFactor; j--)
            {
                (s2[j], s2[j - 1]) = (s2[j - 1], s2[j]);
                (c2[j], c2[j - 1]) = (c2[j - 1], c2[j]);
                (r2[j], r2[j - 1]) = (r2[j - 1], r2[j]);
            }
        }
        Array.Copy(c2, children, head);
        Array.Copy(s2, sizes, head);
        return r2;
    }

    /// <summary>Order of every segment in this layout by full path, for <see cref="Compute"/>'s rankOf.</summary>
    public Dictionary<string, int> PathRanks()
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int n = 0;
        foreach (var s in All) map[s.Node.FullPath] = n++;
        return map;
    }

    /// <summary>Finds the segment on <paramref name="ring"/> that contains <paramref name="angle"/> (radians clockwise from 12 o'clock).</summary>
    public Segment? HitTest(int ring, double angle)
    {
        if (ring < 1 || ring > RingCount) return null;
        var list = _rings[ring];
        int lo = 0, hi = list.Count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            var s = list[mid];
            if (angle < s.Start) hi = mid - 1;
            else if (angle >= s.End) lo = mid + 1;
            else return s;
        }
        return null;
    }

    /// <summary>Converts a point relative to the chart centre (y down, as on screen) to radians clockwise from 12 o'clock.</summary>
    public static double AngleOf(double dx, double dy)
    {
        double a = Math.Atan2(dx, -dy);
        return a < 0 ? a + Math.Tau : a;
    }
}

internal sealed class DescendingLong : IComparer<long>
{
    public static readonly DescendingLong Instance = new();
    public int Compare(long x, long y) => y.CompareTo(x);
}
