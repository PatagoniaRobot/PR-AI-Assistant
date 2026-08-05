using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Documents;

namespace PRImager.AIAssistant.Helpers;

/// <summary>
/// MultiValueConverter que recibe Content + RenderMode y genera FlowDocument.
/// En modo "streaming" retorna texto plano para performance.
/// En modo "markdown" parsea Markdown completo.
/// </summary>
public class MarkdownMultiConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2
            || values[0] is not string text
            || values[1] is not string renderMode
            || string.IsNullOrEmpty(text))
        {
            return CreateEmptyDocument();
        }

        if (renderMode == "streaming")
        {
            var doc = CreateEmptyDocument();
            doc.Blocks.Add(new Paragraph(new Run(text)
            {
                Foreground = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#E0E0E0")!)
            }));
            return doc;
        }

        return MarkdownParser.Parse(text);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static FlowDocument CreateEmptyDocument()
    {
        return new FlowDocument
        {
            PagePadding = new Thickness(0),
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            FontSize = 12
        };
    }
}
