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
            return WebSearchResponse.Fail(Loc.T("searx.notConfigured"));
        }

        if (string.IsNullOrWhiteSpace(query))
            return WebSearchResponse.Fail(Loc.T("searx.emptyQuery"));

        // Uri.ToString() deja barra final, así que concatenar "/search" produciría
        // "//search". Una instancia servida directo lo tolera; detrás de un proxy
        // inverso es un 404.
        string root = baseUri.GetLeftPart(UriPartial.Path).TrimEnd('/');

        // El idioma lo detecta SearXNG de la consulta. Hasta el 21 de octubre de 2026 se le
        // mandaba el de la interfaz, y con la aplicación en inglés una pregunta en español
        // filtrada al último mes daba CERO resultados (la misma, en automático, daba 20).
        string url = $"{root}/search?q={Uri.EscapeDataString(query)}&format=json&categories=general&language=auto";

        string? range = freshness.ToApiValue();

        try
        {
            var respuesta = await PedirAsync(range != null ? $"{url}&time_range={range}" : url, baseUri, count, ct);

            // Si el filtro de fecha deja la búsqueda vacía, se busca otra vez sin él y se
            // avisa: una respuesta con fuentes más viejas sirve más que ninguna fuente.
            if (range != null && respuesta.Success && respuesta.Results.Count == 0)
            {
                var sinFecha = await PedirAsync(url, baseUri, count, ct);
                if (sinFecha.Success && sinFecha.Results.Count > 0)
                    return WebSearchResponse.Ok(sinFecha.Results, timeRangeDropped: true);
            }

            return respuesta;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return WebSearchResponse.Fail(Loc.T("searx.cancelled"));
        }
        catch (TaskCanceledException)
        {
            return WebSearchResponse.Fail(Loc.F("searx.timeout", baseUri));
        }
        catch (HttpRequestException ex)
        {
            return WebSearchResponse.Fail(Loc.F("searx.unreachable", baseUri, ex.Message));
        }
        catch (Exception ex)
        {
            return WebSearchResponse.Fail(Loc.F("searx.unexpected", ex.Message));
        }
    }

    private async Task<WebSearchResponse> PedirAsync(string url, Uri baseUri, int count, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Accept", "application/json");

        using var response = await _http.SendAsync(request, ct);

        if (!response.IsSuccessStatusCode)
            return WebSearchResponse.Fail(DescribeHttpFailure(response.StatusCode, baseUri));

        string body = await response.Content.ReadAsStringAsync(ct);
        return Parse(body, count);
    }

    /// <summary>
    /// El 403 es el fallo típico y confunde: SearXNG trae el formato JSON deshabilitado
    /// de fábrica, así que una instancia que anda perfecto en el navegador rechaza la
    /// consulta de la app. Se explica cómo habilitarlo en lugar de informar el número.
    /// </summary>
    private static string DescribeHttpFailure(HttpStatusCode code, Uri baseUri) => code switch
    {
        HttpStatusCode.Forbidden => Loc.F("searx.http403", baseUri),
        HttpStatusCode.NotFound => Loc.F("searx.http404", baseUri),
        HttpStatusCode.TooManyRequests => Loc.F("searx.http429", baseUri),
        _ => Loc.F("searx.httpOther", (int)code, code)
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
            return WebSearchResponse.Fail(Loc.T("searx.notJson"));
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
