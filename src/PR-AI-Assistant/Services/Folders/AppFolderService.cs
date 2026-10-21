using System;
using System.Collections.Generic;
using System.IO;
using PRImager.AIAssistant.Services.Models;

namespace PRImager.AIAssistant.Services.Folders;

/// <summary>Cuál de las carpetas de datos del usuario.</summary>
public enum AppFolderKind
{
    /// <summary>Modelos de lenguaje descargados desde HuggingFace (E3).</summary>
    LanguageModels,

    /// <summary>Los tres archivos de cada paquete de modelo de imagen (E4.3).</summary>
    ImageModels,

    /// <summary>Las imágenes generadas y sus sidecars .json (E4.4).</summary>
    GeneratedImages
}

/// <summary>
/// Una carpeta de datos configurable, con lo que la interfaz necesita para dibujarla.
/// </summary>
public sealed class AppFolder
{
    public AppFolderKind Kind { get; init; }
    public string DisplayName { get; init; } = "";
    public string Description { get; init; } = "";

    /// <summary>Carpeta efectiva: la configurada, o la que corresponda por defecto.</summary>
    public string Path { get; init; } = "";

    /// <summary>Carpeta que se usaría sin configurar nada.</summary>
    public string DefaultPath { get; init; } = "";

    /// <summary>Qué archivos le pertenecen. Es lo que se mueve y lo que se mide.</summary>
    public IReadOnlyList<string> Patterns { get; init; } = Array.Empty<string>();

    /// <summary>
    /// True si la carpeta cuelga del directorio de salida de la compilación.
    ///
    /// Es el aviso que motiva media pantalla: ahí adentro, cualquier limpieza de build se
    /// lleva los modelos descargados sin preguntar. Era el pendiente 6.3 del maestro y la
    /// única forma de perder trabajo que quedaba en la aplicación.
    /// </summary>
    public bool IsInsideBuildOutput { get; init; }

    public bool IsCustomized => !string.Equals(
        System.IO.Path.TrimEndingDirectorySeparator(Path ?? ""),
        System.IO.Path.TrimEndingDirectorySeparator(DefaultPath ?? ""),
        StringComparison.OrdinalIgnoreCase);

    /// <summary>Cuántos archivos suyos hay y cuánto ocupan. Se mide al listar.</summary>
    public int FileCount { get; init; }
    public long Bytes { get; init; }

    public bool Exists => !string.IsNullOrWhiteSpace(Path) && Directory.Exists(Path);
}

/// <summary>
/// Las tres carpetas de datos del usuario, en un solo lugar para la interfaz.
///
/// **No abre un árbol de configuración nuevo**, que es lo que el pendiente 6.3 del maestro
/// pide justamente no empeorar: cada ruta se guarda donde ya vive la configuración de su
/// etapa. La carpeta de modelos de lenguaje va en <c>settings.json</c>, donde ya estaba; las
/// dos de imagen van en <c>image_models.json</c>, que es donde E4.5 puso la ruta del motor.
///
/// Esa separación no es prolijidad: es la lección del defecto 4.17. Una ruta de imágenes en
/// <c>settings.json</c> llegaría al binario institucional, que no compila nada de imágenes,
/// y volvería a dejar un dato de una capacidad que esa edición no debe siquiera nombrar.
///
/// En la edición Forense no hay ninguna carpeta configurable —no descarga modelos ni genera
/// imágenes—, así que <see cref="List"/> devuelve vacío y la entrada del menú no se muestra.
/// </summary>
public sealed class AppFolderService
{
    private readonly AppSettings _settings;
    private readonly Action _persistSettings;

#if IMAGE_GEN
    private ImageGen.ImageModelService? _imageModels;
#endif

