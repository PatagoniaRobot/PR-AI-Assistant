namespace PRImager.AIAssistant.Services.Models;

/// <summary>
/// Fuente web incorporada a una respuesta, con el número de cita que le tocó.
/// Es lo que muestra el panel de fuentes para que el usuario pueda verificar.
///
/// Vive acá y no en Services/WebSearch/ a propósito: es un objeto de datos sin
/// código de red, así que puede compilar también en la configuración Forense,
/// donde el resto del subsistema de búsqueda no existe.
/// </summary>
public sealed class WebSource
{
    /// <summary>Número de cita [n] usado en el bloque de contexto.</summary>
    public int Index { get; set; }

    public string Title { get; set; } = "";
    public string Url { get; set; } = "";
    public string PublishedDate { get; set; } = "sin fecha";

    /// <summary>True si se pudo descargar y extraer el texto de la página.</summary>
    public bool WasRead { get; set; }

    /// <summary>Motivo por el que no se pudo leer, si corresponde.</summary>
    public string? FailureReason { get; set; }

    /// <summary>Texto para la UI: "leída" o el motivo del fallo.</summary>
    public string StatusText => WasRead ? "leída" : (FailureReason ?? "no leída");
}
