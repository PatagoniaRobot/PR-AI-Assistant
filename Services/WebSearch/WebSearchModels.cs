using System;
using System.Collections.Generic;
using PRImager.AIAssistant.Services.Models;

namespace PRImager.AIAssistant.Services.WebSearch;

/// <summary>
/// Ventana de frescura de los resultados. Se mapea al parámetro time_range de SearXNG.
/// </summary>
public enum SearchFreshness
{
    /// <summary>Sin filtro temporal.</summary>
    Any,
    /// <summary>Último día.</summary>
    Day,
    /// <summary>Última semana. Valor por defecto.</summary>
    Week,
    /// <summary>Último mes.</summary>
    Month
}

/// <summary>Traduce SearchFreshness al código que espera la API.</summary>
public static class SearchFreshnessExtensions
{
    public static string? ToApiValue(this SearchFreshness f) => f switch
    {
        SearchFreshness.Day => "day",
        SearchFreshness.Week => "week",
        SearchFreshness.Month => "month",
        _ => null
    };

    public static string ToDisplayName(this SearchFreshness f) => f switch
    {
        SearchFreshness.Day => Loc.T("web.freshness.day"),
        SearchFreshness.Week => Loc.T("web.freshness.week"),
        SearchFreshness.Month => Loc.T("web.freshness.month"),
        _ => Loc.T("web.freshness.any")
    };
}

/// <summary>
/// Un resultado devuelto por el proveedor de búsqueda, antes de descargar la página.
/// </summary>
public sealed class WebSearchResult
{
    public string Title { get; set; } = "";
    public string Url { get; set; } = "";
    public string Description { get; set; } = "";

    /// <summary>Fecha de publicación informada por el proveedor. Null si no la informa.</summary>
    public string? PublishedDate { get; set; }
}

/// <summary>
/// Respuesta del proveedor. Los fallos se modelan como datos, no como excepciones:
/// la falta de API key o una caída del servicio deben producir un mensaje legible.
/// </summary>
public sealed class WebSearchResponse
{
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
    public List<WebSearchResult> Results { get; init; } = new();

    public static WebSearchResponse Ok(List<WebSearchResult> results) =>
        new() { Success = true, Results = results };

    public static WebSearchResponse Fail(string message) =>
        new() { Success = false, ErrorMessage = message };
}

/// <summary>
/// Resultado del pipeline completo: el bloque de contexto listo para anteponer
/// a la consulta, más las fuentes para mostrar en la UI.
/// </summary>
public sealed class WebSearchContext
{
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }

    /// <summary>Prompt enriquecido que reemplaza a la consulta original.</summary>
    public string EnrichedQuery { get; init; } = "";

    public List<WebSource> Sources { get; init; } = new();

    /// <summary>Cuántas páginas se leyeron con éxito.</summary>
    public int PagesRead { get; init; }

    /// <summary>Cuántas páginas se intentaron descargar.</summary>
    public int PagesAttempted { get; init; }

    public static WebSearchContext Fail(string message) =>
        new() { Success = false, ErrorMessage = message };
}
