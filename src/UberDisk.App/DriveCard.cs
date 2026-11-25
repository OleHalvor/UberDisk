using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using UberDisk.Core.Drives;
using UberDisk.Core.Viz;

namespace UberDisk.App;

/// <summary>One card in the drive rail.</summary>
public sealed class DriveCard : INotifyPropertyChanged
{
    private const double BarWidth = 194; // card width minus padding

    private static readonly Brush Blue = Frozen(0x4C, 0x8D, 0xF6);
    private static readonly Brush Orange = Frozen(0xF2, 0x8C, 0x28);
    private static readonly Brush Red = Frozen(0xE5, 0x48, 0x4D);

    public DriveCard(string root) => Root = root;

    public string Root { get; }

    public string Letter { get; private set; } = "";
    public string Label { get; private set; } = "";
    public string KindText { get; private set; } = "";
    public string StatusText { get; private set; } = "";
    public string UsedText { get; private set; } = "";
    public string FreeText { get; private set; } = "";
    public string Tooltip { get; private set; } = "";
    public double FillWidth { get; private set; }
    public Brush FillBrush { get; private set; } = Blue;
    public bool IsActive { get; private set; }
    public bool IsScanning { get; private set; }
    public bool IsReady { get; private set; }

    public void Update(DriveSession s, bool active, DateTime now)
    {
        var m = s.Meta;
        Set(nameof(Letter), Letter, m.Letter, v => Letter = v);
        Set(nameof(Label), Label, m.Label, v => Label = v);
        Set(nameof(KindText), KindText, m.KindText, v => KindText = v);
        Set(nameof(IsActive), IsActive, active, v => IsActive = v);
        Set(nameof(IsScanning), IsScanning, s.State == ScanState.Scanning, v => IsScanning = v);
        Set(nameof(IsReady), IsReady, m.IsReady, v => IsReady = v);

        string status = s.State switch
        {
            ScanState.Scanning when s.Job?.IsPaused == true => $"waiting… {s.Progress * 100:0} %",
            ScanState.Scanning => $"scanning… {s.Progress * 100:0} %",
            ScanState.Queued => "queued",
            ScanState.Done when s.ScannedAt is { } at => "scanned " + Format.Ago(at, now),
            ScanState.Stopped => "scan stopped",
            _ => m.IsReady ? "not scanned" : "not ready",
        };
        Set(nameof(StatusText), StatusText, status, v => StatusText = v);

        double fill = m.TotalBytes > 0 ? (double)m.UsedBytes / m.TotalBytes : 0;
        Set(nameof(FillWidth), FillWidth, Math.Round(BarWidth * fill), v => FillWidth = v);
        var brush = fill >= 0.95 ? Red : fill >= 0.85 ? Orange : Blue;
        Set(nameof(FillBrush), FillBrush, brush, v => FillBrush = v);
        Set(nameof(UsedText), UsedText, m.IsReady ? Format.Bytes(m.UsedBytes) + " used" : "", v => UsedText = v);
        Set(nameof(FreeText), FreeText, m.IsReady ? Format.Bytes(m.FreeBytes) + " free" : "", v => FreeText = v);

        string tip = $"{m.Root}  {m.Label}\n{Format.Bytes(m.TotalBytes)} total";
        if (s.State == ScanState.Done && s.Tree != null)
            tip += $"\nLast scan took {Format.Duration(s.LastScanDuration)} · {Format.Count(s.Tree.FileCount)} files";
        Set(nameof(Tooltip), Tooltip, tip, v => Tooltip = v);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(string name, T current, T value, Action<T> assign)
    {
        if (EqualityComparer<T>.Default.Equals(current, value)) return;
        assign(value);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var br = new SolidColorBrush(Color.FromRgb(r, g, b));
        br.Freeze();
        return br;
    }
}
