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
    public List<(ModelInfo Model, bool IsAvailable)> GetAvailableModels()
    {
        return _catalog.Models
            .Select(m => (m, IsModelFileAvailable(m)))
            .ToList();
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
    /// Verifica si el archivo .gguf del modelo existe en la carpeta Models/.
    /// </summary>
    private bool IsModelFileAvailable(ModelInfo model)
    {
        if (string.IsNullOrEmpty(model.ModelFileName))
            return false;

        string fullPath = Path.Combine(_modelsDirectory, model.ModelFileName);
        return File.Exists(fullPath);
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
