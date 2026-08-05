namespace PRImager.AIAssistant.Services.Models;

/// <summary>
/// Contenedor del catálogo v2. Se deserializa directamente desde model_catalog.json.
/// Separa modelos (qué se ejecuta) de perfiles de ejecución (cómo se ejecuta).
/// </summary>
public class ModelCatalog
{
    /// <summary>Versión del catálogo para migraciones futuras.</summary>
    public int Version { get; set; } = 2;

    /// <summary>Lista de modelos disponibles.</summary>
    public List<ModelInfo> Models { get; set; } = new();

    /// <summary>Lista de perfiles de ejecución.</summary>
    public List<ExecutionProfile> ExecutionProfiles { get; set; } = new();

    // --- Compatibilidad v1 (solo para deserialización de catálogos antiguos) ---

    /// <summary>Lista de perfiles v1 (solo lectura de catálogos legacy).</summary>
    public List<ModelProfile>? Profiles { get; set; }
}
