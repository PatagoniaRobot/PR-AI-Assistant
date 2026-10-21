using System.IO;

namespace PRImager.AIAssistant.Services.Models;

/// <summary>
/// Información de un modelo de IA, independiente del perfil de ejecución.
/// Se deserializa desde la sección "models" de model_catalog.json v2, o se
/// construye al vuelo para los .gguf encontrados en la carpeta de descargas.
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

    /// <summary>
    /// Carpeta absoluta donde vive el .gguf, cuando no es la carpeta Models/.
    /// La usan los modelos descargados desde el catálogo de HuggingFace, que se
    /// guardan en la ruta de descargas configurada. Vacío para los del catálogo.
    /// No se deserializa: model_catalog.json nunca la trae.
    /// </summary>
    public string DirectoryOverride { get; set; } = "";

    /// <summary>true si el modelo se descubrió en disco, no en el catálogo.</summary>
    public bool IsDiscovered => !string.IsNullOrEmpty(DirectoryOverride);

    /// <summary>
    /// Valor que se persiste en <c>selected_model</c>: el nombre del archivo para
    /// los modelos del catálogo, la ruta completa para los descubiertos. Quien
    /// resuelva la ruta debe respetar que ya venga absoluta.
    /// </summary>
    public string SelectionKey => IsDiscovered
        ? Path.Combine(DirectoryOverride, ModelFileName)
        : ModelFileName;
}
