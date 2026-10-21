using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PRImager.AIAssistant.Services.Enums;
using PRImager.AIAssistant.Services.Models;

namespace PRImager.AIAssistant.Services;

/// <summary>
/// Cliente HTTP que se comunica con la API OpenAI-compatible de llama-server.
/// Soporta streaming SSE y mensajes con imágenes en base64.
/// </summary>
public class LlamaChatClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly string _baseUrl;
    private bool _disposed;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    /// <summary>Notifica cada token recibido durante streaming.</summary>
    public event EventHandler<TokenReceivedEventArgs>? TokenReceived;

    /// <summary>
    /// Notifica cada token de razonamiento. Va por un canal aparte porque no es
    /// respuesta: no se muestra como contenido ni se guarda en el historial, pero
    /// sin él la pantalla queda muda mientras el modelo piensa.
    /// </summary>
    public event EventHandler<TokenReceivedEventArgs>? ReasoningTokenReceived;

    public LlamaChatClient(string baseUrl = "http://127.0.0.1:8787")
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _httpClient = new HttpClient
        {
            Timeout = Timeout.InfiniteTimeSpan // El timeout se controla con CancellationToken
        };
    }

    /// <summary>
    /// Envía un mensaje en modo streaming. Devuelve tokens a medida que llegan via SSE.
    /// </summary>
    public async IAsyncEnumerable<string> SendMessageStreamAsync(
        ChatRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var body = BuildRequestBody(request, stream: true);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/v1/chat/completions")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

        HttpResponseMessage response;
        try
        {
            // ConfigureAwait(false) para no capturar el SynchronizationContext del UI thread
            response = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException(
                Loc.F("llama.connectError", _baseUrl, ex.Message), ex);
        }

        using (response)
        {
            var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            // El fin del stream se detecta por el null de ReadLineAsync: EndOfStream lee
            // en forma sincrónica y bloquea el hilo mientras llama-server piensa (CA2024).
            while (true)
            {
                ct.ThrowIfCancellationRequested();

                var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);

                if (line is null)
                    break;

                if (line.Length == 0)
                    continue;

                // Formato SSE: "data: {json}" o "data: [DONE]"
                if (!line.StartsWith("data: ", StringComparison.Ordinal))
                    continue;

                var data = line.Substring(6); // Saltar "data: "

                if (data == "[DONE]")
                    yield break;

                // Parsear el JSON del chunk
                string? token = null;
                string? reasoning = null;
                try
                {
                    var node = JsonNode.Parse(data);
                    var delta = node?["choices"]?[0]?["delta"];
                    token = delta?["content"]?.GetValue<string>();
                    reasoning = delta?["reasoning_content"]?.GetValue<string>();
                }
                catch (JsonException)
                {
                    // Línea con JSON inválido, ignorar
                    continue;
                }

                // El razonamiento se notifica pero no se emite: no es la respuesta.
                if (!string.IsNullOrEmpty(reasoning))
                    ReasoningTokenReceived?.Invoke(this, new TokenReceivedEventArgs(reasoning));

                if (token is not null)
                {
                    TokenReceived?.Invoke(this, new TokenReceivedEventArgs(token));
                    yield return token;
                }
            }
        }
    }

    /// <summary>
    /// Envía un mensaje y espera la respuesta completa (sin streaming).
    /// </summary>
    public async Task<ChatResponse> SendMessageAsync(
        ChatRequest request,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var body = BuildRequestBody(request, stream: false);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/v1/chat/completions")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

        var stopwatch = Stopwatch.StartNew();

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(httpRequest, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException(
                Loc.F("llama.connectError", _baseUrl, ex.Message), ex);
        }

        using (response)
        {
            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            stopwatch.Stop();

            try
            {
                var node = JsonNode.Parse(json);

                var content = node?["choices"]?[0]?["message"]?["content"]?.GetValue<string>() ?? string.Empty;
                var promptTokens = node?["usage"]?["prompt_tokens"]?.GetValue<int>() ?? 0;
                var completionTokens = node?["usage"]?["completion_tokens"]?.GetValue<int>() ?? 0;

                return new ChatResponse
                {
                    Content = content,
                    PromptTokens = promptTokens,
                    CompletionTokens = completionTokens,
                    ElapsedMs = stopwatch.ElapsedMilliseconds
                };
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException(
                    Loc.F("llama.badResponse", json[..Math.Min(json.Length, 200)]), ex);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _httpClient.Dispose();
        GC.SuppressFinalize(this);
    }

    // --- Métodos privados ---

    /// <summary>
    /// Construye el body JSON para la API OpenAI-compatible de llama-server.
    /// </summary>
    private static string BuildRequestBody(ChatRequest request, bool stream)
    {
        var messages = new JsonArray();

        // Agregar system prompt si existe
        if (!string.IsNullOrEmpty(request.SystemPrompt))
        {
            messages.Add(new JsonObject
            {
                ["role"] = "system",
                ["content"] = request.SystemPrompt
            });
        }

        // Agregar mensajes de la conversación
        foreach (var msg in request.Messages)
        {
            if (msg.Images is { Count: > 0 })
            {
                // Mensaje multimodal con imágenes
                var contentArray = new JsonArray();

                // Texto del mensaje
                contentArray.Add(new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = msg.Content
                });

                // Imágenes en base64
                foreach (var image in msg.Images)
                {
                    contentArray.Add(new JsonObject
                    {
                        ["type"] = "image_url",
                        ["image_url"] = new JsonObject
                        {
                            ["url"] = $"data:{image.MediaType};base64,{image.Base64Data}"
                        }
                    });
                }

                messages.Add(new JsonObject
                {
                    ["role"] = msg.Role,
                    ["content"] = contentArray
                });
            }
            else
            {
                // Mensaje de texto simple
                messages.Add(new JsonObject
                {
                    ["role"] = msg.Role,
                    ["content"] = msg.Content
                });
            }
        }

        var body = new JsonObject
        {
            ["model"] = "local",
            ["messages"] = messages,
            ["stream"] = stream,
            ["temperature"] = request.Temperature,
            ["max_tokens"] = request.MaxTokens,

            // Control del modo razonamiento. Los modelos híbridos (familia Qwen3 y
            // similares) traen el pensamiento ACTIVADO en su plantilla: el servidor
            // registra "chat template, thinking = 1" al arrancar. Con el pensamiento
            // activo el texto no viaja en delta.content sino en delta.reasoning_content,
            // así que sin este parámetro la app no recibía un solo token y se quedaba
            // en "Pensando…" hasta agotar max_tokens.
            //
            // Las plantillas que no conocen enable_thinking ignoran el kwarg, así que
            // mandarlo siempre es inocuo para Gemma y compañía.
            ["chat_template_kwargs"] = new JsonObject
            {
                ["enable_thinking"] = request.EnableThinking
            }
        };

        return body.ToJsonString();
    }
}
