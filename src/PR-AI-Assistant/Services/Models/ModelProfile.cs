namespace PRImager.AIAssistant.Services.Models;

/// <summary>
/// Perfil de modelo preconfigurado. Desacopla la UI del archivo .gguf concreto.
/// Mañana se reemplaza Gemma 4 por Gemma 5 tocando solo el catálogo JSON.
/// </summary>
public class ModelProfile
{
    /// <summary>ID único interno (ej: "performance", "balanced", "quality").</summary>
    public string Id { get; set; } = "";

    /// <summary>Nombre amigable mostrado al usuario (ej: "Rendimiento").</summary>
    public string DisplayName { get; set; } = "";

    /// <summary>Descripción corta para tooltip/subtítulo (ej: "Rápido, hardware mínimo, solo texto").</summary>
    public string Description { get; set; } = "";

    /// <summary>Nombre del archivo .gguf dentro de la carpeta Models/ (ej: "gemma-4-1b-Q4_K_M.gguf").</summary>
    public string ModelFileName { get; set; } = "";

    /// <summary>Nombre del archivo mmproj si el modelo soporta visión. Vacío = solo texto.</summary>
    public string MmprojFileName { get; set; } = "";

    /// <summary>true si el modelo requiere GPU para ser usable.</summary>
    public bool RequiresGpu { get; set; }

    /// <summary>Etiqueta de requisitos mostrada en la UI (ej: "CPU ok", "GPU recomendada", "GPU potente").</summary>
    public string HardwareTag { get; set; } = "";

    /// <summary>Cantidad recomendada de gpu_layers. 0 = CPU puro, 99 = todas las capas en GPU.</summary>
    public int RecommendedGpuLayers { get; set; }

    /// <summary>Tamaño de contexto recomendado para este modelo.</summary>
    public int RecommendedCtxSize { get; set; } = 8192;

    /// <summary>Threads recomendados. 0 = usar Environment.ProcessorCount / 2.</summary>
    public int RecommendedThreads { get; set; }

    /// <summary>true si soporta análisis de imágenes (multimodal).</summary>
    public bool SupportsVision => !string.IsNullOrEmpty(MmprojFileName);

    /// <summary>Orden de presentación en la UI (menor = primero).</summary>
    public int SortOrder { get; set; }
}
