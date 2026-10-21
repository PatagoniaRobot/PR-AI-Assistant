using System;
using System.Collections.Generic;
using System.Linq;

namespace PRImager.AIAssistant.Services;

/// <summary>Cómo correr un modelo en esta PC: lo que se le pasa a llama-server, y por qué.</summary>
public sealed class RunRecommendation
{
    public int ContextTokens { get; init; }

    /// <summary>Capas en la placa. <see cref="AllLayers"/> es todas; 0 es solo CPU.</summary>
    public int GpuLayers { get; init; }

    public int Threads { get; init; }

    /// <summary>Formato de la caché de la conversación: comprimida cuando eso gana contexto o capas.</summary>
    public KvCacheType KvCache { get; init; } = KvCacheType.F16;

    /// <summary>False si ni repartiendo entre placa y RAM entra.</summary>
    public bool Fits { get; init; }

    public bool FullyOnGpu { get; init; }
    public bool CpuOnly => Fits && GpuLayers == 0;

    public long GpuBytes { get; init; }
    public long RamBytes { get; init; }

    /// <summary>Una línea para el usuario: «todo en la placa · contexto 32k».</summary>
    public string Summary { get; init; } = "";

    /// <summary>La explicación, con los números.</summary>
    public string Explanation { get; init; } = "";

    /// <summary>El valor que llama-server entiende como «todas las capas».</summary>
    public const int AllLayers = 99;
}

/// <summary>
/// Recomienda cuantización y parámetros según la PC (etapa E5): el pedido del usuario fue
/// «que la aplicación recomiende, no modelos, sino cuantizaciones y parámetros según la PC
/// donde corre».
///
/// Criterio, en orden:
///
///   1. **Todo en la placa** si entra con un contexto de al menos 8k. Es por lejos lo más
///      rápido: repartir entre placa y RAM cuesta más velocidad que bajar de cuantización.
///   2. Entre las que entran, **la cuantización más grande**, que es la de mejor calidad,
///      sin pasar de 8 bits: F16 y BF16 pesan el doble y no se notan en un chat.
///   3. Con el modelo elegido, **el contexto más grande que entre**, hasta 32k. Más
///      contexto es más memoria y más lentitud al leer documentos largos; 32k ya cubre
///      un informe largo entero.
///   4. Si no entra nada entero: repartir, con Q4_K_M o la más cercana, que es el punto
///      donde la calidad todavía no se resiente. Sin placa: CPU, con la misma regla.
///   5. **La caché de la conversación, comprimida (q8_0) solo si eso gana**: más contexto
///      entero en la placa, o más capas en la placa al repartir.
///
/// Las reservas son de margen y no se miden: 1 GB de VRAM para el escritorio y lo que
/// esté abierto, y 4 GB de RAM para Windows y la aplicación.
/// </summary>
public static class ModelRecommender
{
    public const long VramReserveBytes = 1024L * 1024 * 1024;
    public const long RamReserveBytes = 4096L * 1024 * 1024;

    public const int MinComfortContext = 8192;
    public const int MaxRecommendedContext = 32768;
    private static readonly int[] Contexts = { 32768, 16384, 8192, 4096 };

    private const long Mib = 1024 * 1024;

    public static long VramBudget(HardwareInfo hw) =>
        hw.UsableGpu == null ? 0 : Math.Max(0, (long)hw.UsableVramMb * Mib - VramReserveBytes);

    public static long RamBudget(HardwareInfo hw) =>
        Math.Max(0, (long)hw.TotalRamMb * Mib - RamReserveBytes);

    /// <summary>
    /// Hilos: uno por núcleo físico. Con dos hilos por núcleo compiten por la misma unidad
    /// y llama.cpp rinde menos, no más.
    /// </summary>
    public static int Threads(HardwareInfo hw) =>
        hw.PhysicalCores > 0 ? hw.PhysicalCores : Math.Max(1, hw.LogicalCores / 2);

