using System.IO;

namespace PRImager.AIAssistant.Services;

/// <summary>
/// Controla los límites de ingesta de datos al modelo de IA.
/// Previene saturación del contexto y rechaza tipos de datos prohibidos.
/// </summary>
public class ContextIngestionService
{
    // Límites configurables
    public int MaxTextCharsPerSubmission { get; set; } = 50_000;
    public int MaxMetadataCharsPerSubmission { get; set; } = 30_000;
    public int MaxHexCharsPerSubmission { get; set; } = 5_000;
    public int MaxImagesPerSubmission { get; set; } = 5;
    public long MaxImageSizeBytes { get; set; } = 2 * 1024 * 1024; // 2MB
    
    // Extensiones de binarios prohibidos
    private static readonly HashSet<string> _prohibitedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".sys", ".bin", ".dat", ".ocx", ".drv",
        ".scr", ".com", ".msi", ".cab", ".iso", ".img", ".vhd"
    };
    
    // Extensiones de imagen permitidas
    private static readonly HashSet<string> _imageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp"
    };
    
    // Extensiones de texto permitidas
    private static readonly HashSet<string> _textExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".csv", ".json", ".xml", ".log", ".html", ".htm",
        ".md", ".yaml", ".yml", ".ini", ".cfg", ".sql"
    };

    /// <summary>
    /// Resultado de la validación de ingesta
    /// </summary>
    public record IngestionResult
    {
        public bool IsAccepted { get; init; }
        public string? RejectionReason { get; init; }    // null si aceptado
        public string? ProcessedContent { get; init; }   // contenido procesado (puede estar truncado)
        public bool WasTruncated { get; init; }
        public int OriginalLength { get; init; }
        public int ProcessedLength { get; init; }
        public string ContentType { get; init; } = "";   // "text", "metadata", "hex", "image"
    }

    /// <summary>
    /// Valida y procesa un archivo para ingesta.
    /// Retorna IngestionResult con el contenido procesado o el motivo de rechazo.
    /// </summary>
    public IngestionResult ValidateFile(string filePath)
    {
        // 1. Verificar que el archivo existe
        if (!File.Exists(filePath))
            return new IngestionResult 
            { 
                IsAccepted = false, 
                RejectionReason = Loc.F("ingest.notFound", filePath)  
            };

        var ext = Path.GetExtension(filePath);

        // 2. Verificar extensión no prohibida
        if (_prohibitedExtensions.Contains(ext))
            return new IngestionResult
            {
                IsAccepted = false,
                RejectionReason = Loc.F("ingest.prohibited", ext),
                ContentType = "binary"
            };

        // 3. Imágenes
        if (_imageExtensions.Contains(ext))
            return ValidateImage(filePath);

        // 4. Texto
        if (_textExtensions.Contains(ext))
            return ValidateTextFile(filePath);

        // 5. Extensión desconocida → rechazar por seguridad
        return new IngestionResult
        {
            IsAccepted = false,
            RejectionReason = Loc.F("ingest.unknownType", ext),
            ContentType = "unknown"
        };
    }

    /// <summary>
    /// Valida y procesa texto ingresado directamente (no desde archivo).
    /// Detecta automáticamente si es hex, metadata JSON/CSV, o texto plano.
    /// </summary>
    public IngestionResult ValidateText(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return new IngestionResult { IsAccepted = false, RejectionReason = Loc.T("ingest.empty") };

        var contentType = DetectContentType(content);
        var limit = contentType switch
        {
            "hex" => MaxHexCharsPerSubmission,
            "metadata" => MaxMetadataCharsPerSubmission,
            _ => MaxTextCharsPerSubmission
        };

        var truncated = content.Length > limit;
        var processed = truncated ? content[..limit] : content;

        return new IngestionResult
        {
            IsAccepted = true,
            ProcessedContent = processed,
            WasTruncated = truncated,
            OriginalLength = content.Length,
            ProcessedLength = processed.Length,
            ContentType = contentType
        };
    }

    /// <summary>
    /// Valida un lote de imágenes (máximo MaxImagesPerSubmission).
    /// </summary>
    public IngestionResult ValidateImageBatch(List<string> imagePaths)
    {
        if (imagePaths.Count > MaxImagesPerSubmission)
            return new IngestionResult
            {
                IsAccepted = false,
                RejectionReason = Loc.F("ingest.tooManyImages", MaxImagesPerSubmission, imagePaths.Count)
            };

        foreach (var path in imagePaths)
        {
            var result = ValidateImage(path);
            if (!result.IsAccepted)
                return result;
        }

        return new IngestionResult
        {
            IsAccepted = true,
            ContentType = "image",
            ProcessedLength = imagePaths.Count
        };
    }

    // === Métodos privados ===

    private IngestionResult ValidateImage(string filePath)
    {
        var fileInfo = new FileInfo(filePath);
        var ext = fileInfo.Extension;

        if (!_imageExtensions.Contains(ext))
            return new IngestionResult
            {
                IsAccepted = false,
                RejectionReason = Loc.F("ingest.imageFormat", ext),
                ContentType = "image"
            };

        if (fileInfo.Length > MaxImageSizeBytes)
            return new IngestionResult
            {
                IsAccepted = false,
                RejectionReason = Loc.F("ingest.imageTooLarge", fileInfo.Length / 1024.0 / 1024.0, MaxImageSizeBytes / 1024.0 / 1024.0),
                ContentType = "image"
            };

        return new IngestionResult
        {
            IsAccepted = true,
            ContentType = "image",
            OriginalLength = (int)fileInfo.Length,
            ProcessedLength = (int)fileInfo.Length
        };
    }

    private IngestionResult ValidateTextFile(string filePath)
    {
        try
        {
            var content = File.ReadAllText(filePath);
            var result = ValidateText(content);
            return result;
        }
        catch (Exception ex)
        {
            return new IngestionResult
            {
                IsAccepted = false,
                RejectionReason = Loc.F("ingest.readError", ex.Message)
            };
        }
    }

    /// <summary>
    /// Detecta si el contenido es hex, metadata (JSON/CSV), o texto plano.
    /// </summary>
    private static string DetectContentType(string content)
    {
        var trimmed = content.TrimStart();

        // Detectar hex: líneas con formato "XX XX XX" o "0xXX"
        if (System.Text.RegularExpressions.Regex.IsMatch(trimmed, @"^([0-9A-Fa-f]{2}\s){4,}"))
            return "hex";

        // Detectar JSON
        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
            return "metadata";

        // Detectar CSV (al menos 3 comas en la primera línea)
        var firstLine = trimmed.Split('\n')[0];
        if (firstLine.Count(c => c == ',') >= 3)
            return "metadata";

        return "text";
    }
}
