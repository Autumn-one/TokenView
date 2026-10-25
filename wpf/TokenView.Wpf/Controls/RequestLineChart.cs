using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using TokenView.Wpf.Data;

namespace TokenView.Wpf.Controls;

public sealed class RequestLineChart : FrameworkElement
{
    private static readonly Brush DefaultGridBrush = new SolidColorBrush(Color.FromRgb(222, 229, 237));
    private static readonly Brush DefaultMutedBrush = new SolidColorBrush(Color.FromRgb(104, 117, 137));
    private static readonly Typeface Font = new(new FontFamily("Microsoft YaHei UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    private int _hoverIndex = -1;

    public int HoverIndex => _hoverIndex;

    static RequestLineChart()
    {
        DefaultGridBrush.Freeze();
        DefaultMutedBrush.Freeze();
    }

    public RequestLineChart()
    {
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        Cursor = Cursors.Cross;
        Focusable = true;
        MinHeight = 200;
    }

    public static readonly DependencyProperty PointsProperty = DependencyProperty.Register(
        nameof(Points), typeof(IReadOnlyList<ChartPoint>), typeof(RequestLineChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender,
            (owner, _) => ((RequestLineChart)owner)._hoverIndex = -1));

    public static readonly DependencyProperty MetricProperty = DependencyProperty.Register(
        nameof(Metric), typeof(ChartMetric), typeof(RequestLineChart),
        new FrameworkPropertyMetadata(ChartMetric.Tokens, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(
        nameof(Accent), typeof(Brush), typeof(RequestLineChart),
        new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromRgb(9, 159, 131)),
            FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty GridBrushProperty = DependencyProperty.Register(
        nameof(GridBrush), typeof(Brush), typeof(RequestLineChart),
        new FrameworkPropertyMetadata(DefaultGridBrush, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MutedBrushProperty = DependencyProperty.Register(
        nameof(MutedBrush), typeof(Brush), typeof(RequestLineChart),
        new FrameworkPropertyMetadata(DefaultMutedBrush, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<ChartPoint>? Points
    {
        get => (IReadOnlyList<ChartPoint>?)GetValue(PointsProperty);
        set => SetValue(PointsProperty, value);
    }

    public ChartMetric Metric
    {
        get => (ChartMetric)GetValue(MetricProperty);
        set => SetValue(MetricProperty, value);
    }

    public Brush Accent
    {
        get => (Brush)GetValue(AccentProperty);
        set => SetValue(AccentProperty, value);
    }

    public Brush GridBrush
    {
        get => (Brush)GetValue(GridBrushProperty);
        set => SetValue(GridBrushProperty, value);
    }

    public Brush MutedBrush
    {
        get => (Brush)GetValue(MutedBrushProperty);
        set => SetValue(MutedBrushProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? 760 : availableSize.Width;
        return new Size(width, 240);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        var points = Points;
        if (points is null || points.Count == 0)
        {
            DrawText(drawingContext, "暂无数据", 16, MutedBrush, new Point(24, 24));
            return;
        }

        var width = Math.Max(ActualWidth, 120);
        var height = Math.Max(ActualHeight, 120);
        // A transparent fill makes the whole control hit-testable, including
        // empty plot areas, so the hover tooltip works away from the line.
        drawingContext.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, height));
        var plot = new Rect(64, 14, Math.Max(10, width - 86), Math.Max(10, height - 48));
        var values = points.Select(Value).ToArray();
        var maxValue = values.Where(value => value.HasValue).Select(value => value!.Value).DefaultIfEmpty(0).Max();
        var niceMax = NiceMax(maxValue);
        var accent = Accent;

        var gridPen = new Pen(GridBrush, 1) { DashStyle = new DashStyle([4, 4], 0) };
        gridPen.Freeze();
        var axisPen = new Pen(GridBrush, 1);
        axisPen.Freeze();
        var linePen = new Pen(accent, 2)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };
        linePen.Freeze();

        for (var index = 0; index <= 4; index++)
        {
            var fraction = index / 4d;
            var y = plot.Bottom - plot.Height * fraction;
            drawingContext.DrawLine(index == 0 ? axisPen : gridPen, new Point(plot.Left, y), new Point(plot.Right, y));
            var label = Metric == ChartMetric.Tokens
                ? TokenViewDatabase.FormatTokens((long)Math.Round(niceMax * fraction))
                : FormatNumber((long)Math.Round(niceMax * fraction));
            DrawText(drawingContext, label, 10.5, MutedBrush, new Point(4, y - 8));
        }

        if (points.Count == 1)
        {
            var value = values[0];
            if (value.HasValue)
            {
                var y = Y(plot, value.Value, niceMax);
                drawingContext.DrawEllipse(accent, null, new Point(plot.Left + plot.Width / 2, y), 4, 4);
            }
        }
        else
        {
            var geometry = new StreamGeometry();
            using (var context = geometry.Open())
            {
                var inFigure = false;
                for (var index = 0; index < points.Count; index++)
                {
                    if (!values[index].HasValue)
                    {
                        inFigure = false;
                        continue;
                    }
                    var point = new Point(X(plot, index, points.Count), Y(plot, values[index]!.Value, niceMax));
                    if (!inFigure)
                    {
                        context.BeginFigure(point, false, false);
                        inFigure = true;
                    }
                    else
                    {
                        context.LineTo(point, true, false);
                    }
                }
            }
            geometry.Freeze();
            drawingContext.DrawGeometry(null, linePen, geometry);
        }

        var tickStep = Math.Max(1, (int)Math.Ceiling(points.Count / 12d));
        for (var index = 0; index < points.Count; index += tickStep)
        {
            var x = points.Count == 1 ? plot.Left + plot.Width / 2 : X(plot, index, points.Count);
            DrawText(drawingContext, points[index].Label, 10.5, MutedBrush,
                new Point(x - 18, plot.Bottom + 8));
        }

        if (_hoverIndex >= 0 && _hoverIndex < points.Count)
        {
            // 日期定位标记与用量值无关，未知用量也能定位，不能画到折线上伪造零值。
            drawingContext.DrawEllipse(accent, null,
                new Point(X(plot, _hoverIndex, points.Count), plot.Bottom), 3.5, 3.5);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var points = Points;
        if (points is null || points.Count == 0)
        {
            return;
        }
        var width = Math.Max(ActualWidth, 120);
        var plot = new Rect(64, 14, Math.Max(10, width - 86), Math.Max(10, ActualHeight - 48));
        var position = e.GetPosition(this);
        var index = points.Count == 1
            ? 0
            : (int)Math.Round((position.X - plot.Left) / plot.Width * (points.Count - 1));
        index = Math.Clamp(index, 0, points.Count - 1);
        if (index != _hoverIndex)
        {
            _hoverIndex = index;
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hoverIndex >= 0)
        {
            _hoverIndex = -1;
            InvalidateVisual();
        }
    }

    private double? Value(ChartPoint point) => Metric == ChartMetric.Tokens
        ? point.Requests > 0 && point.RequestsWithoutUsage >= point.Requests
            ? null
            : point.Tokens
        : point.Requests;

    private static double X(Rect plot, int index, int count) =>
        count <= 1 ? plot.Left + plot.Width / 2 : plot.Left + plot.Width * index / (count - 1);

    private static double Y(Rect plot, double value, double max) =>
        plot.Bottom - plot.Height * Math.Clamp(value / max, 0, 1);

    private static double NiceMax(double value)
    {
        if (value <= 0)
        {
            return 1;
        }
        // Match the current four-interval axis: choose a step from a compact
        // set of "readable" fractions so a 2.22B peak ends at 2.40B, not 5B.
        var rawStep = value / 4;
        var exponent = Math.Pow(10, Math.Floor(Math.Log10(rawStep)));
        var fraction = rawStep / exponent;
        double[] fractions = [1, 1.25, 1.5, 2, 2.5, 3, 4, 5, 6, 8, 10];
        var nice = fractions.FirstOrDefault(candidate => candidate >= fraction, 10);
        return nice * exponent * 4;
    }

    private static string FormatNumber(long value) => value.ToString("N0", CultureInfo.CurrentCulture);

    private void DrawText(DrawingContext context, string text, double size, Brush brush, Point origin)
    {
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            Font, size, brush, dpi);
        context.DrawText(formatted, origin);
    }
}
