using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace PRImager.AIAssistant.Services.ImageGen;

/// <summary>
/// Habla con la API nativa de sd-server: alta del job, polling y recuperación de la
/// imagen. Se eligió la API nativa sobre las compatibles con OpenAI y WebUI porque es
/// la única asíncrona, y por lo tanto la única que no bloquea mientras genera.
///
/// Lo que la API NO da, y hay que resolver afuera:
///  · progreso por paso  -> lo emite ImageServerManager parseando stdout
///  · cancelar en vuelo  -> ImageServerManager.CancelAndRestartAsync
/// </summary>
public sealed class ImageGenClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly ImageServerManager _manager;
    private bool _disposed;

    public ImageGenClient(ImageServerManager manager)
    {
        _manager = manager;
        // Sin timeout corto: una generación puede tardar decenas de segundos y el
        // polling se hace con peticiones separadas, pero el POST inicial puede
        // demorar si el motor está ocupado.
        _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
    }

    /// <summary>
    /// Genera una imagen y la escribe en <paramref name="outputPath"/>.
    ///
    /// La cancelación tiene dos caminos según dónde esté el job, porque el servidor
    /// solo acepta cancelar lo que sigue en cola:
    ///  · en cola      -> POST /cancel, que devuelve 200 y lo marca cancelled
    ///  · generando    -> la API devuelve 409, así que se mata y relanza el proceso
    /// </summary>
    public async Task<ImageGenResult> GenerateAsync(
        ImageGenRequest request,
        string outputPath,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (string.IsNullOrWhiteSpace(request.Prompt))
        {
            // El servidor acepta prompt vacío y crea el job igual (verificado en E4.0),
            // así que la validación tiene que estar de este lado.
            return ImageGenResult.Fail("Escribí un prompt antes de generar.");
        }

        // Este límite se comprueba acá y no solo en la interfaz porque no es una
        // advertencia: por encima de MaxSafeDimension el motor muere con un error de
        // CUDA y se lleva puesta la sesión. Un aviso que el llamador pueda ignorar no
        // alcanza para algo que no se puede deshacer.
        int lado = Math.Max(request.Width, request.Height);
        if (lado > ImageVramArbiter.MaxSafeDimension)
        {
            return ImageGenResult.Fail(
                $"El motor no soporta {request.Width}×{request.Height}. El máximo es " +
                $"{ImageVramArbiter.MaxSafeDimension} píxeles de lado: por encima se cae con un " +
                "error de CUDA y hay que reiniciarlo.");
        }

        if (!_manager.IsReady)
            return ImageGenResult.Fail("El motor de imágenes no está listo.");

        // El manager necesita saber cuántos pasos se pidieron para poder distinguir la
        // barra del muestreo de las otras que el motor dibuja igual —la carga de tensores
        // y la decodificación del VAE en tiles—. Se declara acá y no en la interfaz para
        // que cualquier llamador tenga el progreso bien, no solo la vista.
        _manager.BeginJob(request.Steps);
        try
        {
            return await GenerateCoreAsync(request, outputPath, ct).ConfigureAwait(false);
        }
        finally
        {
            // Sin esto, las barras que el motor sigue imprimiendo después del trabajo
            // —o las del siguiente, si tuviera otro número de pasos— se informarían como
            // progreso de una generación que ya terminó.
            _manager.EndJob();
        }
    }

    private async Task<ImageGenResult> GenerateCoreAsync(
        ImageGenRequest request, string outputPath, CancellationToken ct)
    {
        var baseUrl = _manager.BaseUrl;
        string jobId;

        try
        {
            var cuerpo = BuildRequestBody(request);
            using var contenido = new StringContent(cuerpo, Encoding.UTF8, "application/json");
            using var resp = await _httpClient
                .PostAsync($"{baseUrl}/sdcpp/v1/img_gen", contenido, ct)
                .ConfigureAwait(false);

            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
                return ImageGenResult.Fail($"El motor rechazó la solicitud (HTTP {(int)resp.StatusCode}).");

            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("id", out var idProp))
                return ImageGenResult.Fail("El motor no devolvió un identificador de trabajo.");

            jobId = idProp.GetString() ?? "";
            if (string.IsNullOrEmpty(jobId))
                return ImageGenResult.Fail("El motor devolvió un identificador de trabajo vacío.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return ImageGenResult.Cancelled();
        }
        catch (Exception ex)
        {
            return ImageGenResult.Fail($"No se pudo contactar al motor de imágenes: {ex.Message}");
        }

        // --- polling ---
        try
        {
            while (true)
            {
                if (ct.IsCancellationRequested)
                    return await CancelJobAsync(baseUrl, jobId).ConfigureAwait(false);

                var (estado, resultJson, error) = await PollAsync(baseUrl, jobId, ct).ConfigureAwait(false);

                switch (estado)
                {
                    case ImageJobStatus.Completed:
                        return await WriteImageAsync(resultJson, outputPath, request.Seed, ct)
                            .ConfigureAwait(false);

                    case ImageJobStatus.Failed:
                        return ImageGenResult.Fail(
                            string.IsNullOrWhiteSpace(error)
                                ? "El motor no pudo generar la imagen."
                                : $"El motor no pudo generar la imagen: {error}");

                    case ImageJobStatus.Cancelled:
                        return ImageGenResult.Cancelled();

                    case ImageJobStatus.Unknown:
                        return ImageGenResult.Fail("Se perdió el contacto con el motor de imágenes.");
                }

                try { await Task.Delay(250, ct).ConfigureAwait(false); }
                catch (OperationCanceledException)
                {
                    return await CancelJobAsync(baseUrl, jobId).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Cancelar mientras la consulta de estado está EN VUELO no es un error: es
            // el mismo pedido del usuario, que llegó en otro momento del ciclo.
            //
            // Sin este catch la excepción caía en el genérico de abajo y el usuario veía
            // «Error mientras se generaba la imagen: The operation was canceled.» —un
            // error, y en inglés— en lugar de «Generación cancelada.». Pasaba solo cuando
            // el cancelar coincidía con el momento del poll, así que aparecía de a
            // ratos: en el banco de las diez alternancias salió una sola vez, en la
            // primera vuelta, que es la más lenta porque carga el modelo en frío.
            return await CancelJobAsync(baseUrl, jobId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return ImageGenResult.Fail($"Error mientras se generaba la imagen: {ex.Message}");
        }
    }

    /// <summary>
    /// Intenta cancelar por API; si el job ya está generando el servidor responde 409
    /// y no hay más remedio que reiniciar el proceso.
    /// </summary>
    private async Task<ImageGenResult> CancelJobAsync(string baseUrl, string jobId)
    {
        try
        {
            using var resp = await _httpClient
                .PostAsync($"{baseUrl}/sdcpp/v1/jobs/{jobId}/cancel", content: null)
                .ConfigureAwait(false);

            if (resp.IsSuccessStatusCode)
                return ImageGenResult.Cancelled();   // estaba en cola
        }
        catch
        {
            // Da igual por qué falló: se cae al camino duro.
        }

        // Estaba generando. Único camino que corta de verdad.
        await _manager.CancelAndRestartAsync().ConfigureAwait(false);
        return ImageGenResult.Cancelled();
    }

    private async Task<(ImageJobStatus, string?, string?)> PollAsync(
        string baseUrl, string jobId, CancellationToken ct)
    {
        try
        {
            using var resp = await _httpClient
                .GetAsync($"{baseUrl}/sdcpp/v1/jobs/{jobId}", ct)
                .ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
                return (ImageJobStatus.Unknown, null, null);

            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var raiz = doc.RootElement;

            var estado = raiz.TryGetProperty("status", out var s)
                ? ParseStatus(s.GetString())
                : ImageJobStatus.Unknown;

            string? error = null;
            if (raiz.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String)
                error = e.GetString();

            // El resultado se devuelve entero para no volver a pedirlo: viene en la
            // misma respuesta que informa 'completed'.
            return (estado, estado == ImageJobStatus.Completed ? json : null, error);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return (ImageJobStatus.Unknown, null, null);
        }
    }

    private static ImageJobStatus ParseStatus(string? s) => s switch
    {
        "queued" => ImageJobStatus.Queued,
        "generating" => ImageJobStatus.Generating,
        "completed" => ImageJobStatus.Completed,
        "failed" => ImageJobStatus.Failed,
        "cancelled" => ImageJobStatus.Cancelled,
        _ => ImageJobStatus.Unknown
    };

    /// <summary>
    /// La imagen vuelve en base64 dentro de result.images[0].b64_json. Se escribe a un
    /// archivo temporal y recién al final se mueve al nombre definitivo, con el mismo
    /// criterio del .part de las descargas de E3: nadie debe encontrarse un PNG a
    /// medias si el proceso se corta.
    /// </summary>
    private static async Task<ImageGenResult> WriteImageAsync(
        string? json, string outputPath, long seedPedida, CancellationToken ct)
    {
        if (json is null) return ImageGenResult.Fail("El motor informó éxito sin devolver la imagen.");

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("result", out var result) ||
                !result.TryGetProperty("images", out var images) ||
                images.ValueKind != JsonValueKind.Array ||
                images.GetArrayLength() == 0)
            {
                return ImageGenResult.Fail("El motor informó éxito sin devolver la imagen.");
            }

            var primera = images[0];
            if (!primera.TryGetProperty("b64_json", out var b64Prop))
                return ImageGenResult.Fail("La respuesta del motor no trae los datos de la imagen.");

            var b64 = b64Prop.GetString();
            if (string.IsNullOrEmpty(b64))
                return ImageGenResult.Fail("La respuesta del motor trae la imagen vacía.");

            var bytes = Convert.FromBase64String(b64);

            var carpeta = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(carpeta)) Directory.CreateDirectory(carpeta);

            var parcial = outputPath + ".part";
            await File.WriteAllBytesAsync(parcial, bytes, ct).ConfigureAwait(false);
            File.Move(parcial, outputPath, overwrite: true);

            return ImageGenResult.Ok(outputPath, seedPedida);
        }
        catch (OperationCanceledException)
        {
            // Mismo criterio que en el polling: una cancelación no se informa como
            // fallo de guardado. Se propaga para que el llamador la trate como tal.
            throw;
        }
        catch (Exception ex)
        {
            return ImageGenResult.Fail($"No se pudo guardar la imagen: {ex.Message}");
        }
    }

    private static string BuildRequestBody(ImageGenRequest r)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteString("prompt", r.Prompt);
            if (!string.IsNullOrEmpty(r.NegativePrompt))
                w.WriteString("negative_prompt", r.NegativePrompt);
            w.WriteNumber("width", r.Width);
            w.WriteNumber("height", r.Height);
            w.WriteNumber("seed", r.Seed);

            w.WriteStartObject("sample_params");
            w.WriteNumber("sample_steps", r.Steps);
            if (!string.IsNullOrEmpty(r.SampleMethod)) w.WriteString("sample_method", r.SampleMethod);
            if (!string.IsNullOrEmpty(r.Scheduler)) w.WriteString("scheduler", r.Scheduler);

            w.WriteStartObject("guidance");
            w.WriteNumber("txt_cfg", r.CfgScale);
            w.WriteEndObject();

            w.WriteEndObject();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// Interpreta /sdcpp/v1/capabilities. Es estático y público porque
    /// ImageServerManager lo usa como health check antes de que exista un cliente.
    /// </summary>
    public static ImageServerCapabilities? ParseCapabilities(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var raiz = doc.RootElement;

            var nombre = "";
            if (raiz.TryGetProperty("model", out var modelo))
            {
                if (modelo.TryGetProperty("name", out var n)) nombre = n.GetString() ?? "";
                else if (modelo.TryGetProperty("stem", out var st)) nombre = st.GetString() ?? "";
            }

            var caps = new ImageServerCapabilities
            {
                ModelName = nombre,
                Samplers = LeerLista(raiz, "samplers"),
                Schedulers = LeerLista(raiz, "schedulers"),
                MinWidth = LeerLimite(raiz, "min_width"),
                MaxWidth = LeerLimite(raiz, "max_width"),
                MinHeight = LeerLimite(raiz, "min_height"),
                MaxHeight = LeerLimite(raiz, "max_height"),
                MaxBatchCount = LeerLimite(raiz, "max_batch_count")
            };
            return caps;
        }
        catch
        {
            return null;
        }
    }

    private static List<string> LeerLista(JsonElement raiz, string propiedad)
    {
        var lista = new List<string>();
        if (raiz.TryGetProperty(propiedad, out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in arr.EnumerateArray())
                if (item.ValueKind == JsonValueKind.String)
                    lista.Add(item.GetString() ?? "");
        }
        return lista;
    }

    private static int LeerLimite(JsonElement raiz, string propiedad)
    {
        if (raiz.TryGetProperty("limits", out var lim) &&
            lim.TryGetProperty(propiedad, out var v) &&
            v.TryGetInt32(out var n))
        {
            return n;
        }
        return 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _httpClient.Dispose();
    }
}
