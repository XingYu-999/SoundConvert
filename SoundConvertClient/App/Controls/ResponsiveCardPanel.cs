using Avalonia;
using Avalonia.Controls;

namespace SoundConvertClient.Controls;

/// <summary>Reading-order cards become a single column when the workspace narrows.</summary>
public sealed class ResponsiveCardPanel : Panel
{
    const double Gap = 16;
    protected override Size MeasureOverride(Size availableSize)
    {
        double width = double.IsFinite(availableSize.Width) ? availableSize.Width : 1440;
        int columns = width >= 920 ? 2 : 1;
        double cell = (width - Gap * (columns - 1)) / columns, height = 0;
        for (int i = 0; i < Children.Count; i += columns)
        {
            double row = 0;
            for (int j = i; j < Math.Min(i + columns, Children.Count); j++) { Children[j].Measure(new(cell, double.PositiveInfinity)); row = Math.Max(row, Children[j].DesiredSize.Height); }
            height += row + (i == 0 ? 0 : Gap);
        }
        return new(width, height);
    }
    protected override Size ArrangeOverride(Size finalSize)
    {
        int columns = finalSize.Width >= 920 ? 2 : 1;
        double cell = (finalSize.Width - Gap * (columns - 1)) / columns, y = 0;
        for (int i = 0; i < Children.Count; i += columns)
        {
            double row = Children.Skip(i).Take(columns).Max(c => c.DesiredSize.Height);
            for (int j = i; j < Math.Min(i + columns, Children.Count); j++) Children[j].Arrange(new((j - i) * (cell + Gap), y, cell, row));
            y += row + Gap;
        }
        return finalSize;
    }
}
