using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using PRImager.AIAssistant.Services.Models;

namespace PRImager.AIAssistant.Services;

/// <summary>
/// Carga y gestiona modelos y perfiles de ejecución desde model_catalog.json v2.
/// Detecta qué modelos tienen el .gguf presente en disco y cuáles no.
/// </summary>
public class ModelProfileService
{
    private readonly string _catalogPath;
    private readonly string _modelsDirectory;
    private ModelCatalog _catalog = new();

    public ModelProfileService(string catalogPath, string modelsDirectory)
    {
        _catalogPath = catalogPath;
        _modelsDirectory = modelsDirectory;
    }

    /// <summary>
    /// Carga el catálogo desde disco. Si no existe o falla, retorna catálogo vacío.
    /// </summary>
    public ModelCatalog LoadCatalog()
    {
        try
        {
            if (!File.Exists(_catalogPath))
            {
                System.Diagnostics.Debug.WriteLine($"ModelProfileService: Catálogo no encontrado en {_catalogPath}");
                return _catalog;
            }

            string json = File.ReadAllText(_catalogPath);
            var options = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
                PropertyNameCaseInsensitive = true
            };

            _catalog = JsonSerializer.Deserialize<ModelCatalog>(json, options) ?? new ModelCatalog();
            System.Diagnostics.Debug.WriteLine($"ModelProfileService: Cargados {_catalog.Models.Count} modelos, {_catalog.ExecutionProfiles.Count} perfiles de ejecución");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"ModelProfileService: Error cargando catálogo: {ex.Message}");
            _catalog = new ModelCatalog();
        }

        return _catalog;
    }

    /// <summary>
    /// Retorna los modelos disponibles, marcando cada uno según si el .gguf existe en disco.
    /// </summary>
    /// <param name="downloadDirectory">
    /// Carpeta de descargas a escanear además del catálogo. Los .gguf que aparezcan
    /// ahí se ofrecen como modelos seleccionables: bajar un modelo del catálogo de
    /// HuggingFace y después no poder elegirlo deja la descarga sin uso.
    /// Null o vacío para no escanear nada.
    ///
    /// Sin valor por defecto a propósito: cuando lo tuvo, el selector de la pantalla
    /// de selección de modo omitió el argumento, compiló, corrió, y silenciosamente
    /// no escaneó nada. Que el compilador obligue a decidir en cada llamada.
    /// </param>
    public List<(ModelInfo Model, bool IsAvailable)> GetAvailableModels(string? downloadDirectory)
    {
        var result = _catalog.Models
            .Select(m => (Model: m, IsAvailable: IsModelFileAvailable(m)))
            .ToList();

        foreach (var discovered in DiscoverModels(downloadDirectory, result.Select(r => r.Model)))
            result.Add((discovered, true));

        return result;
    }

    /// <summary>
    /// Arma un <see cref="ModelInfo"/> por cada .gguf de la carpeta que no esté ya
    /// en el catálogo.
    ///
    /// Se excluye lo que no es un modelo que se pueda cargar solo —proyectores, cabezas
    /// de borrador, adaptadores, partes de un modelo partido—: ofrecerlo en el selector
    /// solo produce un fallo al iniciar el servidor.
    /// </summary>
    private static IEnumerable<ModelInfo> DiscoverModels(
        string? directory, IEnumerable<ModelInfo> catalogModels)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            yield break;

        var known = new HashSet<string>(
            catalogModels.Select(m => m.ModelFileName),
            StringComparer.OrdinalIgnoreCase);

        string[] files;
        try
        {
            files = Directory.GetFiles(directory, "*.gguf");
        }
        catch (Exception ex)
        {
            // Una carpeta de descargas inaccesible no debe dejar sin modelos al selector.
            System.Diagnostics.Debug.WriteLine($"ModelProfileService: No se pudo leer {directory}: {ex.Message}");
            yield break;
        }

        foreach (string file in files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            string fileName = Path.GetFileName(file);

            if (known.Contains(fileName)) continue;
            if (!IsLoadableModel(file)) continue;

            yield return new ModelInfo
            {
                Id = $"local:{fileName}",
                DisplayName = $"{Path.GetFileNameWithoutExtension(fileName)} (descargado)",
                ModelFileName = fileName,
                DirectoryOverride = directory,
                RequiresGpu = false,
                HardwareTag = "📥 Descargado",
                Capabilities = new List<string>()
            };
        }
    }

    /// <summary>
    /// Lo que el selector puede ofrecer. Manda la cabecera: es lo que el archivo dice de
    /// sí mismo, y desenmascara a la cabeza de borrador de 4.22, que el selector listaba
    /// como un modelo más. Sin cabecera legible decide el nombre, y ante la duda se lista:
    /// si no carga, el error de carga dice por qué.
    /// </summary>
    private static bool IsLoadableModel(string path)
    {
        if (!GgufInspector.FromFileName(path).IsModel) return false;
        var header = GgufInspector.TryReadFile(path);
        return header == null || GgufInspector.FromHeader(header).IsModel;
    }

    /// <summary>
    /// Retorna los perfiles de ejecución definidos en el catálogo.
    /// </summary>
    public List<ExecutionProfile> GetExecutionProfiles()
    {
        return _catalog.ExecutionProfiles;
    }

    /// <summary>
    /// Busca un modelo por Id. Retorna null si no existe.
    /// </summary>
    public ModelInfo? GetModelById(string id)
    {
        return _catalog.Models.FirstOrDefault(m =>
            string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Busca un perfil de ejecución por Id. Retorna null si no existe.
    /// </summary>
    public ExecutionProfile? GetExecutionProfileById(string id)
    {
        return _catalog.ExecutionProfiles.FirstOrDefault(p =>
            string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    // --- Compatibilidad v1 ---

    /// <summary>
    /// Retorna los perfiles v1 ordenados por SortOrder (compatibilidad hacia atrás).
    /// Marca cada uno como disponible/no disponible según si el .gguf existe en disco.
    /// </summary>
    public List<(ModelProfile Profile, bool IsAvailable)> GetAvailableProfiles()
    {
        if (_catalog.Profiles == null || _catalog.Profiles.Count == 0)
            return new List<(ModelProfile, bool)>();

        return _catalog.Profiles
            .OrderBy(p => p.SortOrder)
            .Select(p => (p, IsProfileAvailable(p)))
            .ToList();
    }

    /// <summary>
    /// Detecta si hay GPU disponible consultando el backend del servidor.
    /// Retorna el perfil más adecuado: si hay GPU → "balanced", si no → "performance".
    /// </summary>
    public ModelProfile? RecommendProfile(bool gpuDetected)
    {
        var available = GetAvailableProfiles()
            .Where(x => x.IsAvailable)
            .Select(x => x.Profile)
            .ToList();

        if (!available.Any())
            return null;

        if (gpuDetected)
            return available.FirstOrDefault(p => p.RequiresGpu) ?? available.First();
        else
            return available.FirstOrDefault(p => !p.RequiresGpu) ?? available.First();
    }

    /// <summary>
    /// Verifica si el archivo .gguf del modelo existe, en la carpeta Models/ o en la
    /// carpeta propia del modelo si se descubrió fuera del catálogo.
    /// </summary>
    private bool IsModelFileAvailable(ModelInfo model)
    {
        if (string.IsNullOrEmpty(model.ModelFileName))
            return false;

        string directory = model.IsDiscovered ? model.DirectoryOverride : _modelsDirectory;
        return File.Exists(Path.Combine(directory, model.ModelFileName));
    }

    /// <summary>
    /// Verifica si el archivo .gguf del perfil v1 existe en la carpeta Models/.
    /// Un perfil sin model_file_name se considera no disponible.
    /// </summary>
    private bool IsProfileAvailable(ModelProfile profile)
    {
        if (string.IsNullOrEmpty(profile.ModelFileName))
            return false;

        string fullPath = Path.Combine(_modelsDirectory, profile.ModelFileName);
        return File.Exists(fullPath);
    }
}
