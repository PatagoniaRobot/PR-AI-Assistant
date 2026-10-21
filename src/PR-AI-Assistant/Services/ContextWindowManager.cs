using System;
using System.Collections.Generic;
using System.Linq;
using PRImager.AIAssistant.Services.Models;

namespace PRImager.AIAssistant.Services;

/// <summary>
/// Gestiona el uso de la ventana de contexto del modelo.
/// Recorta mensajes antiguos cuando el historial se acerca al límite.
/// Estimación: ~4 caracteres = 1 token (aproximación para español/inglés mixto).
/// </summary>
public class ContextWindowManager
{
    private const int CharsPerToken = 4;

    /// <summary>Máximo de tokens para el contexto total (ctx_size del servidor).</summary>
    public int MaxContextTokens { get; set; } = 16384;

    /// <summary>Tokens reservados para la respuesta del modelo (max_tokens del request).</summary>
    public int ReservedResponseTokens { get; set; } = 8192;

    /// <summary>Margen de seguridad (tokens extra que se dejan libres).</summary>
    public int SafetyMargin { get; set; } = 512;

    /// <summary>Tokens disponibles para system prompt + historial + contexto forense.</summary>
    public int AvailableInputTokens => MaxContextTokens - ReservedResponseTokens - SafetyMargin;

    /// <summary>Estima los tokens de un texto.</summary>
    public int EstimateTokens(string? text) => (text?.Length ?? 0) / CharsPerToken;

    /// <summary>
    /// Recorta el historial de mensajes para que quepa en la ventana de contexto.
    /// Mantiene siempre: el system prompt, el contexto forense, y los últimos N mensajes.
    /// Recorta los mensajes más antiguos primero.
    /// Retorna la lista de mensajes recortada lista para enviar al modelo.
    /// </summary>
    public List<ChatMessage> TrimHistory(
        string systemPrompt,
        string? forensicContext,
        List<ChatMessage> fullHistory)
    {
        int systemTokens = EstimateTokens(systemPrompt);
        int contextTokens = EstimateTokens(forensicContext);
        int available = AvailableInputTokens - systemTokens - contextTokens;

        if (available <= 0)
        {
            // No queda espacio ni para un mensaje — retornar solo el último
            return fullHistory.Count > 0
                ? new List<ChatMessage> { fullHistory[^1] }
                : new List<ChatMessage>();
        }

        // Recorrer desde el más reciente al más antiguo
        var trimmed = new List<ChatMessage>();
        int usedTokens = 0;

        for (int i = fullHistory.Count - 1; i >= 0; i--)
        {
            int msgTokens = EstimateTokens(fullHistory[i].Content);
            if (usedTokens + msgTokens > available)
                break;

            trimmed.Insert(0, fullHistory[i]);
            usedTokens += msgTokens;
        }

        return trimmed;
    }

    /// <summary>
    /// Calcula estadísticas del uso actual de la ventana de contexto.
    /// </summary>
    public ContextUsage GetUsage(
        string systemPrompt,
        string? forensicContext,
        List<ChatMessage> history)
    {
        int systemTokens = EstimateTokens(systemPrompt);
        int contextTokens = EstimateTokens(forensicContext);
        int historyTokens = history.Sum(m => EstimateTokens(m.Content));
        int totalInput = systemTokens + contextTokens + historyTokens;

        return new ContextUsage
        {
            SystemPromptTokens = systemTokens,
            ForensicContextTokens = contextTokens,
            HistoryTokens = historyTokens,
            TotalInputTokens = totalInput,
            AvailableTokens = AvailableInputTokens,
            UsagePercent = AvailableInputTokens > 0
                ? (double)totalInput / AvailableInputTokens * 100
                : 100,
            IsNearLimit = totalInput > AvailableInputTokens * 0.85,
            IsOverLimit = totalInput > AvailableInputTokens
        };
    }
}

/// <summary>
/// Estadísticas de uso de la ventana de contexto.
/// </summary>
public record ContextUsage
{
    public int SystemPromptTokens { get; init; }
    public int ForensicContextTokens { get; init; }
    public int HistoryTokens { get; init; }
    public int TotalInputTokens { get; init; }
    public int AvailableTokens { get; init; }
    public double UsagePercent { get; init; }
    public bool IsNearLimit { get; init; }
    public bool IsOverLimit { get; init; }
}
