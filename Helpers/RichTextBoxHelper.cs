using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace PRImager.AIAssistant.Helpers;

/// <summary>
/// Renderiza el contenido de un mensaje sobre un RichTextBox. Existe porque
/// RichTextBox.Document no es DependencyProperty y no admite binding directo.
///
/// Durante el streaming el documento NO se reconstruye: se agrega al final solo el
/// texto nuevo. Reemplazar el FlowDocument entero en cada actualización obligaba a
/// WPF a redibujar todo lo acumulado, así que mostrar una respuesta costaba el
/// cuadrado de su largo. Al terminar sí se parsea el Markdown completo, una vez.
/// </summary>
public static class RichTextBoxHelper
{
    private static readonly SolidColorBrush s_streamingFg = CreateFrozen("#E0E0E0");
    private static readonly FontFamily s_consolas = new FontFamily("Consolas");

    /// <summary>Texto del mensaje. Durante el streaming crece por el final.</summary>
    public static readonly DependencyProperty ContentProperty =
        DependencyProperty.RegisterAttached(
            "Content",
            typeof(string),
            typeof(RichTextBoxHelper),
            new PropertyMetadata(null, OnRenderInputChanged));

    public static string? GetContent(DependencyObject obj)
        => (string?)obj.GetValue(ContentProperty);

    public static void SetContent(DependencyObject obj, string? value)
        => obj.SetValue(ContentProperty, value);

    /// <summary>"streaming" mientras se genera, "markdown" al finalizar.</summary>
    public static readonly DependencyProperty RenderModeProperty =
        DependencyProperty.RegisterAttached(
            "RenderMode",
            typeof(string),
            typeof(RichTextBoxHelper),
            new PropertyMetadata("markdown", OnRenderInputChanged));

    public static string? GetRenderMode(DependencyObject obj)
        => (string?)obj.GetValue(RenderModeProperty);

    public static void SetRenderMode(DependencyObject obj, string? value)
        => obj.SetValue(RenderModeProperty, value);

    // Estado interno del render incremental. Va en el control y no en el mensaje
    // porque el ItemsControl recicla contenedores entre mensajes distintos.
    private static readonly DependencyProperty StreamingParagraphProperty =
        DependencyProperty.RegisterAttached(
            "StreamingParagraph", typeof(Paragraph), typeof(RichTextBoxHelper));

    private static readonly DependencyProperty RenderedTextProperty =
        DependencyProperty.RegisterAttached(
            "RenderedText", typeof(string), typeof(RichTextBoxHelper));

    private static void OnRenderInputChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is RichTextBox rtb) Render(rtb);
    }

    private static void Render(RichTextBox rtb)
    {
        string text = GetContent(rtb) ?? string.Empty;

        if (GetRenderMode(rtb) != "streaming")
        {
            // Una sola vez por mensaje: el parseo completo se paga porque no se repite.
            rtb.Document = MarkdownParser.Parse(text);
            rtb.SetValue(StreamingParagraphProperty, null);
            rtb.SetValue(RenderedTextProperty, null);
            return;
        }

        var paragraph = (Paragraph?)rtb.GetValue(StreamingParagraphProperty);
        string rendered = (string?)rtb.GetValue(RenderedTextProperty) ?? string.Empty;

        // Solo se puede agregar al final si lo ya dibujado sigue siendo prefijo de lo
        // nuevo y el párrafo sigue siendo el del documento actual. Si el contenedor se
        // recicló o el texto se reescribió entero, se rehace desde cero.
        bool canAppend = paragraph != null
                         && ReferenceEquals(paragraph.Parent, rtb.Document)
                         && text.Length >= rendered.Length
                         && text.StartsWith(rendered, StringComparison.Ordinal);

        if (!canAppend)
        {
            paragraph = new Paragraph();
            var doc = CreateStreamingDocument();
            doc.Blocks.Add(paragraph);
            rtb.Document = doc;
            rendered = string.Empty;
            rtb.SetValue(StreamingParagraphProperty, paragraph);
        }

        if (text.Length > rendered.Length)
        {
            paragraph!.Inlines.Add(new Run(text.Substring(rendered.Length))
            {
                Foreground = s_streamingFg
            });
        }

        rtb.SetValue(RenderedTextProperty, text);
    }

    private static FlowDocument CreateStreamingDocument()
    {
        return new FlowDocument
        {
            PagePadding = new Thickness(0),
            FontFamily = s_consolas,
            FontSize = 12
        };
    }

    private static SolidColorBrush CreateFrozen(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)!);
        brush.Freeze();
        return brush;
    }
}
