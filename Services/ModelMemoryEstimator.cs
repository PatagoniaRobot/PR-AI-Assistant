using System;

namespace PRImager.AIAssistant.Services;

/// <summary>
/// En qué formato guarda llama.cpp la caché de atención. q8_0 ocupa 34 bytes cada 32
/// valores contra 64 de f16: medido el 20 de octubre de 2026 con Qwen3.5-9B a 32k, 544 MiB
/// contra 1.024, con 1 % menos de velocidad y la misma respuesta, en CUDA y en Vulkan.
/// </summary>
public enum KvCacheType
{
    F16,
    Q8_0
}

/// <summary>
/// Cuánta memoria pide un modelo, en bytes, separada como la reparte llama.cpp.
/// </summary>
public sealed class ModelMemory
{
    /// <summary>Pesos que van a la placa con todas las capas en GPU.</summary>
    public long WeightsGpuBytes { get; init; }

    /// <summary>Pesos que quedan en la RAM aunque todo vaya a la placa (embeddings de entrada).</summary>
    public long WeightsCpuBytes { get; init; }

    /// <summary>Caché de atención (K y V) para el contexto pedido.</summary>
    public long KvCacheBytes { get; init; }

    /// <summary>Estado de las capas recurrentes (los modelos híbridos, como Qwen3.5).</summary>
    public long RecurrentStateBytes { get; init; }

    /// <summary>Búfer de cómputo y contexto del runtime en la placa.</summary>
    public long OverheadBytes { get; init; }

    public int Layers { get; init; }

    /// <summary>True si el desglose sale de la metadata del archivo; false si es una estimación por tamaño.</summary>
    public bool IsExact { get; init; }

    /// <summary>Lo que ocupa en la placa con todas las capas en GPU.</summary>
    public long FullGpuBytes => WeightsGpuBytes + KvCacheBytes + RecurrentStateBytes + OverheadBytes;

    /// <summary>
    /// Lo que ocupa en la placa con <paramref name="gpuLayers"/> capas en GPU. llama.cpp
    /// pone en la placa la caché de las capas que van a la placa, así que pesos y caché
    /// se reparten juntos. El búfer de cómputo está siempre.
    /// </summary>
    public long GpuBytesFor(int gpuLayers)
    {
        if (Layers <= 0 || gpuLayers >= Layers) return FullGpuBytes;
        if (gpuLayers <= 0) return 0;
        double share = (double)gpuLayers / Layers;
        return (long)((WeightsGpuBytes + KvCacheBytes + RecurrentStateBytes) * share) + OverheadBytes;
    }

    /// <summary>Lo que ocupa en la RAM con <paramref name="gpuLayers"/> capas en GPU.</summary>
    public long CpuBytesFor(int gpuLayers)
    {
        long rest = WeightsGpuBytes + KvCacheBytes + RecurrentStateBytes;
        if (Layers <= 0 || gpuLayers >= Layers) return WeightsCpuBytes;
        if (gpuLayers <= 0) return WeightsCpuBytes + rest;
        double share = 1.0 - (double)gpuLayers / Layers;
        return WeightsCpuBytes + (long)(rest * share);
    }
}

/// <summary>
/// Calcula la memoria de un modelo a partir de su cabecera GGUF (etapa E5).
///
/// Reemplaza la estimación por tamaño de archivo de <see cref="VramEstimator"/> cuando se
/// tiene la cabecera. La diferencia no es chica: los modelos de hoy no guardan caché en
/// todas sus capas. Medido el 20 de octubre de 2026 con llama-server, a 32k de contexto:
///
///   Qwen3.5-9B      híbrido: 8 de 32 capas con atención      KV 1.024 MiB + estado 201 MiB
///   Gemma 4 E4B     18 capas comparten caché; de las 24 que   KV 512 MiB (4 globales)
///                   quedan, 20 miran una ventana de 512       + 100 MiB (20 de ventana)
///   Gemma 4 E2B     ídem, 3 globales y 12 de ventana           KV 192 + 30 MiB
///
/// La fórmula de abajo reproduce esos números al MiB. La estimación por tamaño daba para
/// Gemma 4 E4B a 32k ~4,1 GB de caché: siete veces de más.
///
/// Las constantes son las que usa llama-server sin flags: caché en f16, cuatro secuencias
/// en paralelo con caché unificada y lotes de 512. La aplicación no las cambia.
/// </summary>
public static class ModelMemoryEstimator
{
    private const int Parallel = 4;
    private const int Ubatch = 512;
    public static double KvBytesPerValue(KvCacheType t) => t == KvCacheType.Q8_0 ? 34.0 / 32 : 2.0;

    /// <summary>Lo que se le pasa a llama-server (<c>-ctk</c>/<c>-ctv</c>).</summary>
    public static string ToArgument(KvCacheType t) => t == KvCacheType.Q8_0 ? "q8_0" : "f16";
    private const int StateBytesPerValue = 4;   // f32

    /// <summary>
    /// Búfer de cómputo más el contexto del runtime en la placa. El búfer medido anda entre
    /// 501 y 551 MiB en los tres modelos; el resto es margen para el contexto de CUDA.
    /// </summary>
    public const long OverheadBytes = 900L * 1024 * 1024;

