using System;

namespace PRImager.AIAssistant.Services.ImageGen;

/// <summary>Veredicto del árbitro para una generación concreta.</summary>
public enum ImageVramFit
{
    /// <summary>Entra con margen.</summary>
    Green,
    /// <summary>Entra justo. El motor se va a apretar y puede tardar más.</summary>
    Yellow,
    /// <summary>No entra: la generación va a fallar por memoria.</summary>
    Red,
    /// <summary>No se debe intentar: mata el proceso. No es cuestión de memoria.</summary>
    Blocked,
    /// <summary>No se pudo determinar la VRAM.</summary>
    Unknown
}

public sealed class ImageVramVerdict
{
    public ImageVramFit Fit { get; init; }

    /// <summary>VRAM estimada como necesaria, en MiB.</summary>
    public int RequiredMb { get; init; }

    /// <summary>VRAM libre en el momento de evaluar, en MiB.</summary>
    public int FreeMb { get; init; }

    /// <summary>Explicación para mostrar al usuario.</summary>
    public string Explanation { get; init; } = "";

    /// <summary>
    /// Configuración sugerida si la pedida no entra. Null si no hay nada que ajustar
    /// o si ya está todo puesto.
    /// </summary>
    public ImageServerConfig? RecommendedConfig { get; init; }

    /// <summary>
    /// True cuando NO hay que intentarlo aunque el usuario insista: el motor muere.
    /// Es distinto de Red, que es "va a fallar pero se recupera".
    /// </summary>
    public bool MustNotAttempt => Fit == ImageVramFit.Blocked;

    public string Label => Fit switch
    {
        ImageVramFit.Green => Loc.T("imagegen.vram.fits"),
        ImageVramFit.Yellow => Loc.T("imagegen.vram.tight"),
        ImageVramFit.Red => Loc.T("imagegen.vram.noFit"),
        ImageVramFit.Blocked => Loc.T("imagegen.vram.unsupported"),
        _ => Loc.T("imagegen.vramUnknown")
    };

    /// <summary>Mismos colores que el semáforo de modelos, para que se lea igual.</summary>
    public string Color => Fit switch
    {
        ImageVramFit.Green => "#10B981",
        ImageVramFit.Yellow => "#F59E0B",
        ImageVramFit.Red => "#EF4444",
        ImageVramFit.Blocked => "#EF4444",
        _ => "#64748B"
    };
}

/// <summary>
/// Decide si una generación de imagen entra en la VRAM que queda libre, y con qué
/// palancas. Es el semáforo de E4.2.
///
/// Todo lo que hay acá está medido en esta máquina (RTX 5060 Ti, 16.311 MiB) y no
/// derivado de la documentación del motor. Tres cosas que la medición cambió respecto
/// de lo que uno supondría:
///
/// 1. **No se compara contra la suma de los archivos.** `sd-server` carga los tres
///    módulos POR TURNOS y libera entre uno y otro, así que el pico es la etapa mayor
///    y no la suma. El `total params memory size` que informa el motor —7.400 MB— no
///    es residencia simultánea: usarlo como tal sobreestima por más del doble.
///
/// 2. **El motor se adapta a lo que hay libre.** Corriendo solo tomó 14.494 MiB y
///    bajo presión del modelo de lenguaje 6.471, produciendo la misma imagen. Por eso
///    el número a comparar es el PISO —lo que necesita cuando no puede expandirse— y
///    no el pico que se observa en aislamiento.
///
/// 3. **Hay dos modos de falla distintos y solo uno es de memoria.** Ver MaxSafeDimension.
/// </summary>
public static class ImageVramArbiter
{
    /// <summary>
    /// Lado máximo que se puede pedir. A 4096 el motor muere con
    /// "CUDA error: invalid configuration argument" en cudaLaunchKernelEx, y el proceso
    /// queda unos segundos en un estado en el que ni se lo puede matar.
    ///
    /// **No es un problema de memoria**: se reprodujo con 15.355 MiB libres, es decir
    /// con la GPU prácticamente vacía. Es un límite de lanzamiento de kernel.
    ///
    /// Importa además porque /sdcpp/v1/capabilities ANUNCIA max_width y max_height de
    /// 4096. El motor declara un límite que no puede cumplir, así que sus `limits` no
    /// se pueden usar tal cual para poblar la interfaz.
    ///
    /// Verificado: 2048 y 3072 completan; 4096 mata el proceso.
    /// </summary>
    public const int MaxSafeDimension = 3072;

