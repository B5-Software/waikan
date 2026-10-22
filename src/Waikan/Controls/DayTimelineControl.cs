using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Waikan.Controls;

public sealed class DayTimelineControl : FrameworkElement
{
    public static readonly DependencyProperty TimesProperty = DependencyProperty.Register(
        nameof(Times),
        typeof(IEnumerable<DateTime>),
        typeof(DayTimelineControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LineBrushProperty = DependencyProperty.Register(
        nameof(LineBrush),
        typeof(Brush),
        typeof(DayTimelineControl),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TickBrushProperty = DependencyProperty.Register(
        nameof(TickBrush),
        typeof(Brush),
        typeof(DayTimelineControl),
        new FrameworkPropertyMetadata(Brushes.DimGray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MarkerBrushProperty = DependencyProperty.Register(
        nameof(MarkerBrush),
        typeof(Brush),
        typeof(DayTimelineControl),
        new FrameworkPropertyMetadata(Brushes.OrangeRed, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LabelBrushProperty = DependencyProperty.Register(
        nameof(LabelBrush),
        typeof(Brush),
        typeof(DayTimelineControl),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public IEnumerable<DateTime>? Times
    {
        get => (IEnumerable<DateTime>?)GetValue(TimesProperty);
        set => SetValue(TimesProperty, value);
    }

    public Brush LineBrush
    {
        get => (Brush)GetValue(LineBrushProperty);
        set => SetValue(LineBrushProperty, value);
    }

    public Brush TickBrush
    {
        get => (Brush)GetValue(TickBrushProperty);
        set => SetValue(TickBrushProperty, value);
    }

    public Brush MarkerBrush
    {
        get => (Brush)GetValue(MarkerBrushProperty);
        set => SetValue(MarkerBrushProperty, value);
    }

    public Brush LabelBrush
    {
        get => (Brush)GetValue(LabelBrushProperty);
        set => SetValue(LabelBrushProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        var width = ActualWidth;
        var height = ActualHeight;
        if (width < 30 || height < 20)
        {
            return;
        }

        var left = 4.0;
        var right = width - 4;
        var lineY = Math.Max(14, height - 21);
        var linePen = new Pen(LineBrush, 1);
        linePen.Freeze();
        drawingContext.DrawLine(linePen, new Point(left, lineY), new Point(right, lineY));

        var tickPen = new Pen(TickBrush, 1);
        tickPen.Freeze();
        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        for (var hour = 0; hour <= 24; hour += 3)
        {
            var x = left + (right - left) * hour / 24.0;
            drawingContext.DrawLine(tickPen, new Point(x, lineY - 3), new Point(x, lineY + 3));
            if (hour % 6 == 0)
            {
                DrawText(drawingContext, hour.ToString(CultureInfo.InvariantCulture), x, lineY + 6, 9.5, LabelBrush, pixelsPerDip);
            }
        }

        var times = Times?.Where(time => time.Date == DateTime.Today).ToList();
        if (times is null || times.Count == 0)
        {
            return;
        }

        var markerBrush = MarkerBrush.Clone();
        markerBrush.Freeze();
        var baseAlpha = markerBrush is SolidColorBrush solid ? solid.Color.A : (byte)255;
        var markerPen = new Pen(markerBrush, 2)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
        };
        markerPen.Freeze();

        var glowBrush = markerBrush.Clone();
        if (glowBrush is SolidColorBrush glowSolid)
        {
            var color = glowSolid.Color;
            glowBrush = new SolidColorBrush(Color.FromArgb((byte)Math.Min(70, (int)baseAlpha), color.R, color.G, color.B));
            glowBrush.Freeze();
        }

        foreach (var time in times)
        {
            var fraction = (time.Hour * 3600 + time.Minute * 60 + time.Second) / 86400.0;
            var x = left + (right - left) * Math.Clamp(fraction, 0, 1);
            drawingContext.DrawEllipse(glowBrush, null, new Point(x, lineY - 10), 5.5, 5.5);
            drawingContext.DrawLine(markerPen, new Point(x, lineY - 18), new Point(x, lineY - 4));
        }
    }

    private static void DrawText(
        DrawingContext context,
        string text,
        double centerX,
        double top,
        double fontSize,
        Brush brush,
        double pixelsPerDip)
    {
        var formatted = new FormattedText(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI Variable Text"),
            fontSize,
            brush,
            pixelsPerDip);
        context.DrawText(formatted, new Point(centerX - formatted.Width / 2, top));
    }
}
