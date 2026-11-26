using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using UberDisk.Core.Deleting;
using UberDisk.Core.Drives;
using UberDisk.Core.Model;
using UberDisk.Core.Viz;

namespace UberDisk.App;

public partial class MainWindow : Window
{
    private readonly ScanCoordinator _scans = Demo.NewCoordinator();
    private readonly ObservableCollection<DriveCard> _cards = new();
    private readonly Dictionary<string, Navigator> _nav = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _tick;
    private readonly DispatcherTimer _driveRefresh;
    private string? _active;
    private DirNode? _shownTree;
    private bool _wasScanning;
    private bool _refreshingDrives;

    public MainWindow()
    {
        InitializeComponent();
        if (Demo.ShotsDir != null) ShowActivated = false; // don't take the keyboard from whoever is at the PC
        DriveList.ItemsSource = _cards;

        Chart.SegmentClicked += OnSegmentClicked;
        Chart.CenterClicked += () => Nav?.Up();
        Chart.HoverChanged += OnHoverChanged;
        AppSettings.Load();
        ApplyAnimationSetting();
        Chart.ContextRequested += OnContextRequested;
        Chart.BackRequested += () => Nav?.Back();
        Chart.ForwardRequested += () => Nav?.Forward();

        // Scan events arrive on worker threads; just poke the UI.
        _scans.Changed += _ => Dispatcher.BeginInvoke(UpdateAll, DispatcherPriority.Background);

        // While anything scans, refresh the cards and the live chart a few times a second.
        _tick = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => OnTick(), Dispatcher);
        _driveRefresh = new DispatcherTimer(TimeSpan.FromSeconds(15), DispatcherPriority.Background, (_, _) => RefreshDrivesAsync(), Dispatcher);

