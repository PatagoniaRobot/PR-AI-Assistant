using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace PRImager.AIAssistant.Controls;

/// <summary>
/// Un ícono de la aplicación. Reemplaza a los emojis, que cada versión de Windows dibuja
/// distinto y que no siguen el color del texto.
///
/// Estilo «dos tonos», elegido por el usuario el 20 de octubre de 2026 entre tres
/// propuestas: trazo de 1,75 sobre una grilla de 24 que toma el color del texto
/// (<see cref="Foreground"/>, heredado como el de un TextBlock), y un relleno suave en el
/// color de acento de cada ícono. La geometría vive en <see cref="IconCatalog"/>.
///
///     &lt;c:Icon Kind="Lock" Size="14" /&gt;
/// </summary>
public sealed class Icon : FrameworkElement
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind), typeof(IconKind), typeof(Icon),
        new FrameworkPropertyMetadata(IconKind.None, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(
        nameof(Size), typeof(double), typeof(Icon),
        new FrameworkPropertyMetadata(16.0, FrameworkPropertyMetadataOptions.AffectsMeasure |
                                            FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>El color del trazo. Se hereda del control que lo contiene, como el texto.</summary>
    public static readonly DependencyProperty ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner(typeof(Icon),
            new FrameworkPropertyMetadata(Brushes.White,
                FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>
    /// Pisa el color del relleno. Sobre un botón que ya es del color de acento —«Iniciar»,
    /// verde— el relleno de siempre se perdería; ahí se pasa el del texto o transparente.
    /// </summary>
    public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(
        nameof(Accent), typeof(Brush), typeof(Icon),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public IconKind Kind
    {
        get => (IconKind)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public double Size
    {
        get => (double)GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    public Brush Foreground
    {
        get => (Brush)GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public Brush? Accent
    {
        get => (Brush?)GetValue(AccentProperty);
        set => SetValue(AccentProperty, value);
    }

    public Icon()
    {
        IsEnabledChanged += (_, _) => InvalidateVisual();
        SnapsToDevicePixels = true;
    }

    protected override System.Windows.Size MeasureOverride(System.Windows.Size availableSize)
        => new(Size, Size);

    protected override void OnRender(DrawingContext dc)
    {
        var layers = IconCatalog.Get(Kind);
        if (layers.Count == 0) return;

        double scale = Size / IconCatalog.Grid;
        dc.PushTransform(new ScaleTransform(scale, scale));
        if (!IsEnabled) dc.PushOpacity(0.45);

        var foreground = Foreground ?? Brushes.White;
        foreach (var layer in layers)
        {
            switch (layer.Kind)
            {
                case IconLayerKind.Accent:
                    dc.PushOpacity(layer.Opacity);
                    dc.DrawGeometry(Accent ?? layer.Brush, null, layer.Geometry);
                    dc.Pop();
                    break;
                case IconLayerKind.Fill:
                    dc.DrawGeometry(foreground, null, layer.Geometry);
                    break;
                case IconLayerKind.Stroke:
                    dc.DrawGeometry(null, MakePen(foreground, layer), layer.Geometry);
                    break;
            }
        }

        if (!IsEnabled) dc.Pop();
        dc.Pop();
    }

    private static Pen MakePen(Brush brush, IconLayer layer)
    {
        var cap = layer.ButtCap ? PenLineCap.Flat : PenLineCap.Round;
        return new Pen(brush, layer.StrokeWidth)
        {
            StartLineCap = cap,
            EndLineCap = cap,
            LineJoin = PenLineJoin.Round
        };
    }
}
