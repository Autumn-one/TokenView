using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Media;

namespace TokenView.Wpf;

/// <summary>客户端品牌图标；品牌路径来自 @lobehub/icons，按 24×24 视口缩放。</summary>
public sealed class ClientIcon : FrameworkElement
{
    public static readonly DependencyProperty ClientKeyProperty = DependencyProperty.Register(
        nameof(ClientKey), typeof(string), typeof(ClientIcon),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IconSizeProperty = DependencyProperty.Register(
        nameof(IconSize), typeof(double), typeof(ClientIcon),
        new FrameworkPropertyMetadata(16d,
            FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IconBrushProperty = DependencyProperty.Register(
        nameof(IconBrush), typeof(Brush), typeof(ClientIcon),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public string ClientKey
    {
        get => (string)GetValue(ClientKeyProperty);
        set => SetValue(ClientKeyProperty, value);
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

    // 图标路径是静态的，但 WPF 每次 OnRender 都会重新解析；按客户端缓存已冻结几何，
    // 滚动列表或重绘时只做变换与绘制。
    private static readonly ConcurrentDictionary<string, Geometry[]> Geometries = new(StringComparer.Ordinal);

    protected override Size MeasureOverride(Size availableSize) => new(IconSize, IconSize);

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (IconSize <= 0 || !ClientIconData.Icons.TryGetValue(ClientKey, out var icon)) return;
        var geometries = Geometries.GetOrAdd(ClientKey, _ => Build(icon));
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

    private static Geometry[] Build(ClientIconData.IconPaths icon)
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
