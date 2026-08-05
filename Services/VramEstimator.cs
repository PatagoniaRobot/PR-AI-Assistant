using System;

namespace PRImager.AIAssistant.Services;

/// <summary>
/// Clasificación del ajuste de un modelo a la VRAM disponible.
/// </summary>
public enum VramFit
{
    /// <summary>Entra completo: offload total a GPU.</summary>
    Green,
    /// <summary>Entra al 70-100%: offload parcial, se pierde velocidad.</summary>
    Yellow,
    /// <summary>No entra: desaconsejado.</summary>
    Red,
    /// <summary>No se pudo determinar la VRAM disponible.</summary>
    Unknown
}

/// <summary>
/// Resultado de estimar si un modelo entra en la GPU.
/// </summary>
public sealed class VramEstimate
{
    public VramFit Fit { get; init; }

    /// <summary>VRAM estimada como necesaria, en MB.</summary>
    public int RequiredMb { get; init; }

    /// <summary>VRAM total de la GPU, en MB. 0 si no se pudo detectar.</summary>
    public int AvailableMb { get; init; }

    /// <summary>Porcentaje de la VRAM que ocuparía. 0 si no se pudo detectar.</summary>
    public double UsagePercent { get; init; }

    /// <summary>Explicación para mostrar al usuario.</summary>
    public string Explanation { get; init; } = "";

    /// <summary>Etiqueta corta para la tabla.</summary>
    public string Label => Fit switch
    {
        VramFit.Green => "Entra completo",
        VramFit.Yellow => "Offload parcial",
        VramFit.Red => "No entra",
        _ => "VRAM desconocida"
    };

    /// <summary>Color hex del semáforo.</summary>
    public string Color => Fit switch
    {
        VramFit.Green => "#10B981",
        VramFit.Yellow => "#F59E0B",
        VramFit.Red => "#EF4444",
        _ => "#64748B"
    };
}

/// <summary>
/// Estima la VRAM necesaria para correr un GGUF y la clasifica en un semáforo.
///
/// Fórmula: tamaño_gguf + (contexto_tokens × 0.5 MB) + 1 GB de overhead.
/// Es una aproximación deliberadamente conservadora: mejor desaconsejar un modelo
/// que entra justo, que recomendar uno que va a tirar OOM a mitad de una pericia.
///
/// No es código de red: se compila también en la configuración Forense.
/// </summary>
public static class VramEstimator
{
    /// <summary>MB de VRAM que consume cada 1000 tokens de contexto (KV cache).</summary>
    private const double MbPerThousandTokens = 500.0;

    /// <summary>Overhead fijo del runtime, en MB.</summary>
    private const int OverheadMb = 1024;

    /// <summary>A partir de este porcentaje de ocupación se pasa a amarillo.</summary>
    private const double YellowThresholdPercent = 70.0;

    /// <summary>
    /// Calcula la VRAM necesaria en MB para un modelo de <paramref name="fileSizeBytes"/>
    /// con una ventana de <paramref name="contextTokens"/> tokens.
    /// </summary>
    public static int EstimateRequiredMb(long fileSizeBytes, int contextTokens)
    {
        double modelMb = fileSizeBytes / (1024.0 * 1024.0);
        double kvCacheMb = contextTokens / 1000.0 * MbPerThousandTokens;
        return (int)Math.Ceiling(modelMb + kvCacheMb + OverheadMb);
    }

    /// <summary>
    /// Clasifica el ajuste del modelo contra la VRAM detectada.
    /// </summary>
    public static VramEstimate Estimate(long fileSizeBytes, int contextTokens, GpuVramInfo gpu)
    {
        int required = EstimateRequiredMb(fileSizeBytes, contextTokens);

        if (gpu == null || !gpu.Available || gpu.TotalMb <= 0)
        {
            return new VramEstimate
            {
                Fit = VramFit.Unknown,
                RequiredMb = required,
                AvailableMb = 0,
                UsagePercent = 0,
                Explanation = $"Necesita ~{FormatMb(required)}. " +
                              (gpu?.UnavailableReason ?? "No se pudo detectar la VRAM disponible.")
            };
        }

        double percent = required * 100.0 / gpu.TotalMb;

        VramFit fit;
        string explanation;

        if (percent > 100.0)
        {
            fit = VramFit.Red;
            explanation = $"Necesita ~{FormatMb(required)} y la GPU tiene {FormatMb(gpu.TotalMb)}. " +
                          "No entra: correría parcialmente en CPU, muy lento. Desaconsejado.";
        }
        else if (percent >= YellowThresholdPercent)
        {
            fit = VramFit.Yellow;
            explanation = $"Necesita ~{FormatMb(required)} de los {FormatMb(gpu.TotalMb)} de la GPU " +
                          $"({percent:F0}%). Entra, pero con poco margen: puede requerir offload " +
                          "parcial y perder velocidad.";
        }
        else
        {
            fit = VramFit.Green;
            explanation = $"Necesita ~{FormatMb(required)} de los {FormatMb(gpu.TotalMb)} de la GPU " +
                          $"({percent:F0}%). Entra completo, con offload total a GPU.";
        }

        return new VramEstimate
        {
            Fit = fit,
            RequiredMb = required,
            AvailableMb = gpu.TotalMb,
            UsagePercent = percent,
            Explanation = explanation
        };
    }

    private static string FormatMb(int mb) =>
        mb >= 1024 ? $"{mb / 1024.0:F1} GB" : $"{mb} MB";
}