    /// <param name="persistSettings">
    /// Guarda settings.json. Se recibe en lugar de un ConfigManager para no atar este
    /// servicio a cómo se persiste: el ViewModel ya tiene el suyo armado.
    /// </param>
    public AppFolderService(AppSettings settings, Action persistSettings)
    {
        _settings = settings;
        _persistSettings = persistSettings;
    }

#if IMAGE_GEN
    /// <summary>
    /// Conecta el servicio de modelos de imagen, que es dueño de las otras dos rutas.
    ///
    /// Va por acá y no por el constructor para que la firma no nombre un tipo que en la
    /// edición institucional no existe. Tiene que ser **la misma instancia** que usan la
    /// ventana de generación y la de paquetes: con instancias separadas, cambiar la carpeta
    /// no se vería del otro lado hasta reabrir.
    /// </summary>
    public void UseImageModelService(ImageGen.ImageModelService service) => _imageModels = service;
#endif

    // --- Patrones: qué archivos pertenecen a cada carpeta ---

    /// <summary>Los .gguf y los .part de una descarga interrumpida.</summary>
    public static readonly string[] LanguageModelPatterns = { "*.gguf", "*.gguf" + FolderMigrator.PartialExtension };

    /// <summary>Difusión y text encoder son .gguf; el VAE es .safetensors.</summary>
    public static readonly string[] ImageModelPatterns =
        { "*.gguf", "*.safetensors", "*.gguf" + FolderMigrator.PartialExtension, "*.safetensors" + FolderMigrator.PartialExtension };

    /// <summary>
    /// El PNG y su sidecar. Van juntos y no por separado: el .json es lo único que hace
    /// reproducible a la imagen, y una imagen que llega sola a la carpeta nueva perdió eso.
    /// </summary>
    public static readonly string[] GeneratedImagePatterns = { "*.png", "*.json" };

    /// <summary>
    /// Las carpetas que esta edición sabe configurar, ya medidas.
    ///
    /// Medir recorre el directorio, así que esto no va en un getter que la interfaz pueda
    /// llamar sin darse cuenta en cada refresco de binding.
    /// </summary>
    public IReadOnlyList<AppFolder> List()
    {
        var folders = new List<AppFolder>();

        string? descargas = ModelDirectoryResolver.ResolveDownloadDirectory(_settings);
        if (descargas is not null)
        {
            folders.Add(Build(
                AppFolderKind.LanguageModels,
                Loc.T("folders.kind.languageModels"),
                Loc.T("folders.kind.languageModels.description"),
                descargas,
                ModelDirectoryResolver.ResolveModelsDirectory(_settings),
                LanguageModelPatterns));
        }

#if IMAGE_GEN
        if (_imageModels is not null)
        {
            folders.Add(Build(
                AppFolderKind.ImageModels,
                Loc.T("folders.kind.imageModels"),
                Loc.T("folders.kind.imageModels.description"),
                _imageModels.ResolveModelsDirectory(),
                ImageGen.ImageModelService.DefaultModelsDirectory,
                ImageModelPatterns));

            folders.Add(Build(
                AppFolderKind.GeneratedImages,
                Loc.T("folders.kind.generatedImages"),
                Loc.T("folders.kind.generatedImages.description"),
                _imageModels.ResolveOutputDirectory(),
                ImageGen.ImageOutputFolder.DefaultDirectory,
                GeneratedImagePatterns));
        }
#endif

        return folders;
    }

