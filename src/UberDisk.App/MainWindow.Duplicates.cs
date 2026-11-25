using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using UberDisk.Core.Drives;
using UberDisk.Core.Duplicates;
using UberDisk.Core.Model;
using UberDisk.Core.Viz;

namespace UberDisk.App;

/// <summary>
/// Duplicate finder: the Duplicates toggle in the top bar, the groups side panel, and what the chart shows.
/// Read-only. Runs on demand for the drive being looked at, in the background, waiting while any drive scans.
/// </summary>
public partial class MainWindow
{
    private sealed class DupState
    {
        public DirNode? Tree;
        public long MinSize;
        public DuplicateFinder? Running;
        public DuplicateResult? Result;
        public string? Error;
    }

    private static readonly long[] DupMinSizes = { 100L << 20, 500L << 20, 1L << 30 };

    private bool _dupOn;
    private readonly Dictionary<string, DupState> _dups = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashCache _hashCache = new(System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UberDisk", "hashes.tsv"));
    private object? _dupListFor;

    private DupState? ActiveDup => _active != null && _dups.TryGetValue(_active, out var d) ? d : null;

    private void Dup_Click(object sender, RoutedEventArgs e) => SetDuplicateMode(!_dupOn);
    private void DupClose_Click(object sender, RoutedEventArgs e) => SetDuplicateMode(false);

    private void SetDuplicateMode(bool on)
    {
        _dupOn = on;
        if (!on) Chart.SelectedGroup = null;
        EnsureDuplicates();
        UpdateDuplicateUi();
        UpdateView();
    }

    private void DupMin_Click(object sender, RoutedEventArgs e)
    {
        int i = Array.IndexOf(DupMinSizes, AppSettings.DuplicateMinSize);
        AppSettings.DuplicateMinSize = DupMinSizes[(i + 1) % DupMinSizes.Length];
        AppSettings.Save();
        Chart.SelectedGroup = null;
        EnsureDuplicates();
        UpdateDuplicateUi();
    }

    /// <summary>Starts a search for the active drive when its tree or the minimum size changed. Cheap to call often.</summary>
    private void EnsureDuplicates()
    {
        // Runs after every finished scan, also with the Duplicates view off, so hovering a file shows its copies.
        // Cheap after the first time: unchanged files come from the hash cache.
        if (ActiveSession is not { } s || s.Tree is not { } tree || s.State != ScanState.Done) return;
        if (!_dups.TryGetValue(s.Meta.Root, out var st)) _dups[s.Meta.Root] = st = new DupState();
        long min = AppSettings.DuplicateMinSize;
        if (st.Tree == tree && st.MinSize == min && (st.Running != null || st.Result != null || st.Error != null)) return;

        // One search at a time: a search for another drive or an older tree is stopped.
        foreach (var other in _dups.Values)
        {
            other.Running?.Cancel();
            other.Running = null;
        }
        if (st.MinSize != min) st.Result = null; // a different minimum size: old groups would mislead
        st.Tree = tree;
        st.MinSize = min;
        st.Error = null;
        var finder = new DuplicateFinder(tree, new DuplicateOptions
        {
            MinSize = min,
            Cache = _hashCache,
            Busy = () => _scans.Sessions.Any(x => x.State == ScanState.Scanning),
        });
        st.Running = finder;
        finder.RunAsync().ContinueWith(t => Dispatcher.BeginInvoke(() =>
        {
            if (st.Running != finder) return; // superseded
            st.Running = null;
            if (t.IsCompletedSuccessfully)
            {
                st.Result = t.Result;
                Chart.SelectedGroup = null;
                _hashCache.Save();
            }
            else if (!t.IsCanceled) st.Error = t.Exception?.GetBaseException().Message;
            UpdateDuplicateUi();
            UpdateView();
        }));
    }

    /// <summary>Badge, panel and chart state. Called on every refresh tick.</summary>
    private void UpdateDuplicateUi()
    {
        var st = ActiveDup;
        var s = ActiveSession;

        DupBtn.BorderBrush = _dupOn ? new SolidColorBrush(Color.FromRgb(0x2E, 0x8F, 0xA3)) : (Brush)FindResource("CardBorder");
        if (st?.Running is { } r)
        {
            DupBadge.Visibility = Visibility.Visible;
            DupBadgeText.Text = $"{r.Progress * 100:0} %";
        }
        else if (st?.Result is { } res && st.MinSize == AppSettings.DuplicateMinSize)
        {
            DupBadge.Visibility = Visibility.Visible;
            DupBadgeText.Text = res.Groups.Count == 0 ? "none" : Format.Bytes(res.Wasted);
        }
        else DupBadge.Visibility = Visibility.Collapsed;

        DupPanel.Visibility = _dupOn ? Visibility.Visible : Visibility.Collapsed;
        Chart.DuplicateMode = _dupOn;
        Chart.Duplicates = st?.Result;
        if (!_dupOn || s == null) return;

        DupTitle.Text = $"Duplicates on {s.Meta.Letter}";
        DupMinBtn.Content = Format.Bytes(AppSettings.DuplicateMinSize) + " ▾";
        if (st?.Error != null) DupSummary.Text = "Couldn't finish: " + st.Error;
        else if (st?.Running is { } run)
            DupSummary.Text = s.State == ScanState.Scanning || _scans.Sessions.Any(x => x.State == ScanState.Scanning)
                ? $"Checking, waiting for the scan to finish… {run.Progress * 100:0} %"
                : $"Checking files of {Format.Bytes(AppSettings.DuplicateMinSize)} and up… {run.Progress * 100:0} %";
        else if (st?.Result is { } res2)
            DupSummary.Text = res2.Groups.Count == 0
                ? $"No identical files of {Format.Bytes(res2.MinSize)} or more."
                : $"{res2.Groups.Count} groups · {res2.Files} files · {Format.Bytes(res2.Wasted)} reclaimable";
        else DupSummary.Text = s.State == ScanState.Done ? "" : "Starts when the drive scan is done.";

        var key = (st?.Result, Chart.SelectedGroup);
        if (!Equals(_dupListFor, key))
        {
            _dupListFor = key;
            BuildDuplicateList(st?.Result);
        }
    }

    private void BuildDuplicateList(DuplicateResult? result)
    {
        DupList.Children.Clear();
        if (result == null) return;
        foreach (var g in result.Groups)
            DupList.Children.Add(GroupCard(g, g == Chart.SelectedGroup));
    }

    private FrameworkElement GroupCard(DuplicateGroup g, bool selected)
    {
        var sk = SunburstPainter.GroupColor(g.Index);
        var swatch = new Rectangle { Width = 10, Height = 10, RadiusX = 2, RadiusY = 2, Fill = new SolidColorBrush(Color.FromRgb(sk.Red, sk.Green, sk.Blue)), Margin = new Thickness(0, 4, 9, 0), VerticalAlignment = VerticalAlignment.Top };
        var name = new TextBlock { Text = g.Name, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = g.Name };
        var wasted = new TextBlock { Text = Format.Bytes(g.Wasted), FontWeight = FontWeights.SemiBold, Margin = new Thickness(8, 0, 0, 0), ToolTip = "Space freed by keeping one copy" };
        var sub = new TextBlock { Text = $"{g.Paths.Count} copies × {Format.Bytes(g.Length)}", FontSize = 12, Foreground = (Brush)FindResource("TextDim"), Margin = new Thickness(0, 2, 0, 0) };

        var head = new DockPanel();
        DockPanel.SetDock(swatch, Dock.Left);
        DockPanel.SetDock(wasted, Dock.Right);
        head.Children.Add(swatch);
        head.Children.Add(wasted);
        var text = new StackPanel();
        text.Children.Add(name);
        text.Children.Add(sub);
        head.Children.Add(text);

        var body = new StackPanel();
        body.Children.Add(head);
        if (selected)
        {
            body.Children.Add(new Border { Height = 8 });
            foreach (var path in g.Paths) body.Children.Add(CopyRow(path));
        }

        var card = new Border
        {
            Child = body,
            Padding = new Thickness(12, 9, 8, 10),
            Margin = new Thickness(0, 0, 0, 8),
            CornerRadius = new CornerRadius(7),
            BorderThickness = new Thickness(1),
            Background = (Brush)FindResource(selected ? "CardHover" : "CardBg"),
            BorderBrush = selected ? new SolidColorBrush(Color.FromRgb(0x5A, 0x60, 0x6C)) : (Brush)FindResource("CardBorder"),
            Cursor = Cursors.Hand,
            ToolTip = selected ? null : "Click to light up the copies in the chart",
        };
        card.MouseLeftButtonUp += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject d && FindParent<Button>(d) != null) return;
            Chart.SelectedGroup = Chart.SelectedGroup == g ? null : g;
            UpdateDuplicateUi();
        };
        return card;
    }

    private FrameworkElement CopyRow(string path)
    {
        string folder = System.IO.Path.GetDirectoryName(path) ?? path;
        var row = new DockPanel { Margin = new Thickness(19, 2, 0, 0) };
        var open = new Button { Style = (Style)FindResource("IconButton"), Width = 28, Height = 28, FontSize = 13, Content = "", ToolTip = "Open in Explorer (selects the file)" };
        open.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }); }
            catch (Exception) { /* Explorer not available: nothing useful to do */ }
        };
        var copy = new Button { Style = (Style)FindResource("IconButton"), Width = 28, Height = 28, FontSize = 13, Content = "", ToolTip = "Copy path" };
        copy.Click += (_, _) =>
        {
            try { Clipboard.SetText(path); } catch (COMException) { /* clipboard busy */ }
        };
        DockPanel.SetDock(copy, Dock.Right);
        DockPanel.SetDock(open, Dock.Right);
        row.Children.Add(copy);
        row.Children.Add(open);
        row.Children.Add(new TextBlock
        {
            Text = folder, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = path,
        });
        return row;
    }

    private static T? FindParent<T>(DependencyObject d) where T : DependencyObject
    {
        for (var x = d; x != null; x = VisualTreeHelper.GetParent(x))
            if (x is T t) return t;
        return null;
    }

    /// <summary>Centre text while the Duplicates view is on and the drive itself is shown.</summary>
    private CenterText? DuplicateCenter(DriveSession s, DirNode? center)
    {
        if (!_dupOn || center == null || center.Parent != null || s.State == ScanState.Scanning) return null;
        var st = ActiveDup;
        if (st?.Running is { } r) return new CenterText(null, s.Meta.Letter, "Checking duplicates", $"{r.Progress * 100:0} %");
        if (st?.Result is { } res)
            return res.Groups.Count == 0
                ? new CenterText(null, s.Meta.Letter, "No duplicates", $"of {Format.Bytes(res.MinSize)} or more")
                : new CenterText(null, s.Meta.Letter, $"{Format.Bytes(res.Wasted)} in duplicates", $"{res.Groups.Count} groups · {res.Files} files");
        return null;
    }

    /// <summary>Extra hover-card line for a duplicate.</summary>
    private string? DuplicateHoverLine(Node node)
    {
        if (Chart.GroupOf(node) is { } g) return $"{g.Paths.Count} identical copies · {Format.Bytes(g.Wasted)} reclaimable";
        if (_dupOn && Chart.ShownGroup(node) is { } inside) return $"Holds a copy of {inside.Name} · {inside.Paths.Count} copies · {Format.Bytes(inside.Wasted)} reclaimable";
        return null;
    }
}
