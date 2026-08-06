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
/// Fórmula: tamaño_gguf + KV cache + 1 GB de overhead.
/// Es una aproximación deliberadamente conservadora: mejor desaconsejar un modelo
/// que entra justo, que recomendar uno que va a tirar OOM a mitad de una pericia.
///
/// No es código de red: se compila también en la configuración Forense.
/// </summary>
public static class VramEstimator
{
    /// <summary>Overhead fijo del runtime, en MB.</summary>
    private const int OverheadMb = 1024;

    /// <summary>A partir de este porcentaje de ocupación se pasa a amarillo.</summary>
    private const double YellowThresholdPercent = 70.0;

    // El KV cache no es un costo fijo por token: depende de capas, cabezas KV y
    // dimensión de cabeza, que crecen con el tamaño del modelo. Tomarlo como
    // constante (0.5 MB/token) hacía que 32k de contexto sumaran 16 GB fijos y
    // todo quedara en rojo, incluso un modelo de 415 MB.
    //
    // Sin leer los metadatos del GGUF, el tamaño del archivo es el único proxy
    // disponible. Se aproxima con una ley de potencia calibrada contra el cálculo
    // exacto 2 (K+V) × 2 (fp16) × capas × cabezas_kv × dim_cabeza:
    //
    //   Qwen2.5-0.5B  (0.4 GB)  24 capas ×  2 kv × 64  →  0.012 MB/token
    //   Gemma 4 E4B   (4.4 GB)  35 capas ×  4 kv × 256 →  0.14  MB/token
    //   Llama 3 8B    (4.9 GB)  32 capas ×  8 kv × 128 →  0.125 MB/token
    //   Llama 3 70B   (40 GB)   80 capas ×  8 kv × 128 →  0.31  MB/token
    //
    // Los coeficientes quedan por encima de los valores reales en el rango chico
    // y a la par en el grande: se mantiene el sesgo conservador del diseño
    // original sin el rojo universal.
    private const double KvCoefficient = 0.05;
    private const double KvExponent = 0.55;
    private const double MinKvMbPerToken = 0.01;
    private const double MaxKvMbPerToken = 0.60;

    /// <summary>
    /// MB de KV cache por token para un modelo del tamaño dado. Público para que
    /// el cálculo sea auditable y testeable por separado de la clasificación.
    /// </summary>
    public static double KvCacheMbPerToken(long fileSizeBytes)
    {
        double sizeGb = fileSizeBytes / (1024.0 * 1024.0 * 1024.0);
        if (sizeGb <= 0) return MinKvMbPerToken;

        double mbPerToken = KvCoefficient * Math.Pow(sizeGb, KvExponent);
        return Math.Clamp(mbPerToken, MinKvMbPerToken, MaxKvMbPerToken);
    }

    /// <summary>
    /// Calcula la VRAM necesaria en MB para un modelo de <paramref name="fileSizeBytes"/>
    /// con una ventana de <paramref name="contextTokens"/> tokens.
    /// </summary>
    public static int EstimateRequiredMb(long fileSizeBytes, int contextTokens)
    {
        double modelMb = fileSizeBytes / (1024.0 * 1024.0);
        double kvCacheMb = contextTokens * KvCacheMbPerToken(fileSizeBytes);
        return (int)Math.Ceiling(modelMb + kvCacheMb + OverheadMb);
    }

    /// <summary>
    /// Clasifica el ajuste del modelo contra la VRAM detectada.
    /// </summary>
    public static VramEstimate Estimate(long fileSizeBytes, int contextTokens, GpuVramInfo gpu)
    {
        int required = EstimateRequiredMb(fileSizeBytes, contextTokens);
        string breakdown = DescribeBreakdown(fileSizeBytes, contextTokens);

        if (gpu == null || !gpu.Available || gpu.TotalMb <= 0)
        {
            return new VramEstimate
            {
                Fit = VramFit.Unknown,
                RequiredMb = required,
                AvailableMb = 0,
                UsagePercent = 0,
                Explanation = $"Necesita ~{FormatMb(required)}. " +
                              (gpu?.UnavailableReason ?? "No se pudo detectar la VRAM disponible.") +
                              " " + breakdown
            };
        }

        double percent = required * 100.0 / gpu.TotalMb;

        VramFit fit;
        string explanation;

        if (percent > 100.0)
        {
            fit = VramFit.Red;
            explanation = $"Necesita ~{FormatMb(required)} y la GPU tiene {FormatMb(gpu.TotalMb)}. " +
                          "No entra: correría parcialmente en CPU, muy lento. Desaconsejado. " +
                          breakdown;
        }
        else if (percent >= YellowThresholdPercent)
        {
            fit = VramFit.Yellow;
            explanation = $"Necesita ~{FormatMb(required)} de los {FormatMb(gpu.TotalMb)} de la GPU " +
                          $"({percent:F0}%). Entra, pero con poco margen: puede requerir offload " +
                          "parcial y perder velocidad. " + breakdown;
        }
        else
        {
            fit = VramFit.Green;
            explanation = $"Necesita ~{FormatMb(required)} de los {FormatMb(gpu.TotalMb)} de la GPU " +
                          $"({percent:F0}%). Entra completo, con offload total a GPU. " + breakdown;
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

    /// <summary>
    /// Desglosa la estimación. Sin esto, un número grande no dice si viene del
    /// modelo o de la ventana de contexto, que es lo único que el usuario puede bajar.
    /// </summary>
    private static string DescribeBreakdown(long fileSizeBytes, int contextTokens)
    {
        int modelMb = (int)Math.Ceiling(fileSizeBytes / (1024.0 * 1024.0));
        int kvMb = (int)Math.Ceiling(contextTokens * KvCacheMbPerToken(fileSizeBytes));
        return $"(modelo {FormatMb(modelMb)} + KV cache {FormatMb(kvMb)} para " +
               $"{FormatTokens(contextTokens)} de contexto + {FormatMb(OverheadMb)} de runtime)";
    }

    private static string FormatMb(int mb) =>
        mb >= 1024 ? $"{mb / 1024.0:F1} GB" : $"{mb} MB";

    private static string FormatTokens(int tokens) =>
        tokens >= 1024 ? $"{tokens / 1024}k tokens" : $"{tokens} tokens";
}
