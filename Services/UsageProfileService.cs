using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using PRImager.AIAssistant.Services.Models;

namespace PRImager.AIAssistant.Services;

/// <summary>
/// Contenedor serializado de profiles.json. Guarda los perfiles y cuál está activo.
/// </summary>
public sealed class UsageProfileStore
{
    /// <summary>Id del perfil activo. Se restaura al arrancar la app.</summary>
    public string ActiveProfileId { get; set; } = "";

    public List<UsageProfile> Profiles { get; set; } = new();
}

/// <summary>
/// Gestiona los perfiles de uso: persistencia, perfiles de fábrica y guardas de integridad.
/// Archivo: %APPDATA%/PatagoniaRobot/PR-AIA/profiles.json
/// </summary>
public class UsageProfileService
{
    /// <summary>Id del perfil de fábrica forense.</summary>
    public const string ForensicProfileId = "forense";

    /// <summary>Id del perfil de fábrica general.</summary>
    public const string GeneralProfileId = "general";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly string _filePath;
    private UsageProfileStore _store = new();

    /// <summary>Ruta por defecto de profiles.json según la especificación de la etapa E1.</summary>
    public static string DefaultFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "PatagoniaRobot", "PR-AIA", "profiles.json");

    public UsageProfileService(string? filePath = null)
    {
        _filePath = filePath ?? DefaultFilePath;
    }

    /// <summary>Ruta efectiva del archivo de perfiles.</summary>
    public string FilePath => _filePath;

    /// <summary>Todos los perfiles conocidos, de fábrica primero.</summary>
    public IReadOnlyList<UsageProfile> Profiles => _store.Profiles;

    /// <summary>
    /// Carga profiles.json. Si no existe o está corrupto, genera los perfiles de fábrica.
    /// Siempre garantiza que ambos perfiles de fábrica estén presentes e intactos.
    /// </summary>
    public void Load()
    {
        _store = ReadFromDisk() ?? new UsageProfileStore();
        EnsureFactoryProfiles();

        if (!_store.Profiles.Any(p => p.Id == _store.ActiveProfileId))
            _store.ActiveProfileId = ForensicProfileId;

        Save();
    }

    private UsageProfileStore? ReadFromDisk()
    {
        try
        {
            if (!File.Exists(_filePath)) return null;
            string json = File.ReadAllText(_filePath);
            var store = JsonSerializer.Deserialize<UsageProfileStore>(json, JsonOptions);
            if (store == null) return null;

            // Descartar entradas inservibles antes de que lleguen a la UI
            store.Profiles = store.Profiles
                .Where(p => p != null && !string.IsNullOrWhiteSpace(p.Id))
                .ToList();
            return store;
        }
        catch (Exception)
        {
            // JSON inválido o sin permisos: se regenera desde fábrica en vez de romper el arranque
            return null;
        }
    }

    /// <summary>
    /// Reinyecta los perfiles de fábrica. Si fueron editados a mano en el JSON,
    /// se restauran a su definición canónica: son la garantía de comportamiento.
    /// </summary>
    private void EnsureFactoryProfiles()
    {
        var custom = _store.Profiles.Where(p => !IsFactoryId(p.Id)).ToList();
        foreach (var p in custom)
            p.IsFactory = false; // ningún perfil de usuario puede declararse de fábrica

        _store.Profiles = new List<UsageProfile> { CreateForensicProfile(), CreateGeneralProfile() };
        _store.Profiles.AddRange(custom);
    }

    private static bool IsFactoryId(string id) =>
        id == ForensicProfileId || id == GeneralProfileId;

    /// <summary>Escribe profiles.json. Los errores se suprimen para no desestabilizar la app.</summary>
    public void Save()
    {
        try
        {
            string? dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            File.WriteAllText(_filePath, JsonSerializer.Serialize(_store, JsonOptions));
        }
        catch (Exception)
        {
            // Mismo criterio que ConfigManager.Save
        }
    }

    /// <summary>Perfil activo. Nunca null tras <see cref="Load"/>.</summary>
    public UsageProfile ActiveProfile =>
        _store.Profiles.FirstOrDefault(p => p.Id == _store.ActiveProfileId)
        ?? _store.Profiles.First();

    /// <summary>Fija el perfil activo y lo persiste.</summary>
    public void SetActiveProfile(string id)
    {
        if (!_store.Profiles.Any(p => p.Id == id)) return;
        _store.ActiveProfileId = id;
        Save();
    }

    /// <summary>
    /// Clona un perfil existente. Es la única vía para crear perfiles:
    /// garantiza que toda configuración nueva parta de una base válida.
    /// </summary>
    public UsageProfile Clone(UsageProfile source, string newName)
    {
        string baseName = string.IsNullOrWhiteSpace(newName) ? $"{source.Name} (copia)" : newName.Trim();
        var clone = source.CloneAsEditable(GenerateUniqueId(baseName), baseName);
        _store.Profiles.Add(clone);
        Save();
        return clone;
    }

    /// <summary>
    /// Persiste la edición de un perfil. Rechaza los de fábrica: son inmutables por contrato.
    /// </summary>
    public bool Update(UsageProfile edited)
    {
        var existing = _store.Profiles.FirstOrDefault(p => p.Id == edited.Id);
        if (existing == null || existing.IsFactory) return false;

        existing.Name = edited.Name;
        existing.SystemPrompt = edited.SystemPrompt;
        existing.Temperature = Math.Clamp(edited.Temperature, 0.0, 2.0);
        existing.ContextTokens = Math.Max(512, edited.ContextTokens);
        existing.DefaultModelPath = edited.DefaultModelPath;
        existing.WebSearchEnabled = edited.WebSearchEnabled;
        existing.ModelDownloadEnabled = edited.ModelDownloadEnabled;
        existing.MandatoryLog = edited.MandatoryLog;
        existing.AccentColor = edited.AccentColor;
        Save();
        return true;
    }

    /// <summary>
    /// Elimina un perfil de usuario. Rechaza los de fábrica.
    /// Si se elimina el activo, el foco vuelve al perfil forense.
    /// </summary>
    public bool Delete(string id)
    {
        var target = _store.Profiles.FirstOrDefault(p => p.Id == id);
        if (target == null || target.IsFactory) return false;

        _store.Profiles.Remove(target);
        if (_store.ActiveProfileId == id)
            _store.ActiveProfileId = ForensicProfileId;

        Save();
        return true;
    }

    /// <summary>Genera un slug único a partir del nombre.</summary>
    private string GenerateUniqueId(string name)
    {
        string slug = new string(name.ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-')
            .ToArray()).Trim('-');

        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        if (string.IsNullOrEmpty(slug)) slug = "perfil";

        string candidate = slug;
        int n = 2;
        while (_store.Profiles.Any(p => p.Id == candidate))
            candidate = $"{slug}-{n++}";

        return candidate;
    }

    // === Perfiles de fábrica ===

    private static UsageProfile CreateForensicProfile() => new()
    {
        Id = ForensicProfileId,
        Name = "Forense",
        Temperature = 0.2,
        ContextTokens = 8192,
        WebSearchEnabled = false,
        ModelDownloadEnabled = false,
        MandatoryLog = true,
        AccentColor = "#1E5F74",
        IsFactory = true,
        SystemPrompt = ForensicSystemPrompt
    };

    private static UsageProfile CreateGeneralProfile() => new()
    {
        Id = GeneralProfileId,
        Name = "General",
        Temperature = 0.7,
        ContextTokens = 8192,
        WebSearchEnabled = true,
        ModelDownloadEnabled = true,
        MandatoryLog = false,
        AccentColor = "#B85C38",
        IsFactory = true,
        SystemPrompt = GeneralSystemPrompt
    };

    private const string ForensicSystemPrompt =
