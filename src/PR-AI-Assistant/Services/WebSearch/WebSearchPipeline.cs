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
    /// <summary>Resultados que se piden al proveedor. Son candidatos, no lecturas.</summary>
    public const int ResultCount = 12;

    /// <summary>
    /// Fuentes leídas con éxito que se busca reunir. Es un objetivo, no un tope de
    /// intentos: en la práctica ~1 de cada 4 páginas rechaza la descarga, así que
    /// tomar las primeras N y conformarse dejaba al modelo con dos fuentes o menos.
    /// </summary>
    public const int TargetPages = 6;

    /// <summary>
    /// Tope de páginas que se intentan descargar. Acota el peor caso cuando muchas
    /// fuentes seguidas rechazan la conexión.
    /// </summary>
    public const int MaxFetchAttempts = 10;

    /// <summary>Descargas simultáneas. Las páginas se leen en paralelo, no en fila.</summary>
    private const int MaxConcurrentFetches = 4;

    private const int PerPageTimeoutSeconds = 10;

    /// <summary>
    /// Presupuesto por página. Bajó de 8000 al ampliar de 3 a 6 fuentes: seis fuentes
    /// de 6000 son ~9000 tokens, contra ~6000 que costaban tres de 8000. Más variedad
    /// de origen por un costo de contexto comparable.
    /// </summary>
    private const int MaxCharsPerPage = 6000;

    /// <summary>Compatibilidad: cuántas páginas se intentan de arranque.</summary>
    public const int PagesToFetch = TargetPages;

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
        ProgressChanged?.Invoke(this, Loc.T("pipeline.searching"));

        var search = await _provider.SearchAsync(userQuery, ResultCount, freshness, ct)
                                    .ConfigureAwait(false);
        if (!search.Success)
            return WebSearchContext.Fail(search.ErrorMessage ?? Loc.T("pipeline.searchFailed"));

        if (search.Results.Count == 0)
            return WebSearchContext.Fail(Loc.T("pipeline.noResults"));

        var candidates = search.Results.Take(MaxFetchAttempts).ToList();
        var attempted = new List<(WebSearchResult result, string? text, string? failure)>();

        // Se lee en tandas paralelas hasta reunir TargetPages fuentes útiles. Si la
        // primera tanda deja huecos —hay sitios que rechazan la descarga— se siguen
        // probando candidatos en lugar de entregarle al modelo dos fuentes flacas.
        int next = 0;
        while (next < candidates.Count
               && attempted.Count(a => a.failure == null) < TargetPages)
        {
            ct.ThrowIfCancellationRequested();

            int missing = TargetPages - attempted.Count(a => a.failure == null);
            int waveSize = Math.Min(Math.Max(missing, 1), candidates.Count - next);
            var wave = candidates.Skip(next).Take(waveSize).ToList();
            next += waveSize;

            int soFar = attempted.Count(a => a.failure == null);
            ProgressChanged?.Invoke(this, soFar == 0
                ? Loc.F("pipeline.reading", wave.Count)
                : Loc.F("pipeline.readingMore", wave.Count, soFar, TargetPages));

            var waveResults = await ReadPagesAsync(wave, ct).ConfigureAwait(false);
            attempted.AddRange(waveResults);
        }

        // Las que se leyeron encabezan la numeración; las fallidas quedan al final
        // para que el usuario igual las vea en el panel de fuentes.
        var ordered = attempted.Where(a => a.failure == null)
                               .Take(TargetPages)
                               .Concat(attempted.Where(a => a.failure != null))
                               .ToList();

        var sources = new List<WebSource>();
        var pageTexts = new List<string>();

        for (int i = 0; i < ordered.Count; i++)
        {
            var (r, text, failure) = ordered[i];

            var source = new WebSource
            {
                Index = i + 1,
                Title = string.IsNullOrWhiteSpace(r.Title) ? r.Url : r.Title,
                Url = r.Url,
                PublishedDate = string.IsNullOrWhiteSpace(r.PublishedDate) ? Loc.T("web.source.noDate") : r.PublishedDate!,
                WasRead = failure == null
            };

            if (failure == null)
            {
                pageTexts.Add(text!);
            }
            else
            {
                source.FailureReason = failure;
                // Sin página, al menos queda la descripción que dio el buscador
                pageTexts.Add(string.IsNullOrWhiteSpace(r.Description)
                    ? Loc.T("pipeline.sourceUnreadable")
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
                ErrorMessage = Loc.F("pipeline.noneRead", sources.Count),
                Sources = sources,
                PagesRead = 0,
                PagesAttempted = sources.Count
            };
        }

        return new WebSearchContext
        {
            Success = true,
            EnrichedQuery = BuildPrompt(userQuery, sources, pageTexts),
            Sources = sources,
            PagesRead = read,
            PagesAttempted = sources.Count,
            TimeRangeDropped = search.TimeRangeDropped
        };
    }

    /// <summary>
    /// Descarga y extrae un grupo de páginas en paralelo, conservando el orden de
    /// entrada. En serie, seis páginas costaban la suma de sus tiempos.
    /// </summary>
    private async Task<List<(WebSearchResult, string?, string?)>> ReadPagesAsync(
        List<WebSearchResult> targets, CancellationToken ct)
    {
        using var gate = new SemaphoreSlim(MaxConcurrentFetches);

        var tasks = targets.Select(async r =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var (text, failure) = await TryReadPageAsync(r.Url, ct).ConfigureAwait(false);

                if (failure == null && string.IsNullOrWhiteSpace(text))
                    failure = Loc.T("pipeline.fail.noContent");

                return (r, text, failure);
            }
            finally
            {
                gate.Release();
            }
        }).ToList();

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        return results.ToList();
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

            using var response = await _http.GetAsync(url, timeoutCts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return (null, $"HTTP {(int)response.StatusCode}");

            string? mediaType = response.Content.Headers.ContentType?.MediaType;
            if (mediaType != null && !mediaType.Contains("html", StringComparison.OrdinalIgnoreCase))
                return (null, Loc.F("pipeline.fail.mediaType", mediaType));

            string html = await response.Content.ReadAsStringAsync(timeoutCts.Token)
                                        .ConfigureAwait(false);
            string text = await _extractor.ExtractAsync(html, MaxCharsPerPage, timeoutCts.Token)
                                          .ConfigureAwait(false);

            return string.IsNullOrWhiteSpace(text)
                ? (null, Loc.T("pipeline.fail.noText"))
                : (text, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // cancelación del usuario: sí debe propagarse
        }
        catch (OperationCanceledException)
        {
            return (null, Loc.F("pipeline.fail.timeout", PerPageTimeoutSeconds));
        }
        catch (HttpRequestException ex)
        {
            return (null, Loc.F("pipeline.fail.network", ex.Message));
        }
        catch (Exception ex)
        {
            return (null, Loc.F("pipeline.fail.other", ex.Message));
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

        sb.Append(Loc.T("prompt.web.today"))
          .Append(DateTime.Now.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture))
          .Append(".\n\n");

        sb.Append(Loc.T("prompt.web.results")).Append("\n\n");

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
          .Append(Loc.T("prompt.web.question")).Append(userQuery).Append("\n\n")
          .Append(Loc.T("prompt.web.instructions"));

        return sb.ToString();
    }

    public void Dispose() => _http.Dispose();
}
