using System.Windows;
using System.Windows.Input;
using SkiaSharp;
using UberDisk.Core.Model;
using UberDisk.Core.Viz;

namespace UberDisk.App;

/// <summary>The sunburst control: hosts <see cref="SunburstPainter"/> and turns mouse input into events.</summary>
public sealed class SunburstView : SkiaCanvas
{
    private readonly SunburstPainter _painter = new();

    public SunburstView()
    {
        Focusable = false;
        Cursor = Cursors.Arrow;
    }

    public Segment? Hover => _painter.Hover;
    public DirNode? CenterNode => _painter.CenterNode;

    public CenterText? CenterText
    {
        get => _painter.CenterText;
        set => _painter.CenterText = value;
    }

    /// <summary>True while the shown tree is still filling in. Hides labels that would jump around.</summary>
    public bool Smooth => _painter.Smooth;

    public UberDisk.Core.Duplicates.DuplicateResult? Duplicates
    {
        get => _painter.Duplicates;
        set { _painter.Duplicates = value; InvalidateVisual(); }
    }

    public bool DuplicateMode
    {
        get => _painter.DuplicateMode;
        set { _painter.DuplicateMode = value; InvalidateVisual(); }
    }

    public UberDisk.Core.Duplicates.DuplicateGroup? SelectedGroup
    {
        get => _painter.SelectedGroup;
        set { _painter.SelectedGroup = value; InvalidateVisual(); }
    }

    public UberDisk.Core.Duplicates.DuplicateGroup? GroupOf(UberDisk.Core.Model.Node n) => _painter.GroupOf(n);

    /// <summary>The group a segment shows: a copy's own, or that of a copy inside it too deep or too small to draw.</summary>
    public UberDisk.Core.Duplicates.DuplicateGroup? ShownGroup(UberDisk.Core.Model.Node n) => _painter.HoveredGroup(n);

    public ScanAnimation AnimationStyle
    {
        get => _painter.Style;
        set { _painter.Style = value; _painter.Invalidate(); InvalidateVisual(); }
    }

    /// <summary>Average position of the hovered copy's twins, in DIPs (duplicates view only).</summary>
    public Point? TwinCentroid(UberDisk.Core.Model.Node hovered) =>
        _painter.TwinCentroid(hovered) is { } p ? new Point(p.X, p.Y) : null;

    /// <summary>Share of the drive that is free; above 0 the drive view shows it as an empty arc. See the painter.</summary>
    public double FreeFraction
    {
        get => _painter.FreeFraction;
        set { _painter.FreeFraction = value; InvalidateVisual(); }
    }

    /// <summary>True while the mouse is over the free-space arc.</summary>
    public bool HoverFree => _painter.HoverFree;

    public SizeStyle Colors
    {
        get => _painter.Colors;
        set { _painter.Colors = value; InvalidateVisual(); }
    }

    public RingStyle RingStyle
    {
        get => _painter.RingStyle;
        set { _painter.RingStyle = value; _painter.Invalidate(); InvalidateVisual(); }
    }

    public double Fill
    {
        get => _painter.Fill;
        set => _painter.Fill = value;
    }

    public bool Live
    {
        get => _painter.Live;
        set => _painter.Live = value;
    }

    public event Action<Segment>? SegmentClicked;
    public event Action? CenterClicked;
    public event Action<Segment?, Point>? HoverChanged;
    /// <summary>Right-click: the segment under the mouse (null on empty space or the free-space arc), and whether it was the centre disc.</summary>
    public event Action<Segment?, bool, Point>? ContextRequested;
    public event Action? BackRequested;
    public event Action? ForwardRequested;

    public void Show(DirNode? root, DirNode? center)
    {
        if (_painter.Show(root, center)) HoverChanged?.Invoke(null, default);
        InvalidateVisual();
    }

    /// <summary>Redraws; with <paramref name="relayout"/> also re-reads the (growing) tree.</summary>
    public void Refresh(bool relayout)
    {
        if (relayout) _painter.Invalidate();
        InvalidateVisual();
    }

    protected override void OnPaint(SKCanvas canvas, SKImageInfo info, float scale)
    {
        canvas.Scale(scale);
        var hoverBefore = _painter.Hover;
        _painter.Paint(canvas, ActualWidth, ActualHeight);
        // A rescan can replace the hovered folder; drop the hover card instead of showing stale numbers.
        if (hoverBefore != null && _painter.Hover == null)
            Dispatcher.BeginInvoke(() => HoverChanged?.Invoke(null, default));
        SetFrameLoop(_painter.Animating);
    }

    private bool _frameLoop;

    /// <summary>Repaints every display frame while segments are moving, and stops when they settle.</summary>
    private void SetFrameLoop(bool on)
    {
        if (on == _frameLoop) return;
        _frameLoop = on;
        if (on) System.Windows.Media.CompositionTarget.Rendering += OnFrame;
        else System.Windows.Media.CompositionTarget.Rendering -= OnFrame;
    }

    private void OnFrame(object? sender, EventArgs e) => InvalidateVisual();

    // ---------- Mouse ----------

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var p = e.GetPosition(this);
        var (seg, center) = HitTest(p);
        SetHover(seg, center, p);
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        SetHover(null, false, default);
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        var p = e.GetPosition(this);
        var (seg, center) = HitTest(p);
        switch (e.ChangedButton)
        {
            case MouseButton.Left when center:
                CenterClicked?.Invoke();
                break;
            case MouseButton.Left when seg != null:
                SegmentClicked?.Invoke(seg.Value);
                break;
            case MouseButton.Right:
                ContextRequested?.Invoke(seg, center, p);
                break;
            case MouseButton.XButton1:
                BackRequested?.Invoke();
                break;
            case MouseButton.XButton2:
                ForwardRequested?.Invoke();
                break;
        }
        e.Handled = true;
    }

    private void SetHover(Segment? seg, bool center, Point p)
    {
        bool free = seg == null && !center && _painter.HitFree(p.X, p.Y);
        bool changed = !Equals(seg, _painter.Hover) || center != _painter.HoverCenter || free != _painter.HoverFree;
        _painter.HoverFree = free;
        _painter.Hover = seg;
        _painter.HoverCenter = center;
        Cursor = seg is { Node: DirNode { IsSynthetic: false } } || (center && CenterNode?.Parent != null) ? Cursors.Hand : Cursors.Arrow;
        HoverChanged?.Invoke(seg, p);
        if (changed) InvalidateVisual();
    }

    private (Segment? seg, bool center) HitTest(Point p) => _painter.HitTest(p.X, p.Y);

    // ---------- Demo screenshots (see MainWindow.Demo.cs) ----------

    internal bool Animating => _painter.Animating;
    internal Segment? SegmentAt(Point p) => HitTest(p).seg;

    /// <summary>Hovers what is at <paramref name="p"/> as if the mouse were there; null clears the hover.</summary>
    internal void DemoHover(Point? p)
    {
        if (p is { } at) { var (seg, center) = HitTest(at); SetHover(seg, center, at); }
        else SetHover(null, false, default);
    }
}
