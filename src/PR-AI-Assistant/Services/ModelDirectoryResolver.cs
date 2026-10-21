using System;
using System.IO;
using PRImager.AIAssistant.Services.Models;

namespace PRImager.AIAssistant.Services;

/// <summary>
/// Resuelve las carpetas desde las que se ofrecen modelos.
///
/// Vive acá y no en un ViewModel porque hay dos selectores de modelo —el de la
/// pantalla de selección de modo y el del chat— y mientras esta lógica fue privada
/// de <c>MainViewModel</c> el de la pantalla inicial quedó sin la carpeta de
/// descargas: mostraba solo el catálogo distribuido.
/// </summary>
public static class ModelDirectoryResolver
{
    /// <summary>Carpeta de modelos efectiva, resuelta contra el directorio de la app.</summary>
    public static string ResolveModelsDirectory(AppSettings settings) =>
        Path.IsPathRooted(settings.ModelPath)
            ? settings.ModelPath
            : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, settings.ModelPath);

    /// <summary>
    /// Carpeta donde aterrizan las descargas, que los selectores escanean además del
    /// catálogo.
    ///
    /// En la configuración Forense devuelve null: ese binario no descarga modelos y sus
    /// selectores se limitan al catálogo distribuido. Aceptar un .gguf de origen
    /// arbitrario debilitaría la misma garantía que sostiene la barrera de compilación.
    /// </summary>
    public static string? ResolveDownloadDirectory(AppSettings settings)
    {
#if MODEL_DOWNLOAD
        return string.IsNullOrWhiteSpace(settings.ModelDownloadPath)
            ? ResolveModelsDirectory(settings)
            : settings.ModelDownloadPath;
#else
        return null;
#endif
    }
}
