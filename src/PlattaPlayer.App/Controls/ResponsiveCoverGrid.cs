using System;
using System.Collections;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.VisualTree;

namespace PlattaPlayer.App.Controls;

/// <summary>
/// A responsive, virtualizing grid of cover-art tiles built on <see cref="ItemsRepeater"/> +
/// <see cref="UniformGridLayout"/>. The column count and tile size are recomputed from the available width on every
/// resize (there is no user-facing size setting):
/// <list type="number">
/// <item>usable width = control width − <see cref="EdgePadding"/></item>
/// <item>columns = floor((usable + spacing) / (<see cref="MinItemWidth"/> + spacing)), clamped to
/// [<see cref="MinColumns"/>, <see cref="MaxColumns"/>]</item>
/// <item>tile width = floor((usable − spacing·(cols−1)) / cols) — the leftover space split evenly</item>
/// <item>tile height = floor(width · <see cref="Aspect"/>)</item>
/// </list>
/// Results drive both the bound tile bounds (<see cref="ImageWidth"/>/<see cref="ImageHeight"/>) and the layout's
/// own <c>MinItemWidth</c>/<c>MinItemHeight</c> so items flow correctly. <see cref="DecodePixelWidth"/> exposes the
/// matching bitmap decode size (tile width × render scaling × hover headroom) for the cover converter.
/// </summary>
public sealed class ResponsiveCoverGrid : Decorator
{
    /// <summary>Extra pixels reserved for the scrollbar/edge slack when measuring usable width.</summary>
    private const double HoverHeadroom = 1.15; // matches the 1.1× tile hover zoom, with margin to avoid upscaling

    public static readonly StyledProperty<IEnumerable?> ItemsSourceProperty =
        AvaloniaProperty.Register<ResponsiveCoverGrid, IEnumerable?>(nameof(ItemsSource));

    public static readonly StyledProperty<IDataTemplate?> ItemTemplateProperty =
        AvaloniaProperty.Register<ResponsiveCoverGrid, IDataTemplate?>(nameof(ItemTemplate));

    public static readonly StyledProperty<double> MinItemWidthProperty =
        AvaloniaProperty.Register<ResponsiveCoverGrid, double>(nameof(MinItemWidth), 140d);

    public static readonly StyledProperty<int> MinColumnsProperty =
        AvaloniaProperty.Register<ResponsiveCoverGrid, int>(nameof(MinColumns), 4);

    public static readonly StyledProperty<int> MaxColumnsProperty =
        AvaloniaProperty.Register<ResponsiveCoverGrid, int>(nameof(MaxColumns), 6);

    public static readonly StyledProperty<double> SpacingProperty =
        AvaloniaProperty.Register<ResponsiveCoverGrid, double>(nameof(Spacing), 12d);

    /// <summary>Vertical gap between rows; NaN (default) uses <see cref="Spacing"/>.</summary>
    public static readonly StyledProperty<double> RowSpacingProperty =
        AvaloniaProperty.Register<ResponsiveCoverGrid, double>(nameof(RowSpacing), double.NaN);

    /// <summary>Tile height as a multiple of its width. 1.0 = square cover art; 0 = fixed-height cards
    /// sized by <see cref="CaptionHeight"/> alone.</summary>
    public static readonly StyledProperty<double> AspectProperty =
        AvaloniaProperty.Register<ResponsiveCoverGrid, double>(nameof(Aspect), 1d);

    /// <summary>Vertical space each tile needs below the image for its caption.</summary>
    public static readonly StyledProperty<double> CaptionHeightProperty =
        AvaloniaProperty.Register<ResponsiveCoverGrid, double>(nameof(CaptionHeight), 0d);

    /// <summary>Pixels subtracted from the control width before laying out (scrollbar + edge slack).</summary>
    public static readonly StyledProperty<double> EdgePaddingProperty =
        AvaloniaProperty.Register<ResponsiveCoverGrid, double>(nameof(EdgePadding), 24d);

    private double _imageWidth;
    public static readonly DirectProperty<ResponsiveCoverGrid, double> ImageWidthProperty =
        AvaloniaProperty.RegisterDirect<ResponsiveCoverGrid, double>(nameof(ImageWidth), o => o._imageWidth);

    private double _imageHeight;
    public static readonly DirectProperty<ResponsiveCoverGrid, double> ImageHeightProperty =
        AvaloniaProperty.RegisterDirect<ResponsiveCoverGrid, double>(nameof(ImageHeight), o => o._imageHeight);

    private double _decodePixelWidth;
    public static readonly DirectProperty<ResponsiveCoverGrid, double> DecodePixelWidthProperty =
        AvaloniaProperty.RegisterDirect<ResponsiveCoverGrid, double>(nameof(DecodePixelWidth), o => o._decodePixelWidth);

