using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace PRImager.AIAssistant.Views.Converters;

/// <summary>
/// Convierte un color en hex (#RRGGBB) a Brush. Usado por la franja de acento del perfil.
/// Un hex inválido cae a gris neutro en vez de romper el render.
/// </summary>
public class HexToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Fallback =
        new(Color.FromRgb(0x64, 0x74, 0x8B));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string hex && !string.IsNullOrWhiteSpace(hex))
        {
            try
            {
                var color = (Color)ColorConverter.ConvertFromString(hex);
                var brush = new SolidColorBrush(color);
                brush.Freeze();
                return brush;
            }
            catch (Exception)
            {
                // Hex mal formado en profiles.json editado a mano
            }
        }
        return Fallback;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
