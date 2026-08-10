using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace PRImager.AIAssistant.Views.Converters;

public class BoolToVisibilityConverter : IValueConverter
{
    public Visibility TrueValue { get; set; } = Visibility.Visible;
    public Visibility FalseValue { get; set; } = Visibility.Collapsed;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool b)
            return b ? TrueValue : FalseValue;
        return FalseValue;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is Visibility v)
            return v == TrueValue;
        return false;
    }
}

/// <summary>
/// Muestra el elemento solo cuando el texto tiene contenido.
///
/// Existe para no dejar franjas muertas: un aviso o un resultado con su margen ocupando
/// lugar desde el arranque es exactamente lo que la galería vacía de la vista de generación
/// hacía, y lo encontró mirar la captura, no una aserción.
/// </summary>
public class StringToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public class InverseBoolToVisibilityConverter : IValueConverter
{
    public Visibility TrueValue { get; set; } = Visibility.Collapsed;
    public Visibility FalseValue { get; set; } = Visibility.Visible;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool b)
            return b ? TrueValue : FalseValue;
        return FalseValue;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is Visibility v)
            return v == TrueValue;
        return false;
    }
}
