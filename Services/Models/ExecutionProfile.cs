namespace PRImager.AIAssistant.Services.Models;

/// <summary>
/// Perfil de ejecución que define cómo se ejecuta llama-server, independiente del modelo.
/// Se deserializa desde la sección "execution_profiles" de model_catalog.json v2.
/// </summary>
public class ExecutionProfile
{
    /// <summary>ID único del perfil (ej: "performance", "balanced", "quality").</summary>
    public string Id { get; set; } = "";

    /// <summary>Nombre amigable mostrado al usuario (ej: "Equilibrado").</summary>
    public string DisplayName { get; set; } = "";

    /// <summary>Cantidad de gpu_layers. 0 = CPU puro, 99 = todas las capas en GPU.</summary>
    public int GpuLayers { get; set; }

    /// <summary>Tamaño de contexto.</summary>
    public int CtxSize { get; set; } = 8192;

    /// <summary>Threads. 0 = usar Environment.ProcessorCount / 2.</summary>
    public int Threads { get; set; }
}
