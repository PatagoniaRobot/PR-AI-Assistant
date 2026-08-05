using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using PRImager.AIAssistant.Services.Enums;

namespace PRImager.AIAssistant.Views.Converters;

public class ServerStatusToColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is ServerStatus status)
        {
            return status switch
            {
                ServerStatus.Stopped or ServerStatus.Error => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#EF4444")),
                ServerStatus.Starting or ServerStatus.LoadingModel => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F59E0B")),
                ServerStatus.Ready => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#10B981")),
                _ => new SolidColorBrush(Colors.Gray)
            };
        }
        return new SolidColorBrush(Colors.Gray);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
