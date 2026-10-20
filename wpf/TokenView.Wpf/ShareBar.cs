using System.Windows;
using System.Windows.Media;

namespace TokenView.Wpf;

/// <summary>按比例填充的细进度条；避免原生 ProgressBar 模板在不同主题下的指示器宽度问题。</summary>
public sealed class ShareBar : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(ShareBar),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush), typeof(Brush), typeof(ShareBar),
        new FrameworkPropertyMetadata(Brushes.Gainsboro, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BarBrushProperty = DependencyProperty.Register(
        nameof(BarBrush), typeof(Brush), typeof(ShareBar),
        new FrameworkPropertyMetadata(Brushes.Teal, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public Brush TrackBrush
    {
        get => (Brush)GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public Brush BarBrush
    {
        get => (Brush)GetValue(BarBrushProperty);
        set => SetValue(BarBrushProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? 120 : availableSize.Width;
        return new Size(width, 6);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        var width = Math.Max(0, ActualWidth);
        var height = Math.Max(2, ActualHeight);
        if (width <= 0) return;
        var radius = height / 2;
        drawingContext.DrawRoundedRectangle(TrackBrush, null,
            new Rect(0, 0, width, height), radius, radius);
        var fill = Math.Clamp(Value, 0, 1) * width;
        if (fill <= 0) return;
        drawingContext.DrawRoundedRectangle(BarBrush, null,
            new Rect(0, 0, fill, height), radius, radius);
    }
}
