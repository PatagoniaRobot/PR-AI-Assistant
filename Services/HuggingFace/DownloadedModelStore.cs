using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PRImager.AIAssistant.Services.HuggingFace;

/// <summary>
/// Un .gguf presente en la carpeta de descargas, candidato a ser eliminado.
/// </summary>
public sealed class DownloadedModelInfo
{
    public string FileName { get; init; } = "";
    public string FullPath { get; init; } = "";
    public long SizeBytes { get; init; }

    /// <summary>true si es el modelo seleccionado hoy en la configuración.</summary>
    public bool IsActive { get; init; }

    /// <summary>Archivo .part con el mismo nombre, de una descarga interrumpida.</summary>
    public string? PartialPath { get; init; }

    public string SizeText => FormatSize(SizeBytes);

    /// <summary>
    /// Tamaño legible. Un modelo real pesa GB, pero un archivo chico no debe
    /// informarse como "0 MB": ver un tamaño nulo al lado de un botón que borra
    /// invita a desconfiar del dato.
    /// </summary>
    public static string FormatSize(long bytes) => bytes >= 1024L * 1024 * 1024
        ? $"{bytes / (1024.0 * 1024 * 1024):F2} GB"
        : bytes >= 1024L * 1024
            ? $"{bytes / (1024.0 * 1024):F0} MB"
            : $"{bytes / 1024.0:F0} KB";

    /// <summary>Motivo por el que no se puede eliminar, o vacío si se puede.</summary>
    public string BlockedReason => IsActive
        ? "Es el modelo activo. Elegí otro modelo antes de eliminarlo."
        : "";

    public bool CanDelete => BlockedReason.Length == 0;

    public override string ToString() => FileName;
}

/// <summary>
/// Resultado de una baja: qué se borró y cuánto espacio se liberó.
/// </summary>
public sealed class ModelDeletionRecord
{
    /// <summary>Discrimina la línea en model-downloads.jsonl, que hasta ahora solo tenía altas.</summary>
    public string Event { get; set; } = "deletion";

    public string FileName { get; set; } = "";
    public long SizeBytes { get; set; }
    public DateTime DeletedUtc { get; set; }
    public string DeletedPath { get; set; } = "";

    /// <summary>Archivos accesorios borrados junto al modelo: .part y mmproj huérfanos.</summary>
    public List<string> AlsoDeleted { get; set; } = new();
}

/// <summary>
/// Lista y elimina los modelos que están en la carpeta de descargas.
///
/// Vive bajo Services\HuggingFace\ a propósito: esa carpeta no se compila en la
/// configuración Forense, que no descarga modelos y por lo tanto tampoco los borra.
/// </summary>
public sealed class DownloadedModelStore
{
    private readonly string? _downloadDirectory;
    private readonly HashSet<string> _catalogFileNames;
    private readonly Func<string> _activeSelectionKey;

    /// <param name="downloadDirectory">
    /// Carpeta de descargas. Null en un binario sin descargas: el store queda vacío.
    /// </param>
    /// <param name="catalogFileNames">
    /// Nombres de archivo del catálogo distribuido. Nunca se ofrecen para eliminar,
    /// aunque compartan carpeta con las descargas: cuando model_download_path está
    /// vacío las descargas aterrizan en la misma carpeta Models/ que los modelos que
    /// vienen con el producto, y borrar uno de esos rompería la instalación.
    /// </param>
    /// <param name="activeSelectionKey">
    /// El <c>selected_model</c> vigente. Se consulta en cada listado y no se captura
    /// una sola vez: el usuario puede cambiar de modelo con esta ventana abierta.
    /// </param>
    public DownloadedModelStore(
        string? downloadDirectory,
        IEnumerable<string> catalogFileNames,
        Func<string> activeSelectionKey)
    {
        _downloadDirectory = downloadDirectory;
        _catalogFileNames = new HashSet<string>(catalogFileNames, StringComparer.OrdinalIgnoreCase);
        _activeSelectionKey = activeSelectionKey;
    }

    /// <summary>Carpeta que se está gestionando, vacía si no hay ninguna.</summary>
    public string DirectoryText => _downloadDirectory ?? "";

    /// <summary>
    /// Modelos eliminables de la carpeta de descargas, de mayor a menor tamaño:
    /// quien viene a liberar disco busca primero el más grande.
    /// </summary>
    public List<DownloadedModelInfo> List()
    {
        var result = new List<DownloadedModelInfo>();
        if (string.IsNullOrWhiteSpace(_downloadDirectory) || !Directory.Exists(_downloadDirectory))
            return result;

        string[] files;
        try
        {
            files = Directory.GetFiles(_downloadDirectory, "*.gguf");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"DownloadedModelStore: no se pudo leer {_downloadDirectory}: {ex.Message}");
            return result;
        }

        string active = _activeSelectionKey() ?? "";

        foreach (string path in files)
        {
            string name = Path.GetFileName(path);

            if (_catalogFileNames.Contains(name)) continue;
            if (IsProjector(name)) continue;

            long size;
            try { size = new FileInfo(path).Length; }
            catch (Exception) { continue; }

            string partial = path + ModelDownloader.PartialExtension;

            result.Add(new DownloadedModelInfo
            {
                FileName = name,
                FullPath = path,
                SizeBytes = size,
                // El chat guarda la ruta completa de un descargado, pero un
                // settings.json viejo puede traer solo el nombre: se aceptan ambos.
                IsActive = PathsMatch(active, path) || string.Equals(active, name, StringComparison.OrdinalIgnoreCase),
                PartialPath = File.Exists(partial) ? partial : null
            });
        }

