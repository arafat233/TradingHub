using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using SkiaSharp;

namespace TradingHub.App.Controls;

/// <summary>
/// A control that draws with SkiaSharp. It records the commands into an SKPicture on the UI thread
/// (where the data lives) and the render thread only replays it: no locks and hardware accelerated.
/// </summary>
public abstract class SkiaView : Control
{
    protected SkiaView()
    {
        ClipToBounds = true;
        Focusable = true;
    }

    protected abstract void Draw(SKCanvas canvas, float width, float height);

    /// <summary>Rounded corners for the drawing (0 = square), so the background follows the box containing it.</summary>
    public double CornerRadius { get; set; }

    public override void Render(DrawingContext context)
    {
        float w = (float)Bounds.Width, h = (float)Bounds.Height;
        if (w < 2 || h < 2) return;
        using var recorder = new SKPictureRecorder();
        var canvas = recorder.BeginRecording(new SKRect(0, 0, w, h));
        if (CornerRadius > 0)
        {
            float r = (float)CornerRadius;
            canvas.ClipRoundRect(new SKRoundRect(new SKRect(0, 0, w, h), r, r), SKClipOperation.Intersect, antialias: true);
        }

        try
        {
            Draw(canvas, w, h);
        }
        catch (Exception ex)
        {
            using var p = new SKPaint { Color = SKColors.OrangeRed, IsAntialias = true };
            canvas.DrawText("Error while drawing: " + ex.Message, 10, 20, p);
            CrashLog.Write(ex);
        }

        var picture = recorder.EndRecording();
        context.Custom(new PictureDrawOperation(new Rect(0, 0, w, h), picture));
    }

    private sealed class PictureDrawOperation : ICustomDrawOperation
    {
        private readonly SKPicture _picture;

        public PictureDrawOperation(Rect bounds, SKPicture picture)
        {
            Bounds = bounds;
            _picture = picture;
        }

        public Rect Bounds { get; }

        public void Dispose() => _picture.Dispose();

        public bool Equals(ICustomDrawOperation? other) => false;

        public bool HitTest(Point p) => Bounds.Contains(p);

        public void Render(ImmediateDrawingContext context)
        {
            var feature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
            if (feature == null) return;
            using var lease = feature.Lease();
            lease.SkCanvas.DrawPicture(_picture);
        }
    }
}
