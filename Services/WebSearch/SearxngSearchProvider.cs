using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PRImager.AIAssistant.Services.WebSearch;

/// <summary>
/// Proveedor basado en SearXNG, un metabuscador libre.
///
/// No hay cuenta, ni API key, ni tarjeta: la consulta sale de una instancia que
/// controla el usuario. Para una herramienta pericial eso además es más defendible
/// que una key corporativa contra un servicio comercial: no hay un tercero con un
/// registro de las búsquedas asociado a una identidad de facturación.
///
/// La instancia se configura por URL. Lo esperable es una propia
/// (http://127.0.0.1:8080 con Docker), pero admite cualquier instancia pública que
/// tenga habilitado el formato JSON.
/// </summary>
public sealed class SearxngSearchProvider : IWebSearchProvider, IDisposable
{
    private readonly HttpClient _http;
    private readonly Func<string> _baseUrlProvider;
    private readonly bool _ownsClient;

    public string Name => "SearXNG";

    public bool IsConfigured => TryBuildBaseUri(_baseUrlProvider(), out _);

    /// <param name="baseUrlProvider">
    /// Se resuelve en cada llamada, no en el constructor: así un cambio de instancia
    /// en la configuración rige sin recrear el proveedor.
    /// </param>
    public SearxngSearchProvider(Func<string> baseUrlProvider, HttpClient? http = null)
    {
        _baseUrlProvider = baseUrlProvider;
        _ownsClient = http == null;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
    }

    /// <summary>
    /// Valida y normaliza la URL de la instancia. Acepta que falte el esquema
    /// (127.0.0.1:8080) porque es lo que uno escribe naturalmente.
    /// </summary>
    public static bool TryBuildBaseUri(string? raw, out Uri? uri)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        string candidate = raw.Trim().TrimEnd('/');
        if (!candidate.Contains("://", StringComparison.Ordinal))
            candidate = "http://" + candidate;

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var parsed)) return false;
        if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps) return false;

        uri = parsed;
        return true;
    }

    public async Task<WebSearchResponse> SearchAsync(
        string query, int count, SearchFreshness freshness, CancellationToken ct = default)
    {
        if (!TryBuildBaseUri(_baseUrlProvider(), out var baseUri) || baseUri == null)
        {
            return WebSearchResponse.Fail(
                "No hay instancia de SearXNG configurada. " +
                "Cargá su dirección en el panel lateral, sección BÚSQUEDA WEB " +
                "(por ejemplo http://127.0.0.1:8080).");
        }

        if (string.IsNullOrWhiteSpace(query))
            return WebSearchResponse.Fail("La consulta está vacía.");

        // Uri.ToString() deja barra final, así que concatenar "/search" produciría
        // "//search". Una instancia servida directo lo tolera; detrás de un proxy
        // inverso es un 404.
        string root = baseUri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        string url = $"{root}/search?q={Uri.EscapeDataString(query)}&format=json&categories=general&language=es";

        string? range = freshness.ToApiValue();
        if (range != null) url += $"&time_range={range}";

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("Accept", "application/json");

            using var response = await _http.SendAsync(request, ct);

            if (!response.IsSuccessStatusCode)
                return WebSearchResponse.Fail(DescribeHttpFailure(response.StatusCode, baseUri));

            string body = await response.Content.ReadAsStringAsync(ct);
            return Parse(body, count);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return WebSearchResponse.Fail("Búsqueda cancelada.");
        }
        catch (TaskCanceledException)
        {
            return WebSearchResponse.Fail(
                $"La instancia de SearXNG en {baseUri} no respondió a tiempo.");
        }
        catch (HttpRequestException ex)
        {
            return WebSearchResponse.Fail(
                $"No se pudo contactar la instancia de SearXNG en {baseUri}: {ex.Message} " +
                "Verificá que esté levantada.");
        }
        catch (Exception ex)
        {
            return WebSearchResponse.Fail($"Error inesperado en la búsqueda: {ex.Message}");
        }
    }

    /// <summary>
    /// El 403 es el fallo típico y confunde: SearXNG trae el formato JSON deshabilitado
    /// de fábrica, así que una instancia que anda perfecto en el navegador rechaza la
    /// consulta de la app. Se explica cómo habilitarlo en lugar de informar el número.
    /// </summary>
    private static string DescribeHttpFailure(HttpStatusCode code, Uri baseUri) => code switch
    {
        HttpStatusCode.Forbidden =>
            $"La instancia {baseUri} rechazó la consulta (403). Casi siempre es que no " +
            "tiene habilitado el formato JSON: en su settings.yml, dentro de «search», " +
            "agregá «json» a la lista «formats» y reiniciala. En una instancia pública " +
            "ajena esto no se puede cambiar: usá una propia.",

        HttpStatusCode.NotFound =>
            $"No se encontró el endpoint de búsqueda en {baseUri} (404). " +
            "Revisá que la dirección apunte a la raíz de la instancia, sin /search.",

        HttpStatusCode.TooManyRequests =>
            $"La instancia {baseUri} limitó la cantidad de consultas (429). " +
            "Las instancias públicas suelen restringir el uso automatizado; " +
            "una instancia propia no tiene ese límite.",

        _ => $"La instancia de SearXNG respondió {(int)code} ({code})."
    };

    /// <summary>
    /// Extrae los resultados del JSON. Tolera campos ausentes: la forma exacta
    /// varía entre versiones de SearXNG y entre motores agregados.
    /// </summary>
    private static WebSearchResponse Parse(string json, int count)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("results", out var results) ||
                results.ValueKind != JsonValueKind.Array)
            {
                return WebSearchResponse.Ok(new List<WebSearchResult>());
            }

            var list = new List<WebSearchResult>();
            foreach (var item in results.EnumerateArray())
            {
                if (list.Count >= count) break;

                string url = GetString(item, "url");
                if (string.IsNullOrWhiteSpace(url)) continue;

                list.Add(new WebSearchResult
                {
                    Title = GetString(item, "title"),
                    Url = url,
                    Description = GetString(item, "content"),
                    PublishedDate = NormalizeDate(GetOptionalString(item, "publishedDate"))
                });
            }

            return WebSearchResponse.Ok(list);
        }
        catch (JsonException)
        {
            // Una instancia con el formato JSON deshabilitado puede devolver 200 con
            // HTML. Decirlo así es más útil que volcar el error del parser.
            return WebSearchResponse.Fail(
                "La instancia no devolvió JSON. Verificá que tenga habilitado el " +
                "formato «json» en la lista «formats» de su settings.yml.");
        }
    }

    /// <summary>SearXNG informa fecha ISO completa; para citar alcanza el día.</summary>
    private static string? NormalizeDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return DateTime.TryParse(raw, out var parsed)
            ? parsed.ToString("dd/MM/yyyy")
            : raw;
    }

    private static string GetString(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? (v.GetString() ?? "") : "";

    private static string? GetOptionalString(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    public void Dispose()
    {
        if (_ownsClient) _http.Dispose();
    }
}
