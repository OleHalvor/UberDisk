using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using UberDisk.Core.Deleting;
using UberDisk.Core.Viz;

namespace UberDisk.App;

/// <summary>
/// "Are you sure?" for Delete. Shows the full path, kind and size. Cancel has the focus and is what Enter and Esc
/// do, so only a deliberate click on "Move to Recycle Bin" confirms.
/// </summary>
public sealed class ConfirmDeleteWindow : Window
{
    private readonly TextBox _path;

    /// <summary>The path that was on screen when the user confirmed. The delete is refused unless it equals the plan's path.</summary>
    public string? ConfirmedPath { get; private set; }

    public ConfirmDeleteWindow(DeletePlan plan)
    {
        Title = "Delete";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = (Brush)FindResource("Bg");
        Foreground = (Brush)FindResource("Text");
        FontFamily = (FontFamily)FindResource("UiFont");
        FontSize = 13.5;

        var root = new StackPanel { Margin = new Thickness(22, 18, 22, 18) };
        root.Children.Add(new TextBlock
        {
            Text = plan.IsDirectory ? "Move this folder to the Recycle Bin?" : "Move this file to the Recycle Bin?",
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 12),
        });

        _path = new TextBox
        {
            Text = plan.Path,
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 13,
            Background = (Brush)FindResource("CardBg"),
            Foreground = (Brush)FindResource("Text"),
            BorderBrush = (Brush)FindResource("CardBorder"),
            Padding = new Thickness(8, 6, 8, 6),
            Focusable = true,
        };
        root.Children.Add(_path);

        string what = plan.IsDirectory
            ? $"Folder · {Format.Bytes(plan.Size)} · {plan.FileCount:N0} files in {plan.DirCount:N0} folders"
            : $"File · {Format.Bytes(plan.Size)}";
        root.Children.Add(new TextBlock { Text = what, Margin = new Thickness(0, 10, 0, 0), Foreground = (Brush)FindResource("Text") });
        root.Children.Add(new TextBlock
        {
            Text = "You can restore it from the Recycle Bin. If it's too big for the Recycle Bin, Windows asks before deleting it for good.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)FindResource("TextDim"),
            Margin = new Thickness(0, 6, 0, 0),
        });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        var delete = new Button
        {
            Content = "Move to Recycle Bin",
            Style = (Style)FindResource("OutlineButton"),
            Focusable = true,
            Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x8A, 0x8A)),
            BorderBrush = (Brush)FindResource("BarRed"),
            Margin = new Thickness(0, 0, 10, 0),
        };
        var cancel = new Button
        {
            Content = "Cancel",
            Style = (Style)FindResource("OutlineButton"),
            Focusable = true,
            IsDefault = true,
            IsCancel = true,
            MinWidth = 90,
        };
        delete.Click += (_, _) => { ConfirmedPath = _path.Text; DialogResult = true; };
        cancel.Click += (_, _) => DialogResult = false;
        buttons.Children.Add(delete);
        buttons.Children.Add(cancel);
        root.Children.Add(buttons);
        Content = root;

        Loaded += (_, _) => cancel.Focus();
        SourceInitialized += (_, _) => DarkTitleBar();
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private void DarkTitleBar()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        int on = 1;
        if (DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int)) != 0)
            DwmSetWindowAttribute(hwnd, 19, ref on, sizeof(int));
    }
}
