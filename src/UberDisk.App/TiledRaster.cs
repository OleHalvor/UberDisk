using SkiaSharp;

namespace UberDisk.App;

/// <summary>
/// Draws into a pixel buffer on several cores. The drawing is first recorded (cheap, one thread), then the
/// recording is played back into horizontal bands of the buffer at the same time. Skia draws on the CPU, so on a
/// big window most of a frame is filling pixels: at 3600 x 2000 this takes a frame from about 6.6 ms to 2 ms.
/// No WPF here, so the tools can compare it with a plain one-thread draw.
/// </summary>
public static class TiledRaster
{
    /// <summary>Below this many pixels one thread is as quick (starting the bands costs about as much as it saves).</summary>
    public const int MinPixels = 1_000_000;

    private const int MaxBands = 8;

    public static void Draw(IntPtr pixels, int stride, int width, int height, Action<SKCanvas> paint)
    {
        int bands = (long)width * height < MinPixels ? 1 : Math.Clamp(Environment.ProcessorCount / 2, 1, MaxBands);
        if (bands == 1)
        {
            DrawDirect(pixels, stride, width, height, paint);
            return;
        }

        using var recorder = new SKPictureRecorder();
        paint(recorder.BeginRecording(new SKRect(0, 0, width, height)));
        using var picture = recorder.EndRecording();
        Parallel.For(0, bands, new ParallelOptions { MaxDegreeOfParallelism = bands }, band =>
        {
            int top = (int)((long)height * band / bands), bottom = (int)((long)height * (band + 1) / bands);
            if (bottom <= top) return;
            // Each band draws through a clip on a surface over the whole buffer, not on a surface of its own: Skia then
            // rasterises every edge exactly as a one-thread draw would (a band-sized surface changes the anti-aliasing).
            var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var surface = SKSurface.Create(info, pixels, stride);
            surface.Canvas.ClipRect(new SKRect(0, top, width, bottom));
            surface.Canvas.DrawPicture(picture);
            surface.Canvas.Flush();
        });
    }

    /// <summary>The plain one-thread draw.</summary>
    public static void DrawDirect(IntPtr pixels, int stride, int width, int height, Action<SKCanvas> paint)
    {
        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info, pixels, stride);
        paint(surface.Canvas);
        surface.Canvas.Flush();
    }
}
