using System.Windows;
using System.Windows.Controls;

namespace TokenView.Wpf.Controls;

public sealed class DailyStripPanel : Panel
{
    protected override Size MeasureOverride(Size availableSize)
    {
        var count = InternalChildren.Count;
        if (count == 0)
        {
            return new Size(0, 0);
        }

        var width = double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width;
        var pitch = width / count;
        var height = 0d;
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(pitch, availableSize.Height));
            height = Math.Max(height, child.DesiredSize.Height);
        }

        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var count = InternalChildren.Count;
        if (count == 0)
        {
            return finalSize;
        }

        var pitch = finalSize.Width / count;
        for (var index = 0; index < count; index++)
        {
            InternalChildren[index].Arrange(new Rect(index * pitch, 0, pitch, finalSize.Height));
        }

        return finalSize;
    }
}
