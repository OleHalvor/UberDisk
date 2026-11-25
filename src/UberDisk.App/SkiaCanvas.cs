using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkiaSharp;

namespace UberDisk.App;

/// <summary>
/// Minimal WPF host for SkiaSharp drawing: renders into a WriteableBitmap at device pixels (so it's crisp
/// on high-DPI screens) and shows it. Replaces SkiaSharp.Views.WPF, whose WPF build for .NET 8 drags in
/// .NET Framework assets and OpenTK.
/// </summary>
public abstract class SkiaCanvas : FrameworkElement
{
    private WriteableBitmap? _bitmap;

    /// <summary>Draw here. The canvas is in device pixels; <paramref name="scale"/> converts DIPs to pixels.</summary>
    protected abstract void OnPaint(SKCanvas canvas, SKImageInfo info, float scale);

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        var dpi = VisualTreeHelper.GetDpi(this);
        int pw = Math.Max(1, (int)Math.Round(w * dpi.DpiScaleX));
        int ph = Math.Max(1, (int)Math.Round(h * dpi.DpiScaleY));

        if (_bitmap == null || _bitmap.PixelWidth != pw || _bitmap.PixelHeight != ph)
            _bitmap = new WriteableBitmap(pw, ph, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32, null);

        var info = new SKImageInfo(pw, ph, SKColorType.Bgra8888, SKAlphaType.Premul);
        _bitmap.Lock();
        try
        {
            float scale = (float)dpi.DpiScaleX;
            // On several cores when the window is big (see TiledRaster).
            TiledRaster.Draw(_bitmap.BackBuffer, _bitmap.BackBufferStride, pw, ph, canvas => OnPaint(canvas, info, scale));
            _bitmap.AddDirtyRect(new Int32Rect(0, 0, pw, ph));
        }
        finally
        {
            _bitmap.Unlock();
        }
        dc.DrawImage(_bitmap, new Rect(0, 0, w, h));
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        InvalidateVisual();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        InvalidateVisual();
    }
}