    /// <summary>Cómo correr este archivo en esta PC.</summary>
    /// <param name="extraGpuBytes">Lo que va a la placa además del modelo: el proyector de visión.</param>
    public static RunRecommendation RecommendRun(GgufHeader header, long fileBytes, HardwareInfo hw,
                                                 long probedFileBytes = 0, long extraGpuBytes = 0)
    {
        long vram = VramBudget(hw);
        long ram = RamBudget(hw);
        int threads = Threads(hw);
        long trained = header.ArchScalar("context_length") ?? MaxRecommendedContext;

        ModelMemory? At(int ctx, KvCacheType kv) =>
            ModelMemoryEstimator.Estimate(header, fileBytes, ctx, probedFileBytes, extraGpuBytes, kv);

        // 1. Todo en la placa, con el contexto más grande que entre. A igual contexto, caché
        //    normal antes que comprimida; pero 32k comprimida le gana a 16k normal: la
        //    compresión no se nota en la respuesta (ModelMemoryEstimator) y el contexto sí.
        if (vram > 0)
        {
            foreach (int ctx in Contexts.Where(c => c <= trained))
            {
                foreach (var kv in KvOptions)
                {
                    var m = At(ctx, kv);
                    if (m == null) return NoData();
                    if (m.FullGpuBytes <= vram && m.WeightsCpuBytes <= ram)
                    {
                        string cache = kv == KvCacheType.Q8_0 ? Loc.T("rec.compressedCache") : "";
                        return new RunRecommendation
                        {
                            ContextTokens = ctx,
                            GpuLayers = RunRecommendation.AllLayers,
                            Threads = threads,
                            KvCache = kv,
                            Fits = true,
                            FullyOnGpu = true,
                            GpuBytes = m.FullGpuBytes,
                            RamBytes = m.WeightsCpuBytes,
                            Summary = Loc.F("rec.full.summary", K(ctx), cache),
                            Explanation = Loc.F(kv == KvCacheType.Q8_0 ? "rec.full.explanationQ8" : "rec.full.explanation",
                                                K(ctx), Gb(m.FullGpuBytes), Gb(vram))
                        };
                    }
                }
            }
        }

        int baseCtx = (int)Math.Min(MinComfortContext, trained);
        var mem = At(baseCtx, KvCacheType.F16);
        if (mem == null) return NoData();

        // 2. Repartido: las capas que entren en la placa, el resto en RAM. Con la caché
        //    comprimida entran más capas, y cada capa en la placa es velocidad.
        if (vram > 0)
        {
            var best = KvOptions
                .Select(kv => (kv, m: At(baseCtx, kv)!))
                .Select(x => (x.kv, x.m, layers: LayersThatFit(x.m, vram)))
                .Where(x => x.layers > 0 && x.m.CpuBytesFor(x.layers) <= ram)
                .OrderByDescending(x => x.layers)   // estable: a igual capas, la normal
                .FirstOrDefault();
            if (best.m != null)
            {
                int pct = best.layers * 100 / best.m.Layers;
                string cache = best.kv == KvCacheType.Q8_0 ? Loc.T("rec.compressedCache") : "";
                return new RunRecommendation
                {
                    ContextTokens = baseCtx,
                    GpuLayers = best.layers,
                    Threads = threads,
                    KvCache = best.kv,
                    Fits = true,
                    GpuBytes = best.m.GpuBytesFor(best.layers),
                    RamBytes = best.m.CpuBytesFor(best.layers),
                    Summary = Loc.F("rec.split.summary", best.layers, best.m.Layers, K(baseCtx), cache),
                    Explanation = Loc.F("rec.split.explanation", best.layers, best.m.Layers, pct)
                };
            }
        }

        // 3. Solo CPU. La RAM sobra más que la placa: caché normal
        long cpu = mem.CpuBytesFor(0);
        if (cpu <= ram)
        {
            string why = hw.UsableGpu == null
                ? (hw.GpuNotUsableReason ?? Loc.T("rec.cpu.noGpu")) + "."
                : Loc.T("rec.cpu.gpuTooSmall");
            return new RunRecommendation
            {
                ContextTokens = baseCtx,
                GpuLayers = 0,
                Threads = threads,
                Fits = true,
                RamBytes = cpu,
                Summary = Loc.F("rec.cpu.summary", K(baseCtx), threads),
                Explanation = Loc.F("rec.cpu.explanation", why, threads, Gb(cpu))
            };
        }

        return new RunRecommendation
        {
            ContextTokens = baseCtx,
            Fits = false,
            Summary = Loc.T("rec.noFit.summary"),
            Explanation = Loc.F("rec.noFit.explanation", Gb(cpu), Gb(vram), Gb(ram))
        };
    }

