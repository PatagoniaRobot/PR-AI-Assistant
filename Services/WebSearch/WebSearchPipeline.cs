using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PRImager.AIAssistant.Services.Models;

namespace PRImager.AIAssistant.Services.WebSearch;

/// <summary>
/// Enriquece una consulta con resultados web frescos antes de mandarla al modelo.
///
/// El modelo NO decide cuándo buscar: lo decide el usuario con el toggle. No hay
/// tool-calling ni parseo de JSON emitido por el modelo — un GGUF de 3-7B cuantizado
/// no es confiable para eso.
/// </summary>
public sealed class WebSearchPipeline : IDisposable
{
    /// <summary>Resultados que se piden al proveedor.</summary>
    public const int ResultCount = 5;

    /// <summary>Páginas que efectivamente se descargan y leen.</summary>
    public const int PagesToFetch = 3;

    private const int PerPageTimeoutSeconds = 10;
    private const int MaxCharsPerPage = 8000;

    private readonly IWebSearchProvider _provider;
    private readonly HtmlTextExtractor _extractor = new();
    private readonly HttpClient _http;

    /// <summary>Reporta el avance para el indicador de la UI.</summary>
    public event EventHandler<string>? ProgressChanged;

    public WebSearchPipeline(IWebSearchProvider provider, HttpClient? http = null)
    {
        _provider = provider;
        _http = http ?? CreateHttpClient();
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = System.Net.DecompressionMethods.GZip
                | System.Net.DecompressionMethods.Deflate
        };

        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(PerPageTimeoutSeconds)
        };
        client.DefaultRequestHeaders.Add("User-Agent",
            "PR-AI-Assistant/1.0 (+https://patagoniarobot.com; asistente forense local)");
        return client;
    }

    /// <summary>
    /// Busca, descarga las primeras páginas y arma la consulta enriquecida.
    /// El fallo de una URL no aborta el pipeline: se informa y se sigue con el resto.
    /// </summary>
    public async Task<WebSearchContext> BuildContextAsync(
        string userQuery, SearchFreshness freshness, CancellationToken ct = default)
    {
        ProgressChanged?.Invoke(this, "Buscando…");

        var search = await _provider.SearchAsync(userQuery, ResultCount, freshness, ct);
        if (!search.Success)
            return WebSearchContext.Fail(search.ErrorMessage ?? "Fallo la búsqueda.");

        if (search.Results.Count == 0)
            return WebSearchContext.Fail("La búsqueda no devolvió resultados.");

        var targets = search.Results.Take(PagesToFetch).ToList();
        ProgressChanged?.Invoke(this, $"Leyendo {targets.Count} páginas…");

        var sources = new List<WebSource>();
        var pageTexts = new List<string>();

        for (int i = 0; i < targets.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var r = targets[i];
            var source = new WebSource
            {
                Index = i + 1,
                Title = string.IsNullOrWhiteSpace(r.Title) ? r.Url : r.Title,
                Url = r.Url,
                PublishedDate = string.IsNullOrWhiteSpace(r.PublishedDate) ? "sin fecha" : r.PublishedDate!
            };

            var (text, failure) = await TryReadPageAsync(r.Url, ct);

            if (failure == null && !string.IsNullOrWhiteSpace(text))
            {
                source.WasRead = true;
                pageTexts.Add(text);
            }
            else
            {
                source.WasRead = false;
                source.FailureReason = failure ?? "sin contenido legible";
                // Sin página, al menos queda la descripción que dio el buscador
                pageTexts.Add(string.IsNullOrWhiteSpace(r.Description)
                    ? "[No se pudo leer esta fuente.]"
                    : r.Description);
            }

            sources.Add(source);
        }

        int read = sources.Count(s => s.WasRead);
        if (read == 0)
        {
            return new WebSearchContext
            {
                Success = false,
                ErrorMessage = $"No se pudo leer ninguna de las {targets.Count} fuentes encontradas.",
                Sources = sources,
                PagesRead = 0,
                PagesAttempted = targets.Count
            };
        }

        return new WebSearchContext
        {
            Success = true,
            EnrichedQuery = BuildPrompt(userQuery, sources, pageTexts),
            Sources = sources,
            PagesRead = read,
            PagesAttempted = targets.Count
        };
    }

    /// <summary>
    /// Descarga y extrae una página. Devuelve el motivo del fallo en vez de lanzar:
    /// una URL caída no puede tumbar la consulta entera.
    /// </summary>
    private async Task<(string? text, string? failure)> TryReadPageAsync(string url, CancellationToken ct)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(PerPageTimeoutSeconds));

            using var response = await _http.GetAsync(url, timeoutCts.Token);
            if (!response.IsSuccessStatusCode)
                return (null, $"HTTP {(int)response.StatusCode}");

            string? mediaType = response.Content.Headers.ContentType?.MediaType;
            if (mediaType != null && !mediaType.Contains("html", StringComparison.OrdinalIgnoreCase))
                return (null, $"tipo no soportado ({mediaType})");

            string html = await response.Content.ReadAsStringAsync(timeoutCts.Token);
            string text = await _extractor.ExtractAsync(html, MaxCharsPerPage, ct);

            return string.IsNullOrWhiteSpace(text)
                ? (null, "sin texto extraíble")
                : (text, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // cancelación del usuario: sí debe propagarse
        }
        catch (OperationCanceledException)
        {
            return (null, "timeout (10s)");
        }
        catch (HttpRequestException ex)
        {
            return (null, $"error de red: {ex.Message}");
        }
        catch (Exception ex)
        {
            return (null, $"error: {ex.Message}");
        }
    }

    /// <summary>
    /// Arma el prompt con las fuentes numeradas y las instrucciones de citado.
    /// Las instrucciones son deliberadamente restrictivas: el objetivo es que el
    /// modelo responda desde las fuentes y declare cuando no alcanzan.
    /// </summary>
    private static string BuildPrompt(string userQuery, List<WebSource> sources, List<string> texts)
    {
        var sb = new StringBuilder(16 * 1024);

        sb.Append("Fecha de hoy: ")
          .Append(DateTime.Now.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture))
          .Append(".\n\n");

        sb.Append("Resultados de búsqueda web:\n\n");

        for (int i = 0; i < sources.Count; i++)
        {
            var s = sources[i];
            sb.Append('[').Append(s.Index).Append("] ")
              .Append(s.Title).Append(" — ")
              .Append(s.Url).Append(" — ")
              .Append(s.PublishedDate).Append('\n')
              .Append(texts[i]).Append("\n\n");
        }

        sb.Append("---\n")
          .Append("Pregunta del usuario: ").Append(userQuery).Append("\n\n")
          .Append("Respondé usando ÚNICAMENTE la información de los resultados anteriores.\n")
          .Append("Citá la fuente con [n] después de cada afirmación.\n")
          .Append("Si los resultados no contienen la respuesta, decilo explícitamente.\n")
          .Append("No completes con conocimiento propio.");

        return sb.ToString();
    }

    public void Dispose() => _http.Dispose();
}
