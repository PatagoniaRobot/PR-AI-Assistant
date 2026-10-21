using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace PRImager.AIAssistant.Services.ImageGen;

/// <summary>
/// Persiste los paquetes de modelo de imagen y cuál está seleccionado.
///
/// Archivo: %APPDATA%/PatagoniaRobot/PR-AIA/image_models.json, junto a profiles.json.
/// Se elige esa carpeta y no la de settings.json a propósito: el proyecto ya arrastra
/// dos árboles de configuración (pendiente 6.3 del maestro) y agregar un tercero
/// empeoraría el problema. Va con lo más nuevo.
///
/// El paquete recomendado se regenera si el archivo no existe o está corrupto, con el
/// mismo criterio que los perfiles de uso de E1, y un paquete de usuario no puede
/// declararse de fábrica.
/// </summary>
public sealed class ImageModelService
{
    private readonly string _filePath;
    private ImageModelStore _store = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public static string DefaultFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "PatagoniaRobot", "PR-AIA", "image_models.json");

    /// <summary>Identificador del paquete de fábrica.</summary>
    public const string RecommendedPackageId = "z-image-turbo-q4k";

    public ImageModelService(string? filePath = null)
    {
        _filePath = filePath ?? DefaultFilePath;
        Load();
    }

    public string FilePath => _filePath;

    public IReadOnlyList<ImageModelPackage> Packages => _store.Packages;

    /// <summary>
    /// Ruta a sd-server.exe elegida por el usuario, o vacío para que la resuelva
    /// <see cref="ImageEngineLocator"/>.
    ///
    /// Vive acá y no en settings.json a propósito: es configuración **de la generación de
    /// imágenes**, y este archivo ya está detrás de la barrera de compilación. Ponerla en
    /// settings.json la habría metido en el binario institucional, que no tiene motor.
    /// </summary>
    public string EngineExecutablePath
    {
        get => _store.EngineExecutablePath ?? "";
        set
        {
            _store.EngineExecutablePath = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            Persist();
        }
    }

    /// <summary>
    /// Ruta efectiva del motor: la configurada si hay una, o la que se descubra sola.
    /// Es lo que hay que pasarle al ViewModel de generación.
    /// </summary>
    public string ResolveEnginePath() =>
        string.IsNullOrWhiteSpace(_store.EngineExecutablePath)
            ? ImageEngineLocator.ResolveExecutablePath()
            : _store.EngineExecutablePath;

    /// <summary>
    /// Carpeta donde viven los archivos de los paquetes, o vacío para usar la de fábrica.
    /// La gestiona <c>AppFolderService</c>; acá se guarda porque este archivo ya está
    /// detrás de la barrera de compilación y settings.json no lo está (defecto 4.17).
    /// </summary>
    public string ModelsDirectory
    {
        get => _store.ModelsDirectory ?? "";
        set
        {
            _store.ModelsDirectory = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            Persist();
        }
    }

    /// <summary>Carpeta donde se guardan las imágenes generadas y sus sidecars.</summary>
    public string OutputDirectory
    {
        get => _store.OutputDirectory ?? "";
        set
        {
            _store.OutputDirectory = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            Persist();
        }
    }

    /// <summary>Carpeta efectiva de los modelos de imagen.</summary>
    public string ResolveModelsDirectory() =>
        string.IsNullOrWhiteSpace(_store.ModelsDirectory)
            ? DefaultModelsDirectory
            : _store.ModelsDirectory;

    /// <summary>Carpeta efectiva de las imágenes generadas.</summary>
    public string ResolveOutputDirectory() =>
        string.IsNullOrWhiteSpace(_store.OutputDirectory)
            ? ImageOutputFolder.DefaultDirectory
            : _store.OutputDirectory;

    /// <summary>
    /// Reapunta a <paramref name="newDirectory"/> los archivos de los paquetes que hoy
    /// viven en <paramref name="oldDirectory"/>.
    ///
    /// Hace falta porque un paquete **no guarda una carpeta sino una ruta absoluta por
    /// cada uno de sus tres archivos**. Cambiar la carpeta de modelos de imagen sin esto
    /// dejaría todos los paquetes apuntando al lugar viejo: después de mover los archivos
    /// el selector los mostraría incompletos, con los tres componentes faltantes, y el
    /// mensaje no diría nada sobre la carpeta.
    ///
    /// Solo toca las rutas cuya carpeta es exactamente la vieja. Un paquete que el usuario
    /// armó a mano apuntando a otro lado no se toca: nunca estuvo en la carpeta que se movió.
    ///
    /// Y solo reapunta **si el archivo efectivamente está** en la carpeta nueva. Eso es lo
    /// que hace que un movimiento cancelado a la mitad no rompa nada: cada uno de los tres
    /// archivos queda apuntado adonde está de verdad, unos al lugar nuevo y otros al viejo,
    /// y el paquete sigue completo mientras los tres existan en algún lado.
    /// </summary>
    public int RepointPackages(string oldDirectory, string newDirectory)
    {
        if (string.IsNullOrWhiteSpace(oldDirectory) || string.IsNullOrWhiteSpace(newDirectory))
            return 0;

        int cambiados = 0;
        var lista = new List<ImageModelPackage>();

        foreach (var p in _store.Packages)
        {
            var difusion = Repoint(p.DiffusionModelPath, oldDirectory, newDirectory);
            var encoder = Repoint(p.TextEncoderPath, oldDirectory, newDirectory);
            var vae = Repoint(p.VaePath, oldDirectory, newDirectory);

            if (difusion == p.DiffusionModelPath && encoder == p.TextEncoderPath && vae == p.VaePath)
            {
                lista.Add(p);
                continue;
            }

            cambiados++;
            lista.Add(p with
            {
                DiffusionModelPath = difusion,
                TextEncoderPath = encoder,
                VaePath = vae
            });
        }

        if (cambiados > 0)
        {
            _store.Packages = lista;
            Persist();
        }

        return cambiados;
    }

    private static string Repoint(string ruta, string oldDirectory, string newDirectory)
    {
        if (string.IsNullOrWhiteSpace(ruta)) return ruta;

        try
        {
            var carpeta = Path.GetDirectoryName(Path.GetFullPath(ruta));
            if (carpeta is null) return ruta;

            var vieja = Path.TrimEndingDirectorySeparator(Path.GetFullPath(oldDirectory));
            if (!string.Equals(Path.TrimEndingDirectorySeparator(carpeta), vieja,
                    StringComparison.OrdinalIgnoreCase))
                return ruta;

            var candidata = Path.Combine(newDirectory, Path.GetFileName(ruta));
            return File.Exists(candidata) ? candidata : ruta;
        }
        catch (Exception)
        {
            return ruta;
        }
    }

    /// <summary>Paquetes con los tres archivos presentes.</summary>
    public IReadOnlyList<ImageModelPackage> CompletePackages =>
        _store.Packages.Where(p => p.IsComplete).ToList();

    /// <summary>
    /// Paquete seleccionado, o null si el guardado ya no existe o quedó incompleto.
    /// Se devuelve null en lugar de caer en el primero de la lista: la pantalla de
    /// selección de modo hacía exactamente eso y terminaba sobrescribiendo la
    /// elección del usuario en cada arranque (defecto 4.12 del maestro).
    /// </summary>
    public ImageModelPackage? SelectedPackage =>
        _store.Packages.FirstOrDefault(p => p.Id == _store.SelectedPackageId);

    public string? SelectedPackageId => _store.SelectedPackageId;

    // Los dos archivos que comparten todas las variantes de Z-Image. Bajar una segunda
    // variante baja solo su archivo de difusión: el descargador saltea lo que ya está.
    private static readonly ImagePackageComponent TextEncoderZImage = new("text encoder",
        "unsloth/Qwen3-4B-Instruct-2507-GGUF", "Qwen3-4B-Instruct-2507-Q4_K_M.gguf", 2_497_281_120);

    // El VAE NO está en el repo de black-forest-labs, que es gated y devuelve 401 sin
    // token, sino en el espejo de Comfy-Org; el archivo es idéntico (335.304.388 bytes).
    private static readonly ImagePackageComponent VaeZImage = new("VAE",
        "Comfy-Org/z_image_turbo", "split_files/vae/ae.safetensors", 335_304_388);

    private static ImagePackageSource ZImage(string id, string quant, long diffusionBytes) => new()
    {
        Id = id,
        DisplayName = $"Z-Image Turbo {quant}",
        Family = "z-image",
        DefaultSteps = 8,
        DefaultCfgScale = 1.0,
        Components = new[]
        {
            new ImagePackageComponent("modelo de difusión", "leejet/Z-Image-Turbo-GGUF",
                $"z_image_turbo-{quant}.gguf", diffusionBytes),
            TextEncoderZImage,
            VaeZImage
        }
    };

    /// <summary>
    /// Descriptor del paquete recomendado por defecto: de dónde sale cada archivo.
    /// Los tres repositorios son abiertos.
    /// </summary>
    public static readonly ImagePackageSource RecommendedSource =
        ZImage(RecommendedPackageId, "Q4_K", 3_864_250_304);

    /// <summary>
    /// Los paquetes que la aplicación sabe bajar, para elegir como se elige un modelo de
    /// lenguaje en el catálogo. Hasta el 21 de octubre de 2026 había uno solo, y el
    /// selector ofrecía descargarlo aunque ya estuviera bajado.
    ///
    /// Son las tres cuantizaciones de Z-Image medidas en 10.12 del maestro, todas con el
    /// mismo text encoder y el mismo VAE. Las imágenes de Q6_K y Q8_0 salen prácticamente
    /// iguales entre sí; Q8_0 está para quien tenga placa de sobra. Tamaños del árbol del
    /// repositorio, al byte.
    /// </summary>
    public static readonly IReadOnlyList<ImagePackageSource> Catalog = new[]
    {
        RecommendedSource,
        ZImage("z-image-turbo-q6k", "Q6_K", 5_263_239_104),
        ZImage("z-image-turbo-q8_0", "Q8_0", 6_577_440_704)
    };

    /// <summary>True si el paquete es uno de los que la aplicación sabe bajar.</summary>
    public static bool IsCatalogId(string? id) => Catalog.Any(c => c.Id == id);

    /// <summary>De dónde se baja un paquete, o null si lo armó el usuario a mano.</summary>
    public static ImagePackageSource? SourceFor(string? id) => Catalog.FirstOrDefault(c => c.Id == id);

    /// <summary>
    /// El paquete que conviene para la placa, medido conviviendo con el modelo de lenguaje
    /// (10.12): en 16 GB, Q4_K deja 2,7 GB libres y Q6_K 2 GB y tarda un 30 % más, así que
    /// por debajo de 20 GB va Q4_K. Con 20 GB o más, Q6_K deja más de 5 GB de aire. Q8_0
    /// no se recomienda nunca: cuesta 1,3 GB más que Q6_K por la misma imagen.
    ///
    /// Sin datos de la placa se recomienda Q4_K, que es el que menos pide.
    /// </summary>
    public static string RecommendedIdFor(GpuVramInfo gpu) =>
        gpu.Available && gpu.TotalMb >= 20 * 1024 ? "z-image-turbo-q6k" : RecommendedPackageId;

    /// <summary>
    /// Construye un paquete del catálogo apuntando a la carpeta indicada. Los archivos
    /// pueden no existir todavía: el paquete se muestra incompleto hasta que se
    /// descarguen, que es justamente lo que el selector tiene que dejar ver.
    /// </summary>
    public static ImageModelPackage BuildPackage(ImagePackageSource source, string modelsDirectory)
    {
        // El archivo se guarda por su nombre, sin la subcarpeta del repositorio.
        string Ruta(string rol) => source.Components
            .Where(c => c.Role == rol)
            .Select(c => Path.Combine(modelsDirectory, c.LocalFileName))
            .FirstOrDefault() ?? "";

        return new ImageModelPackage
        {
            Id = source.Id,
            DisplayName = source.DisplayName,
            Family = source.Family,
            DefaultSteps = source.DefaultSteps,
            DefaultCfgScale = source.DefaultCfgScale,
            IsFactory = true,
            DiffusionModelPath = Ruta("modelo de difusión"),
            TextEncoderPath = Ruta("text encoder"),
            VaePath = Ruta("VAE")
        };
    }

    /// <summary>El paquete recomendado por defecto, apuntando a la carpeta indicada.</summary>
    public static ImageModelPackage BuildRecommendedPackage(string modelsDirectory) =>
        BuildPackage(RecommendedSource, modelsDirectory);

    // --- Operaciones ---

    /// <summary>
    /// Agrega o reemplaza un paquete. Un paquete de usuario nunca queda marcado como
    /// de fábrica, aunque el llamador lo pida.
    /// </summary>
    public void Save(ImageModelPackage package)
    {
        if (string.IsNullOrWhiteSpace(package.Id))
            throw new ArgumentException("El paquete necesita un identificador.", nameof(package));

        var aGuardar = package with { IsFactory = IsCatalogId(package.Id) };

        var lista = _store.Packages.ToList();
        int i = lista.FindIndex(p => p.Id == aGuardar.Id);
        if (i >= 0) lista[i] = aGuardar; else lista.Add(aGuardar);

        _store.Packages = lista;
        Persist();
    }

    /// <summary>
    /// Elimina un paquete de la lista. **No borra archivos**: quitar un paquete es
    /// una operación de configuración, y borrar modelos del disco es otra cosa, con
    /// sus propias barreras (ver DownloadedModelStore de E3).
    /// </summary>
    public bool Remove(string id)
    {
        if (IsCatalogId(id)) return false;   // los de fábrica se regeneran igual

        var lista = _store.Packages.ToList();
        int quitados = lista.RemoveAll(p => p.Id == id);
        if (quitados == 0) return false;

        _store.Packages = lista;
        if (_store.SelectedPackageId == id) _store.SelectedPackageId = null;
        Persist();
        return true;
    }

    /// <summary>
    /// Selecciona un paquete. Rechaza los incompletos: dejar seleccionado uno al que
    /// le falta un archivo haría que el motor no arranque y el mensaje llegaría tarde
    /// y sin contexto.
    /// </summary>
    public bool Select(string id)
    {
        var p = _store.Packages.FirstOrDefault(x => x.Id == id);
        if (p is null || !p.IsComplete) return false;

        _store.SelectedPackageId = id;
        Persist();
        return true;
    }

    /// <summary>Vuelve a leer el archivo. Útil después de una descarga.</summary>
    public void Reload() => Load();

    // --- Persistencia ---

    private void Load()
    {
        try
        {
            if (File.Exists(_filePath))
            {
                var json = File.ReadAllText(_filePath);
                var leido = JsonSerializer.Deserialize<ImageModelStore>(json, JsonOptions);
                if (leido?.Packages is not null)
                {
                    _store = leido;
                    AsegurarPaqueteDeFabrica();
                    return;
                }
            }
        }
        catch (Exception)
        {
            // Un archivo corrupto no debe impedir arrancar: se regenera lo de fábrica
            // y se pierde solo lo que el usuario haya agregado. Mismo criterio que E1.
        }

        _store = new ImageModelStore();
        AsegurarPaqueteDeFabrica();
        Persist();
    }

    /// <summary>
    /// Garantiza que los paquetes del catálogo existan en la lista, en el orden del
    /// catálogo y antes que los del usuario. Si el usuario editó sus rutas, se respetan:
    /// solo se agrega el que no está.
    /// </summary>
    private void AsegurarPaqueteDeFabrica()
    {
        var lista = _store.Packages.ToList();
        int posicion = 0;
        foreach (var fuente in Catalog)
        {
            int existente = lista.FindIndex(p => p.Id == fuente.Id);
            if (existente >= 0)
            {
                posicion = existente + 1;
                continue;
            }

            // La carpeta efectiva, no la de fábrica: si el usuario la cambió, el paquete
            // tiene que nacer apuntando adonde van a caer sus descargas.
            lista.Insert(Math.Min(posicion, lista.Count), BuildPackage(fuente, ResolveModelsDirectory()));
            posicion++;
        }
        _store.Packages = lista;
    }

    /// <summary>
    /// Carpeta por defecto de los modelos de imagen. Fuera de bin\, a diferencia de la
    /// de descargas de E3, que apunta adentro y se pierde con cualquier limpieza de
    /// build (pendiente 6.3 del maestro). No se repite ese error.
    /// </summary>
    public static string DefaultModelsDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "PatagoniaRobot", "PR-AIA", "ModelosImagen");

    private void Persist()
    {
        try
        {
            var carpeta = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(carpeta)) Directory.CreateDirectory(carpeta);
            File.WriteAllText(_filePath, JsonSerializer.Serialize(_store, JsonOptions));
        }
        catch (Exception)
        {
            // No poder escribir la configuración no debe tumbar la aplicación.
        }
    }

    private sealed class ImageModelStore
    {
        public List<ImageModelPackage> Packages { get; set; } = new();
        public string? SelectedPackageId { get; set; }

        /// <summary>Null = descubrir el motor solo. Ver ImageEngineLocator.</summary>
        public string? EngineExecutablePath { get; set; }

        /// <summary>Null = la carpeta de fábrica. La gestiona AppFolderService.</summary>
        public string? ModelsDirectory { get; set; }

        /// <summary>Null = la carpeta de fábrica de las imágenes generadas.</summary>
        public string? OutputDirectory { get; set; }
    }
}

/// <summary>Un componente del paquete recomendado y de dónde se baja.</summary>
public sealed record ImagePackageComponent(string Role, string RepoId, string FileName, long ExpectedBytes)
{
    /// <summary>Nombre con el que se guarda en disco, sin la subcarpeta del repo.</summary>
    public string LocalFileName => Path.GetFileName(FileName);
}

/// <summary>Descriptor de dónde sale cada archivo de un paquete descargable.</summary>
public sealed class ImagePackageSource
{
    public string Id { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string Family { get; init; } = "";
    public int DefaultSteps { get; init; } = 8;
    public double DefaultCfgScale { get; init; } = 1.0;
    public IReadOnlyList<ImagePackageComponent> Components { get; init; } = Array.Empty<ImagePackageComponent>();

    public long TotalExpectedBytes
    {
        get
        {
            long t = 0;
            foreach (var c in Components) t += c.ExpectedBytes;
            return t;
        }
    }
}
