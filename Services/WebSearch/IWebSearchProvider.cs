using System.Threading;
using System.Threading.Tasks;

namespace PRImager.AIAssistant.Services.WebSearch;

/// <summary>
/// Proveedor de búsqueda web. Existe para poder sumar SearXNG u otro backend
/// sin tocar el pipeline ni la UI.
///
/// Contrato: las implementaciones NO lanzan por fallos esperables (falta de
/// credenciales, error HTTP, timeout). Devuelven <see cref="WebSearchResponse.Fail"/>
/// con un mensaje legible para el usuario.
/// </summary>
public interface IWebSearchProvider
{
    /// <summary>Nombre del proveedor, para mostrar en la UI y registrar en el log.</summary>
    string Name { get; }

    /// <summary>True si el proveedor tiene lo que necesita para operar (ej: API key).</summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Ejecuta la búsqueda y devuelve hasta <paramref name="count"/> resultados.
    /// </summary>
    Task<WebSearchResponse> SearchAsync(
        string query,
        int count,
        SearchFreshness freshness,
        CancellationToken ct = default);
}