        return result.OrderByDescending(m => m.SizeBytes).ToList();
    }

    /// <summary>
    /// Elimina el modelo, su .part si quedó uno, y los proyectores que hayan quedado
    /// huérfanos. Los fallos se devuelven como datos, no como excepciones.
    /// </summary>
    public HfResult<ModelDeletionRecord> Delete(DownloadedModelInfo model)
    {
        // Las tres barreras se revalidan acá y no solo en la UI: el estado pudo
        // cambiar entre que se dibujó la lista y que el usuario confirmó.
        if (string.IsNullOrWhiteSpace(_downloadDirectory))
            return HfResult<ModelDeletionRecord>.Fail("Esta edición no gestiona modelos descargados.");

        if (_catalogFileNames.Contains(model.FileName))
            return HfResult<ModelDeletionRecord>.Fail(
                $"{model.FileName} forma parte del catálogo distribuido con el producto y no se elimina desde acá.");

        if (!IsInsideDownloadDirectory(model.FullPath))
            return HfResult<ModelDeletionRecord>.Fail(
                "El archivo está fuera de la carpeta de descargas. Solo se eliminan modelos descargados.");

        string active = _activeSelectionKey() ?? "";
        if (PathsMatch(active, model.FullPath) || string.Equals(active, model.FileName, StringComparison.OrdinalIgnoreCase))
            return HfResult<ModelDeletionRecord>.Fail(
                $"{model.FileName} es el modelo activo. Elegí otro modelo antes de eliminarlo.");

        if (!File.Exists(model.FullPath))
            return HfResult<ModelDeletionRecord>.Fail($"{model.FileName} ya no está en la carpeta.");

        long size;
        try { size = new FileInfo(model.FullPath).Length; }
        catch (Exception) { size = model.SizeBytes; }

        try
        {
            File.Delete(model.FullPath);
        }
        catch (Exception ex)
        {
            // El caso real es tener el modelo cargado en llama-server
            return HfResult<ModelDeletionRecord>.Fail(
                $"No se pudo eliminar {model.FileName}: {ex.Message}. " +
                "Si el modelo está cargado, cerrá la sesión y volvé a intentar.");
        }

        var accesorios = new List<string>();

        if (model.PartialPath != null && TryDelete(model.PartialPath))
            accesorios.Add(Path.GetFileName(model.PartialPath));

        foreach (string huerfano in OrphanProjectors())
            if (TryDelete(huerfano))
                accesorios.Add(Path.GetFileName(huerfano));

        return HfResult<ModelDeletionRecord>.Ok(new ModelDeletionRecord
        {
            FileName = model.FileName,
            SizeBytes = size,
            DeletedUtc = DateTime.UtcNow,
            DeletedPath = model.FullPath,
            AlsoDeleted = accesorios
        });
    }

    /// <summary>
    /// Borra los .part sueltos de descargas interrumpidas que ya no acompañan a
    /// ningún modelo. Devuelve los nombres borrados y los bytes liberados.
    /// </summary>
    public (List<string> Deleted, long BytesFreed) CleanPartials()
    {
        var deleted = new List<string>();
        long freed = 0;

        if (string.IsNullOrWhiteSpace(_downloadDirectory) || !Directory.Exists(_downloadDirectory))
            return (deleted, freed);

        string[] partials;
        try { partials = Directory.GetFiles(_downloadDirectory, "*" + ModelDownloader.PartialExtension); }
        catch (Exception) { return (deleted, freed); }

        foreach (string p in partials)
        {
            long size;
            try { size = new FileInfo(p).Length; }
            catch (Exception) { continue; }

            if (TryDelete(p))
            {
                deleted.Add(Path.GetFileName(p));
                freed += size;
            }
        }

        return (deleted, freed);
    }

    /// <summary>
    /// Proyectores sin ningún modelo que los pueda usar.
    ///
    /// Se considera huérfano solo cuando no queda un solo .gguf cargable en la
    /// carpeta: un mmproj no dice a qué modelo pertenece, así que asociarlo por
    /// parecido de nombre podría borrar el proyector de un modelo que sigue estando.
    /// Ante la duda no se toca.
    /// </summary>
    private IEnumerable<string> OrphanProjectors()
    {
        string[] restantes;
        try { restantes = Directory.GetFiles(_downloadDirectory!, "*.gguf"); }
        catch (Exception) { yield break; }

        bool quedaAlgunModelo = restantes.Any(f =>
        {
            string n = Path.GetFileName(f);
            return !IsProjector(n) && !_catalogFileNames.Contains(n);
        });

        if (quedaAlgunModelo) yield break;

        foreach (string f in restantes)
        {
            string n = Path.GetFileName(f);
            if (IsProjector(n) && !_catalogFileNames.Contains(n))
                yield return f;
        }
    }

    private static bool IsProjector(string fileName) =>
        fileName.Contains("mmproj", StringComparison.OrdinalIgnoreCase);

    private bool IsInsideDownloadDirectory(string path)
    {
        try
        {
            string carpeta = Path.GetFullPath(_downloadDirectory!)
                                 .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string archivo = Path.GetFullPath(path);
            string padre = Path.GetDirectoryName(archivo) ?? "";
            return string.Equals(
                padre.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                carpeta, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool PathsMatch(string a, string b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
        catch (Exception) { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
    }

    private static bool TryDelete(string path)
    {
        try { File.Delete(path); return true; }
        catch (Exception) { return false; }
    }
}