    /// <summary>
    /// Buffer de decodificación del VAE SIN tiling, por megapíxel de salida. Medido
    /// exacto: 6.657 MB a 1024×1024 (1 Mpx) y 26.628 MB a 2048×2048 (4 Mpx), que es
    /// ×4 clavado.
    ///
    /// Es el término que hace estallar el presupuesto: a 2048 sin tiling el motor pide
    /// una sola asignación de 26 GB y falla, en una GPU de 16.
    /// </summary>
    public const int UntiledVaeMbPerMegapixel = 6650;

    /// <summary>
    /// El mismo buffer CON tiling, que no depende de la resolución porque el tamaño de
    /// tile es fijo. Medido en 416 MB a 512, 1024 y 2048.
    /// </summary>
    public const int TiledVaeBufferMb = 420;

    /// <summary>
    /// Piso de trabajo con los pesos en RAM (--offload-to-cpu): lo que ocupa el motor
    /// aun generando la imagen más chica. Medido 4.008 MiB a 512×512 bajo presión.
    /// </summary>
    public const int OffloadedBaseMb = 4000;

    /// <summary>
    /// Crecimiento por megapíxel con tiling y offload puestos, ajustado sobre las
    /// mediciones de 0,25 a 4 Mpx (4.008, 4.352, 4.668, 5.594 y 6.886 MiB).
    /// </summary>
    public const int PerMegapixelMb = 750;

    /// <summary>
    /// Pesos del paquete cuando NO se usa --offload-to-cpu y viven en VRAM.
    /// Medido: 7.400,58 MB para Z-Image Q4_K + Qwen3-4B Q4_K_M + VAE.
    ///
    /// Es un valor por defecto para cuando no hay calibración del paquete concreto.
    /// El de otro paquete puede ser muy distinto y conviene medirlo, no heredarlo.
    /// </summary>
    public const int DefaultResidentWeightsMb = 7400;

    /// <summary>Margen que se deja libre para no dejar la GPU al filo.</summary>
    private const int SafetyMarginMb = 512;

    /// <summary>Por debajo de este margen sobrante se pasa a amarillo.</summary>
    private const int TightMarginMb = 1024;

    /// <summary>
    /// Estima los MiB que necesita una generación con la configuración dada.
    /// Público para que el cálculo sea auditable por separado del veredicto.
    /// </summary>
    public static int EstimateRequiredMb(int width, int height, ImageServerConfig config,
                                         int? calibratedWeightsMb = null)
    {
        double megapixels = Math.Max(width, 1) * (double)Math.Max(height, 1) / (1024.0 * 1024.0);

        double total = OffloadedBaseMb + PerMegapixelMb * megapixels;

        // El buffer del VAE es lo que decide si esto entra o estalla.
        total += config.VaeTiling
            ? TiledVaeBufferMb
            : UntiledVaeMbPerMegapixel * megapixels;

        // Sin offload los pesos también ocupan VRAM durante su etapa.
        if (!config.OffloadToCpu)
            total += calibratedWeightsMb ?? DefaultResidentWeightsMb;

        return (int)Math.Ceiling(total);
    }

