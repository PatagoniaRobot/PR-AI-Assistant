using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PRImager.AIAssistant.Services.HuggingFace;

/// <summary>
/// Cliente de la API pública de HuggingFace. Las operaciones básicas no requieren
/// autenticación; los repos gated sí, y ese caso se informa explícitamente en vez
/// de esconderse tras un error genérico.
/// </summary>
public sealed class HuggingFaceClient : IDisposable
{
    private const string ApiBase = "https://huggingface.co/api";
    private const string SiteBase = "https://huggingface.co";

    private readonly HttpClient _http;
    private readonly bool _ownsClient;
    private readonly Func<string>? _tokenProvider;

    /// <param name="tokenProvider">
    /// Token de acceso opcional, necesario solo para repos gated.
    /// </param>
    public HuggingFaceClient(HttpClient? http = null, Func<string>? tokenProvider = null)
    {
        _ownsClient = http == null;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _tokenProvider = tokenProvider;

        if (!_http.DefaultRequestHeaders.Contains("User-Agent"))
        {
            _http.DefaultRequestHeaders.Add("User-Agent",
                "PR-AI-Assistant/1.0 (+https://patagoniarobot.com; asistente forense local)");
        }
    }

    /// <summary>Construye la URL de descarga directa de un archivo del repo.</summary>
    public static string BuildDownloadUrl(string repoId, string fileName) =>
        $"{SiteBase}/{repoId}/resolve/main/{Uri.EscapeDataString(fileName)}";

    private void ApplyAuth(HttpRequestMessage request)
    {
        string? token = _tokenProvider?.Invoke();
        if (!string.IsNullOrWhiteSpace(token))
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
    }

    /// <summary>
    /// Busca repositorios que contengan archivos GGUF, ordenados por descargas.
    /// </summary>
    public async Task<HfResult<List<HfRepo>>> SearchAsync(
        string query, int limit = 20, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return HfResult<List<HfRepo>>.Fail("Escribí algo para buscar.");

        string url = $"{ApiBase}/models?search={Uri.EscapeDataString(query)}" +
                     $"&filter=gguf&sort=downloads&direction=-1&limit={limit}";

        var response = await GetAsync(url, ct);
        if (!response.Success) return HfResult<List<HfRepo>>.Fail(response.ErrorMessage!, response.IsGated);

        try
        {
            using var doc = JsonDocument.Parse(response.Value!);
            var repos = new List<HfRepo>();

            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return HfResult<List<HfRepo>>.Ok(repos);

            foreach (var item in doc.RootElement.EnumerateArray())
            {
                string id = GetString(item, "id", "modelId");
                if (string.IsNullOrWhiteSpace(id)) continue;

                repos.Add(new HfRepo
                {
                    Id = id,
                    Author = GetString(item, "author") is { Length: > 0 } a ? a : DeriveAuthor(id),
                    Downloads = GetInt(item, "downloads"),
                    Likes = GetInt(item, "likes"),
                    License = ExtractLicense(item),
                    IsGated = IsGatedFlag(item)
                });
            }

            return HfResult<List<HfRepo>>.Ok(repos);
        }
        catch (JsonException ex)
        {
            return HfResult<List<HfRepo>>.Fail($"Respuesta ilegible de HuggingFace: {ex.Message}");
        }
    }

