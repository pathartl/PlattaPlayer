using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace PlattaPlayer.App.Controls;

/// <summary>
/// Draws a design-system icon: a geometry on a <see cref="GridSize"/>-unit grid (24 for line icons, 10 for
/// caption glyphs), scaled to <see cref="Size"/> and stroked with round caps/joins in the inherited
/// <see cref="Foreground"/>, like the mockups' inline SVGs. <see cref="IsFilled"/> also fills the shape
/// (transport glyphs). Hover/active colours therefore come for free from the parent button's Foreground.
/// </summary>
public sealed class LineIcon : Control
{
    public static readonly StyledProperty<Geometry?> DataProperty =
        AvaloniaProperty.Register<LineIcon, Geometry?>(nameof(Data));

    public static readonly StyledProperty<double> SizeProperty =
        AvaloniaProperty.Register<LineIcon, double>(nameof(Size), 20d);

    public static readonly StyledProperty<double> GridSizeProperty =
        AvaloniaProperty.Register<LineIcon, double>(nameof(GridSize), 24d);

    /// <summary>Stroke width in grid units (the mockups use 1.6 on the 24 grid).</summary>
    public static readonly StyledProperty<double> StrokeThicknessProperty =
        AvaloniaProperty.Register<LineIcon, double>(nameof(StrokeThickness), 1.6d);

    public static readonly StyledProperty<bool> IsFilledProperty =
        AvaloniaProperty.Register<LineIcon, bool>(nameof(IsFilled));

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<LineIcon>();

    static LineIcon()
    {
        AffectsMeasure<LineIcon>(SizeProperty);
        AffectsRender<LineIcon>(DataProperty, SizeProperty, GridSizeProperty, StrokeThicknessProperty,
            IsFilledProperty, ForegroundProperty);
    }

    public Geometry? Data { get => GetValue(DataProperty); set => SetValue(DataProperty, value); }
    public double Size { get => GetValue(SizeProperty); set => SetValue(SizeProperty, value); }
    public double GridSize { get => GetValue(GridSizeProperty); set => SetValue(GridSizeProperty, value); }
    public double StrokeThickness { get => GetValue(StrokeThicknessProperty); set => SetValue(StrokeThicknessProperty, value); }
    public bool IsFilled { get => GetValue(IsFilledProperty); set => SetValue(IsFilledProperty, value); }
    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    protected override Size MeasureOverride(Size availableSize) => new(Size, Size);

    public override void Render(DrawingContext context)
    {
        if (Data is not { } data || Foreground is not { } brush) return;

        var scale = Size / GridSize;
        var pen = StrokeThickness > 0
            ? new Pen(brush, StrokeThickness, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round)
            : null;

        using (context.PushTransform(Matrix.CreateScale(scale, scale)))
            context.DrawGeometry(IsFilled ? brush : null, pen, data);
    }
}
