using System;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AngleSharp;
using AngleSharp.Dom;

namespace PRImager.AIAssistant.Services.WebSearch;

/// <summary>
/// Convierte HTML a texto plano utilizable como contexto del modelo.
/// Descarta la cáscara de navegación, que solo gasta tokens.
/// </summary>
public sealed class HtmlTextExtractor
{
    /// <summary>Elementos que nunca aportan contenido y se eliminan antes de extraer.</summary>
    private static readonly string[] NoiseSelectors =
    {
        "script", "style", "nav", "footer", "aside", "noscript",
        "header", "form", "iframe", "svg", "button"
    };

    private readonly IConfiguration _config = Configuration.Default;

    /// <summary>
    /// Extrae el texto principal del HTML, truncado a <paramref name="maxChars"/>.
    /// Se trunca por caracteres: a ~4 chars por token, 6000 chars ≈ 1500 tokens.
    ///
    /// El trabajo va a Task.Run porque es CPU pura —parsear el DOM, recorrerlo entero
    /// y normalizar el texto— y AngleSharp resuelve el parseo sin ceder el hilo. Sin
    /// esto corría completo sobre quien llamara, que en la aplicación es el hilo de UI:
    /// el costo lo fija el peso del HTML de entrada, no los caracteres que se usan, así
    /// que una página pesada congelaba la ventana medio segundo o más.
    /// </summary>
    public Task<string> ExtractAsync(string html, int maxChars = 6000, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(html)) return Task.FromResult("");

        return Task.Run(() => Extract(html, maxChars, ct), ct);
    }

    private string Extract(string html, int maxChars, CancellationToken ct)
    {
        var context = BrowsingContext.New(_config);

        // OpenAsync no cede el hilo con contenido en memoria; ya estamos fuera del
        // hilo de UI, así que esperarlo acá no bloquea nada que importe.
        var document = context.OpenAsync(req => req.Content(html), ct).GetAwaiter().GetResult();

        ct.ThrowIfCancellationRequested();

        foreach (var selector in NoiseSelectors)
        {
            foreach (var node in document.QuerySelectorAll(selector).ToList())
                node.Remove();
        }

        // Preferir el contenedor de contenido si la página lo declara
        var root = document.QuerySelector("article")
            ?? document.QuerySelector("main")
            ?? document.Body as IElement;

        if (root == null) return "";

        ct.ThrowIfCancellationRequested();

        string text = root.TextContent ?? "";
        text = Normalize(text);

        if (text.Length > maxChars)
            text = text.Substring(0, maxChars) + Loc.T("pipeline.truncated");

        return text;
    }

    /// <summary>
    /// Colapsa el espacio en blanco que deja el HTML, conservando los saltos de párrafo.
    /// </summary>
    private static string Normalize(string raw)
    {
        var sb = new StringBuilder(raw.Length);
        int consecutiveNewlines = 0;
        bool lastWasSpace = false;

        foreach (char c in raw)
        {
            if (c == '\n' || c == '\r')
            {
                if (consecutiveNewlines < 2)
                {
                    sb.Append('\n');
                    consecutiveNewlines++;
                }
                lastWasSpace = true;
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                if (!lastWasSpace) { sb.Append(' '); lastWasSpace = true; }
                continue;
            }

            sb.Append(c);
            lastWasSpace = false;
            consecutiveNewlines = 0;
        }

        return sb.ToString().Trim();
    }
}
