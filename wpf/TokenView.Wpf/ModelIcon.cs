using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Media;

namespace TokenView.Wpf;

/// <summary>模型品牌图标；无法识别时使用通用 CPU 图标。</summary>
public sealed class ModelIcon : FrameworkElement
{
    public static readonly DependencyProperty ModelProperty = DependencyProperty.Register(
        nameof(Model), typeof(string), typeof(ModelIcon),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IconSizeProperty = DependencyProperty.Register(
        nameof(IconSize), typeof(double), typeof(ModelIcon),
        new FrameworkPropertyMetadata(14d,
            FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IconBrushProperty = DependencyProperty.Register(
        nameof(IconBrush), typeof(Brush), typeof(ModelIcon),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public string Model
    {
        get => (string)GetValue(ModelProperty);
        set => SetValue(ModelProperty, value);
    }

    public double IconSize
    {
        get => (double)GetValue(IconSizeProperty);
        set => SetValue(IconSizeProperty, value);
    }

    public Brush IconBrush
    {
        get => (Brush)GetValue(IconBrushProperty);
        set => SetValue(IconBrushProperty, value);
    }

    // 与 ClientIcon 同理：模型品牌几何按 kind 只解析一次。
    private static readonly ConcurrentDictionary<string, Geometry[]> Geometries = new(StringComparer.Ordinal);

    protected override Size MeasureOverride(Size availableSize) => new(IconSize, IconSize);

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (IconSize <= 0) return;
        var kind = ModelIconData.KindOf(Model);
        if (!ModelIconData.Icons.TryGetValue(kind, out var icon)) return;
        var geometries = Geometries.GetOrAdd(kind, _ => Build(icon));
        var scale = IconSize / 24;
        drawingContext.PushTransform(new ScaleTransform(scale, scale));
        if (icon.Stroke)
        {
            var pen = new Pen(IconBrush, 2)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round,
            };
            foreach (var geometry in geometries)
            {
                drawingContext.DrawGeometry(null, pen, geometry);
            }
        }
        else
        {
            foreach (var geometry in geometries)
            {
                drawingContext.DrawGeometry(IconBrush, null, geometry);
            }
        }
        drawingContext.Pop();
    }

    private static Geometry[] Build(ModelIconData.IconPaths icon)
    {
        var result = new Geometry[icon.Paths.Count];
        for (var index = 0; index < result.Length; index++)
        {
            Geometry geometry = icon.Stroke
                ? Geometry.Parse(icon.Paths[index])
                : PathGeometry.CreateFromGeometry(Geometry.Parse(icon.Paths[index]));
            if (!icon.Stroke && geometry is PathGeometry path)
            {
                path.FillRule = FillRule.Nonzero;
            }
            geometry.Freeze();
            result[index] = geometry;
        }
        return result;
    }
}
