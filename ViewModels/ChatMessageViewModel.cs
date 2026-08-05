using System;
using System.Collections.Generic;
using PRImager.AIAssistant.Helpers;

namespace PRImager.AIAssistant.ViewModels;

/// <summary>
/// Representa un mensaje individual en la conversación del chat.
/// </summary>
public class ChatMessageViewModel : ObservableObject
{
    private string _content = string.Empty;
    private bool _isStreaming;

    public string Role { get; }
    public DateTime Timestamp { get; }
    public List<string>? ImagePaths { get; }
    
    public bool IsUser => Role == "user";
    public bool IsAssistant => Role == "assistant";
    public bool IsSystem => Role == "system";
    
    // Para el disclaimer
    public bool HasDisclaimer => IsAssistant;
    public string DisclaimerText => "\n\n---\n[PR-AI Assistant – Generado por IA]\nEste contenido fue generado por inteligencia artificial (Gemma 4 E2B) ejecutándose localmente. Debe ser verificado por el perito antes de incorporarse a cualquier informe o actuación judicial.";

    public string Content
    {
        get => _content;
        set
        {
            if (SetProperty(ref _content, value))
            {
                OnPropertyChanged(nameof(RenderMode));
                OnPropertyChanged(nameof(IsWaitingForFirstToken));
            }
        }
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

    public ChatMessageViewModel(string role, string content, List<string>? imagePaths = null)
    {
        Role = role;
        Content = content;
        Timestamp = DateTime.Now;
        ImagePaths = imagePaths;
    }
}
