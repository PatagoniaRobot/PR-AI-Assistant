using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PRImager.AIAssistant.Services.HuggingFace;

/// <summary>
/// Descarga archivos GGUF desde HuggingFace por streaming a disco.
///
/// Nunca carga el archivo en memoria: son varios GB. Escribe a un archivo temporal
/// .part y solo lo promueve al nombre final cuando la descarga termina íntegra, de
/// modo que una cancelación o una caída no dejen un GGUF corrupto que parezca válido.
/// </summary>
public sealed class ModelDownloader : IDisposable
{
    /// <summary>Extensión del archivo parcial mientras la descarga está en curso.</summary>
    public const string PartialExtension = ".part";

    private const int BufferSize = 1024 * 256;

    private readonly HttpClient _http;
    private readonly bool _ownsClient;
    private readonly Func<string>? _tokenProvider;

    /// <summary>Progreso de la descarga en curso.</summary>
    public event EventHandler<DownloadProgress>? ProgressChanged;

    public ModelDownloader(HttpClient? http = null, Func<string>? tokenProvider = null)
    {
        _ownsClient = http == null;
        // Sin timeout global: una descarga de varios GB puede durar mucho.
        // La cancelación es responsabilidad del CancellationToken.
        _http = http ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        _tokenProvider = tokenProvider;

        if (!_http.DefaultRequestHeaders.Contains("User-Agent"))
        {
            _http.DefaultRequestHeaders.Add("User-Agent",
                "PR-AI-Assistant/1.0 (+https://patagoniarobot.com; asistente forense local)");
        }
    }

    /// <summary>
    /// Descarga <paramref name="fileName"/> del repo al directorio indicado.
    /// Al completarse calcula el SHA-256 del archivo y devuelve el registro.
    /// </summary>
    public async Task<HfResult<ModelDownloadRecord>> DownloadAsync(
        string repoId,
        string fileName,
        string destinationDirectory,
        CancellationToken ct = default)
    {
        string url = HuggingFaceClient.BuildDownloadUrl(repoId, fileName);
        string finalPath = Path.Combine(destinationDirectory, Path.GetFileName(fileName));
        string partialPath = finalPath + PartialExtension;

        try
        {
            Directory.CreateDirectory(destinationDirectory);

            if (File.Exists(finalPath))
            {
                return HfResult<ModelDownloadRecord>.Fail(
                    $"Ya existe {Path.GetFileName(finalPath)} en la carpeta de modelos. " +
                    "Borralo o renombralo si querés volver a descargarlo.");
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            string? token = _tokenProvider?.Invoke();
            if (!string.IsNullOrWhiteSpace(token))
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

            using var response = await _http.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, ct);

            if (!response.IsSuccessStatusCode)
            {
                var (message, gated) = HuggingFaceClient.DescribeFailure(response);
                return HfResult<ModelDownloadRecord>.Fail(message, gated);
            }

            long total = response.Content.Headers.ContentLength ?? 0;

            await StreamToFileAsync(response, partialPath, total, ct);

            // Solo ahora el archivo pasa a tener el nombre definitivo
            File.Move(partialPath, finalPath);

            string hash = await ComputeSha256Async(finalPath, ct);

            return HfResult<ModelDownloadRecord>.Ok(new ModelDownloadRecord
            {
                RepoId = repoId,
                FileName = Path.GetFileName(finalPath),
                SizeBytes = new FileInfo(finalPath).Length,
                Sha256 = hash,
                CompletedUtc = DateTime.UtcNow,
                DestinationPath = finalPath
            });
        }
        catch (OperationCanceledException)
        {
            DeletePartial(partialPath);
            return HfResult<ModelDownloadRecord>.Fail("Descarga cancelada. No quedó archivo parcial.");
        }
        catch (HttpRequestException ex)
        {
            DeletePartial(partialPath);
            return HfResult<ModelDownloadRecord>.Fail($"Error de red durante la descarga: {ex.Message}");
        }
        catch (IOException ex)
        {
            DeletePartial(partialPath);
            return HfResult<ModelDownloadRecord>.Fail($"Error de disco: {ex.Message}");
        }
        catch (Exception ex)
        {
            DeletePartial(partialPath);
            return HfResult<ModelDownloadRecord>.Fail($"Error inesperado: {ex.Message}");
        }
    }

    private async Task StreamToFileAsync(
        HttpResponseMessage response, string partialPath, long total, CancellationToken ct)
    {
        using var source = await response.Content.ReadAsStreamAsync(ct);
        using var destination = new FileStream(
            partialPath, FileMode.Create, FileAccess.Write, FileShare.None,
            BufferSize, useAsync: true);

        var buffer = new byte[BufferSize];
        long downloaded = 0;
        var watch = Stopwatch.StartNew();
        long lastReportedBytes = 0;
        TimeSpan lastReport = TimeSpan.Zero;

        int read;
        while ((read = await source.ReadAsync(buffer, ct)) > 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, read), ct);
            downloaded += read;

            // Reportar como mucho 4 veces por segundo: la UI no necesita más
            var elapsed = watch.Elapsed;
            if ((elapsed - lastReport).TotalMilliseconds >= 250)
            {
                double seconds = (elapsed - lastReport).TotalSeconds;
                double speed = seconds > 0
                    ? (downloaded - lastReportedBytes) / (1024.0 * 1024.0) / seconds
                    : 0;

                ProgressChanged?.Invoke(this, new DownloadProgress
                {
                    BytesDownloaded = downloaded,
                    TotalBytes = total,
                    SpeedMbPerSecond = speed
                });

                lastReport = elapsed;
                lastReportedBytes = downloaded;
            }
        }

        await destination.FlushAsync(ct);

        ProgressChanged?.Invoke(this, new DownloadProgress
        {
            BytesDownloaded = downloaded,
            TotalBytes = total > 0 ? total : downloaded,
            SpeedMbPerSecond = 0
        });
    }

    /// <summary>
    /// SHA-256 del archivo, leído por streaming para no cargarlo en memoria.
    /// </summary>
    public static async Task<string> ComputeSha256Async(string path, CancellationToken ct = default)
    {
        using var sha = SHA256.Create();
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true);

        byte[] hash = await sha.ComputeHashAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void DeletePartial(string partialPath)
    {
        try
        {
            if (File.Exists(partialPath)) File.Delete(partialPath);
        }
        catch (IOException)
        {
            // Si el handle todavía no se liberó, el .part queda visible pero
            // nunca con el nombre final: no se confunde con un modelo válido
        }
    }

    public void Dispose()
    {
        if (_ownsClient) _http.Dispose();
    }
}

/// <summary>
/// Registra las descargas completadas en un archivo JSON Lines, junto a los logs.
/// Cada línea es un registro independiente: el archivo sobrevive a una escritura
/// interrumpida sin quedar ilegible.
/// </summary>
public sealed class ModelDownloadLog
{
    private readonly string _path;

    public ModelDownloadLog(string logDirectory)
    {
        _path = Path.Combine(logDirectory, "model-downloads.jsonl");
    }

    public string FilePath => _path;

    public void Record(ModelDownloadRecord record)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

            string line = JsonSerializer.Serialize(record, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
            });

            File.AppendAllText(_path, line + Environment.NewLine);
        }
        catch (Exception)
        {
            // El registro no debe tumbar una descarga ya completada
        }
    }
}
