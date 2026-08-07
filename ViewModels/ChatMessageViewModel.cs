using System;
using System.Collections.Generic;
using System.Linq;
using PRImager.AIAssistant.Helpers;

namespace PRImager.AIAssistant.ViewModels;

/// <summary>
/// Representa un mensaje individual en la conversación del chat.
/// </summary>
public class ChatMessageViewModel : ObservableObject
{
    private readonly System.Text.StringBuilder _builder = new();
    private string? _content = string.Empty;
    private bool _isStreaming;

    public string Role { get; }
    public DateTime Timestamp { get; }
    public List<string>? ImagePaths { get; }
    
    public bool IsUser => Role == "user";
    public bool IsAssistant => Role == "assistant";
    public bool IsSystem => Role == "system";
    
    /// <summary>
    /// Modelo que generó este mensaje. Se fija al crearlo, no se lee del estado
    /// actual: si el perito cambia de modelo a mitad de sesión, cada respuesta debe
    /// seguir declarando con cuál se produjo.
    /// </summary>
    public string? ModelDisplayName { get; }

    // Para el disclaimer
    public bool HasDisclaimer => IsAssistant;

    public string DisclaimerText =>
        "\n\n---\n[PR-AI Assistant – Generado por IA]\n" +
        $"Este contenido fue generado por inteligencia artificial ({ModelLabel}) " +
        "ejecutándose localmente. Debe ser verificado por el perito antes de " +
        "incorporarse a cualquier informe o actuación judicial.";

    private string ModelLabel => string.IsNullOrWhiteSpace(ModelDisplayName)
        ? "modelo local"
        : ModelDisplayName;

    /// <summary>
    /// Texto del mensaje. Se respalda en un StringBuilder porque el streaming crece
    /// por el final: concatenar con += copiaba todo lo acumulado en cada actualización.
    /// La cadena materializada se cachea hasta el próximo append.
    /// </summary>
    public string Content
    {
        get => _content ??= _builder.ToString();
        set
        {
            value ??= string.Empty;
            if (Content == value) return;

            _builder.Clear();
            _builder.Append(value);
            _content = value;

            OnPropertyChanged(nameof(Content));
            OnPropertyChanged(nameof(IsWaitingForFirstToken));
        }
    }

    /// <summary>
    /// Agrega texto al final sin recomponer lo ya acumulado. Es el camino del streaming.
    /// </summary>
    public void AppendContent(string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        _builder.Append(text);
        _content = null;

        OnPropertyChanged(nameof(Content));
        OnPropertyChanged(nameof(IsWaitingForFirstToken));
    }

    public bool IsStreaming
    {
        get => _isStreaming;
        set
        {
            if (SetProperty(ref _isStreaming, value))
            {
                OnPropertyChanged(nameof(RenderMode));
                OnPropertyChanged(nameof(IsWaitingForFirstToken));
            }
        }
    }

    public bool IsWaitingForFirstToken => IsStreaming && string.IsNullOrWhiteSpace(Content);

    private string _generationMetrics = string.Empty;
    public string GenerationMetrics
    {
        get => _generationMetrics;
        set => SetProperty(ref _generationMetrics, value);
    }

    /// <summary>
    /// Indica el modo de renderizado: "streaming" durante la generación, "markdown" al finalizar.
    /// </summary>
    public string RenderMode => IsStreaming ? "streaming" : "markdown";

    // === Fuentes web (E2) ===

    private List<Services.Models.WebSource>? _webSources;

    /// <summary>
    /// Fuentes web que alimentaron esta respuesta. El usuario debe poder verificarlas.
    /// </summary>
    public List<Services.Models.WebSource>? WebSources
    {
        get => _webSources;
        set
        {
            if (SetProperty(ref _webSources, value))
            {
                OnPropertyChanged(nameof(HasWebSources));
                OnPropertyChanged(nameof(WebSourcesHeader));
            }
        }
    }

    /// <summary>True si esta respuesta se construyó con búsqueda web.</summary>
    public bool HasWebSources => _webSources is { Count: > 0 };

    /// <summary>Encabezado del panel de fuentes, con cuántas se leyeron efectivamente.</summary>
    public string WebSourcesHeader
    {
        get
        {
            if (_webSources == null || _webSources.Count == 0) return "";
            int read = _webSources.Count(s => s.WasRead);
            return $"🌐 Fuentes web ({read} de {_webSources.Count} leídas)";
        }
    }

    private bool _areSourcesExpanded;

    /// <summary>Estado del panel colapsable de fuentes.</summary>
    public bool AreSourcesExpanded
    {
        get => _areSourcesExpanded;
        set => SetProperty(ref _areSourcesExpanded, value);
    }

    public ChatMessageViewModel(string role, string content, List<string>? imagePaths = null,
                                string? modelDisplayName = null)
    {
        Role = role;
        Content = content;
        Timestamp = DateTime.Now;
        ImagePaths = imagePaths;
        ModelDisplayName = modelDisplayName;
    }
}
