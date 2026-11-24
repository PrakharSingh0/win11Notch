using System.Windows;
using System.Windows.Media;

namespace Win11Notch.Views.Controls;

/// <summary>
/// High-performance vector circular progress ring with rounded caps and track.
/// Used by the Timers dashboard dial.
/// </summary>
public sealed class ArcProgressRing : FrameworkElement
{
    public static readonly DependencyProperty ProgressProperty =
        DependencyProperty.Register(nameof(Progress), typeof(double), typeof(ArcProgressRing),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeBrushProperty =
        DependencyProperty.Register(nameof(StrokeBrush), typeof(Brush), typeof(ArcProgressRing),
            new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromRgb(0xFF, 0x5E, 0x36)), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BackgroundBrushProperty =
        DependencyProperty.Register(nameof(BackgroundBrush), typeof(Brush), typeof(ArcProgressRing),
            new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeThicknessProperty =
        DependencyProperty.Register(nameof(StrokeThickness), typeof(double), typeof(ArcProgressRing),
            new FrameworkPropertyMetadata(9.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Progress
    {
        get => (double)GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    public Brush StrokeBrush
    {
        get => (Brush)GetValue(StrokeBrushProperty);
        set => SetValue(StrokeBrushProperty, value);
    }

    public Brush BackgroundBrush
    {
        get => (Brush)GetValue(BackgroundBrushProperty);
        set => SetValue(BackgroundBrushProperty, value);
    }

    public double StrokeThickness
    {
        get => (double)GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        double w = ActualWidth;
        double h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        double stroke = Math.Max(1.0, StrokeThickness);
        double radius = Math.Max(0, Math.Min(w, h) / 2.0 - stroke / 2.0);
        Point center = new(w / 2.0, h / 2.0);

        // 1. Draw outer groove track
        if (BackgroundBrush != null)
        {
            var trackPen = new Pen(BackgroundBrush, stroke)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap   = PenLineCap.Round
            };
            trackPen.Freeze();
            dc.DrawEllipse(null, trackPen, center, radius, radius);
        }

        // 2. Draw active progress arc
        double p = Math.Clamp(Progress, 0.0, 1.0);
        if (p <= 0.001 || StrokeBrush == null) return;

        var arcPen = new Pen(StrokeBrush, stroke)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap   = PenLineCap.Round
        };
        arcPen.Freeze();

        if (p >= 0.999)
        {
            dc.DrawEllipse(null, arcPen, center, radius, radius);
            return;
        }

        double angle = p * 360.0;
        double rad = (angle - 90.0) * Math.PI / 180.0;
        Point startPoint = new(center.X, center.Y - radius);
        Point endPoint   = new(center.X + radius * Math.Cos(rad), center.Y + radius * Math.Sin(rad));
        bool isLargeArc  = angle > 180.0;

        var geom = new StreamGeometry();
        using (var ctx = geom.Open())
        {
            ctx.BeginFigure(startPoint, false, false);
            ctx.ArcTo(endPoint, new Size(radius, radius), 0, isLargeArc, SweepDirection.Clockwise, true, false);
        }
        geom.Freeze();
        dc.DrawGeometry(null, arcPen, geom);
    }
}