    /// <summary>
    /// Evalúa una generación concreta contra la VRAM libre.
    /// </summary>
    /// <param name="gpu">Estado de la GPU. Se usa FreeMb, no TotalMb: lo que importa
    /// es lo que queda después del modelo de lenguaje y del escritorio.</param>
    public static ImageVramVerdict Evaluate(ImageGenRequest request, ImageServerConfig config,
                                            GpuVramInfo gpu, int? calibratedWeightsMb = null)
    {
        // El bloqueo por resolución va PRIMERO y no depende de la VRAM: no es un
        // problema de memoria y no se arregla liberando nada.
        int lado = Math.Max(request.Width, request.Height);
        if (lado > MaxSafeDimension)
        {
            return new ImageVramVerdict
            {
                Fit = ImageVramFit.Blocked,
                RequiredMb = 0,
                FreeMb = gpu?.FreeMb ?? 0,
                Explanation = Loc.F("imagegen.vram.blocked", request.Width, request.Height, MaxSafeDimension)
            };
        }

        int required = EstimateRequiredMb(request.Width, request.Height, config, calibratedWeightsMb);

        if (gpu is null || !gpu.Available || gpu.FreeMb <= 0)
        {
            return new ImageVramVerdict
            {
                Fit = ImageVramFit.Unknown,
                RequiredMb = required,
                FreeMb = 0,
                Explanation = Loc.F("imagegen.vram.needs", Mb(required)) + " " +
                              (gpu?.UnavailableReason ?? Loc.T("imagegen.vram.noFreeInfo"))
            };
        }

        int libre = gpu.FreeMb;
        int sobra = libre - required - SafetyMarginMb;

        if (sobra >= TightMarginMb)
        {
            return new ImageVramVerdict
            {
                Fit = ImageVramFit.Green,
                RequiredMb = required,
                FreeMb = libre,
                Explanation = Loc.F("imagegen.vram.green", Mb(required), Mb(libre))
            };
        }

        if (sobra >= 0)
        {
            return new ImageVramVerdict
            {
                Fit = ImageVramFit.Yellow,
                RequiredMb = required,
                FreeMb = libre,
                Explanation = Loc.F("imagegen.vram.yellow", Mb(required), Mb(libre))
            };
        }

        // No entra. Antes de decir que no, se mira si alguna palanca lo arregla: es
        // mucho mejor sugerir el ajuste que rechazar la generación.
        var sugerida = Recommend(request, config, libre, calibratedWeightsMb);
        string comoArreglarlo = DescribeFix(config, sugerida, request, libre, calibratedWeightsMb);

        return new ImageVramVerdict
        {
            Fit = ImageVramFit.Red,
            RequiredMb = required,
            FreeMb = libre,
            RecommendedConfig = sugerida,
            Explanation = Loc.F("imagegen.vram.red", Mb(required), Mb(libre), comoArreglarlo)
        };
    }

    /// <summary>
    /// Devuelve una configuración que sí entre, o null si ni con todas las palancas
    /// puestas alcanza. Se activan en orden de rendimiento: primero la que casi no
    /// cuesta, después la que cuesta un poco.
    /// </summary>
    private static ImageServerConfig? Recommend(ImageGenRequest request, ImageServerConfig config,
                                                int libre, int? calibratedWeightsMb)
    {
        // El tiling primero: cuesta ~2 s de decodificación y puede ahorrar decenas de GB.
        var candidata = config;
        if (!candidata.VaeTiling)
        {
            candidata = candidata with { VaeTiling = true };
            if (Cabe(request, candidata, libre, calibratedWeightsMb)) return candidata;
        }

        // Después el offload: cuesta ~1,7 s y 8 GB de RAM, y saca los pesos de la VRAM.
        if (!candidata.OffloadToCpu)
        {
            candidata = candidata with { OffloadToCpu = true };
            if (Cabe(request, candidata, libre, calibratedWeightsMb)) return candidata;
        }

        return null;
    }

    private static bool Cabe(ImageGenRequest r, ImageServerConfig c, int libre, int? pesos) =>
        EstimateRequiredMb(r.Width, r.Height, c, pesos) + SafetyMarginMb <= libre;

    private static string DescribeFix(ImageServerConfig actual, ImageServerConfig? sugerida,
                                      ImageGenRequest request, int libre, int? pesos)
    {
        if (sugerida is not null)
        {
            var cambios = new System.Collections.Generic.List<string>();
            if (sugerida.VaeTiling && !actual.VaeTiling) cambios.Add(Loc.T("imagegen.vram.fix.tiling"));
            if (sugerida.OffloadToCpu && !actual.OffloadToCpu) cambios.Add(Loc.T("imagegen.vram.fix.offload"));
            return Loc.F("imagegen.vram.fix", string.Join(Loc.T("imagegen.vram.fix.and"), cambios));
        }

        // Ni con todo puesto. Lo único que queda es bajar la resolución: se calcula
        // cuál es la más grande que sí entraría, en lugar de decir "probá más chico".
        var conTodo = actual with { VaeTiling = true, OffloadToCpu = true };
        for (int lado = MaxSafeDimension; lado >= 256; lado -= 128)
        {
            if (EstimateRequiredMb(lado, lado, conTodo, pesos) + SafetyMarginMb <= libre)
                return Loc.F("imagegen.vram.maxSize", lado);
        }

        return Loc.T("imagegen.vram.none");
    }

    private static string Mb(int mb) => mb >= 1024 ? $"{mb / 1024.0:F1} GB" : $"{mb} MB";
}
