using System.Collections.Generic;

namespace PRImager.AIAssistant.Services.ImageGen;

/// <summary>
/// Parámetros de una generación. Los valores por defecto son los de un modelo Turbo
/// destilado (Z-Image): 8 pasos y CFG 1.0.
///
/// Ojo: NO son los que devuelve /sdcpp/v1/capabilities. Ese endpoint informa los
/// defaults del MOTOR —20 pasos y CFG 7.0—, que en un Turbo dan una imagen mala. Los
/// valores buenos vienen del paquete de modelo, no del servidor.
/// </summary>
public sealed class ImageGenRequest
{
    public string Prompt { get; set; } = "";
    public string NegativePrompt { get; set; } = "";
    public int Width { get; set; } = 1024;
    public int Height { get; set; } = 1024;
    public int Steps { get; set; } = 8;
    public double CfgScale { get; set; } = 1.0;

    /// <summary>Semilla. Menor que 0 pide una aleatoria al motor.</summary>
    public long Seed { get; set; } = -1;

    /// <summary>Sampler. Vacío deja el que elija el motor para la familia del modelo.</summary>
    public string? SampleMethod { get; set; }

    /// <summary>Scheduler. Vacío deja el del modelo.</summary>
    public string? Scheduler { get; set; }
}

/// <summary>
/// Progreso de muestreo. Llega por stdout del proceso, no por la API: la respuesta de
/// GET /sdcpp/v1/jobs/{id} no tiene ningún campo de progreso (verificado en E4.0).
/// </summary>
public sealed class ImageGenProgress
{
    public int Step { get; init; }
    public int TotalSteps { get; init; }
    public double SecondsPerIteration { get; init; }

    public double Fraction => TotalSteps > 0 ? (double)Step / TotalSteps : 0;

    /// <summary>Segundos restantes estimados con el ritmo actual.</summary>
    public double EstimatedSecondsRemaining =>
        SecondsPerIteration > 0 ? (TotalSteps - Step) * SecondsPerIteration : 0;
}

/// <summary>
/// Resultado de una generación. Los fallos se modelan como datos, no como excepciones,
/// con el mismo criterio que WebSearchResponse en E2: que falte el motor, que el modelo
/// no esté o que el servidor se caiga tienen que producir un mensaje legible.
/// </summary>
public sealed class ImageGenResult
{
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }

    /// <summary>Ruta del archivo escrito. Null si no se generó nada.</summary>
    public string? ImagePath { get; init; }

    /// <summary>Semilla efectivamente usada, para poder reproducir.</summary>
    public long Seed { get; init; }

    /// <summary>True si terminó porque el usuario canceló, no por un fallo.</summary>
    public bool WasCancelled { get; init; }

    public static ImageGenResult Ok(string path, long seed) =>
        new() { Success = true, ImagePath = path, Seed = seed };

    public static ImageGenResult Fail(string message) =>
        new() { Success = false, ErrorMessage = message };

    public static ImageGenResult Cancelled() =>
        new() { Success = false, WasCancelled = true, ErrorMessage = Loc.T("imagegen.status.cancelled") };
}

/// <summary>
/// Lo que el motor declara que sabe hacer. La UI se puebla desde acá en lugar de
/// hardcodear listas que envejecen con cada release.
/// </summary>
public sealed class ImageServerCapabilities
{
    /// <summary>Nombre del archivo de difusión cargado. Sirve para detectar si una
    /// instancia ya levantada tiene el modelo que se le está pidiendo.</summary>
    public string ModelName { get; init; } = "";

    public List<string> Samplers { get; init; } = new();
    public List<string> Schedulers { get; init; } = new();

    public int MinWidth { get; init; }
    public int MaxWidth { get; init; }
    public int MinHeight { get; init; }
    public int MaxHeight { get; init; }
    public int MaxBatchCount { get; init; }
}

/// <summary>
/// Estado de un job en el servidor. Los valores son los que devuelve la API tal cual.
/// </summary>
public enum ImageJobStatus
{
    Queued,
    Generating,
    Completed,
    Failed,
    Cancelled,
    /// <summary>No se pudo interpretar la respuesta, o el job no existe.</summary>
    Unknown
}