    /// <summary>
    /// Cambia la carpeta. **Solo redirige**: no toca un archivo, es instantáneo y no puede
    /// fallar a la mitad. Mover lo que quedó atrás es <see cref="FolderMigrator"/>, que el
    /// usuario dispara aparte y puede cancelar sin dejar la configuración inconsistente.
    ///
    /// Devuelve la carpeta anterior, que es lo que hace falta para ofrecer el movimiento.
    /// </summary>
    public string Set(AppFolderKind kind, string path)
    {
        string nueva = (path ?? "").Trim();
        if (nueva.Length == 0) throw new ArgumentException("La carpeta no puede estar vacía.", nameof(path));

        nueva = Path.GetFullPath(nueva);
        string anterior = Resolve(kind);

        switch (kind)
        {
            case AppFolderKind.LanguageModels:
                _settings.ModelDownloadPath = nueva;
                _persistSettings();
                break;

#if IMAGE_GEN
            case AppFolderKind.ImageModels:
                if (_imageModels is null) break;
                _imageModels.ModelsDirectory = nueva;
                // Acá NO se reapuntan los paquetes, y es deliberado. Los paquetes guardan
                // una ruta absoluta por archivo, y los archivos todavía están en la carpeta
                // vieja: reapuntarlos ahora dejaría el paquete del usuario incompleto por el
                // solo hecho de haber cambiado una preferencia. Se reapunta después de
                // mover, con RepointAfterMove, que es cuando los archivos ya están del otro
                // lado. Quien cambia la carpeta y no mueve nada sigue generando igual.
                break;

            case AppFolderKind.GeneratedImages:
                if (_imageModels is null) break;
                _imageModels.OutputDirectory = nueva;
                break;
#endif
        }

        return anterior;
    }

    /// <summary>
    /// Ajusta lo que quedó apuntando a la carpeta vieja después de un movimiento.
    ///
    /// Hoy solo aplica a los modelos de imagen, porque un <c>ImageModelPackage</c> guarda
    /// una ruta absoluta por cada uno de sus tres archivos en lugar de una carpeta. Sin
    /// esto, mover los archivos dejaría todos los paquetes rotos apuntando al lugar viejo,
    /// y el selector los mostraría incompletos sin decir una palabra sobre la carpeta.
    ///
    /// Las otras dos no lo necesitan: los modelos de lenguaje se descubren escaneando la
    /// carpeta, y las imágenes generadas no las referencia nadie más que la galería de la
    /// sesión en curso.
    ///
    /// Devuelve cuántos paquetes cambiaron.
    /// </summary>
    public int RepointAfterMove(AppFolderKind kind, string oldFolder, string newFolder)
    {
#if IMAGE_GEN
        if (kind == AppFolderKind.ImageModels && _imageModels is not null)
            return _imageModels.RepointPackages(oldFolder, newFolder);
#endif
        return 0;
    }

    /// <summary>Carpeta efectiva de un tipo, sin medir nada.</summary>
    public string Resolve(AppFolderKind kind) => kind switch
    {
        AppFolderKind.LanguageModels => ModelDirectoryResolver.ResolveDownloadDirectory(_settings) ?? "",
#if IMAGE_GEN
        AppFolderKind.ImageModels => _imageModels?.ResolveModelsDirectory() ?? "",
        AppFolderKind.GeneratedImages => _imageModels?.ResolveOutputDirectory() ?? "",
#endif
        _ => ""
    };

    /// <summary>Patrones de un tipo de carpeta, para medir y para mover.</summary>
    public static IReadOnlyList<string> PatternsFor(AppFolderKind kind) => kind switch
    {
        AppFolderKind.LanguageModels => LanguageModelPatterns,
        AppFolderKind.ImageModels => ImageModelPatterns,
        AppFolderKind.GeneratedImages => GeneratedImagePatterns,
        _ => Array.Empty<string>()
    };

    /// <summary>
    /// True si la carpeta cuelga del directorio desde el que corre la aplicación, que en
    /// desarrollo es <c>bin\</c>. Ver <see cref="AppFolder.IsInsideBuildOutput"/>.
    /// </summary>
    public static bool IsInsideBuildOutput(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        try
        {
            string baseDir = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory));
            string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

            return full.StartsWith(baseDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                   || string.Equals(full, baseDir, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static AppFolder Build(
        AppFolderKind kind, string nombre, string descripcion,
        string ruta, string porDefecto, string[] patrones)
    {
        var (archivos, bytes) = FolderMigrator.Survey(ruta, patrones);

        return new AppFolder
        {
            Kind = kind,
            DisplayName = nombre,
            Description = descripcion,
            Path = ruta,
            DefaultPath = porDefecto,
            Patterns = patrones,
            IsInsideBuildOutput = IsInsideBuildOutput(ruta),
            FileCount = archivos,
            Bytes = bytes
        };
    }
}