    /// <summary>
    /// Memoria del modelo con ese contexto. Con la tabla de tensores el reparto entre placa
    /// y RAM es exacto; sin ella se toma el archivo entero como pesos de placa, que es
    /// pasarse a propósito. Null si la cabecera no trae lo mínimo (capas).
    /// </summary>
    /// <param name="probedFileBytes">
    /// Tamaño del archivo del que salió la cabecera, cuando se la usa para otra cuantización
    /// del mismo modelo: el catálogo lee una sola por repositorio. La arquitectura es la
    /// misma; los pesos se escalan por tamaño.
    /// </param>
    /// <param name="extraGpuBytes">Pesos que van a la placa además del modelo: el proyector de visión.</param>
    public static ModelMemory? Estimate(GgufHeader header, long fileBytes, int contextTokens, long probedFileBytes = 0,
                                        long extraGpuBytes = 0, KvCacheType kvCache = KvCacheType.F16)
    {
        if (header.BlockCount is not { } blocks || blocks == 0) return null;
        int n = (int)blocks;

        double scale = probedFileBytes > 0 ? (double)fileBytes / probedFileBytes : 1.0;
        long weightsGpu = (header.Tensors is { } t ? (long)(t.GpuBytes * scale) : fileBytes) + extraGpuBytes;
        long weightsCpu = header.Tensors is { } t2 ? (long)(t2.CpuResidentBytes * scale) : 0;

        return new ModelMemory
        {
            WeightsGpuBytes = weightsGpu,
            WeightsCpuBytes = weightsCpu,
            KvCacheBytes = KvCacheBytes(header, contextTokens, kvCache),
            RecurrentStateBytes = RecurrentStateBytes(header),
            OverheadBytes = OverheadBytes,
            Layers = n,
            IsExact = header.Tensors != null && probedFileBytes <= 0
        };
    }

    /// <summary>Bytes de caché K+V para el contexto dado.</summary>
    public static long KvCacheBytes(GgufHeader h, int contextTokens, KvCacheType kvCache = KvCacheType.F16)
    {
        if (h.BlockCount is not { } blocks || blocks == 0) return 0;
        int n = (int)blocks;

        long embd = h.ArchScalar("embedding_length") ?? 0;
        long[]? heads = h.Arch("attention.head_count");
        long[]? headsKv = h.Arch("attention.head_count_kv") ?? heads;
        long head0 = heads is { Length: > 0 } ? heads[0] : 0;
        long defaultHeadDim = head0 > 0 ? embd / head0 : 0;

        long keyLen = h.ArchScalar("attention.key_length") ?? defaultHeadDim;
        long valLen = h.ArchScalar("attention.value_length") ?? defaultHeadDim;
        long keyLenSwa = h.ArchScalar("attention.key_length_swa") ?? keyLen;
        long valLenSwa = h.ArchScalar("attention.value_length_swa") ?? valLen;
        long window = h.ArchScalar("attention.sliding_window") ?? 0;
        long[]? pattern = h.Arch("attention.sliding_window_pattern");

        // Capas que no guardan caché propia: las que la comparten (Gemma 4) y las de
        // predicción multi-token (MTP), que llama.cpp no usa para generar.
        int kvLayers = n - (int)(h.ArchScalar("attention.shared_kv_layers") ?? 0)
                         - (int)(h.ArchScalar("nextn_predict_layers") ?? 0);

        // Híbridos (Qwen3.5): solo una de cada N capas tiene atención; el resto es recurrente
        long interval = h.ArchScalar("full_attention_interval") ?? 0;

        long swaCells = Math.Min(contextTokens, (long)Parallel * window + Ubatch);
        double bytesPerValue = KvBytesPerValue(kvCache);
        double total = 0;
        for (int i = 0; i < kvLayers; i++)
        {
            if (interval > 1 && (i + 1) % interval != 0) continue;

            long hkv = headsKv == null ? 0 : headsKv.Length == 1 ? headsKv[0] : i < headsKv.Length ? headsKv[i] : 0;
            if (hkv == 0) continue;

            bool swa = window > 0 && IsSlidingLayer(pattern, i);
            long cells = swa ? swaCells : contextTokens;
            long dims = swa ? keyLenSwa + valLenSwa : keyLen + valLen;
            total += cells * hkv * dims * bytesPerValue;
        }
        return (long)total;
    }

    /// <summary>
    /// Si la capa mira una ventana. El patrón viene como arreglo por capa (Gemma 4: true es
    /// ventana) o como un período (Gemma 3: cada N, una es global). Sin patrón se la trata
    /// como global, que es lo que más ocupa: ante la duda, pasarse.
    /// </summary>
    private static bool IsSlidingLayer(long[]? pattern, int layer)
    {
        if (pattern == null || pattern.Length == 0) return false;
        if (pattern.Length > 1) return layer < pattern.Length && pattern[layer] != 0;
        long period = pattern[0];
        return period > 1 && (layer + 1) % period != 0;
    }

    /// <summary>
    /// Estado de las capas recurrentes: una matriz por secuencia y capa, más el búfer de la
    /// convolución. Para Qwen3.5-9B da 201 MiB, lo medido.
    /// </summary>
    public static long RecurrentStateBytes(GgufHeader h)
    {
        long state = h.ArchScalar("ssm.state_size") ?? 0;
        long inner = h.ArchScalar("ssm.inner_size") ?? 0;
        if (state == 0 || inner == 0 || h.BlockCount is not { } blocks) return 0;

        long conv = h.ArchScalar("ssm.conv_kernel") ?? 0;
        long groups = h.ArchScalar("ssm.group_count") ?? 0;
        long interval = h.ArchScalar("full_attention_interval") ?? 0;

        long layers = interval > 1 ? (long)blocks - (long)blocks / interval : (long)blocks;
        long perLayer = state * inner + Math.Max(0, conv - 1) * (inner + 2 * groups * state);
        return layers * Parallel * perLayer * StateBytesPerValue;
    }
}
