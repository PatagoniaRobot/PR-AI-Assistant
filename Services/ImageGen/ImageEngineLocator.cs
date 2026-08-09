using System;
using System.IO;

namespace PRImager.AIAssistant.Services.ImageGen;

/// <summary>
/// Encuentra sd-server.exe. El motor vive FUERA del repositorio —igual que la instancia
/// de SearXNG— porque son ~925 MB de build CUDA que no tiene sentido versionar ni meter
/// en el bundle de PublishSingleFile.
///
/// Se busca en dos lugares y en este orden:
///
///   1. <c>%SD_RAIZ%\bin\sd-server.exe</c>, que es la variable que ya usa el banco de
///      pruebas desde E4.0. Es el camino de desarrollo.
///   2. <c>&lt;carpeta de la aplicación&gt;\Tools\sd\bin\sd-server.exe</c>, que es donde
///      quedaría en una instalación.
///
/// **No hay una ruta configurable por el usuario todavía**, y es deliberado: agregar un
/// campo a settings.json o a image_models.json en esta fase mezclaría la vista de
/// generación con la configuración del motor. Corresponde a E4.5, junto con la entrada
/// en la barra lateral y la capacidad del perfil.
///
/// Cuando no encuentra nada devuelve igual la ruta esperada de la instalación, para que
/// el mensaje de error de <see cref="ImageServerManager.StartAsync"/> nombre un lugar
/// concreto en lugar de una cadena vacía.
/// </summary>
public static class ImageEngineLocator
{
    /// <summary>Nombre del ejecutable del servidor de stable-diffusion.cpp.</summary>
    public const string ExecutableName = "sd-server.exe";

    /// <summary>Variable de entorno que mueve la raíz del motor. La usa el banco.</summary>
    public const string RootEnvironmentVariable = "SD_RAIZ";

    /// <summary>
    /// Ruta al ejecutable, exista o no. Nunca devuelve null: un motor ausente es una
    /// situación normal en una instalación nueva y se informa como dato, no como
    /// excepción, con el mismo criterio que el resto de E4.
    /// </summary>
    public static string ResolveExecutablePath()
    {
        var raizDeEntorno = Environment.GetEnvironmentVariable(RootEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(raizDeEntorno))
        {
            var candidata = Path.Combine(raizDeEntorno, "bin", ExecutableName);
            if (File.Exists(candidata)) return candidata;
        }

        return InstalledPath;
    }

    /// <summary>Dónde va el motor en una instalación, al lado de Tools\ de llama.cpp.</summary>
    public static string InstalledPath => Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "Tools", "sd", "bin", ExecutableName);

    public static bool IsInstalled => File.Exists(ResolveExecutablePath());
}
