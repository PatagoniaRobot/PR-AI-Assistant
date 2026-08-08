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

    /// <summary>
    /// Descriptor del paquete recomendado: de dónde sale cada archivo.
    /// Los tres repositorios son abiertos. El VAE NO está en el de black-forest-labs,
    /// que es gated y devuelve 401 sin token, sino en el espejo de Comfy-Org; el
    /// archivo es idéntico (335.304.388 bytes).
    /// </summary>
    public static readonly ImagePackageSource RecommendedSource = new()
    {
        Id = RecommendedPackageId,
        DisplayName = "Z-Image Turbo Q4_K",
        Family = "z-image",
        DefaultSteps = 8,
        DefaultCfgScale = 1.0,
        Components = new[]
        {
            new ImagePackageComponent("modelo de difusión", "leejet/Z-Image-Turbo-GGUF",
                "z_image_turbo-Q4_K.gguf", 3_866_000_000),
            new ImagePackageComponent("text encoder", "unsloth/Qwen3-4B-Instruct-2507-GGUF",
                "Qwen3-4B-Instruct-2507-Q4_K_M.gguf", 2_497_281_120),
            new ImagePackageComponent("VAE", "Comfy-Org/z_image_turbo",
                "split_files/vae/ae.safetensors", 335_304_388)
        }
    };

    /// <summary>
    /// Construye el paquete de fábrica apuntando a la carpeta indicada. Los archivos
    /// pueden no existir todavía: el paquete se muestra incompleto hasta que se
    /// descarguen, que es justamente lo que el selector tiene que dejar ver.
    /// </summary>
    public static ImageModelPackage BuildRecommendedPackage(string modelsDirectory) => new()
    {
        Id = RecommendedSource.Id,
        DisplayName = RecommendedSource.DisplayName,
        Family = RecommendedSource.Family,
        DefaultSteps = RecommendedSource.DefaultSteps,
        DefaultCfgScale = RecommendedSource.DefaultCfgScale,
        IsFactory = true,
        DiffusionModelPath = Path.Combine(modelsDirectory, "z_image_turbo-Q4_K.gguf"),
        TextEncoderPath = Path.Combine(modelsDirectory, "Qwen3-4B-Instruct-2507-Q4_K_M.gguf"),
        // El archivo se guarda por su nombre, sin la subcarpeta del repositorio.
        VaePath = Path.Combine(modelsDirectory, "ae.safetensors")
    };

    // --- Operaciones ---

    /// <summary>
    /// Agrega o reemplaza un paquete. Un paquete de usuario nunca queda marcado como
    /// de fábrica, aunque el llamador lo pida.
    /// </summary>
    public void Save(ImageModelPackage package)
    {
        if (string.IsNullOrWhiteSpace(package.Id))
            throw new ArgumentException("El paquete necesita un identificador.", nameof(package));

        bool esElDeFabrica = package.Id == RecommendedPackageId;
        var aGuardar = package with { IsFactory = esElDeFabrica };

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
        if (id == RecommendedPackageId) return false;   // el de fábrica se regenera igual

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
    /// Garantiza que el paquete recomendado exista en la lista. Si el usuario editó
    /// sus rutas, se respetan: solo se agrega cuando no está.
    /// </summary>
    private void AsegurarPaqueteDeFabrica()
    {
        if (_store.Packages.Any(p => p.Id == RecommendedPackageId)) return;

        var lista = _store.Packages.ToList();
        lista.Insert(0, BuildRecommendedPackage(DefaultModelsDirectory));
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
