using SkiaSharp;
using UberDisk.Core.Duplicates;
using UberDisk.Core.Model;
using UberDisk.Core.Viz;

namespace UberDisk.App;

/// <summary>Text for the centre disc. Top is the small blue "Up to ..." line when zoomed in.</summary>
public sealed record CenterText(string? Top, string Title, string Line2, string Line3);

/// <summary>
/// Draws the sunburst with SkiaSharp. The centre disc is the current folder; each ring outwards is one
/// level deeper. Works in DIPs. Recomputes the layout only when the tree, the zoom or the size changes
/// (or when told to, on each refresh tick while a scan fills the tree in). No WPF here, so it can also
/// render previews and tests headless.
/// </summary>
public sealed class SunburstPainter
{
    // Geometry, from OverDisk's defaults: centre disc about a third of the radius, 3 px visibility threshold.
    private const double CenterFraction = 0.30;
    private const int Rings = 7;
    private const double ThresholdPx = 3;
    private const double ChartMargin = 24;

    private static readonly SKColor Background = new(0x16, 0x18, 0x1D);
    private static readonly SKColor GuideColor = new(0x22, 0x25, 0x2C);
    private static readonly SKColor DiscColor = new(0x23, 0x26, 0x2D);
    private static readonly SKColor DiscHover = new(0x2A, 0x2E, 0x37);
    private static readonly SKColor DiscRim = new(0x14, 0x16, 0x1A);
    private static readonly SKColor TextColor = new(0xF2, 0xF3, 0xF5);
    private static readonly SKColor TextDim = new(0xB4, 0xB9, 0xC1);
    private static readonly SKColor TextFaint = new(0x8A, 0x90, 0x99);
    private static readonly SKColor DarkInk = new(0x1A, 0x1C, 0x21);
    private static readonly SKColor LinkBlue = new(0x6C, 0xA8, 0xFF);

