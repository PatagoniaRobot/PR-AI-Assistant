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