    private readonly ItemsRepeater _repeater;
    private readonly UniformGridLayout _layout;
    private readonly ScrollViewer _scroll;

    public ResponsiveCoverGrid()
    {
        _layout = new UniformGridLayout
        {
            Orientation = Orientation.Horizontal,
            MinColumnSpacing = Spacing,
            MinRowSpacing = Spacing,
        };

        _repeater = new ItemsRepeater { Layout = _layout };

        Child = _scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            // Inset the tiles from the left/right edges so the first/last columns aren't flush against
            // the page and hover-zoom has room to breathe; Recompute subtracts this from the usable width.
            Padding = new Thickness(12, 0, 12, 0),
            Content = _repeater,
        };
    }

    public IEnumerable? ItemsSource
    {
        get => GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public IDataTemplate? ItemTemplate
    {
        get => GetValue(ItemTemplateProperty);
        set => SetValue(ItemTemplateProperty, value);
    }

    public double MinItemWidth
    {
        get => GetValue(MinItemWidthProperty);
        set => SetValue(MinItemWidthProperty, value);
    }

    public int MinColumns
    {
        get => GetValue(MinColumnsProperty);
        set => SetValue(MinColumnsProperty, value);
    }

    public int MaxColumns
    {
        get => GetValue(MaxColumnsProperty);
        set => SetValue(MaxColumnsProperty, value);
    }

    public double Spacing
    {
        get => GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    public double RowSpacing
    {
        get => GetValue(RowSpacingProperty);
        set => SetValue(RowSpacingProperty, value);
    }

    public double Aspect
    {
        get => GetValue(AspectProperty);
        set => SetValue(AspectProperty, value);
    }

    public double CaptionHeight
    {
        get => GetValue(CaptionHeightProperty);
        set => SetValue(CaptionHeightProperty, value);
    }

    public double EdgePadding
    {
        get => GetValue(EdgePaddingProperty);
        set => SetValue(EdgePaddingProperty, value);
    }

    public double ImageWidth => _imageWidth;
    public double ImageHeight => _imageHeight;
    public double DecodePixelWidth => _decodePixelWidth;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == ItemsSourceProperty)
            _repeater.ItemsSource = ItemsSource;
        else if (change.Property == ItemTemplateProperty)
            _repeater.ItemTemplate = ItemTemplate;
        else if (change.Property == SpacingProperty || change.Property == RowSpacingProperty)
        {
            _layout.MinColumnSpacing = Spacing;
            _layout.MinRowSpacing = EffectiveRowSpacing;
            Recompute();
        }
        else if (change.Property == BoundsProperty ||
                 change.Property == MinItemWidthProperty || change.Property == MinColumnsProperty ||
                 change.Property == MaxColumnsProperty || change.Property == AspectProperty ||
                 change.Property == CaptionHeightProperty || change.Property == EdgePaddingProperty)
        {
            Recompute();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Recompute(); // render scaling becomes available once attached
    }

    private double EffectiveRowSpacing => double.IsNaN(RowSpacing) ? Spacing : RowSpacing;

    private int _columns = 1;

    /// <summary>Scrolls so the row holding item <paramref name="index"/> is at the top.</summary>
    public void ScrollToItem(int index)
    {
        var row = index / Math.Max(1, _columns);
        _scroll.Offset = new Vector(0, row * (_layout.MinItemHeight + EffectiveRowSpacing));
    }

    private void Recompute()
    {
        var usable = Bounds.Width - EdgePadding - _scroll.Padding.Left - _scroll.Padding.Right;
        if (usable <= 0)
            return;

        var spacing = Spacing;
        var minItem = Math.Max(1, MinItemWidth);
        var maxCols = Math.Max(1, MaxColumns);
        var minCols = Math.Clamp(MinColumns, 1, maxCols);

        var columns = (int)Math.Floor((usable + spacing) / (minItem + spacing));
        columns = Math.Clamp(columns, minCols, maxCols);

        var width = Math.Max(1, Math.Floor((usable - spacing * (columns - 1)) / columns));
        var height = Aspect > 0 ? Math.Max(1, Math.Floor(width * Aspect)) : 0;

        SetAndRaise(ImageWidthProperty, ref _imageWidth, width);
        SetAndRaise(ImageHeightProperty, ref _imageHeight, height);

        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        SetAndRaise(DecodePixelWidthProperty, ref _decodePixelWidth, width * scaling * HoverHeadroom);

        _layout.MinItemWidth = width;
        _layout.MinItemHeight = height + CaptionHeight;
        _layout.MaximumRowsOrColumns = columns;
        _layout.MinRowSpacing = EffectiveRowSpacing;
        _columns = columns;
    }
}