    private static readonly SKTypeface UiFace = SKTypeface.FromFamilyName("Segoe UI") ?? SKTypeface.Default;
    private static readonly SKTypeface UiBold = SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold) ?? SKTypeface.Default;
    private static readonly SKTypeface UiSemi = SKTypeface.FromFamilyName("Segoe UI", SKFontStyleWeight.SemiBold, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright) ?? SKTypeface.Default;

    private DirNode? _root;
    private DirNode? _center;
    private SunburstLayout? _layout;
    private bool _layoutDirty = true;
    private (double, double) _layoutSize;

    // Geometry of the last paint, in DIPs, for hit testing.
    private double _cx, _cy, _r0, _rw, _rOuter;

    public Segment? Hover { get; set; }
    public bool HoverCenter { get; set; }
    public CenterText? CenterText { get; set; }
    /// <summary>True while the shown tree is still filling in. Hides labels that would jump around.</summary>
    public bool Live { get; set; }

    /// <summary>Share of the circle (0..1) the chart fills. Below 1 while a scan runs: the rings sweep round
    /// clockwise as the scan progresses, then close the circle when it finishes.</summary>
    public double Fill { get; set; } = 1;

    private double _free;
    /// <summary>
    /// Share of the drive that is free (0..1). Above 0, the drive view leaves that share of the circle empty,
    /// so every slice is drawn against the whole disk instead of the used space.
    /// </summary>
    public double FreeFraction
    {
        get => _free;
        set { value = Math.Clamp(value, 0, 0.98); if (Math.Abs(value - _free) > 1e-9) { _free = value; _layoutDirty = true; } }
    }

    private bool FreeShown => _free > 0 && _center != null && _center == _root;

    /// <summary>
    /// Smooth: segments move on critically damped springs (no sudden speed changes when new scan data
    /// arrives), siblings keep their order while a scan runs, and segments don't blink in and out at the size
    /// threshold. Everything settles into the final sorted layout after the scan. Off: quick, snappier easing.
    /// </summary>
    public bool Smooth => Style != ScanAnimation.Quick;

    // ---------- Duplicates ----------

    private DuplicateResult? _dups;
    private bool _dupMode;
    private object? _dupResolvedFor;
    private readonly Dictionary<Node, DuplicateGroup> _dupFiles = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<Node> _dupPaths = new(ReferenceEqualityComparer.Instance); // files and the folders leading to them
    // Every copy of each group found in the tree (a file, or the bundle of other files it is hidden in).
    private readonly Dictionary<DuplicateGroup, List<Node>> _groupNodes = new();
    // Folders holding duplicates, with their groups: a folder on the outermost ring stands in for copies deeper than the chart.
    private readonly Dictionary<Node, List<DuplicateGroup>> _dupBelow = new(ReferenceEqualityComparer.Instance);
    private readonly List<(Node node, float r1, float r2, double start, double sweep, DuplicateGroup group)> _dupDrawn = new();

    /// <summary>Duplicates found on this drive (matched to the shown tree by path).</summary>
    public DuplicateResult? Duplicates
    {
        get => _dups;
        set { if (value != _dups) { _dups = value; _dupResolvedFor = null; _layoutDirty = true; } }
    }

    /// <summary>On: everything but duplicates fades, duplicates glow in their group's colour.</summary>
    public bool DuplicateMode
    {
        get => _dupMode;
        set { if (value != _dupMode) { _dupMode = value; _layoutDirty = true; } }
    }

    /// <summary>Group picked in the side panel: its copies stand out, the other groups dim.</summary>
    public DuplicateGroup? SelectedGroup { get; set; }

    /// <summary>The duplicate group a shown node belongs to, if any.</summary>
    public DuplicateGroup? GroupOf(Node n) => _dupFiles.GetValueOrDefault(n);

    /// <summary>Distinct, bright colours for duplicate groups, by group index.</summary>
    public static SKColor GroupColor(int index) => GroupColors[index % GroupColors.Length];
    private static readonly SKColor[] GroupColors =
    {
        new(0x4D, 0xD9, 0xF0), new(0xB8, 0xF2, 0x4D), new(0xF2, 0x5C, 0xC2), new(0xF2, 0xA2, 0x3D),
        new(0x9B, 0x7B, 0xF2), new(0xF2, 0xE2, 0x4D), new(0x3D, 0xF2, 0xA6), new(0xF2, 0x6B, 0x5C),
        new(0x5C, 0x9D, 0xF2), new(0xE8, 0xA6, 0xF2), new(0x8F, 0xE0, 0xD0), new(0xD9, 0xC2, 0x9A),
    };

    private void ResolveDuplicates()
    {
        var key = (_root, _dups);
        if (Equals(_dupResolvedFor, key)) return;
        _dupResolvedFor = key;
        _dupFiles.Clear();
        _dupPaths.Clear();
        _dupBelow.Clear();
        _groupNodes.Clear();
        if (_root == null || _dups == null) return;
        string rootPath = _root.FullPath;
        foreach (var g in _dups.Groups)
            foreach (var path in g.Paths)
            {
                if (!path.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase)) continue;
                var parts = path[rootPath.Length..].Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
                Node? n = _root;
                var chain = new List<Node>();
                for (int i = 0; i < parts.Length; i++)
                {
                    if (n is not DirNode d || d.Children == null) { n = null; break; }
                    n = null;
                    foreach (var ch in d.Children)
                        if (string.Equals(ch.Name, parts[i], StringComparison.OrdinalIgnoreCase)) { n = ch; break; }
                    // A copy in a crowded folder isn't in the tree on its own: the folder's bundle of other files stands in.
                    if (n == null && i == parts.Length - 1)
                    {
                        var bundle = d.Children.FirstOrDefault(ch => ch is FileNode { IsAggregate: true });
                        if (bundle != null) { chain.Add(bundle); n = bundle; }
                        break;
                    }
                    if (n == null) break;
                    chain.Add(n);
                }
                if (n != null)
                {
                    if (!_groupNodes.TryGetValue(g, out var gn)) _groupNodes[g] = gn = new();
                    gn.Add(n);
                }
                if (n is FileNode { IsAggregate: true } agg)
                {
                    foreach (var x in chain)
                    {
                        _dupPaths.Add(x);
                        if (!_dupBelow.TryGetValue(x, out var gs)) _dupBelow[x] = gs = new();
                        if (!gs.Contains(g)) gs.Add(g);
                    }
                }
                else if (n is FileNode f)
                {
                    _dupFiles[f] = g;
                    foreach (var x in chain)
                    {
                        _dupPaths.Add(x);
                        if (x == f) continue;
                        if (!_dupBelow.TryGetValue(x, out var gs)) _dupBelow[x] = gs = new();
                        if (!gs.Contains(g)) gs.Add(g);
                    }
                }
            }
    }

    /// <summary>How a scan of the whole drive is shown. See <see cref="ScanAnimation"/>.</summary>
    public ScanAnimation Style { get; set; } = ScanAnimation.Sweep;

    /// <summary>How segments are coloured. See <see cref="ChartColors"/>.</summary>
    public ChartColors Colors { get; set; } = ChartColors.Size;

    /// <summary>Ring thickness: even, or OverDisk's equal-area rings that get thinner further out.</summary>
    public RingStyle RingStyle { get; set; } = RingStyle.Even;

    // Size colouring: the biggest child of each folder, cached for one frame.
    private readonly Dictionary<DirNode, long> _maxChild = new(ReferenceEqualityComparer.Instance);

    private Argb ColorOf(Node node, int absDepth, int ring)
    {
        if (Colors == ChartColors.Folders || node is DirNode { IsSynthetic: true }) return Palette.For(node, absDepth);
        double t;
        if (Colors == ChartColors.View)
        {
            long total = _center?.Size ?? 0;
            // Logarithmic: the whole view is red, 10 % of it two thirds of the way, 0.1 % and below yellow.
            t = total > 0 && node.Size > 0 ? 1 + Math.Log10((double)node.Size / total) / 3 : 0;
        }
        else
        {
            long max = 0;
            if (node.Parent is { } parent && !_maxChild.TryGetValue(parent, out max))
            {
                foreach (var c in parent.Children ?? Array.Empty<Node>()) max = Math.Max(max, c.Size);
                _maxChild[parent] = max;
            }
            t = max > 0 ? (double)node.Size / max : 1;
        }
        return Palette.BySize(t, ring, Rings);
    }

    // Reveal progress (0..1) actually shown, on its own spring, so a jump in the target (a rescan starting:
    // full circle to nothing) becomes a calm wipe back, and the sweep never stutters on scan updates.
    private double _reveal = 1, _revealV;
    private bool _snapReveal;
    private readonly double[] _ringAngle = new double[Rings + 1];
    private readonly double[] _ringDepth = new double[Rings + 1];

    // Smooth mode: segment order by path. Live = from the previous frame of a running scan. Final = from the last
    // finished layout of this drive; a rescan keeps that order frozen, so nothing swaps places while it fills in.
    private Dictionary<string, int>? _liveRanks, _finalRanks;
    private bool _wasLive;

    // ---------- Order kept between runs ----------

    private const int MaxSavedOrder = 50_000;

    private static string OrderFile(DirNode root)
    {
        var name = new string(root.Name.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray()).Trim('_');
        return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "UberDisk", "order", (name.Length == 0 ? "root" : name) + ".txt");
    }

    /// <summary>Segment order (full paths, first = rank 0) of the last finished whole-drive layout, or null.</summary>
    private static Dictionary<string, int>? LoadOrder(DirNode? root)
    {
        if (root == null || Demo.On) return null;
        try
        {
            var file = OrderFile(root);
            if (!System.IO.File.Exists(file)) return null;
            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            int n = 0;
            foreach (var line in System.IO.File.ReadLines(file))
            {
                if (n >= MaxSavedOrder) break;
                if (line.Length > 0) map.TryAdd(line, n++);
            }
            return map.Count > 0 ? map : null;
        }
        catch { return null; } // no saved order just means the first scan settles by itself
    }

    private static void SaveOrder(DirNode root, Dictionary<string, int> ranks)
    {
        if (Demo.On) return;
        var file = OrderFile(root);
        var lines = ranks.OrderBy(kv => kv.Value).Take(MaxSavedOrder).Select(kv => kv.Key).ToList();
        Task.Run(() =>
        {
            try
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
                var tmp = file + ".tmp";
                System.IO.File.WriteAllLines(tmp, lines);
                System.IO.File.Move(tmp, file, overwrite: true);
            }
            catch { /* only a nicety */ }
        });
    }
    public DirNode? CenterNode => _center;


    public DirNode? Root => _root;

    /// <summary>Sets the tree and the zoomed folder. Returns true when that changed (hover is cleared then).</summary>
    public bool Show(DirNode? root, DirNode? center)
    {
        bool changed = root != _root || center != _center;
        if (changed)
        {
            _root = root;
            _center = center;
            Hover = null;
            HoverCenter = false;
        }
        _layoutDirty = true;
        return changed;
    }

    /// <summary>Makes the next paint re-read the (growing) tree.</summary>
    public void Invalidate() => _layoutDirty = true;

    /// <summary>Paints at <paramref name="w"/> x <paramref name="h"/> DIPs. The canvas must already be scaled to DIPs.</summary>
    public void Paint(SKCanvas canvas, double w, double h)
    {
        canvas.Clear(Background);
        if (w <= 0 || h <= 0) return;
        double radius = Math.Max(60, Math.Min(w, h) / 2 - ChartMargin);
        _cx = w / 2;
        _cy = h / 2;
        _r0 = radius * CenterFraction;
        _rw = (radius - _r0) / Rings;
        _rOuter = radius;

        if (_layoutDirty || _layoutSize != (w, h))
        {
            if ((_root?.Name ?? "") != _rootKey)
            {
                // Another drive (or start-up): begin with the order its last finished scan ended in, so a fresh
                // scan fills in already in place instead of swapping folders round when it finishes.
                _finalRanks = LoadOrder(_root);
                _liveRanks = null;
            }
            if (_dupMode) _dupResolvedFor = null; // the tree may have filled in since
            ResolveDuplicates();
            // Quick squeezes the live chart into the scanned share of the circle. The smooth styles keep the
            // full-circle geometry and reveal it instead (see UpdateReveal), so nothing has to move.
            _layout = _center == null ? null : SunburstLayout.Compute(_center, Rings, ring => ThresholdPx / RingInner(ring + 0.5),
                totalSweep: (Smooth ? Math.Tau : Math.Tau * Math.Clamp(Fill, 0.02, 1)) * (FreeShown ? 1 - _free : 1),
                mustShow: DupActive ? n => _dupPaths.Contains(n) : null,
                rankOf: Smooth && Live ? RankFunc() : null,
                frozen: Smooth && Live && _finalRanks != null);
            if (_layout != null && Live) _liveRanks = Smooth && _finalRanks == null ? _layout.PathRanks() : null;
            else if (_layout != null)
            {
                // Merged, so zooming around keeps what is known about the rest of the drive.
                _finalRanks ??= new(StringComparer.OrdinalIgnoreCase);
                var ranks = _layout.PathRanks();
                foreach (var (k, v) in ranks) _finalRanks[k] = v;
                _liveRanks = null;
                if (_wasLive && _center == _root && _root != null) SaveOrder(_root, ranks);
            }
            _wasLive = Live && _layout != null;
            _layoutSize = (w, h);
            _layoutDirty = false;
            if (Hover is { } hv) Hover = FindSegment(hv.Node);
            SyncAnims();
        }
        if (!AnimationsEnabled) SnapAnims();
        StepAnims();
        UpdateReveal();

        DrawGuides(canvas, radius);
        DrawSegments(canvas);
        DrawCenter(canvas);
    }

    /// <summary>
    /// Per ring: how far round (radians) and how deep (0..1 of the ring's width) it is revealed.
    /// <list type="bullet">
    /// <item>Sweep: one clockwise sweep, with rings growing outwards as it goes round.</item>
    /// <item>Spiral: the inner ring leads and each ring further out follows a little behind.</item>
    /// <item>Bloom: the whole circle at once, rings opening outwards one after another.</item>
    /// </list>
    /// </summary>
    private void UpdateReveal()
    {
        double p = Math.Clamp(_reveal, 0, 1);
        for (int r = 1; r <= Rings; r++)
        {
            double angle = 1, depth = 1;
            switch (Style)
            {
                case ScanAnimation.Sweep:
                    angle = p;
                    depth = Math.Clamp(1 + p * Rings - (r - 1), 0, 1);
                    break;
                case ScanAnimation.Spiral:
                    const double lag = 0.6; // the outer ring starts when the inner one is 60 % round
                    angle = Math.Clamp(p * (1 + lag) - lag * (r - 1) / (Rings - 1), 0, 1);
                    depth = Math.Clamp(angle * 8, 0, 1); // grows to full width over the first eighth
                    break;
                case ScanAnimation.Bloom:
                    double d = p * (Rings + 0.5) + 0.5 - (r - 1);
                    depth = Math.Clamp(d, 0, 1);
                    depth = depth * depth * (3 - 2 * depth); // smoothstep: each ring eases open
                    break;
            }
            _ringAngle[r] = angle * Math.Tau;
            _ringDepth[r] = depth;
        }
    }

    private void DrawGuides(SKCanvas c, double radius)
    {
        using var p = new SKPaint { Color = GuideColor, IsStroke = true, StrokeWidth = 1, IsAntialias = true };
        for (int i = 1; i <= Rings; i++)
            c.DrawCircle((float)_cx, (float)_cy, (float)RingInner(i + 1), p);
    }

    private void DrawSegments(SKCanvas c)
    {
        using var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
        using var gap = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1f, Color = Background };
        var hover = Hover;
        Anim? hoverAnim = null;
        bool dupActive = DupActive;
        // The hovered segment's group: its own, or the one it stood in for last frame (an outer-ring folder).
        var hoverGroup = dupActive && hover != null
            ? HoveredGroup(hover.Value.Node) : null;
        _drawnGroup.Clear();
        // Duplicates are widened to stay visible; lay the widened ones out per ring in angle order, so neighbours
        // sit side by side instead of on top of each other.
        var widened = dupActive ? WidenDuplicates(hoverGroup) : null;
        using var glow = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 5) };
        var dupDrawn = new List<(Node node, float r1, float r2, double start, double sweep, DuplicateGroup group)>();
        var dupColors = new List<SKColor>();
        _maxChild.Clear();

        // Outgoing segments first, so the ones growing in are drawn on top of them. When siblings trade places,
        // the ones moving back (towards 12 o'clock) are drawn last, so a folder overtaking its neighbour slides
        // over it as one piece, with its own subfolders, instead of the two subtrees interleaving.
        foreach (var an in _anims.Values.OrderBy(a => a.Alive).ThenBy(a => a.VStart < -0.05 ? 1 : 0))
        {
            if (an.Sweep < 1e-5 || an.Grow < 0.01) continue;
            // Rings outside the chart (while zooming in) or inside the centre disc (zooming out) aren't drawn.
            if (an.Ring < 0.6 || an.Ring > Rings + 0.4) continue;
            int ri = Math.Clamp((int)Math.Round(an.Ring), 1, Rings);
            double clip = _ringAngle[ri];
            if (an.Start >= clip - 1e-6) continue;
            double sweep = Math.Min(an.Sweep, clip - an.Start);
            float r1 = (float)(RingInner(an.Ring) + 1);
            float r2 = r1 + (float)((RingWidth(an.Ring) - 1) * (an.Fade ? 1 : an.Grow) * _ringDepth[ri]);
            if (r1 < _r0) r1 = (float)_r0;
            if (r2 <= r1 + 0.3f) continue;

            var argb = ColorOf(an.Node, an.AbsDepth, ri);
            var color = new SKColor(argb.R, argb.G, argb.B);
            double start = an.Start;
            DuplicateGroup? dup = null;
            if (dupActive)
            {
                if ((dup = DupGroupAt(an.Node, ri, hoverGroup)) != null)
                {
                    _drawnGroup[an.Node] = dup;
                    color = GroupColor(dup.Index);
                    if (SelectedGroup != null && SelectedGroup != dup && hoverGroup != dup) color = Blend(color, Background, 0.55f);
                    if (widened!.TryGetValue(an, out var w)) (start, sweep) = w;
                }
                else color = Blend(color, Background, _dupPaths.Contains(an.Node) ? 0.7f : 0.84f);
                if (hover != null && an.Node == hover.Value.Node) hoverAnim = an;
            }
            else if (hover != null && !InBranch(an.Node, hover.Value.Node))
                color = Blend(color, Background, 0.62f);
            else if (hover != null && an.Node == hover.Value.Node)
            {
                color = Blend(color, SKColors.White, 0.12f);
                hoverAnim = an;
            }
            // Segments on their way out fade as they shrink.
            if (!an.Alive || an.Fade) color = color.WithAlpha((byte)(255 * Math.Clamp(an.Grow, 0, 1)));
            if (dup != null)
            {
                // Drawn after everything else, so neighbouring segments never cover a widened copy.
                // A stand-in for copies of several groups: one stripe per group.
                // Stand-ins only light up a band at their outer edge, so a big folder or bundle holding one copy
                // doesn't look like a big duplicate.
                if (!_dupFiles.ContainsKey(an.Node))
                {
                    var baseColor = Blend(new SKColor(argb.R, argb.G, argb.B), Background, 0.7f);
                    if (!an.Alive) baseColor = baseColor.WithAlpha((byte)(255 * Math.Clamp(an.Grow, 0, 1)));
                    using var basePath = Sector((float)_cx, (float)_cy, r1, r2, an.Start, Math.Min(an.Sweep, clip - an.Start));
                    fill.Color = baseColor;
                    c.DrawPath(basePath, fill);
                    if (an.Alive && an.Grow > 0.9) c.DrawPath(basePath, gap);
                    r1 = Math.Max(r1, r2 - Math.Max(5f, (r2 - r1) * 0.35f));
                }
                var groups = !_dupFiles.ContainsKey(an.Node) && _dupBelow.TryGetValue(an.Node, out var gl) && gl.Count > 1
                    ? gl.OrderByDescending(x => x.Wasted).ToList() : null;
                if (groups == null)
                {
                    dupDrawn.Add((an.Node, r1, r2, start, sweep, dup));
                    dupColors.Add(color);
                }
                else
                {
                    double part = sweep / groups.Count;
                    for (int k = 0; k < groups.Count; k++)
                    {
                        var gk = groups[k];
                        var ck = GroupColor(gk.Index);
                        if (SelectedGroup != null && SelectedGroup != gk && hoverGroup != gk) ck = Blend(ck, Background, 0.55f);
                        if (!an.Alive) ck = ck.WithAlpha((byte)(255 * Math.Clamp(an.Grow, 0, 1)));
                        dupDrawn.Add((an.Node, r1, r2, start + k * part, part, gk));
                        dupColors.Add(ck);
                    }
                }
                continue;
            }
            using var path = Sector((float)_cx, (float)_cy, r1, r2, start, sweep);
            fill.Color = color;
            c.DrawPath(path, fill);
            if (an.Alive && an.Grow > 0.9 && sweep * r1 > 4) c.DrawPath(path, gap);
        }
        for (int i = 0; i < dupDrawn.Count; i++)
        {
            var d = dupDrawn[i];
            using var path = Sector((float)_cx, (float)_cy, d.r1, d.r2, d.start, d.sweep);
            // Glow: a blurred copy underneath (not for stripes, where it would tint the neighbouring stripe).
            bool striped = (i > 0 && dupDrawn[i - 1].node == d.node) || (i + 1 < dupDrawn.Count && dupDrawn[i + 1].node == d.node);
            if (!striped)
            {
                glow.Color = dupColors[i].WithAlpha((byte)(dupColors[i].Alpha * 0.8));
                c.DrawPath(path, glow);
            }
            fill.Color = dupColors[i];
            c.DrawPath(path, fill);
        }

        DrawFree(c);
        DrawSweepEdge(c);
        _dupDrawn.Clear();
        _dupDrawn.AddRange(dupDrawn);
        if (dupActive) DrawDuplicateMarks(c, hoverAnim?.Node);

        if (hoverAnim is { } h && !dupActive)
        {
            int ri = Math.Clamp((int)Math.Round(h.Ring), 1, Rings);
            float r1 = (float)(RingInner(h.Ring) + 1);
            float r2 = r1 + (float)((RingWidth(h.Ring) - 1) * h.Grow * _ringDepth[ri]);
            double sweep = Math.Min(h.Sweep, _ringAngle[ri] - h.Start);
            if (sweep <= 0 || r2 <= r1) return;
            using var outline = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2.5f, Color = SKColors.White };
            using var path = Sector((float)_cx, (float)_cy, r1, r2, h.Start, sweep);
            c.DrawPath(path, outline);
            if (_dups != null && _dupFiles.TryGetValue(h.Node, out var g)) DrawHoverTwins(c, h, r1, r2, sweep, g);
        }
    }

    private bool DupActive => _dupMode && _dups != null;

    /// <summary>
    /// Normal view, hovering a file that has identical copies: outline each copy (or, if it is too small to draw,
    /// the nearest folder on screen that holds it) and draw a dashed line to it.
    /// </summary>
    private void DrawHoverTwins(SKCanvas c, Anim h, float hr1, float hr2, double hsweep, DuplicateGroup g)
    {
        if (!_groupNodes.TryGetValue(g, out var copies)) return;
        var byNode = new Dictionary<Node, Anim>(ReferenceEqualityComparer.Instance);
        foreach (var an in _anims.Values) if (an.Alive && an.Grow > 0.5) byNode[an.Node] = an;
        double ha = h.Start + hsweep / 2, hr = (hr1 + hr2) / 2;
        var from = new SKPoint((float)(_cx + Math.Sin(ha) * hr), (float)(_cy - Math.Cos(ha) * hr));
        using var outline = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2f, Color = SKColors.White.WithAlpha(230) };
        using var dash = new SKPaint
        {
            IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.4f, Color = SKColors.White.WithAlpha(210),
            PathEffect = SKPathEffect.CreateDash(new[] { 5f, 4f }, 0),
        };
        var done = new HashSet<Anim>(ReferenceEqualityComparer.Instance) { h };
        foreach (var copy in copies)
        {
            Anim? target = null;
            for (Node? n = copy; n != null && target == null; n = n.Parent) byNode.TryGetValue(n, out target);
            if (target == null || !done.Add(target)) continue;
            int ri = Math.Clamp((int)Math.Round(target.Ring), 1, Rings);
            float r1 = (float)(RingInner(target.Ring) + 1);
            float r2 = r1 + (float)((RingWidth(target.Ring) - 1) * target.Grow * _ringDepth[ri]);
            double sweep = Math.Min(target.Sweep, _ringAngle[ri] - target.Start);
            if (sweep <= 0 || r2 <= r1) continue;
            using var path = Sector((float)_cx, (float)_cy, r1, r2, target.Start, Math.Max(sweep, 3.0 / ((r1 + r2) / 2)));
            c.DrawPath(path, outline);
            double a = target.Start + sweep / 2, r = (r1 + r2) / 2;
            c.DrawLine(from, new SKPoint((float)(_cx + Math.Sin(a) * r), (float)(_cy - Math.Cos(a) * r)), dash);
        }
    }

    /// <summary>Where the free-space arc starts: just after the drive's first ring, following its animation.</summary>
    private double FreeStart()
    {
        double end = 0;
        foreach (var an in _anims.Values)
            if (an.Alive && Math.Abs(an.Ring - 1) < 0.5) end = Math.Max(end, an.Start + an.Sweep);
        return end;
    }

    /// <summary>The drive's free space: a faint arc with a dashed edge across the first ring.</summary>
    private void DrawFree(SKCanvas c)
    {
        if (!FreeShown) return;
        double start = FreeStart(), end = Math.Min(Math.Tau, _ringAngle[1]);
        if (end - start < 0.002) return;
        float r1 = (float)(RingInner(1) + 1), r2 = (float)(RingInner(1) + 1 + (RingWidth(1) - 1) * _ringDepth[1]);
        if (r2 <= r1 + 0.3f) return;
        using var path = Sector((float)_cx, (float)_cy, r1, r2, start, end - start);
        using var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = HoverFree ? new SKColor(0x30, 0x34, 0x3D) : new SKColor(0x24, 0x27, 0x2E) };
        c.DrawPath(path, fill);
        using var edge = new SKPaint
        {
            IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, Color = new SKColor(0x7A, 0x80, 0x8A),
            PathEffect = SKPathEffect.CreateDash(new[] { 4f, 4f }, 0),
        };
        c.DrawPath(path, edge);
    }

    /// <summary>True while the mouse is over the free-space arc.</summary>
    public bool HoverFree { get; set; }

    /// <summary>Whether a point is on the free-space arc.</summary>
    public bool HitFree(double x, double y)
    {
        if (!FreeShown) return false;
        double dx = x - _cx, dy = y - _cy, d = Math.Sqrt(dx * dx + dy * dy);
        if (d < RingInner(1) || d > RingInner(2)) return false;
        double a = SunburstLayout.AngleOf(dx, dy);
        return a >= FreeStart() && a <= _ringAngle[1];
    }

    /// <summary>Where the hovered copy's twins are on average (last painted frame), so the hover card can keep clear of the lines.</summary>
    public SKPoint? TwinCentroid(Node hovered)
    {
        if (!DupActive || HoveredGroup(hovered) is not { } g) return null;
        float x = 0, y = 0; int n = 0;
        foreach (var d in _dupDrawn)
        {
            if (d.node == hovered || d.group != g) continue;
            double a = d.start + d.sweep / 2, r = (d.r1 + d.r2) / 2;
            x += (float)(_cx + Math.Sin(a) * r); y += (float)(_cy - Math.Cos(a) * r); n++;
        }
        return n == 0 ? null : new SKPoint(x / n, y / n);
    }

    private readonly Dictionary<Node, DuplicateGroup> _drawnGroup = new(ReferenceEqualityComparer.Instance);
    private (Node node, DuplicateGroup group)? _hitGroup; // the stripe last pointed at, for folders that show several groups

    /// <summary>The group a hovered segment shows: its own, the stripe under the mouse, or what it stood in for last frame.</summary>
    public DuplicateGroup? HoveredGroup(Node n) =>
        _dupFiles.GetValueOrDefault(n)
        ?? (_hitGroup is { } h && h.node == n ? h.group : null)
        ?? _drawnGroup.GetValueOrDefault(n);

    /// <summary>
    /// The duplicate group a segment shows: its own for a duplicate file, or, for a folder on the outermost ring,
    /// that of copies inside it that are too deep to draw (the selected or hovered group first, then the biggest).
    /// </summary>
    private DuplicateGroup? DupGroupAt(Node n, int ring, DuplicateGroup? hoverGroup)
    {
        if (_dupFiles.TryGetValue(n, out var g)) return g;
        bool bundle = n is FileNode { IsAggregate: true };
        if ((ring < Rings && !bundle) || !_dupBelow.TryGetValue(n, out var gs)) return null;
        if (SelectedGroup != null && gs.Contains(SelectedGroup)) return SelectedGroup;
        if (hoverGroup != null && gs.Contains(hoverGroup)) return hoverGroup;
        return gs.MaxBy(x => x.Wasted);
    }

    /// <summary>Start and sweep for each duplicate drawn wider than its true size (at least 4 px), without overlaps.</summary>
    private Dictionary<Anim, (double start, double sweep)> WidenDuplicates(DuplicateGroup? hoverGroup)
    {
        var result = new Dictionary<Anim, (double, double)>();
        var perRing = new Dictionary<int, List<Anim>>();
        foreach (var an in _anims.Values)
        {
            if (!an.Alive || an.Sweep < 1e-5 || an.Ring < 0.6 || an.Ring > Rings + 0.4) continue;
            int ri = Math.Clamp((int)Math.Round(an.Ring), 1, Rings);
            if (DupGroupAt(an.Node, ri, hoverGroup) == null) continue;
            // Stand-ins (a folder or bundle holding copies) that are wide enough keep their place and take no room.
            if (!_dupFiles.ContainsKey(an.Node) && an.Sweep * (RingInner(ri) + RingWidth(ri) / 2) >= 4) continue;
            if (!perRing.TryGetValue(ri, out var list)) perRing[ri] = list = new();
            list.Add(an);
        }
        var byNode = new Dictionary<Node, Anim>(ReferenceEqualityComparer.Instance);
        foreach (var an in _anims.Values) if (an.Alive) byNode[an.Node] = an;
        foreach (var (ri, list) in perRing)
        {
            double mid = RingInner(ri) + RingWidth(ri) / 2;
            double min = 4.0 / mid, gap = 1.5 / mid;
            // Copies stay inside their folder's arc: when many small copies share a folder, they get narrower
            // (down to 1.5 px) rather than spill over the folder next to it.
            foreach (var siblings in list.GroupBy(a => (object?)a.Node.Parent ?? a))
            {
                var run = siblings.OrderBy(a => a.Start).ToList();
                double lo = 0, hi = Math.Tau;
                if (siblings.Key is Node parent && byNode.TryGetValue(parent, out var pa)) { lo = pa.Start; hi = pa.Start + pa.Sweep; }
                // Widen what is too thin to see, centred on its true place; push apart what then overlaps.
                double prevEnd = double.NegativeInfinity;
                foreach (var an in run)
                {
                    double start = an.Start, sweep = an.Sweep;
                    bool widen = sweep < min;
                    if (widen) { start += sweep / 2 - min / 2; sweep = min; }
                    double space = widen ? gap : 0;
                    if (start < prevEnd + space) start = prevEnd + space;
                    if (start < lo) start = lo;
                    prevEnd = start + sweep;
                    result[an] = (start, sweep);
                }
                // Past the folder's end: pack the run side by side so it ends there, and if it still doesn't fit,
                // narrow every copy to share the folder's arc (never below 1.5 px).
                if (prevEnd > hi + 1e-9)
                {
                    double total = run.Sum(an => result[an].Item2) + gap * (run.Count - 1);
                    double scale = total > hi - lo ? Math.Max((hi - lo) / total, 1.5 / mid / min) : 1;
                    double at = Math.Max(lo, hi - total * scale);
                    foreach (var an in run)
                    {
                        double sw = result[an].Item2 * scale;
                        result[an] = (at, sw);
                        at += sw + gap * scale;
                    }
                }
            }
        }
        return result;
    }

    /// <summary>
    /// White outlines on the copies of the selected group, and on the hovered duplicate's twins, with a dashed
    /// line from the hovered copy to each twin.
    /// </summary>
    private void DrawDuplicateMarks(SKCanvas c, Node? hovered)
    {
        var hoverGroup = hovered != null ? HoveredGroup(hovered) : null;
        using var outline = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2f, Color = SKColors.White };
        using var thin = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.3f, Color = SKColors.White.WithAlpha(200) };
        using var dash = new SKPaint
        {
            IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.4f, Color = SKColors.White.WithAlpha(210),
            PathEffect = SKPathEffect.CreateDash(new[] { 5f, 4f }, 0),
        };
        SKPoint? from = null;
        foreach (var d in _dupDrawn)
            if (d.node == hovered && (from == null || d.group == hoverGroup)) from = Mid(d);
        foreach (var d in _dupDrawn)
        {
            var g = d.group;
            bool twin = hoverGroup != null && g == hoverGroup;
            bool selected = SelectedGroup != null && g == SelectedGroup;
            if (!twin && !selected) continue;
            using var path = Sector((float)_cx, (float)_cy, d.r1, d.r2, d.start, d.sweep);
            c.DrawPath(path, twin ? outline : thin);
            if (twin && from is { } f && d.node != hovered)
            {
                var to = Mid(d);
                c.DrawLine(f, to, dash);
            }
        }

        SKPoint Mid((Node node, float r1, float r2, double start, double sweep, DuplicateGroup group) d)
        {
            double a = d.start + d.sweep / 2, r = (d.r1 + d.r2) / 2;
            return new SKPoint((float)(_cx + Math.Sin(a) * r), (float)(_cy - Math.Cos(a) * r));
        }
    }

    /// <summary>Sweep style: a soft line along the leading edge while it goes round.</summary>
    private void DrawSweepEdge(SKCanvas c)
    {
        if (Style != ScanAnimation.Sweep) return;
        double p = _reveal;
        if (p <= 0.005 || p >= 0.995) return;
        // Fade in and out at the very start and end, so it never pops.
        double strength = Math.Min(1, Math.Min(p, 1 - p) * 20);
        double a = p * Math.Tau;
        if (_layout == null) return;
        float dx = (float)Math.Sin(a), dy = (float)-Math.Cos(a);
        using var paint = new SKPaint
        {
            IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.6f, StrokeCap = SKStrokeCap.Butt,
            Color = SKColors.White.WithAlpha((byte)(110 * strength)),
        };
        // Only across rings that have something at the edge, so the line follows the chart's outline.
        for (int r = 1; r <= Math.Min(Rings, _layout.RingCount); r++)
        {
            if (_ringDepth[r] <= 0.01 || _layout.HitTest(r, Math.Max(0, a - 0.002)) == null) continue;
            float r1 = (float)(RingInner(r) + 1), r2 = r1 + (float)((RingWidth(r) - 1) * _ringDepth[r]);
            c.DrawLine((float)_cx + dx * r1, (float)_cy + dy * r1, (float)_cx + dx * r2, (float)_cy + dy * r2, paint);
        }
    }

    // ---------- Animation ----------

    /// <summary>
    /// What is on screen for one segment. Every value eases towards its target each frame, so when sizes
    /// change (live scan, rescan) arcs widen and narrow smoothly, new folders grow outwards from their
    /// parent ring, removed ones shrink and fade, and zooming slides segments between rings.
    /// Keyed by full path, so the same folder is matched across rescans (new tree, same names).
    /// </summary>
    private sealed class Anim
    {
        public Node Node = null!;
        public int AbsDepth;
        public double Start, Sweep, Ring, Grow;
        public double TStart, TSweep, TRing, TGrow;
        public double VStart, VSweep, VRing, VGrow; // velocities, smooth mode only
        public bool Alive;
        public bool Ghost; // left behind by a segment that jumped to a new place: fades out where it was
        public bool Fade;  // Grow is drawn as opacity at full ring depth (a jump's cross-fade), not as depth
    }

    private readonly Dictionary<string, Anim> _anims = new();
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private double _lastFrame;
    private string? _rootKey;
    private int _ghostId;

    /// <summary>A sibling has to travel at least this far (radians) before it jumps instead of sliding.</summary>
    private const double JumpDistance = 0.12;

    /// <summary>
    /// Finds siblings that trade places in the new layout. Of each pair that crosses, the one that travels farther
    /// (usually the smaller one being overtaken) is returned: it fades out where it was and grows back in at its new
    /// place, subtree and all, while the other slides over. Sliding both through each other opened gaps at both
    /// ends of the pair and made the two subtrees cross, which read as the chart falling apart for a moment.
    /// </summary>
    private HashSet<string>? FindJumps(Dictionary<Node, string> keys, string centerKey)
    {
        if (!Smooth || !AnimationsEnabled || _layout == null) return null;
        var groups = new Dictionary<string, List<(string key, double oldStart, double newStart)>>();
        for (int ring = 1; ring <= _layout.RingCount; ring++)
        {
            foreach (var sg in _layout.Ring(ring))
            {
                string parentKey = sg.Node.Parent != null && keys.TryGetValue(sg.Node.Parent, out var pk) ? pk : centerKey;
                string key = keys[sg.Node];
                if (!_anims.TryGetValue(key, out var an) || !an.Alive || an.Grow < 0.5) continue;
                if (!groups.TryGetValue(parentKey, out var list)) groups[parentKey] = list = new();
                list.Add((key, an.TStart, sg.Start));
            }
        }
        HashSet<string>? jumps = null;
        foreach (var list in groups.Values)
        {
            if (list.Count < 2 || list.Count > 200) continue;
            list.Sort((x, y) => x.oldStart.CompareTo(y.oldStart));
            for (int i = 0; i < list.Count; i++)
                for (int j = i + 1; j < list.Count; j++)
                {
                    if (list[i].newStart <= list[j].newStart + 1e-9) continue; // same order
                    double ti = Math.Abs(list[i].newStart - list[i].oldStart);
                    double tj = Math.Abs(list[j].newStart - list[j].oldStart);
                    var far = ti >= tj ? list[i] : list[j];
                    if (Math.Max(ti, tj) >= JumpDistance) (jumps ??= new()).Add(far.key);
                }
        }
        return jumps;
    }

    /// <summary>Turns the segment at <paramref name="key"/> and everything under it into fading ghosts.</summary>
    private void Ghost(string key)
    {
        string prefix = key + "\\";
        var moved = _anims.Where(kv => !kv.Value.Ghost && (kv.Key == key || kv.Key.StartsWith(prefix, StringComparison.Ordinal))).ToList();
        foreach (var (k, an) in moved)
        {
            _anims.Remove(k);
            an.Ghost = true;
            an.Fade = true;
            an.Alive = false;
            an.TStart = an.Start; an.TSweep = an.Sweep; an.TRing = an.Ring; an.TGrow = 0;
            _anims[k + "|ghost" + (++_ghostId)] = an;
        }
    }

    /// <summary>Off: segments jump straight to their place (used for still previews).</summary>
    public bool AnimationsEnabled { get; set; } = true;

    private void SnapAnims()
    {
        foreach (var an in _anims.Values)
        {
            an.Start = an.TStart; an.Sweep = an.TSweep; an.Ring = an.TRing; an.Grow = an.TGrow;
            an.VStart = an.VSweep = an.VRing = an.VGrow = 0;
        }
    }

    /// <summary>True when what's left of the motion is too small to see; labels come back then.</summary>
    public bool NearlySettled { get; private set; } = true;

    /// <summary>True while segments are still moving; the view keeps repainting until it's false.</summary>
    public bool Animating { get; private set; }

    /// <summary>Points every animation at the new layout's geometry.</summary>
    private void SyncAnims()
    {
        string rootKey = _root?.Name ?? "";
        if (rootKey != _rootKey)
        {
            // Another drive: let the old one's segments shrink away, the new ones grow in. The reveal starts where
            // this drive's scan is, instead of winding back from a full circle (seen as a flash on start-up).
            _rootKey = rootKey;
            _snapReveal = true;
        }
        if (_layout == null || _center == null)
        {
            foreach (var an in _anims.Values) an.Alive = false;
            return;
        }

        var keys = new Dictionary<Node, string>(ReferenceEqualityComparer.Instance);
        string centerKey = KeyOf(_center);
        int centerDepth = _center.Depth;
        keys[_center] = centerKey;
        for (int ring = 1; ring <= _layout.RingCount; ring++)
            foreach (var s in _layout.Ring(ring))
                keys[s.Node] = (s.Node.Parent != null && keys.TryGetValue(s.Node.Parent, out var pk) ? pk : centerKey) + "\\" + s.Node.Name;

        var jumps = FindJumps(keys, centerKey);
        if (jumps != null) foreach (var k in jumps) Ghost(k);
        foreach (var an in _anims.Values) an.Alive = false;

        for (int ring = 1; ring <= _layout.RingCount; ring++)
        {
            foreach (var s in _layout.Ring(ring))
            {
                string parentKey = s.Node.Parent != null && keys.TryGetValue(s.Node.Parent, out var pk) ? pk : centerKey;
                string key = keys[s.Node];
                if (!_anims.TryGetValue(key, out var an))
                {
                    an = new Anim
                    {
                        // New: start as a sliver in the middle of where it will be, on its own ring, flat.
                        Start = s.Start + s.Sweep / 2,
                        Sweep = 0,
                        Ring = ring,
                        Grow = 0,
                    };
                    if (jumps != null && (jumps.Contains(key) || jumps.Contains(parentKey)))
                    {
                        // Jumped to a new place: grow outwards right there, at full width.
                        jumps.Add(key);
                        an.Start = s.Start;
                        an.Sweep = s.Sweep;
                        an.Fade = true;
                    }
                    // If its parent is on screen, start out of the parent's arc so children fan out of it.
                    else if (s.Node.Parent != null && keys.TryGetValue(s.Node.Parent, out var ppk) && _anims.TryGetValue(ppk, out var pa) && pa.Sweep > 0)
                    {
                        an.Start = pa.Start + pa.Sweep / 2;
                        an.Ring = pa.Ring + 1; // ride along with a parent that is still sliding between rings
                    }
                    _anims[key] = an;
                }
                an.Node = s.Node;
                an.AbsDepth = centerDepth + ring;
                an.TStart = s.Start;
                an.TSweep = s.Sweep;
                an.TRing = ring;
                an.TGrow = 1;
                an.Alive = true;
            }
        }

        foreach (var (key, an) in _anims)
        {
            if (an.Alive || an.Ghost) continue;
            // Gone (deleted, below the threshold, or outside the zoom): collapse and fade. If an ancestor is
            // still on screen, collapse at the matching spot of where that ancestor is going, so pieces move
            // with the chart (e.g. when a rescan sweep shrinks it) instead of being left behind where they were.
            double mid = an.Start + an.Sweep / 2;
            for (int cut = key.LastIndexOf('\\'); cut > 0; cut = key.LastIndexOf('\\', cut - 1))
            {
                if (_anims.TryGetValue(key[..cut], out var pa) && pa.Alive)
                {
                    if (pa.Sweep > 1e-9)
                    {
                        double rel = Math.Clamp((mid - pa.Start) / pa.Sweep, 0, 1);
                        mid = pa.TStart + rel * pa.TSweep;
                    }
                    break;
                }
            }
            an.TStart = mid;
            an.TSweep = 0;
            an.TGrow = 0;
            an.TRing = an.Ring;
        }

        // Zooming: segments that are no longer shown but sit on the path to the new centre move inwards
        // and widen to the full circle, so zooming in reads as "this folder becomes the centre".
        for (var n = (Node?)_center; n != null; n = n.Parent)
        {
            string k = KeyOf(n);
            if (_anims.TryGetValue(k, out var an) && !an.Alive)
            {
                an.TRing = n.Depth - centerDepth; // 0 = the centre disc, negative = further in
                an.TStart = 0;
                an.TSweep = Math.Tau;
                an.TGrow = 1;
            }
        }
    }

    /// <summary>One semi-implicit Euler step of a critically damped spring towards <paramref name="target"/>.</summary>
    private static void Spring(ref double x, ref double v, double target, double w, double h)
    {
        v += (w * w * (target - x) - 2 * w * v) * h;
        x += v * h;
    }

    private Func<Node, int>? RankFunc()
    {
        var map = _finalRanks ?? _liveRanks;
        if (map == null) return static _ => int.MaxValue;
        return n => map.TryGetValue(n.FullPath, out var r) ? r : int.MaxValue;
    }

    private static string KeyOf(Node n)
    {
        var parts = new Stack<string>();
        for (var x = n; x != null; x = x.Parent) parts.Push(x.Name);
        return string.Join("\\", parts);
    }

    /// <summary>Moves every animation one frame towards its target.</summary>
    private void StepAnims()
    {
        double now = _clock.Elapsed.TotalSeconds;
        double dt = Math.Clamp(now - _lastFrame, 0, 0.1);
        _lastFrame = now;
        // Quick: exponential ease-out, about 90 % of the way in a quarter second.
        double k = 1 - Math.Exp(-dt * 11);
        bool smooth = Smooth;
        // Smooth: critically damped spring, about 0.8 s to settle. Speed carries over when the target moves,
        // so a stream of small scan updates reads as one continuous motion.
        const double w = 6.0;
        int steps = smooth ? Math.Max(1, (int)Math.Ceiling(dt * 240)) : 0;
        double h = steps > 0 ? dt / steps : 0;

        bool moving = false, nearly = true;
        List<string>? dead = null;
        foreach (var (key, an) in _anims)
        {
            if (smooth)
            {
                for (int i = 0; i < steps; i++)
                {
                    Spring(ref an.Start, ref an.VStart, an.TStart, w, h);
                    Spring(ref an.Sweep, ref an.VSweep, an.TSweep, w, h);
                    Spring(ref an.Ring, ref an.VRing, an.TRing, w, h);
                    // A jump's ghost clears out quickly, so the cross-fade never shows two half-faded charts.
                    Spring(ref an.Grow, ref an.VGrow, an.TGrow, an.Ghost ? w * 2.5 : w, h);
                }
                if (an.Sweep < 0) { an.Sweep = 0; an.VSweep = 0; }
                an.Grow = Math.Clamp(an.Grow, 0, 1);
            }
            else
            {
                an.Start += (an.TStart - an.Start) * k;
                an.Sweep += (an.TSweep - an.Sweep) * k;
                an.Ring += (an.TRing - an.Ring) * k;
                an.Grow += (an.TGrow - an.Grow) * k;
                an.VStart = an.VSweep = an.VRing = an.VGrow = 0;
            }
            bool settled = Math.Abs(an.TStart - an.Start) < 1e-4 && Math.Abs(an.TSweep - an.Sweep) < 1e-4
                        && Math.Abs(an.TRing - an.Ring) < 1e-3 && Math.Abs(an.TGrow - an.Grow) < 1e-3
                        && Math.Abs(an.VStart) + Math.Abs(an.VSweep) < 1e-3 && Math.Abs(an.VRing) + Math.Abs(an.VGrow) < 1e-2;
            if (settled)
            {
                an.Start = an.TStart; an.Sweep = an.TSweep; an.Ring = an.TRing; an.Grow = an.TGrow;
                an.VStart = an.VSweep = an.VRing = an.VGrow = 0;
                if (!an.Alive) (dead ??= new()).Add(key);
                else an.Fade = false;
            }
            else
            {
                moving = true;
                if (an.Alive && (Math.Abs(an.TStart - an.Start) > 0.004 || Math.Abs(an.TSweep - an.Sweep) > 0.004
                                 || Math.Abs(an.TRing - an.Ring) > 0.03 || Math.Abs(an.TGrow - an.Grow) > 0.03))
                    nearly = false;
            }
        }
        if (dead != null) foreach (var key in dead) _anims.Remove(key);

        // Reveal: only the smooth styles use it, and only while the whole drive is being scanned.
        double target = Smooth && Live && Fill < 1 ? Math.Clamp(Fill, 0, 1) : 1;
        if (!AnimationsEnabled || !Smooth || _snapReveal) { _reveal = target; _revealV = 0; _snapReveal = false; }
        else
        {
            const double wr = 5.0; // a bit slower than the segments: the sweep is the calm, steady part
            int n = Math.Max(1, (int)Math.Ceiling(dt * 240));
            for (int i = 0; i < n; i++) Spring(ref _reveal, ref _revealV, target, wr, dt / n);
            if (Math.Abs(target - _reveal) < 1e-4 && Math.Abs(_revealV) < 1e-4) { _reveal = target; _revealV = 0; }
            else moving = true;
        }
        Animating = moving;
        NearlySettled = nearly;
    }

    private void DrawCenter(SKCanvas c)
    {
        float cx = (float)_cx, cy = (float)_cy, r = (float)(_r0 - 2);
        using (var rim = new SKPaint { IsAntialias = true, Color = DiscRim })
            c.DrawCircle(cx, cy, r + 2, rim);
        using (var disc = new SKPaint { IsAntialias = true, Color = HoverCenter ? DiscHover : DiscColor })
            c.DrawCircle(cx, cy, r, disc);

        var t = CenterText;
        if (t == null) return;
        float maxW = r * 1.7f;
        float titleSize = (float)Math.Clamp(r * 0.24, 14, 30);
        float lineSize = (float)Math.Clamp(r * 0.12, 10, 15);
        using var paint = new SKPaint { IsAntialias = true };
        using var title = new SKFont(UiBold, titleSize) { Subpixel = true };
        using var line = new SKFont(UiFace, lineSize) { Subpixel = true };
        using var small = new SKFont(UiFace, lineSize * 0.92f) { Subpixel = true };
        using var top = new SKFont(UiSemi, lineSize * 0.95f) { Subpixel = true };

        float y = cy - titleSize * 0.15f;
        if (t.Top != null)
        {
            paint.Color = LinkBlue;
            c.DrawText(Fit(t.Top, top, maxW * 0.9f), cx, y - titleSize * 1.15f, SKTextAlign.Center, top, paint);
        }
        paint.Color = TextColor;
        c.DrawText(Fit(t.Title, title, maxW), cx, y, SKTextAlign.Center, title, paint);
        paint.Color = TextDim;
        c.DrawText(Fit(t.Line2, line, maxW), cx, y + lineSize * 1.75f, SKTextAlign.Center, line, paint);
        paint.Color = TextFaint;
        c.DrawText(Fit(t.Line3, small, maxW), cx, y + lineSize * 3.3f, SKTextAlign.Center, small, paint);
    }

    public (Segment? seg, bool center) HitTest(double x, double y)
    {
        double dx = x - _cx, dy = y - _cy;
        double d = Math.Sqrt(dx * dx + dy * dy);
        if (d < _r0) return (null, true);
        if (_layout == null || _rw <= 0) return (null, false);
        int ring = RingAt(d);
        double angle = SunburstLayout.AngleOf(dx, dy);
        if (DupActive)
        {
            // Duplicates are drawn wider than their true size; point at what is drawn (3 px of slack).
            foreach (var dd in _dupDrawn)
            {
                double slack = 3.0 / Math.Max(d, 1);
                if (d < dd.r1 - 1 || d > dd.r2 + 1) continue;
                double rel = angle - dd.start;
                if (rel < -slack || rel > dd.sweep + slack) continue;
                if (FindSegment(dd.node) is { } hit) { _hitGroup = (dd.node, dd.group); return (hit, false); }
            }
        }
        // Not yet revealed by a scan animation: nothing to point at there.
        if (ring >= 1 && ring <= Rings && (angle >= _ringAngle[ring] || d > RingInner(ring) + _ringDepth[ring] * RingWidth(ring)))
            return (null, false);
        return (_layout.HitTest(ring, angle), false);
    }

    // ---------- Helpers ----------

    /// <summary>Inner radius of ring <paramref name="ring"/> (1 = innermost; fractional while animating).</summary>
    private double RingInner(double ring)
    {
        double f = Math.Clamp((ring - 1) / Rings, 0, 1.5);
        if (RingStyle == RingStyle.Even) return _r0 + f * (_rOuter - _r0);
        // OverDisk: every ring encloses the same area, so rings get thinner further out.
        return Math.Sqrt(_r0 * _r0 + f * (_rOuter * _rOuter - _r0 * _r0));
    }

    private double RingWidth(double ring) => RingInner(ring + 1) - RingInner(ring);

    /// <summary>The ring a distance from the centre falls in (1-based; 0 or Rings+1 outside).</summary>
    private int RingAt(double d)
    {
        if (RingStyle == RingStyle.Even) return (int)((d - _r0) / _rw) + 1;
        double a = _rOuter * _rOuter - _r0 * _r0;
        return a <= 0 ? 0 : (int)((d * d - _r0 * _r0) / a * Rings) + 1;
    }

    private Segment? FindSegment(Node node)
    {
        foreach (var s in _layout!.All)
            if (s.Node == node) return s;
        return null;
    }

    /// <summary>True when <paramref name="n"/> is the hovered node, inside it, or on the path to it.</summary>
    private static bool InBranch(Node n, Node hovered)
    {
        for (Node? x = n; x != null; x = x.Parent)
            if (x == hovered) return true;
        for (Node? x = hovered.Parent; x != null; x = x.Parent)
            if (x == n) return true;
        return false;
    }

    private static SKPath Sector(float cx, float cy, float r1, float r2, double start, double sweep)
    {
        var p = new SKPath();
        if (sweep >= Math.Tau - 1e-6)
        {
            p.FillType = SKPathFillType.EvenOdd;
            p.AddCircle(cx, cy, r2);
            p.AddCircle(cx, cy, r1);
            return p;
        }
        float a0 = (float)(start * 180 / Math.PI - 90);
        float sw = (float)(sweep * 180 / Math.PI);
        p.ArcTo(new SKRect(cx - r2, cy - r2, cx + r2, cy + r2), a0, sw, true);
        p.ArcTo(new SKRect(cx - r1, cy - r1, cx + r1, cy + r1), a0 + sw, -sw, false);
        p.Close();
        return p;
    }

    private static SKColor Blend(SKColor a, SKColor b, float t) => new(
        (byte)(a.Red + (b.Red - a.Red) * t),
        (byte)(a.Green + (b.Green - a.Green) * t),
        (byte)(a.Blue + (b.Blue - a.Blue) * t));

    private static string Fit(string text, SKFont font, float maxWidth)
    {
        if (font.MeasureText(text) <= maxWidth) return text;
        for (int n = text.Length - 1; n > 1; n--)
        {
            var t = text[..n].TrimEnd() + "…";
            if (font.MeasureText(t) <= maxWidth) return t;
        }
        return "…";
    }
}

/// <summary>How a scan of the whole drive is animated. All but Quick reveal the chart calmly.</summary>
/// <summary>How the chart is coloured.</summary>
public enum ChartColors
{
    /// <summary>OverDisk's size colouring: red for the biggest item in each folder, towards yellow for smaller ones.</summary>
    Size,
    /// <summary>Like Size, but against the whole view: only items big on screen are red.</summary>
    View,
    /// <summary>Each top-level folder keeps its own hue.</summary>
    Folders,
}

/// <summary>Ring thickness.</summary>
public enum RingStyle
{
    /// <summary>All rings equally thick.</summary>
    Even,
    /// <summary>OverDisk: every ring covers the same area, so outer rings are thinner.</summary>
    Thinning,
}

public enum ScanAnimation
{
    /// <summary>One clockwise sweep from 12 o'clock, rings growing outwards as it goes.</summary>
    Sweep,
    /// <summary>Each ring sweeps round a little behind the one inside it.</summary>
    Spiral,
    /// <summary>The full circle at once, rings opening outwards one after another.</summary>
    Bloom,
    /// <summary>The live chart as data arrives, squeezed into the scanned share. Snappy, busier.</summary>
    Quick,
}
