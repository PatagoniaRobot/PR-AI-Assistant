namespace PRImager.AIAssistant.Services.Models;

/// <summary>
/// Imagen adjunta a un mensaje de chat, codificada en base64.
/// </summary>
public class ChatImage
{
    /// <summary>Datos de la imagen en base64.</summary>
    public required string Base64Data { get; init; }

    /// <summary>Tipo MIME (ej: "image/jpeg", "image/png").</summary>
    public required string MediaType { get; init; }
}

/// <summary>
/// Mensaje individual dentro de una conversación.
/// </summary>
public class ChatMessage
{
    /// <summary>Rol: "system", "user" o "assistant".</summary>
    public required string Role { get; init; }

    /// <summary>Contenido textual del mensaje.</summary>
    public required string Content { get; init; }

    /// <summary>Imágenes adjuntas (solo para mensajes de usuario).</summary>
    public List<ChatImage>? Images { get; init; }
}

/// <summary>
/// Solicitud de chat hacia llama-server.
/// </summary>
public class ChatRequest
{
    /// <summary>Prompt de sistema.</summary>
    public string? SystemPrompt { get; init; }

    /// <summary>Lista de mensajes de la conversación.</summary>
    public required List<ChatMessage> Messages { get; init; }

    /// <summary>Temperatura de muestreo (0.0 - 2.0).</summary>
    public float Temperature { get; init; } = 0.7f;

    /// <summary>Máximo de tokens a generar.</summary>
    public int MaxTokens { get; init; } = 4096;

    /// <summary>Si es true, la respuesta se envía en streaming SSE.</summary>
    public bool Stream { get; init; } = true;

    /// <summary>Si es true, habilita el modo "thinking" del modelo (cadena de razonamiento visible).</summary>
    public bool EnableThinking { get; init; } = false;
}

/// <summary>
/// Respuesta completa de chat (modo no-streaming).
/// </summary>
public class ChatResponse
{
    /// <summary>Texto generado por el modelo.</summary>
    public required string Content { get; init; }

    /// <summary>Tokens consumidos por el prompt.</summary>
    public int PromptTokens { get; init; }

    /// <summary>Tokens generados en la respuesta.</summary>
    public int CompletionTokens { get; init; }

    /// <summary>Tiempo total de inferencia en milisegundos.</summary>
    public long ElapsedMs { get; init; }
}
