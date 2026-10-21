using System.Windows;
using System.Windows.Media;

namespace PRImager.AIAssistant.Controls;

/// <summary>Los íconos de la aplicación. El nombre dice qué representa, no dónde se usa.</summary>
public enum IconKind
{
    None,
    Menu,
    Settings,
    Lock,
    Search,
    Help,
    Folder,
    Monitor,
    Warning,
    Globe,
    Close,
    Attach,
    Image,
    Send,
    Stop,
    Refresh,
    GenerateImage,
    ArrowBack,
    ArrowForward,
    Check,
    Scales,
    Consultation,
    Eye,
    Audio,
    Document,
    Success,
    Error,
    Trash,
    Star,
    Download,
    Chip,
    Play,
    Info,
    Language
}

public enum IconLayerKind
{
    /// <summary>Relleno suave en el color de acento, debajo del trazo.</summary>
    Accent,
    /// <summary>Trazo en el color del texto.</summary>
    Stroke,
    /// <summary>Relleno pleno en el color del texto: los puntos de «?» y «!».</summary>
    Fill
}

public sealed record IconLayer(
    IconLayerKind Kind,
    Geometry Geometry,
    double StrokeWidth = IconCatalog.Stroke,
    bool ButtCap = false,
    Brush? Brush = null,
    double Opacity = 1.0);

/// <summary>
/// Geometría de los íconos, sobre una grilla de 24×24. Es la de la propuesta C, «dos tonos»,
/// pasada a la sintaxis de trazados de WPF, que es la misma de SVG.
///
/// Los acentos: verde agua para todo, ámbar para lo que pide atención —avisos, la
/// cuantización recomendada—, y verde y rojo solo para el resultado de algo.
/// </summary>
public static class IconCatalog
{
    public const double Grid = 24;
    public const double Stroke = 1.75;

    public static readonly Brush Teal = Frozen("#00D4AA");
    public static readonly Brush Amber = Frozen("#F59E0B");
    public static readonly Brush Green = Frozen("#10B981");
    public static readonly Brush Red = Frozen("#EF4444");

    private const double Soft = 0.35;

    private static readonly Dictionary<IconKind, IReadOnlyList<IconLayer>> Icons = Build();

    public static IReadOnlyList<IconLayer> Get(IconKind kind)
        => Icons.TryGetValue(kind, out var layers) ? layers : Array.Empty<IconLayer>();

    /// <summary>Todos los íconos definidos. Lo usa el banco para comprobar que ninguno quedó vacío.</summary>
    public static IEnumerable<IconKind> Defined => Icons.Keys;

