using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PRImager.AIAssistant.Services.WebSearch;

/// <summary>
/// Proveedor basado en Brave Search API (2000 consultas/mes en el plan gratuito).
/// La API key llega desde la configuración de usuario: nunca se hardcodea ni se versiona.
/// </summary>
public sealed class BraveSearchProvider : IWebSearchProvider, IDisposable
{
    private const string Endpoint = "https://api.search.brave.com/res/v1/web/search";

    private readonly HttpClient _http;
    private readonly Func<string> _apiKeyProvider;
    private readonly bool _ownsClient;

    public string Name => "Brave Search";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_apiKeyProvider());

    /// <param name="apiKeyProvider">
    /// Se resuelve en cada llamada, no en el constructor: así un cambio de key en
    /// la configuración rige sin recrear el proveedor.
    /// </param>
    public BraveSearchProvider(Func<string> apiKeyProvider, HttpClient? http = null)
    {
        _apiKeyProvider = apiKeyProvider;
        _ownsClient = http == null;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    }

    public async Task<WebSearchResponse> SearchAsync(
        string query, int count, SearchFreshness freshness, CancellationToken ct = default)
    {
        string apiKey = _apiKeyProvider();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return WebSearchResponse.Fail(
                "No hay API key de Brave Search configurada. " +
                "Cargala en el panel lateral, sección BÚSQUEDA WEB. " +
                "Se obtiene gratis en brave.com/search/api.");
        }

        if (string.IsNullOrWhiteSpace(query))
            return WebSearchResponse.Fail("La consulta está vacía.");

        string url = $"{Endpoint}?q={Uri.EscapeDataString(query)}&count={count}";
        string? fresh = freshness.ToApiValue();
        if (fresh != null) url += $"&freshness={fresh}";

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("X-Subscription-Token", apiKey);
            request.Headers.Add("Accept", "application/json");
            request.Headers.Add("Accept-Encoding", "gzip");

            using var response = await _http.SendAsync(request, ct);

            string json = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
                return WebSearchResponse.Fail(
                    DescribeHttpFailure(response.StatusCode, ExtractErrorCode(json)));

            return Parse(json);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return WebSearchResponse.Fail("Búsqueda cancelada.");
        }
        catch (TaskCanceledException)
        {
            return WebSearchResponse.Fail("La búsqueda superó el tiempo de espera.");
        }
        catch (HttpRequestException ex)
        {
            return WebSearchResponse.Fail($"No se pudo contactar a Brave Search: {ex.Message}");
        }
        catch (Exception ex)
        {
            return WebSearchResponse.Fail($"Error inesperado en la búsqueda: {ex.Message}");
        }
    }

    /// <summary>
    /// Traduce el fallo HTTP a un mensaje accionable.
    ///
    /// Brave no usa 401 para una key inválida: responde 422 con
    /// <c>SUBSCRIPTION_TOKEN_INVALID</c> en el cuerpo, el mismo estado que usa para
    /// una consulta mal formada. Mirar solo el código mandaba al usuario a revisar
    /// la consulta cuando el problema era la key, así que se discrimina por
    /// <c>error.code</c>.
    /// </summary>
    private static string DescribeHttpFailure(HttpStatusCode code, string? errorCode)
    {
        if (errorCode == "SUBSCRIPTION_TOKEN_INVALID")
            return "La API key de Brave Search es inválida: el servicio la rechazó " +
                   $"({(int)code} SUBSCRIPTION_TOKEN_INVALID). Cargá la key —no la URL de la " +
                   "página— en el panel lateral, sección BÚSQUEDA WEB. Se obtiene en " +
                   "brave.com/search/api y empieza con \"BSA\".";

        if (errorCode == "RATE_LIMITED" || code == HttpStatusCode.TooManyRequests)
            return "Se agotó la cuota de Brave Search (429). El plan gratuito permite " +
                   "2000 consultas por mes y 1 por segundo.";

        return code switch
        {
            HttpStatusCode.Unauthorized =>
                "La API key de Brave Search fue rechazada (401). Verificá que esté bien copiada.",
            HttpStatusCode.Forbidden =>
                "Brave Search rechazó la petición (403). La key puede no tener habilitado este plan.",
            HttpStatusCode.UnprocessableEntity =>
                $"Brave Search no pudo procesar la consulta (422{FormatCode(errorCode)}).",
            _ => $"Brave Search respondió {(int)code} ({code}{FormatCode(errorCode)})."
        };
    }

    private static string FormatCode(string? errorCode) =>
        string.IsNullOrEmpty(errorCode) ? "" : $" {errorCode}";

    /// <summary>Lee <c>error.code</c> del cuerpo de error. Null si el cuerpo no lo trae.</summary>
    private static string? ExtractErrorCode(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;

        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("error", out var error) &&
                   error.ValueKind == JsonValueKind.Object
                ? GetOptionalString(error, "code")
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Extrae los resultados del JSON. Tolera que falten campos opcionales:
    /// la forma exacta de la respuesta puede variar entre planes.
    /// </summary>
    private static WebSearchResponse Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("web", out var web) ||
                !web.TryGetProperty("results", out var results) ||
                results.ValueKind != JsonValueKind.Array)
            {
                return WebSearchResponse.Ok(new List<WebSearchResult>());
            }

            var list = new List<WebSearchResult>();
            foreach (var item in results.EnumerateArray())
            {
                string url = GetString(item, "url");
                if (string.IsNullOrWhiteSpace(url)) continue;

                list.Add(new WebSearchResult
                {
                    Title = GetString(item, "title"),
                    Url = url,
                    Description = StripHighlightTags(GetString(item, "description")),
                    PublishedDate = GetOptionalString(item, "age")
                        ?? GetOptionalString(item, "page_age")
                });
            }

            return WebSearchResponse.Ok(list);
        }
        catch (JsonException ex)
        {
            return WebSearchResponse.Fail($"Respuesta ilegible de Brave Search: {ex.Message}");
        }
    }

    private static string GetString(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? (v.GetString() ?? "") : "";

    private static string? GetOptionalString(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    /// <summary>Brave marca los términos coincidentes con etiquetas; sobran en el prompt.</summary>
    private static string StripHighlightTags(string s) =>
        s.Replace("<strong>", "").Replace("</strong>", "");

    public void Dispose()
    {
        if (_ownsClient) _http.Dispose();
    }
}
