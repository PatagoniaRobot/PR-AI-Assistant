using System;

namespace PRImager.AIAssistant.Services.Models;

/// <summary>
/// Resultado de combinar un <see cref="UsageProfile"/> con el <see cref="OperationMode"/> activo.
///
/// Regla de precedencia (única fuente de verdad del proyecto):
/// el perfil de uso manda, EXCEPTO en <see cref="OperationMode.Investigation"/>, donde el marco
/// institucional pisa al perfil:
///
///   - SystemPrompt: gana el prompt institucional de investigación.
///   - MandatoryLog: forzado a true.
///   - WebSearchEnabled / ModelDownloadEnabled: forzados a false.
///   - Temperature: la define el perfil en ambos modos. Es un parámetro de muestreo,
///     no una capacidad, y el perito debe poder elegirla.
///
/// Nunca construir esta clase a mano: usar <see cref="Resolve"/>.
/// </summary>
public sealed class EffectiveProfile
{
    /// <summary>Perfil de uso del que se derivan los valores no pisados.</summary>
    public UsageProfile Profile { get; }

    /// <summary>Modo de operación que se aplicó al resolver.</summary>
    public OperationMode Mode { get; }

    /// <summary>Temperatura efectiva de muestreo.</summary>
    public double Temperature { get; }

    /// <summary>Búsqueda web efectivamente disponible.</summary>
    public bool WebSearchEnabled { get; }

    /// <summary>Descarga de modelos efectivamente disponible.</summary>
    public bool ModelDownloadEnabled { get; }

    /// <summary>Registro de auditoría obligatorio.</summary>
    public bool MandatoryLog { get; }

    /// <summary>
    /// True si el prompt del perfil fue descartado en favor del institucional.
    /// La UI lo usa para avisar que el perfil está parcialmente pisado.
    /// </summary>
    public bool IsSystemPromptOverridden { get; }

    private EffectiveProfile(
        UsageProfile profile,
        OperationMode mode,
        double temperature,
        bool webSearchEnabled,
        bool modelDownloadEnabled,
        bool mandatoryLog,
        bool isSystemPromptOverridden)
    {
        Profile = profile;
        Mode = mode;
        Temperature = temperature;
        WebSearchEnabled = webSearchEnabled;
        ModelDownloadEnabled = modelDownloadEnabled;
        MandatoryLog = mandatoryLog;
        IsSystemPromptOverridden = isSystemPromptOverridden;
    }

    /// <summary>
    /// Aplica la regla de precedencia entre el perfil de uso y el modo de operación.
    /// </summary>
    public static EffectiveProfile Resolve(UsageProfile profile, OperationMode mode)
    {
        if (profile == null) throw new ArgumentNullException(nameof(profile));

        bool isInvestigation = mode == OperationMode.Investigation;

        return new EffectiveProfile(
            profile: profile,
            mode: mode,
            temperature: profile.Temperature,
            webSearchEnabled: !isInvestigation && profile.WebSearchEnabled,
            modelDownloadEnabled: !isInvestigation && profile.ModelDownloadEnabled,
            mandatoryLog: isInvestigation || profile.MandatoryLog,
            isSystemPromptOverridden: isInvestigation);
    }

    /// <summary>
    /// Devuelve el prompt de sistema que corresponde usar.
    /// En investigación ignora el del perfil y usa el institucional recibido.
    /// </summary>
    public string ResolveSystemPrompt(string institutionalPrompt)
    {
        return IsSystemPromptOverridden ? institutionalPrompt : Profile.SystemPrompt;
    }
}