@"Sos un asistente de análisis técnico que opera bajo la doctrina 'inferido ≠ verificado'.

Regla central: distinguí siempre, de forma explícita, entre lo que el dato PRUEBA y lo que
el dato SUGIERE. Son cosas distintas y nunca se presentan juntas sin etiquetar.

1. Marcá cada afirmación como VERIFICADO (se desprende directamente del dato aportado)
   o INFERIDO (es una hipótesis compatible con el dato, pero no probada por él).
2. No completes huecos. Si falta un dato para sostener una conclusión, declaralo:
   'No hay dato que permita determinar X'. La ausencia de información es un hallazgo,
   no un espacio a rellenar.
3. No aportes conocimiento general como si fuera evidencia del caso.
4. Citá la fuente concreta de cada afirmación verificada: artefacto, tabla, registro, offset.
5. Ante ambigüedad, enumerá las hipótesis alternativas en vez de elegir una.
6. Lenguaje técnico preciso, sin adornos. Respondé en español; términos técnicos en su idioma original.";

    private const string GeneralSystemPrompt =
@"Sos un asistente general. Respondé de forma concisa y directa.

- Andá al grano: sin preámbulos ni resúmenes de lo que te preguntaron.
- Si no sabés algo, decilo en vez de improvisar.
- Usá listas o tablas solo cuando mejoren la claridad.
- Respondé en español, con términos técnicos en su idioma original.";
}