    /// <summary>
    /// Trae el detalle del repo y sus archivos GGUF con tamaño.
    /// Los tamaños vienen de /tree/main; si esa llamada falla, se devuelven
    /// igual los archivos con tamaño desconocido.
    /// </summary>
    public async Task<HfResult<HfRepoDetail>> GetRepoDetailAsync(string repoId, CancellationToken ct = default)
    {
        var response = await GetAsync($"{ApiBase}/models/{repoId}", ct);
        if (!response.Success) return HfResult<HfRepoDetail>.Fail(response.ErrorMessage!, response.IsGated);

        try
        {
            using var doc = JsonDocument.Parse(response.Value!);
            var root = doc.RootElement;

            var detail = new HfRepoDetail
            {
                Repo = new HfRepo
                {
                    Id = GetString(root, "id", "modelId") is { Length: > 0 } id ? id : repoId,
                    Author = GetString(root, "author") is { Length: > 0 } a ? a : DeriveAuthor(repoId),
                    Downloads = GetInt(root, "downloads"),
                    Likes = GetInt(root, "likes"),
                    License = ExtractLicense(root),
                    IsGated = IsGatedFlag(root)
                }
            };

            // siblings: lista de archivos del repo
            if (root.TryGetProperty("siblings", out var siblings) &&
                siblings.ValueKind == JsonValueKind.Array)
            {
                foreach (var s in siblings.EnumerateArray())
                {
                    string name = GetString(s, "rfilename");
                    if (name.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
                        detail.GgufFiles.Add(new HfFile { FileName = name });
                }
            }

            if (detail.GgufFiles.Count > 0)
                await FillSizesAsync(repoId, detail.GgufFiles, ct);

            detail.GgufFiles = detail.GgufFiles
                .OrderBy(f => f.SizeBytes == 0 ? long.MaxValue : f.SizeBytes)
                .ToList();

            return HfResult<HfRepoDetail>.Ok(detail);
        }
        catch (JsonException ex)
        {
            return HfResult<HfRepoDetail>.Fail($"Respuesta ilegible de HuggingFace: {ex.Message}");
        }
    }

    /// <summary>
    /// Baja solo el comienzo de un .gguf y lee su cabecera, para saber antes de bajar
    /// varios GB si es un modelo o un archivo que no carga solo (pendiente 6.5).
    /// Es un pedido de 64 KB con <c>Range</c>; HuggingFace lo respeta también después
    /// de redirigir al CDN.
    /// </summary>
    public async Task<HfResult<GgufHeader>> ProbeGgufAsync(
        string repoId, string fileName, CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, BuildDownloadUrl(repoId, fileName));
            ApplyAuth(request);
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, GgufInspector.ProbeBytes - 1);

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                var (message, gated) = DescribeFailure(response);
                return HfResult<GgufHeader>.Fail(message, gated);
            }

            // Si el servidor ignorara el Range mandaría el archivo entero: se lee solo
            // lo necesario y se corta.
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            var buffer = new byte[GgufInspector.ProbeBytes];
            int read = 0;
            while (read < buffer.Length)
            {
                int n = await stream.ReadAsync(buffer.AsMemory(read), ct);
                if (n == 0) break;
                read += n;
            }

            var header = GgufInspector.TryParse(buffer.AsSpan(0, read));
            return header == null
                ? HfResult<GgufHeader>.Fail("El archivo no tiene una cabecera GGUF legible.")
                : HfResult<GgufHeader>.Ok(header);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return HfResult<GgufHeader>.Fail("Operación cancelada.");
        }
        catch (TaskCanceledException)
        {
            return HfResult<GgufHeader>.Fail("HuggingFace no respondió a tiempo.");
        }
        catch (HttpRequestException ex)
        {
            return HfResult<GgufHeader>.Fail($"No se pudo contactar a HuggingFace: {ex.Message}");
        }
    }

    /// <summary>
    /// Completa los tamaños desde /tree/main. Un fallo acá no invalida el detalle:
    /// los archivos quedan con tamaño desconocido y el semáforo lo refleja.
    /// </summary>
    private async Task FillSizesAsync(string repoId, List<HfFile> files, CancellationToken ct)
    {
        var tree = await GetAsync($"{ApiBase}/models/{repoId}/tree/main", ct);
        if (!tree.Success) return;

        try
        {
            using var doc = JsonDocument.Parse(tree.Value!);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return;

            var sizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in doc.RootElement.EnumerateArray())
            {
                string path = GetString(entry, "path");
                if (string.IsNullOrEmpty(path)) continue;

                // Los archivos grandes viven en LFS: el tamaño real está anidado
                long size = 0;
                if (entry.TryGetProperty("lfs", out var lfs) &&
                    lfs.ValueKind == JsonValueKind.Object &&
                    lfs.TryGetProperty("size", out var lfsSize) &&
                    lfsSize.TryGetInt64(out long ls))
                {
                    size = ls;
                }
                else if (entry.TryGetProperty("size", out var plain) && plain.TryGetInt64(out long ps))
                {
                    size = ps;
                }

                if (size > 0) sizes[path] = size;
            }

            foreach (var f in files)
                if (sizes.TryGetValue(f.FileName, out long s)) f.SizeBytes = s;
        }
        catch (JsonException)
        {
            // Tamaños no disponibles: no es motivo para invalidar el listado
        }
    }

    /// <summary>
    /// GET con traducción de fallos a mensajes legibles. Nunca lanza por errores
    /// esperables de red o autorización.
    /// </summary>
    private async Task<HfResult<string>> GetAsync(string url, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            ApplyAuth(request);

            using var response = await _http.SendAsync(request, ct);

            if (!response.IsSuccessStatusCode)
            {
                var (message, gated) = DescribeFailure(response);
                return HfResult<string>.Fail(message, gated);
            }

            return HfResult<string>.Ok(await response.Content.ReadAsStringAsync(ct));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return HfResult<string>.Fail("Operación cancelada.");
        }
        catch (TaskCanceledException)
        {
            return HfResult<string>.Fail("HuggingFace no respondió a tiempo.");
        }
        catch (HttpRequestException ex)
        {
            return HfResult<string>.Fail($"No se pudo contactar a HuggingFace: {ex.Message}");
        }
    }

    /// <summary>
    /// Traduce una respuesta fallida a un mensaje legible.
    ///
    /// HuggingFace devuelve 401 tanto para repos restringidos como para repos que no
    /// existen o son privados: no distingue por código de estado, a propósito, para no
    /// filtrar la existencia de repos privados. El discriminador confiable es el header
    /// x-error-code (GatedRepo, RepoNotFound, EntryNotFound...).
    /// </summary>
    internal static (string message, bool gated) DescribeFailure(HttpResponseMessage response)
    {
        string errorCode = GetHeader(response, "x-error-code");
        string errorMessage = GetHeader(response, "x-error-message");

        if (errorCode.Equals("GatedRepo", StringComparison.OrdinalIgnoreCase))
            return (GatedMessage(), true);

        if (errorCode.Equals("RepoNotFound", StringComparison.OrdinalIgnoreCase))
            return (RepoNotFoundMessage(), false);

        if (errorCode.Equals("EntryNotFound", StringComparison.OrdinalIgnoreCase) ||
            errorCode.Equals("RevisionNotFound", StringComparison.OrdinalIgnoreCase))
        {
            return ("El archivo no existe en la rama principal del repositorio.", false);
        }

        return response.StatusCode switch
        {
            HttpStatusCode.Forbidden => (GatedMessage(), true),
            // Sin header que lo aclare, 401 es ambiguo: se dicen las dos posibilidades
            HttpStatusCode.Unauthorized => (RepoNotFoundMessage(), false),
            HttpStatusCode.NotFound => ("El repositorio o el archivo no existe en HuggingFace (404).", false),
            HttpStatusCode.TooManyRequests =>
                ("HuggingFace está limitando las peticiones (429). Probá de nuevo en unos minutos.", false),
            _ => (string.IsNullOrWhiteSpace(errorMessage)
                    ? $"HuggingFace respondió {(int)response.StatusCode} ({response.StatusCode})."
                    : $"HuggingFace respondió {(int)response.StatusCode}: {errorMessage}", false)
        };
    }

    private static string GetHeader(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values)
            ? string.Join("", values) : "";

    /// <summary>
    /// Mensaje para repos gated. Explícito y accionable: el fallo no se esconde.
    /// </summary>
    internal static string GatedMessage() =>
        "Este modelo requiere aceptar su licencia en huggingface.co y un token de acceso. " +
        "Entrá al repositorio con tu cuenta, aceptá los términos, y cargá un token de lectura " +
        "en la configuración para poder descargarlo.";

    /// <summary>
    /// Mensaje para 401 sin marca de gated. HuggingFace no distingue entre
    /// "no existe" y "es privado", así que el mensaje tampoco lo inventa.
    /// </summary>
    internal static string RepoNotFoundMessage() =>
        "El repositorio no existe, es privado, o requiere autenticación. " +
        "Verificá el nombre; si es privado, cargá un token de acceso en la configuración.";

    // --- Utilidades de parseo ---

    private static string GetString(JsonElement el, params string[] props)
    {
        foreach (var p in props)
        {
            if (el.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String)
                return v.GetString() ?? "";
        }
        return "";
    }

    private static int GetInt(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i)
            ? i : 0;

    private static string DeriveAuthor(string repoId)
    {
        int i = repoId.IndexOf('/');
        return i > 0 ? repoId.Substring(0, i) : "";
    }

    /// <summary>
    /// La licencia puede venir en cardData.license o como tag "license:xxx".
    /// </summary>
    private static string ExtractLicense(JsonElement el)
    {
        if (el.TryGetProperty("cardData", out var card) &&
            card.ValueKind == JsonValueKind.Object &&
            card.TryGetProperty("license", out var lic))
        {
            if (lic.ValueKind == JsonValueKind.String)
                return lic.GetString() ?? "no declarada";
            if (lic.ValueKind == JsonValueKind.Array)
            {
                var first = lic.EnumerateArray().FirstOrDefault();
                if (first.ValueKind == JsonValueKind.String)
                    return first.GetString() ?? "no declarada";
            }
        }

        if (el.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array)
        {
            foreach (var t in tags.EnumerateArray())
            {
                if (t.ValueKind != JsonValueKind.String) continue;
                string s = t.GetString() ?? "";
                if (s.StartsWith("license:", StringComparison.OrdinalIgnoreCase))
                    return s.Substring("license:".Length);
            }
        }

        return "no declarada";
    }

    private static bool IsGatedFlag(JsonElement el)
    {
        if (!el.TryGetProperty("gated", out var g)) return false;
        // La API devuelve false, o bien "auto"/"manual" cuando está restringido
        return g.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.String => !string.Equals(g.GetString(), "false", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    public void Dispose()
    {
        if (_ownsClient) _http.Dispose();
    }
}
