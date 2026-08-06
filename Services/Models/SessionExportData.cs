using System;
using System.Collections.Generic;
using System.Linq;

namespace PRImager.AIAssistant.Services.Models;

/// <summary>
/// Contiene todos los datos necesarios para exportar una sesión de investigación.
/// </summary>
public record SessionExportData
{
    public required InvestigationSession Session { get; init; }
    public required List<ExportMessage> Messages { get; init; }
    public required List<ExportArtifact> Artifacts { get; init; }
    public DateTime ExportTimeUtc { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// Modelo con el que se produjo la sesión, para la declaración del reporte.
    /// </summary>
    public string ModelName { get; init; } = "modelo local";
    
    // Estadísticas
    public int TotalQueries => Messages.Count(m => m.Role == "user");
    public int TotalResponses => Messages.Count(m => m.Role == "assistant");
    public long TotalInferenceMs { get; init; }
    public int TotalTokensEstimated { get; init; }
}

public record ExportMessage
{
    public string Role { get; init; } = "";        // "user", "assistant", "system"
    public string Content { get; init; } = "";
    public DateTime Timestamp { get; init; }
    public bool HasImages { get; init; }
}

public record ExportArtifact
{
    public string FileName { get; init; } = "";
    public string FileType { get; init; } = "";
    public long FileSize { get; init; }
    public string? Notes { get; init; }
}

public enum ExportFormat { Txt, Html, Json }

public record ExportResult
{
    public bool Success { get; init; }
    public string? FilePath { get; init; }
    public string? FileHash { get; init; }  // SHA-256 del archivo generado
    public string? ErrorMessage { get; init; }
}
