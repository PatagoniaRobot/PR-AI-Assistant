using System;

namespace PRImager.AIAssistant.Services.Models;

/// <summary>
/// Perfil de uso: define cómo se comporta el asistente frente al usuario
/// (prompt, temperatura, capacidades habilitadas).
///
/// Eje distinto de <see cref="ExecutionProfile"/> (cómo se ejecuta llama-server)
/// y de <see cref="OperationMode"/> (marco institucional forense).
/// Ver <see cref="EffectiveProfile"/> para las reglas de precedencia entre ambos.
/// </summary>
public sealed class UsageProfile
{
    /// <summary>Slug único e inmutable. Identifica al perfil en profiles.json.</summary>
    public string Id { get; set; } = "";

    /// <summary>Nombre mostrado al usuario en el selector y el título de ventana.</summary>
    public string Name { get; set; } = "";

    /// <summary>Prompt de sistema base del perfil.</summary>
    public string SystemPrompt { get; set; } = "";

    /// <summary>Temperatura de muestreo aplicada a la inferencia.</summary>
    public double Temperature { get; set; } = 0.7;

    /// <summary>Tamaño de contexto sugerido por el perfil, en tokens.</summary>
    public int ContextTokens { get; set; } = 8192;

    /// <summary>Ruta al .gguf preferido por el perfil. Vacío = usar el modelo activo.</summary>
    public string DefaultModelPath { get; set; } = "";

    /// <summary>Habilita la búsqueda web manual (E2).</summary>
    public bool WebSearchEnabled { get; set; }

    /// <summary>Habilita el catálogo y la descarga de modelos (E3).</summary>
    public bool ModelDownloadEnabled { get; set; }

    /// <summary>Fuerza el registro de auditoría de toda la sesión.</summary>
    public bool MandatoryLog { get; set; }

    /// <summary>Color de acento en hex (#RRGGBB) para identificación visual del perfil.</summary>
    public string AccentColor { get; set; } = "#64748B";

    /// <summary>Perfil de fábrica: no editable ni eliminable. Solo se puede clonar.</summary>
    public bool IsFactory { get; set; }

    /// <summary>
    /// Crea una copia editable de este perfil con nuevo Id y nombre.
    /// La copia nunca es de fábrica, sin importar el origen.
    /// </summary>
    public UsageProfile CloneAsEditable(string newId, string newName) => new()
    {
        Id = newId,
        Name = newName,
        SystemPrompt = SystemPrompt,
        Temperature = Temperature,
        ContextTokens = ContextTokens,
        DefaultModelPath = DefaultModelPath,
        WebSearchEnabled = WebSearchEnabled,
        ModelDownloadEnabled = ModelDownloadEnabled,
        MandatoryLog = MandatoryLog,
        AccentColor = AccentColor,
        IsFactory = false
    };

    public override string ToString() => Name;
}