        SourceInitialized += (_, _) => UseDarkTitleBar();
        Loaded += async (_, _) => await StartAsync();
        Closing += (_, _) => StopDuplicatesAndSave();
        PreviewKeyDown += OnKey;
        MouseUp += OnWindowMouseUp;
    }

    private Navigator? Nav => _active != null && _nav.TryGetValue(_active, out var n) ? n : null;
    private DriveSession? ActiveSession => _active != null ? _scans.Sessions.FirstOrDefault(s => s.Meta.Root == _active) : null;

    // ======================= Startup and drives =======================

    private async Task StartAsync()
    {
        var drives = await Task.Run(Demo.ListDrives);
        _scans.UpdateDrives(drives);
        SyncCards();

        // Open the system drive (or the first ready fixed drive), then queue the rest in the background.
        string sys = Demo.On ? "C:\\" : System.IO.Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
        var first = drives.FirstOrDefault(d => d.IsReady && string.Equals(d.Root, sys, StringComparison.OrdinalIgnoreCase))
                    ?? drives.FirstOrDefault(d => d.IsReady && d.AutoScan)
                    ?? drives.FirstOrDefault(d => d.IsReady);
        if (first != null) Activate(first.Root);
        _scans.QueueAllInBackground();
        _tick.Start();
        _driveRefresh.Start();
        UpdateAll();
        if (Demo.ShotsDir is { } shots) await TakeDemoShotsAsync(shots);
    }

    private async void RefreshDrivesAsync()
    {
        if (_refreshingDrives) return;
        _refreshingDrives = true;
        try
        {
            var drives = await Task.Run(Demo.ListDrives);
            _scans.UpdateDrives(drives);
            SyncCards();
            UpdateAll();
        }
        finally { _refreshingDrives = false; }
    }

    private void SyncCards()
    {
        var roots = _scans.Sessions.Select(s => s.Meta.Root).OrderBy(r => r, StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var gone in _cards.Where(c => !roots.Contains(c.Root, StringComparer.OrdinalIgnoreCase)).ToList())
            _cards.Remove(gone);
        for (int i = 0; i < roots.Count; i++)
        {
            int at = _cards.ToList().FindIndex(c => string.Equals(c.Root, roots[i], StringComparison.OrdinalIgnoreCase));
            if (at < 0) _cards.Insert(i, new DriveCard(roots[i]));
            else if (at != i) _cards.Move(at, i);
        }
    }

    private void Activate(string root)
    {
        var s = _scans.Sessions.FirstOrDefault(x => string.Equals(x.Meta.Root, root, StringComparison.OrdinalIgnoreCase));
        if (s == null || !s.Meta.IsReady) return;
        _active = s.Meta.Root;
        if (!_nav.ContainsKey(_active))
        {
            var nav = new Navigator();
            nav.Changed += () => { if (Nav == nav) UpdateView(); };
            _nav[_active] = nav;
        }
        _scans.Activate(_active);
        HideHoverCard();
        UpdateAll();
    }

    // ======================= Refresh =======================

    /// <summary>How far round the circle a running scan has got: its progress (time-based on a rescan),
    /// held below a full turn until the scan really finishes.</summary>
    private static double ScanFill(DriveSession s) => Math.Clamp(s.Progress, 0.02, 0.99);

    private void OnTick()
    {
        bool anyScanning = _scans.Sessions.Any(s => s.State == ScanState.Scanning);
        if (anyScanning || _wasScanning) UpdateAll();
        else if (_dups.Values.Any(d => d.Running != null))
        {
            UpdateDuplicateUi(); // progress in the badge, panel and centre
            UpdateView();
        }
        else UpdateCards(); // keeps "scanned 2 min ago" current
        _wasScanning = anyScanning;
    }

    private void UpdateAll()
    {
        UpdateCards();
        EnsureDuplicates();
        UpdateDuplicateUi();
        UpdateView();
    }

    private void UpdateCards()
    {
        var now = DateTime.Now;
        foreach (var c in _cards)
        {
            var s = _scans.Sessions.FirstOrDefault(x => x.Meta.Root == c.Root);
            if (s != null) c.Update(s, c.Root == _active, now);
        }
        int queued = _scans.Sessions.Count(s => s.State == ScanState.Queued);
        var bg = _scans.Sessions.FirstOrDefault(s => s.State == ScanState.Scanning && s.Meta.Root != _active);
        RailStatus.Text = bg != null
            ? $"Scanning {bg.Meta.Letter} in the background{(queued > 0 ? $", {queued} more queued" : "")}."
            : queued > 0 ? $"{queued} drive{(queued == 1 ? "" : "s")} queued." : "";
    }

    /// <summary>Syncs the chart, centre text, breadcrumb, nav buttons and Rescan/Stop with the active drive.</summary>
    private void UpdateView()
    {
        var s = ActiveSession;
        var nav = Nav;
        if (s == null || nav == null)
        {
            Chart.Show(null, null);
            Chart.CenterText = null;
            EmptyText.Text = "No drive selected";
            EmptyText.Visibility = Visibility.Visible;
            return;
        }

        var tree = s.Tree;
        bool scanning = s.State == ScanState.Scanning;
        EmptyText.Visibility = tree == null ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = s.State == ScanState.Stopped ? "Scan stopped. Press Rescan to scan this drive." : "";

        var center = tree != null ? nav.Resolve(tree) : null;
        // Smooth styles, during a rescan: reveal the last finished result instead of the half-built new tree,
        // whose folders turn up in whatever order the scan reaches them. The new result glides in when the scan
        // ends (usually only small changes). Drive view: the reveal follows the scan's progress. Zoomed in: it
        // follows how much of that folder has been read again, by size (the zoomed folder is scanned first).
        var liveCenter = center;
        double fill = 1;
        if (scanning && Chart.Smooth && center != null && s.PreviousTree is { } previous)
        {
            var prevCenter = nav.Resolve(previous);
            if (center == tree)
            {
                tree = center = previous;
                fill = ScanFill(s);
            }
            else if (Navigator.PathOf(prevCenter).Length == Navigator.PathOf(center).Length && prevCenter.Size > 0)
            {
                fill = Math.Clamp((double)center.Size / prevCenter.Size, 0.02, 0.99);
                tree = previous;
                center = prevCenter;
            }
        }
        else if (scanning && center != null && center == tree)
        {
            fill = ScanFill(s); // first scan, or Quick
        }
        bool treeChanged = tree != _shownTree;
        _shownTree = tree;
        Chart.Live = scanning;
        Chart.Fill = fill;
        var meta = s.Meta;
        Chart.FreeFraction = AppSettings.ShowFree && meta.TotalBytes > 0 ? (double)meta.FreeBytes / meta.TotalBytes : 0;
        // Centre counts up from the live scan even while the previous result is drawn.
        Chart.CenterText = DuplicateCenter(s, liveCenter) ?? CenterFor(s, liveCenter);
        if (treeChanged || center != Chart.CenterNode) Chart.Show(tree, center);
        // Always re-read the tree: when a scan finishes the tree object is the same, but its children are
        // re-sorted and final, and the unscanned-space slice is added.
        else Chart.Refresh(relayout: true);

        // Breadcrumb. Rebuilt only when the path changes: rebuilding it on every refresh tick during a scan
        // replaced the buttons between mouse down and up, so clicks on it got lost.
        var shownPath = center != null ? Navigator.PathOf(center) : Array.Empty<string>();
        string crumbKey = s.Meta.Root + "|" + string.Join("|", shownPath);
        if (crumbKey != _crumbKey)
        {
            _crumbKey = crumbKey;
            BuildCrumbs(s, nav, shownPath);
        }


        BackBtn.IsEnabled = nav.CanGoBack;
        ForwardBtn.IsEnabled = nav.CanGoForward;
        UpBtn.IsEnabled = center?.Parent != null;

        // Rescan becomes Stop while the active drive scans.
        if (scanning)
        {
            RescanBtn.Tag = "stop";
            RescanIcon.Text = "\uE71A";
            RescanText.Text = "Stop";
            ProgressBox.Visibility = Visibility.Visible;
            ProgressFill.Width = Math.Round(ProgressBox.Width * s.Progress);
        }
        else
        {
            RescanBtn.Tag = null;
            RescanIcon.Text = "\uE72C";
            RescanText.Text = $"Rescan {s.Meta.Letter}";
            ProgressBox.Visibility = Visibility.Collapsed;
        }

        ScanInfo.Text = s.State switch
        {
            ScanState.Done when tree != null =>
                $"{s.Meta.Letter} scanned in {Format.Duration(s.LastScanDuration)} · {Format.Count(tree.FileCount)} files · {Format.Count(tree.DirCount)} folders",
            ScanState.Scanning when s.Job is { } j =>
                $"{Format.Count(j.FoldersRead)} folders read · {Format.Duration(j.Elapsed)}",
            _ => "",
        };
        FitFooter();
    }

    private string? _crumbKey;

    private void BuildCrumbs(DriveSession s, Navigator nav, string[] shownPath)
    {
        Crumbs.Items.Clear();
        var parts = new List<(string text, string[] path)> { (s.Meta.Root, Array.Empty<string>()) };
        for (int i = 0; i < shownPath.Length; i++) parts.Add((shownPath[i], shownPath[..(i + 1)]));
        // Collapse the middle of very deep paths.
        int skipFrom = parts.Count > 7 ? 2 : int.MaxValue, skipTo = parts.Count - 4;
        for (int i = 0; i < parts.Count; i++)
        {
            if (i >= skipFrom && i <= skipTo)
            {
                if (i == skipFrom) Crumbs.Items.Add(Sep("…"));
                continue;
            }
            if (i > 0) Crumbs.Items.Add(Sep("›"));
            var (text, path) = parts[i];
            var b = new Button
            {
                Content = text.Length > 40 ? text[..38] + "…" : text,
                Style = (Style)FindResource("CrumbButton"),
                Tag = i == parts.Count - 1 ? "current" : null,
                ToolTip = text,
            };
            b.Click += (_, _) => nav.Go(path);
            Crumbs.Items.Add(b);
        }
    }

    private static TextBlock Sep(string t) => new()
    {
        Text = t,
        Margin = new Thickness(4, 0, 4, 0),
        Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("TextFaint"),
        VerticalAlignment = VerticalAlignment.Center,
    };

    private static CenterText? CenterFor(DriveSession s, DirNode? center)
    {
        var m = s.Meta;
        if (center == null) return null;
        if (s.State == ScanState.Scanning && center.Parent == null)
        {
            var left = s.Remaining is { } r ? " · " + Format.Remaining(r) : "";
            string waiting = s.Job?.IsPaused == true ? " · waiting" : "";
            return new CenterText(null, $"Scanning {m.Letter}",
                $"{Format.Bytes(center.Size)} · {Format.Count(center.FileCount)} files",
                $"{s.Progress * 100:0} %{left}{waiting}");
        }
        if (center.Parent == null)
        {
            return new CenterText(null, m.Letter,
                $"{Format.Bytes(m.UsedBytes)} used",
                $"of {Format.Bytes(m.TotalBytes)} · {Format.Bytes(m.FreeBytes)} free");
        }
        string parentName = center.Parent.Parent == null ? m.Letter : center.Parent.Name;
        double ofDrive = m.UsedBytes > 0 ? (double)center.Size / m.UsedBytes : 0;
        return new CenterText($"↑ Up to {parentName}", center.Name, Format.Bytes(center.Size),
            $"{Format.Percent(ofDrive)} of {m.Letter}");
    }

    // ======================= Chart interaction =======================

    private void OnSegmentClicked(Segment seg)
    {
        switch (seg.Node)
        {
            case DirNode { IsSynthetic: true }:
                return;
            case DirNode d:
                Nav?.ZoomTo(d);
                break;
            case FileNode f when f.Parent != null && f.Parent != Chart.CenterNode:
                Nav?.ZoomTo(f.Parent);
                break;
        }
    }

    private void OnHoverChanged(Segment? seg, Point p)
    {
        Node? node = null;
        if (seg is { } s)
        {
            node = s.Node;
            HcName.Text = node.Name;
            HcSize.Text = Format.Bytes(node.Size);
        }
        else if (Chart.HoverFree && ActiveSession is { } fs)
        {
            HcName.Text = "Free space";
            HcSize.Text = $"{Format.Bytes(fs.Meta.FreeBytes)} · {Format.Percent(fs.Meta.TotalBytes > 0 ? (double)fs.Meta.FreeBytes / fs.Meta.TotalBytes : 0)} of {fs.Meta.Letter}";
        }
        else { HideHoverCard(); return; }
        var dupLine = node != null ? DuplicateHoverLine(node) : null;
        HcDup.Text = dupLine ?? "";
        HcDup.Visibility = dupLine != null ? Visibility.Visible : Visibility.Collapsed;

        HoverCard.Visibility = Visibility.Visible;
        HoverCard.UpdateLayout();
        double w = HoverCard.ActualWidth, h = HoverCard.ActualHeight;
        // Centred right under the cursor, so the name sits where the eyes already are.
        // Only pushed aside where the window edge would cut it off.
        double x = p.X - w / 2;
        double y = p.Y + 24;
        if (y + h > ChartHost.ActualHeight - 8) y = p.Y - h - 12;
        // Duplicates view: keep the card off the dashed lines to the twins (put it on the other side of the cursor).
        else if (node != null && Chart.TwinCentroid(node) is { } twins && twins.Y > p.Y && Math.Abs(twins.Y - p.Y) > Math.Abs(twins.X - p.X) * 0.5 && p.Y - h - 12 >= 8)
            y = p.Y - h - 12;
        x = Math.Min(x, ChartHost.ActualWidth - w - 8);
        Canvas.SetLeft(HoverCard, Math.Max(8, x));
        Canvas.SetTop(HoverCard, Math.Max(8, y));
    }

    private void HideHoverCard() => HoverCard.Visibility = Visibility.Collapsed;

    private void OnContextRequested(Segment? seg, bool center, Point p)
    {
        Node? node = seg?.Node ?? Chart.CenterNode;
        if (node == null || node is DirNode { IsSynthetic: true }) return;
        HideHoverCard();
        // Delete is offered only for what is under the mouse: a segment or the centre disc. Empty space and the
        // free-space arc fall back to the centre folder's menu for Explorer/Copy path, but never offer Delete.
        var menu = BuildMenu(node, allowDelete: seg != null || center);
        menu.PlacementTarget = Chart;
        menu.IsOpen = true;
    }

    private ContextMenu BuildMenu(Node node, bool allowDelete)
    {
        var menu = new ContextMenu();
        var header = new MenuItem
        {
            Header = new TextBlock
            {
                Inlines =
                {
                    new System.Windows.Documents.Run(node.Name) { FontWeight = FontWeights.SemiBold },
                    new System.Windows.Documents.Run(" · " + Format.Bytes(node.Size)) { Foreground = (System.Windows.Media.Brush)FindResource("TextDim") },
                },
                MaxWidth = 300,
                TextTrimming = TextTrimming.CharacterEllipsis,
            },
            IsHitTestVisible = false,
        };
        menu.Items.Add(header);
        menu.Items.Add(new Separator());

        bool isDir = node is DirNode;
        string folderPath = node is FileNode { IsAggregate: true } ? node.Parent!.FullPath : node.FullPath;

        if (isDir && node != Chart.CenterNode)
            menu.Items.Add(Item("\uE71E", "Zoom into folder", "Click", () => Nav?.ZoomTo((DirNode)node)));
        if (!Demo.On) menu.Items.Add(Item("\uE838", "Open in Explorer", "Ctrl+E", () => OpenInExplorer(node)));
        menu.Items.Add(Item("\uE8C8", "Copy path", "Ctrl+C", () => CopyPath(folderPath)));
        if (isDir && ActiveSession?.State == ScanState.Done)
            menu.Items.Add(Item("\uE72C", node.Parent == null ? "Rescan drive" : "Rescan this folder only", node.Parent == null ? "F5" : "",
                () => RescanFolder((DirNode)node)));
        if (allowDelete && !Demo.On && ActiveSession is { } s)
        {
            var block = DeleteSafety.CheckTree(node, s.Tree, s.State != ScanState.Done);
            if (block is not (DeleteBlock.DriveRoot or DeleteBlock.Synthetic or DeleteBlock.NotInTree))
            {
                menu.Items.Add(new Separator());
                var del = Item("\uE74D", node.IsDirectory ? "Delete folder…" : "Delete file…", "", () => DeleteNode(node, s));
                if (block != DeleteBlock.None)
                {
                    del.IsEnabled = false;
                    del.ToolTip = DeleteSafety.Explain(block);
                    ToolTipService.SetShowOnDisabled(del, true);
                }
                menu.Items.Add(del);
            }
        }
        return menu;
    }

    /// <summary>
    /// Delete: checks, then the confirmation with the full path, then the Recycle Bin for exactly that path. Every
    /// check runs again right before the Recycle Bin call (see <see cref="Deleter"/>).
    /// </summary>
    private async void DeleteNode(Node node, DriveSession s)
    {
        if (Demo.On) return; // demo paths may exist on the real disk
        DeletePlan? plan;
        DeleteBlock block;
        var tree = s.Tree;
        bool running = s.State != ScanState.Done;
        Mouse.OverrideCursor = Cursors.Wait;
        try { (plan, block) = await Task.Run(() => DeleteSafety.Plan(node, tree, running)); }
        finally { Mouse.OverrideCursor = null; }
        if (plan == null)
        {
            MessageBox.Show(this, DeleteSafety.Explain(block), "Can't delete", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dlg = new ConfirmDeleteWindow(plan) { Owner = this };
        if (dlg.ShowDialog() != true || dlg.ConfirmedPath == null) return;

        DeleteResult result;
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            var deleter = new Deleter(new ShellRecycler(new WindowInteropHelper(this).Handle));
            result = deleter.Execute(plan, dlg.ConfirmedPath, s.Tree, s.State != ScanState.Done, _scans.SyncRoot);
        }
        finally { Mouse.OverrideCursor = null; }

        if (result.Outcome == DeleteOutcome.Deleted)
        {
            // The chart is redrawn only after this, so it goes straight from before to after: the deleted item now
            // counts in the Recycle Bin's folder, as a rescan would show it.
            Mouse.OverrideCursor = Cursors.Wait;
            try { await _scans.AfterDeleteAsync(s.Meta.Root); }
            catch (Exception) { /* the delete itself worked; a rescan puts the numbers right */ }
            finally { Mouse.OverrideCursor = null; }
            // Groups may name the deleted file: stop any search for this drive and search again (fast, from the hash cache).
            if (_dups.TryGetValue(s.Meta.Root, out var dupState)) dupState.Running?.Cancel();
            _dups.Remove(s.Meta.Root);
            Chart.SelectedGroup = null;
            EnsureDuplicates();
            UpdateAll();
        }
        else
            MessageBox.Show(this, result.Message, "Not deleted", MessageBoxButton.OK,
                result.Outcome == DeleteOutcome.Failed ? MessageBoxImage.Warning : MessageBoxImage.Information);
    }

    private static MenuItem Item(string glyph, string text, string gesture, Action action)
    {
        var mi = new MenuItem
        {
            Header = text,
            InputGestureText = gesture,
            Icon = new TextBlock
            {
                Text = glyph,
                FontFamily = (System.Windows.Media.FontFamily)Application.Current.FindResource("IconFont"),
                FontSize = 14,
                Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("TextDim"),
            },
        };
        mi.Click += (_, _) => action();
        return mi;
    }

    private static void OpenInExplorer(Node node)
    {
        if (Demo.On) return;
        try
        {
            if (node is FileNode { IsAggregate: true } agg)
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{agg.Parent!.FullPath}\"") { UseShellExecute = true });
            else if (node is FileNode)
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{node.FullPath}\"") { UseShellExecute = true });
            else
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{node.FullPath}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Couldn't open Explorer", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static void CopyPath(string path)
    {
        try { Clipboard.SetText(path); } catch (COMException) { /* clipboard busy; ignore */ }
    }

    private async void RescanFolder(DirNode node)
    {
        if (_active == null) return;
        if (node.Parent == null) { Rescan(); return; }
        ScanInfo.Text = $"Rescanning {node.FullPath}…";
        await _scans.RescanFolderAsync(_active, node);
        UpdateAll();
    }

    // ======================= Commands =======================

    private void Rescan()
    {
        var s = ActiveSession;
        if (s == null) return;
        if (s.State == ScanState.Scanning) _scans.Stop(s.Meta.Root);
        else _scans.Rescan(s.Meta.Root, Nav?.Path);
        UpdateAll();
    }

    private void Rescan_Click(object sender, RoutedEventArgs e) => Rescan();
    private void Back_Click(object sender, RoutedEventArgs e) => Nav?.Back();
    private void Forward_Click(object sender, RoutedEventArgs e) => Nav?.Forward();
    private void Up_Click(object sender, RoutedEventArgs e) => Nav?.Up();

    private void Card_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: DriveCard card } && card.Root != _active) Activate(card.Root);
    }

    private void CardRescan_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement { DataContext: DriveCard card })
        {
            if (card.Root != _active) Activate(card.Root);
            else Rescan();
        }
    }

    private void OnWindowMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.XButton1) Nav?.Back();
        else if (e.ChangedButton == MouseButton.XButton2) Nav?.Forward();
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;
        switch (key)
        {
            case Key.F5:
                Rescan();
                break;
            case Key.Back:
            case Key.Up when mods == ModifierKeys.Alt:
                Nav?.Up();
                break;
            case Key.Left when mods == ModifierKeys.Alt:
            case Key.BrowserBack:
                Nav?.Back();
                break;
            case Key.Right when mods == ModifierKeys.Alt:
            case Key.BrowserForward:
                Nav?.Forward();
                break;
            case Key.E when mods == ModifierKeys.Control:
            {
                var target = Chart.Hover?.Node ?? Chart.CenterNode;
                if (target != null) OpenInExplorer(target);
                break;
            }
            case Key.C when mods == ModifierKeys.Control:
            {
                var n = Chart.Hover?.Node ?? Chart.CenterNode;
                if (n != null) CopyPath(n is FileNode { IsAggregate: true } ? n.Parent!.FullPath : n.FullPath);
                break;
            }
            case >= Key.A and <= Key.Z when mods == ModifierKeys.None:
                string root = $"{(char)('A' + (key - Key.A))}:\\";
                if (_cards.Any(c => string.Equals(c.Root, root, StringComparison.OrdinalIgnoreCase))) Activate(root);
                else return;
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    /// <summary>Hides the shortcut hints when the window is too narrow for them and the scan info together.</summary>
    private void AnimToggle_Click(object sender, RoutedEventArgs e)
    {
        var all = Enum.GetValues<ScanAnimation>();
        AppSettings.Animation = all[(Array.IndexOf(all, AppSettings.Animation) + 1) % all.Length];
        AppSettings.Save();
        ApplyAnimationSetting();
    }

    private void ApplyAnimationSetting()
    {
        Chart.AnimationStyle = AppSettings.Animation;
        AnimToggle.Content = $"Animation: {AppSettings.Animation.ToString().ToLowerInvariant()}";
        Chart.Colors = AppSettings.Colors;
        ColorToggle.Content = AppSettings.Colors switch
        {
            ChartColors.Size => "Colour: size",
            ChartColors.View => "Colour: view",
            _ => "Colour: folders",
        };
        Chart.RingStyle = AppSettings.Rings;
        RingToggle.Content = AppSettings.Rings == RingStyle.Even ? "Rings: even" : "Rings: thin";
        FreeToggle.Content = AppSettings.ShowFree ? "Free space: shown" : "Free space: hidden";
        if (IsLoaded) UpdateView();
        FitFooter();
        // Again once the new labels have been laid out.
        Dispatcher.BeginInvoke(new Action(FitFooter), DispatcherPriority.Loaded);
    }

    private void ColorToggle_Click(object sender, RoutedEventArgs e)
    {
        var all = Enum.GetValues<ChartColors>();
        AppSettings.Colors = all[(Array.IndexOf(all, AppSettings.Colors) + 1) % all.Length];
        AppSettings.Save();
        ApplyAnimationSetting();
    }

    private void FreeToggle_Click(object sender, RoutedEventArgs e)
    {
        AppSettings.ShowFree = !AppSettings.ShowFree;
        AppSettings.Save();
        ApplyAnimationSetting();
    }

    private void RingToggle_Click(object sender, RoutedEventArgs e)
    {
        AppSettings.Rings = AppSettings.Rings == RingStyle.Even ? RingStyle.Thinning : RingStyle.Even;
        AppSettings.Save();
        ApplyAnimationSetting();
    }

    private void Footer_SizeChanged(object sender, SizeChangedEventArgs e) => FitFooter();

    private void FitFooter()
    {
        Hints.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        ScanInfo.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Toggles.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        bool fits = Hints.DesiredSize.Width + ScanInfo.DesiredSize.Width + Toggles.DesiredSize.Width + 48 <= Footer.ActualWidth;
        Hints.Visibility = fits ? Visibility.Visible : Visibility.Hidden;
    }

    // ======================= Window chrome =======================

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>Dark title bar to match the dark-only theme (Windows 10 20H1+ and 11).</summary>
    private void UseDarkTitleBar()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        int on = 1;
        if (DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int)) != 0)
            DwmSetWindowAttribute(hwnd, 19, ref on, sizeof(int));
        int caption = 0x001D1816; // COLORREF (BGR) of #16181D
        DwmSetWindowAttribute(hwnd, 35, ref caption, sizeof(int));
    }
}
