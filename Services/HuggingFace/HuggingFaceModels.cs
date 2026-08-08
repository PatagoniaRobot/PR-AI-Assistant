using System;
using System.Collections.Generic;

namespace PRImager.AIAssistant.Services.HuggingFace;

/// <summary>
/// Repositorio de modelos devuelto por la búsqueda.
/// </summary>
public sealed class HfRepo
{
    /// <summary>Identificador completo, ej: "Qwen/Qwen3-8B-GGUF".</summary>
    public string Id { get; set; } = "";

    /// <summary>Autor u organización. Se deriva del Id si la API no lo informa.</summary>
    public string Author { get; set; } = "";

    public int Downloads { get; set; }
    public int Likes { get; set; }

    /// <summary>Licencia declarada, o "no declarada".</summary>
    public string License { get; set; } = "no declarada";

    /// <summary>True si el repo requiere aceptar términos para descargar.</summary>
    public bool IsGated { get; set; }

    /// <summary>Nombre corto sin el prefijo del autor.</summary>
    public string ShortName
    {
        get
        {
            int i = Id.IndexOf('/');
            return i >= 0 && i < Id.Length - 1 ? Id.Substring(i + 1) : Id;
        }
    }

    public string DownloadsText => Downloads >= 1_000_000
        ? $"{Downloads / 1_000_000.0:F1}M"
        : Downloads >= 1000 ? $"{Downloads / 1000.0:F1}k" : Downloads.ToString();

    /// <summary>Las filas del DataGrid se anuncian con esto en accesibilidad.</summary>
    public override string ToString() => Id;
}

/// <summary>
/// Un archivo .gguf dentro de un repositorio, con su tamaño.
/// </summary>
public sealed class HfFile
{
    /// <summary>Ruta del archivo dentro del repo, ej: "Qwen3-8B-Q4_K_M.gguf".</summary>
    public string FileName { get; set; } = "";

    /// <summary>Tamaño en bytes. 0 si la API no lo informó.</summary>
    public long SizeBytes { get; set; }

    public string SizeText => SizeBytes <= 0
        ? "tamaño desconocido"
        : SizeBytes >= 1024L * 1024 * 1024
            ? $"{SizeBytes / (1024.0 * 1024 * 1024):F2} GB"
            : $"{SizeBytes / (1024.0 * 1024):F0} MB";

    /// <summary>
    /// Reconoce las familias de cuantización que usa llama.cpp hoy:
    /// Q4_K_M, IQ2_XXS, TQ1_0, F16/BF16/F32, con prefijo opcional UD- (unsloth dynamic).
    /// Se toma la última coincidencia porque el nombre del modelo puede contener
    /// tokens parecidos antes de la cuantización real.
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex QuantPattern = new(
        @"(?<=^|[-_.])(UD-)?((?:IQ|TQ|Q)\d+(?:_[A-Za-z0-9]+)*|BF16|F16|F32)(?=$|[-_.])",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase |
        System.Text.RegularExpressions.RegexOptions.Compiled);

    public override string ToString() => FileName;

    /// <summary>
    /// Etiqueta de cuantización extraída del nombre, ej: "Q4_K_M" o "UD-IQ2_XXS".
    /// Vacía si no se reconoce el patrón.
    /// </summary>
    public string Quantization
    {
        get
        {
            string name = System.IO.Path.GetFileNameWithoutExtension(FileName);
            var matches = QuantPattern.Matches(name);
            if (matches.Count == 0) return "";

            // Los .gguf partidos terminan en -00001-of-00003; la cuantización va antes
            string value = matches[^1].Value;
            return value.ToUpperInvariant();
        }
    }
}

/// <summary>
/// Resultado de una operación contra la API. Los fallos son datos, no excepciones.
/// </summary>
public sealed class HfResult<T>
{
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
    public T? Value { get; init; }

    /// <summary>True si el fallo fue por un repo gated (401/403).</summary>
    public bool IsGated { get; init; }

    public static HfResult<T> Ok(T value) => new() { Success = true, Value = value };

    public static HfResult<T> Fail(string message, bool gated = false) =>
        new() { Success = false, ErrorMessage = message, IsGated = gated };
}

/// <summary>
/// Detalle de un repositorio con sus archivos GGUF.
/// </summary>
public sealed class HfRepoDetail
{
    public HfRepo Repo { get; set; } = new();
    public List<HfFile> GgufFiles { get; set; } = new();
}

/// <summary>
/// Registro de una descarga completada, para la trazabilidad exigida por el proyecto.
/// </summary>
public sealed class ModelDownloadRecord
{
    /// <summary>
    /// Discrimina la línea en model-downloads.jsonl, donde ahora conviven altas y
    /// bajas. Las líneas anteriores a este campo son todas altas.
    /// </summary>
    public string Event { get; set; } = "download";

    public string RepoId { get; set; } = "";
    public string FileName { get; set; } = "";
    public long SizeBytes { get; set; }
    public string Sha256 { get; set; } = "";
    public DateTime CompletedUtc { get; set; }
    public string DestinationPath { get; set; } = "";
}

/// <summary>Progreso de una descarga en curso.</summary>
public sealed class DownloadProgress
{
    public long BytesDownloaded { get; init; }

    /// <summary>Total esperado. 0 si el servidor no informó Content-Length.</summary>
    public long TotalBytes { get; init; }

    /// <summary>Velocidad instantánea en MB/s.</summary>
    public double SpeedMbPerSecond { get; init; }

    public double Percent => TotalBytes > 0
        ? BytesDownloaded * 100.0 / TotalBytes : 0;

    public string Text
    {
        get
        {
            string done = $"{BytesDownloaded / (1024.0 * 1024):F0} MB";
            string total = TotalBytes > 0 ? $" / {TotalBytes / (1024.0 * 1024):F0} MB" : "";
            return $"{done}{total} · {SpeedMbPerSecond:F1} MB/s";
        }
    }
}