    private static Dictionary<IconKind, IReadOnlyList<IconLayer>> Build()
    {
        var plane = P("M4.2 4.1l16 7.4a0.6 0.6 0 0 1 0 1L4.2 19.9a0.6 0.6 0 0 1-0.8-0.7L5.5 12 3.4 4.8a0.6 0.6 0 0 1 0.8-0.7z");
        var triangle = P("M10.3 4.6a2 2 0 0 1 3.4 0l7.6 13a2 2 0 0 1-1.7 3H4.4a2 2 0 0 1-1.7-3z");
        var meridian = P("M12 3c2.5 2.6 3.8 5.6 3.8 9s-1.3 6.4-3.8 9c-2.5-2.6-3.8-5.6-3.8-9S9.5 5.6 12 3z");
        var star = P("M12 3.5l2.6 5.3 5.9 0.9-4.3 4.1 1 5.9L12 16.9l-5.2 2.8 1-5.9-4.3-4.1 5.9-0.9z");
        var mortarboard = P("M2 9.5L12 5l10 4.5L12 14z");
        var speaker = P("M4 9.5h3.5L12 5.5v13l-4.5-4H4z");
        var almond = P("M2.5 12S6 5.5 12 5.5 21.5 12 21.5 12 18 18.5 12 18.5 2.5 12 2.5 12z");
        var playTriangle = P("M8 5.5v13l10.5-6.5z");
        var page = P("M6.5 3h7l5 5v12a1 1 0 0 1-1 1h-11a1 1 0 0 1-1-1V4a1 1 0 0 1 1-1z");
        var pans = P("M2.5 13a2.5 2 0 0 0 5 0zM16.5 13a2.5 2 0 0 0 5 0z");

        return new()
        {
            [IconKind.Menu] = L(S("M4 7h16M4 12h11M4 17h16"), Solid(C(19, 12, 1.4))),
            [IconKind.Settings] = L(A(C(12, 12, 6.5)), S(C(12, 12, 6.5)), S(C(12, 12, 2.5)),
                new IconLayer(IconLayerKind.Stroke,
                    P("M18.5 12H21M12 18.5V21M5.5 12H3M12 5.5V3M16.6 16.6l1.8 1.8M7.4 16.6l-1.8 1.8M7.4 7.4L5.6 5.6M16.6 7.4l1.8-1.8"),
                    StrokeWidth: 2.6, ButtCap: true)),
            [IconKind.Lock] = L(A(R(5, 10.5, 14, 10, 2)), S(R(5, 10.5, 14, 10, 2)),
                S("M8 10.5V7.5a4 4 0 0 1 8 0v3M12 14.5V17")),
            [IconKind.Search] = L(A(C(10.5, 10.5, 6)), S(C(10.5, 10.5, 6)), S("M15 15l5 5")),
            [IconKind.Help] = L(A(C(12, 12, 9)), S(C(12, 12, 9)),
                S("M9.5 9.5a2.5 2.5 0 1 1 3.5 2.3c-0.6 0.3-1 0.8-1 1.5v0.5"), F(C(12, 16.8, 1))),
            [IconKind.Info] = L(A(C(12, 12, 9)), S(C(12, 12, 9)), S("M12 11v5.5"), F(C(12, 7.8, 1))),
            [IconKind.Folder] = L(A(P("M3 9.5h18v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z")),
                S("M3 7.5a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z")),
            [IconKind.Monitor] = L(A(R(3, 4, 18, 12, 2)), S(R(3, 4, 18, 12, 2)), S("M9 20h6M12 16v4")),
            [IconKind.Warning] = L(A(triangle, Amber, 0.45), S(triangle), S("M12 10v4"), F(C(12, 17, 1))),
            [IconKind.Globe] = L(A(meridian), S(C(12, 12, 9)), S("M3 12h18"), S(meridian)),
            [IconKind.Close] = L(S("M6 6l12 12M18 6L6 18")),
            [IconKind.Attach] = L(S("M20 11.5l-7.8 7.8a5 5 0 0 1-7.1-7.1l8.5-8.5a3.3 3.3 0 0 1 4.7 4.7l-8.5 8.5a1.7 1.7 0 0 1-2.4-2.4l7.8-7.8")),
            [IconKind.Image] = L(A(P("M21 15.5l-5-5-9 9h12a2 2 0 0 0 2-2z")), S(R(3, 4.5, 18, 15, 2)),
                S(C(8.5, 9.5, 1.6)), S("M21 15.5l-5-5-9 9")),
            [IconKind.Send] = L(A(plane), S(plane), S("M5.5 12H12")),
            [IconKind.Stop] = L(A(R(6.5, 6.5, 11, 11, 2)), S(R(6.5, 6.5, 11, 11, 2))),
            [IconKind.Refresh] = L(S("M19.5 12a7.5 7.5 0 1 1-2.2-5.3M19.5 4.5v4h-4")),
            [IconKind.GenerateImage] = L(A(R(3, 7, 14, 14, 2)), S(R(3, 7, 14, 14, 2)),
                S("M3 17.5l4.5-4.5 4.5 4.5 2-2 3 3"),
                Solid(P("M19.5 1.5l1 2.5 2.5 1-2.5 1-1 2.5-1-2.5-2.5-1 2.5-1z"))),
            [IconKind.ArrowBack] = L(S("M19 12H5M11 6l-6 6 6 6")),
            [IconKind.ArrowForward] = L(S("M5 12h14M13 6l6 6-6 6")),
            [IconKind.Check] = L(S("M5 12.5l4.5 4.5L19 7.5")),
            [IconKind.Scales] = L(A(pans, Teal, 0.5),
                S("M12 4v16M8 20h8M5 7h14M5 7l-2.5 6M5 7l2.5 6M19 7l-2.5 6M19 7l2.5 6"), S(pans)),
            [IconKind.Consultation] = L(A(mortarboard), S(mortarboard),
                S("M6 11.5V16c0 1.4 2.7 3 6 3s6-1.6 6-3v-4.5M22 9.5v5")),
            [IconKind.Eye] = L(A(C(12, 12, 3), Teal, 0.6), S(almond), S(C(12, 12, 3))),
            [IconKind.Audio] = L(A(speaker), S(speaker), S("M15.5 9a4 4 0 0 1 0 6M18.5 6.5a7.5 7.5 0 0 1 0 11")),
            [IconKind.Document] = L(A(P("M13.5 3v5h5z"), Teal, 0.6), S(page), S("M13.5 3v5h5M8.5 13h7M8.5 16.5h7")),
            [IconKind.Success] = L(A(C(12, 12, 9), Green, 0.45), S(C(12, 12, 9)), S("M8 12.3l2.7 2.7L16 9.7")),
            [IconKind.Error] = L(A(C(12, 12, 9), Red, 0.45), S(C(12, 12, 9)), S("M9 9l6 6M15 9l-6 6")),
            [IconKind.Trash] = L(A(P("M6 6.5h12l-1 13a1.5 1.5 0 0 1-1.5 1.5h-7A1.5 1.5 0 0 1 7 19.5z")),
                S("M4 6.5h16M9.5 6.5v-2h5v2M6 6.5l1 13a1.5 1.5 0 0 0 1.5 1.5h7a1.5 1.5 0 0 0 1.5-1.5l1-13M10 10.5v6M14 10.5v6")),
            [IconKind.Star] = L(A(star, Amber, 0.55), S(star)),
            [IconKind.Download] = L(Solid(R(4.5, 18.5, 15, 2, 1)), S("M12 4v11M7 10.5l5 5 5-5")),
            [IconKind.Chip] = L(A(R(9.5, 9.5, 5, 5, 0.5), Teal, 0.6), S(R(6, 6, 12, 12, 1.5)), S(R(9.5, 9.5, 5, 5, 0.5)),
                S("M9 3v3M15 3v3M9 18v3M15 18v3M3 9h3M3 15h3M18 9h3M18 15h3")),
            [IconKind.Play] = L(A(playTriangle), S(playTriangle)),
            // Un globo con «A» encima: el selector de idioma. No estaba en las propuestas.
            [IconKind.Language] = L(A(C(12, 12, 9)), S(C(12, 12, 9)), S("M8.5 16l3.5-8 3.5 8M9.8 13h4.4")),
        };
    }

    // ---------------------------------------------------------------- piezas

    private static IReadOnlyList<IconLayer> L(params IconLayer[] layers) => layers;

    private static IconLayer S(string data) => new(IconLayerKind.Stroke, P(data));
    private static IconLayer S(Geometry g) => new(IconLayerKind.Stroke, g);
    private static IconLayer F(Geometry g) => new(IconLayerKind.Fill, g);
    private static IconLayer A(Geometry g, Brush? brush = null, double opacity = Soft)
        => new(IconLayerKind.Accent, g, Brush: brush ?? Teal, Opacity: opacity);
    private static IconLayer Solid(Geometry g) => new(IconLayerKind.Accent, g, Brush: Teal, Opacity: 1.0);

    private static Geometry P(string data)
    {
        var g = Geometry.Parse(data);
        g.Freeze();
        return g;
    }

    private static Geometry C(double cx, double cy, double r)
    {
        var g = new EllipseGeometry(new Point(cx, cy), r, r);
        g.Freeze();
        return g;
    }

    private static Geometry R(double x, double y, double w, double h, double radius)
    {
        var g = new RectangleGeometry(new Rect(x, y, w, h), radius, radius);
        g.Freeze();
        return g;
    }

    private static Brush Frozen(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }
}
