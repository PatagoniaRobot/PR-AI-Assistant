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
///   - WebSearchEnabled / ModelDownloadEnabled / ImageGenEnabled: forzados a false.
///     ImageGenEnabled además es false SIEMPRE en la edición institucional, por
///     compilación y no por configuración. Ver <see cref="ImageGenCompiledIn"/>.
///   - FileUploadEnabled: forzado a TRUE. Es la única capacidad que el marco
///     institucional habilita en vez de quitar: cargar evidencia es constitutivo de
///     la investigación y un perfil no puede sacárselo al perito.
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

    /// <summary>
    /// Generación de imágenes efectivamente disponible.
    ///
    /// **En la edición institucional es false siempre**, cualquiera sea el perfil y
    /// cualquiera sea el modo: ver <see cref="ImageGenCompiledIn"/>.
    /// </summary>
    public bool ImageGenEnabled { get; }

    /// <summary>
    /// True si la generación de imágenes existe en este binario.
    ///
    /// `UsageProfile` sí se compila en la edición Forense —es configuración, no código de
    /// red ni de imágenes—, así que sin esta constante un `profiles.json` con
    /// `image_gen_enabled: true` haría que un binario pericial informara la capacidad
    /// como disponible. No podría hacer nada con ella, porque el código no está, pero
    /// **un flag en true es exactamente lo que la sección 1.3 del maestro no quiere tener
    /// que explicar ante una impugnación**: «no la tiene» es más sólido que «la tiene
    /// apagada».
    ///
    /// Por eso la barrera de compilación entra acá, en el único lugar donde vive la
    /// precedencia, y no repartida por la interfaz.
    /// </summary>
    public const bool ImageGenCompiledIn =
#if IMAGE_GEN
        true;
#else
        false;
#endif

    /// <summary>Carga de archivos e imágenes efectivamente disponible.</summary>
    public bool FileUploadEnabled { get; }

    /// <summary>
    /// True cuando lo que se cargue entra como contexto forense auditado, en lugar de
    /// como un adjunto del mensaje. Es la diferencia entre evidencia y un archivo
    /// cualquiera, y decide tanto el destino del contenido como el texto del diálogo.
    /// </summary>
    public bool FileUploadIsForensic => Mode == OperationMode.Investigation;

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
        bool imageGenEnabled,
        bool fileUploadEnabled,
        bool mandatoryLog,
        bool isSystemPromptOverridden)
    {
        Profile = profile;
        Mode = mode;
        Temperature = temperature;
        WebSearchEnabled = webSearchEnabled;
        ModelDownloadEnabled = modelDownloadEnabled;
        ImageGenEnabled = imageGenEnabled;
        FileUploadEnabled = fileUploadEnabled;
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
            // Tres condiciones, y la primera es de compilación: en Forense es false
            // constante, así que el compilador se lleva la rama entera.
            imageGenEnabled: ImageGenCompiledIn && !isInvestigation && profile.ImageGenEnabled,
            // Nótese el || y no el &&: acá el modo habilita, no restringe
            fileUploadEnabled: isInvestigation || profile.FileUploadEnabled,
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
