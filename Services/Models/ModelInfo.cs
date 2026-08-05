namespace PRImager.AIAssistant.Services.Models;

/// <summary>
/// Información de un modelo de IA, independiente del perfil de ejecución.
/// Se deserializa desde la sección "models" de model_catalog.json v2.
/// </summary>
public class ModelInfo
{
    /// <summary>ID único del modelo (ej: "e2b", "e4b").</summary>
    public string Id { get; set; } = "";

    /// <summary>Nombre amigable mostrado al usuario (ej: "Gemma 4 E4B (4.5B efectivos)").</summary>
    public string DisplayName { get; set; } = "";

    /// <summary>Nombre del archivo .gguf dentro de la carpeta Models/.</summary>
    public string ModelFileName { get; set; } = "";

    /// <summary>Nombre del archivo mmproj si el modelo soporta visión. Vacío = solo texto.</summary>
    public string MmprojFileName { get; set; } = "";

    /// <summary>true si el modelo requiere GPU para ser usable.</summary>
    public bool RequiresGpu { get; set; }

    /// <summary>Etiqueta de requisitos mostrada en la UI (ej: "⚡ GPU recomendada").</summary>
    public string HardwareTag { get; set; } = "";

    /// <summary>Capacidades del modelo (ej: ["Vision", "Audio"]).</summary>
    public List<string> Capabilities { get; set; } = new();

    /// <summary>true si soporta análisis de imágenes (multimodal).</summary>
    public bool SupportsVision => !string.IsNullOrEmpty(MmprojFileName);
}
