namespace PRImager.AIAssistant.Services.Models;

/// <summary>
/// Datos de la sesión de investigación forense.
/// Se genera al completar el formulario obligatorio del Modo Investigación.
/// Inmutable una vez creado — no se puede modificar durante la sesión.
/// </summary>
public record InvestigationSession
{
    // Campos obligatorios
    public required string PeritoName { get; init; }
    public required string PeritoBadge { get; init; }    // Legajo / N° credencial
    public required string CaseNumber { get; init; }      // N° de causa / expediente
    
    // Campos opcionales
    public string? Court { get; init; }                    // Juzgado / Fiscalía
    public string? Description { get; init; }              // Descripción breve del análisis
    
    // Generados automáticamente
    public DateTime StartTimeUtc { get; init; } = DateTime.UtcNow;
    public string StartTimeLocal => StartTimeUtc.ToLocalTime().ToString("yyyy-MM-ddTHH:mm:sszzz");
    public string SessionId { get; init; } = Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
    
    // Tracking
    public bool DisclaimerAccepted { get; init; } = false;
    public DateTime? DisclaimerAcceptedTimeUtc { get; init; }
    
    // Hash del disclaimer aceptado (para auditoría)
    public string? DisclaimerHash { get; init; }
}