    private static readonly KvCacheType[] KvOptions = { KvCacheType.F16, KvCacheType.Q8_0 };

    private static RunRecommendation NoData() =>
        new() { Fits = false, Summary = Loc.T("rec.noData.summary"), Explanation = Loc.T("rec.noData.explanation") };

    private static int LayersThatFit(ModelMemory m, long vram)
    {
        for (int ngl = m.Layers - 1; ngl >= 1; ngl--)
            if (m.GpuBytesFor(ngl) <= vram) return ngl;
        return 0;
    }

    /// <summary>
    /// Cuál de las cuantizaciones de un mismo modelo conviene en esta PC. Devuelve el índice
    /// en <paramref name="sizes"/>, o -1 si ninguna entra. La cabecera es de una sola de
    /// ellas, la de <paramref name="probedFileBytes"/>.
    /// </summary>
    public static int PickQuantization(IReadOnlyList<(string Quant, long Bytes)> sizes, GgufHeader header,
                                       long probedFileBytes, HardwareInfo hw)
    {
        var candidates = sizes
            .Select((s, i) => (s.Quant, s.Bytes, Index: i))
            .Where(s => s.Bytes > 0 && !IsUnquantized(s.Quant))
            .OrderByDescending(s => s.Bytes)
            .ToList();
        if (candidates.Count == 0) return -1;

        var runs = candidates
            .Select(c => (c, run: RecommendRun(header, c.Bytes, hw, probedFileBytes)))
            .ToList();

        // La más grande que entra entera en la placa con contexto cómodo, sin bajar a
        // 2 bits: un Q2_K entero rinde peor que un Q4_K_M repartido.
        var full = runs.FirstOrDefault(r => r.run.FullyOnGpu && r.run.ContextTokens >= MinComfortContext
                                            && !IsLowQuality(r.c.Quant));
        if (full.run != null) return full.c.Index;

        // Si no, la más cercana a Q4_K_M por debajo de ella que entre de alguna forma;
        // si ninguna de esas entra, la más chica que entre.
        long q4 = candidates.FirstOrDefault(c => c.Quant.StartsWith("Q4_K_M", StringComparison.OrdinalIgnoreCase)
                                              || c.Quant.StartsWith("UD-Q4_K_M", StringComparison.OrdinalIgnoreCase)).Bytes;
        var fitting = runs.Where(r => r.run.Fits).ToList();
        if (fitting.Count == 0) return -1;

        // 2 bits solo como último recurso: si es lo único que entra
        var decent = fitting.Where(r => !IsLowQuality(r.c.Quant)).ToList();
        if (decent.Count == 0) return fitting[^1].c.Index;

        if (q4 > 0)
        {
            var atOrBelowQ4 = decent.FirstOrDefault(r => r.c.Bytes <= q4);
            if (atOrBelowQ4.run != null) return atOrBelowQ4.c.Index;
        }
        return decent[^1].c.Index;
    }

    /// <summary>
    /// Por debajo de 3 bits la calidad se nota en un chat: respuestas incoherentes, sobre
    /// todo en modelos chicos. Se recomiendan solo si no entra otra cosa, y con aviso.
    /// </summary>
    public static bool IsLowQuality(string quant)
    {
        string q = quant.StartsWith("UD-", StringComparison.OrdinalIgnoreCase) ? quant.Substring(3) : quant;
        return q.StartsWith("Q2", StringComparison.OrdinalIgnoreCase) ||
               q.StartsWith("Q1", StringComparison.OrdinalIgnoreCase) ||
               q.StartsWith("IQ1", StringComparison.OrdinalIgnoreCase) ||
               q.StartsWith("IQ2", StringComparison.OrdinalIgnoreCase) ||
               q.StartsWith("TQ", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUnquantized(string quant) =>
        quant.Equals("F16", StringComparison.OrdinalIgnoreCase) ||
        quant.Equals("BF16", StringComparison.OrdinalIgnoreCase) ||
        quant.Equals("F32", StringComparison.OrdinalIgnoreCase);

    public static string K(int tokens) => tokens >= 1024 ? $"{tokens / 1024}k" : tokens.ToString();

    public static string Gb(long bytes) => $"{bytes / (1024.0 * 1024 * 1024):F1} GB";
}
