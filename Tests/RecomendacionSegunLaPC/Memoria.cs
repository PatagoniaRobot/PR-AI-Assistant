using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PRImager.AIAssistant.Services;

partial class Banco
{
    static string Repo => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\.."));

    // Medido el 20 de octubre de 2026 con el llama-server de Tools, -ngl 99, sin más
    // flags que -c: lo que imprime en llama_kv_cache, llama_memory_recurrent y
    // load_tensors. Son los números contra los que se calibró la fórmula.
    sealed record Medicion(string Archivo, double GpuMib, double CpuMib,
                           double Kv8k, double Kv32k, double Estado);

    static readonly Medicion[] Medidos =
    {
        new(@"Models\gemma-4-E2B-it-Q4_K_M.gguf", 1407.73, 1756.00, 48 + 30, 192 + 30, 0),
        new(@"Models\gemma-4-E4B-it-Q4_K_M.gguf", 2883.51, 2208.00, 128 + 100, 512 + 100, 0),
        new(@"bin\Debug\net8.0-windows\ModelosDescargados\Qwen3.5-9B-Q5_K_S.gguf", 5389.13, 666.88, 256, 1024, 201),
    };

    static void CasoMemoriaMedida()
    {
        Console.WriteLine("\nla memoria calculada contra la que midió llama-server");
        foreach (var m in Medidos)
        {
            string ruta = Path.Combine(Repo, m.Archivo);
            string nombre = Path.GetFileName(ruta);
            if (!File.Exists(ruta)) { Console.WriteLine($"  --   {nombre}: no está en esta máquina, se omite"); continue; }

            var h = GgufInspector.TryReadFileFull(ruta);
            Check($"{nombre}: se lee la tabla de tensores", h?.Tensors != null);
            if (h?.Tensors == null) continue;

            long bytes = new FileInfo(ruta).Length;
            var a8 = ModelMemoryEstimator.Estimate(h, bytes, 8192)!;
            var a32 = ModelMemoryEstimator.Estimate(h, bytes, 32768)!;

            CercaMib($"{nombre}: pesos en la placa", a8.WeightsGpuBytes, m.GpuMib, 1);
            CercaMib($"{nombre}: pesos que quedan en RAM", a8.WeightsCpuBytes, m.CpuMib, 1);
            CercaMib($"{nombre}: caché a 8k", a8.KvCacheBytes, m.Kv8k, 0.5);
            CercaMib($"{nombre}: caché a 32k", a32.KvCacheBytes, m.Kv32k, 0.5);
            CercaMib($"{nombre}: estado recurrente", a8.RecurrentStateBytes, m.Estado, 0.5);
            Check($"{nombre}: el desglose es exacto", a8.IsExact);
        }
    }

    static void CercaMib(string nombre, long bytes, double esperadoMib, double tolerancia)
    {
        double mib = bytes / (1024.0 * 1024);
        Check(nombre, Math.Abs(mib - esperadoMib) <= tolerancia, $"{mib:F2} MiB contra {esperadoMib:F2} medidos");
    }

    // --- la recomendación, con PCs descritas ---

    static HardwareInfo Pc(GpuAdapterInfo? gpu, int ramGb, int cores = 8, string? motivo = null) => new()
    {
        TotalRamMb = ramGb * 1024,
        AvailableRamMb = ramGb * 512,
        PhysicalCores = cores,
        LogicalCores = cores * 2,
        UsableGpu = gpu,
        Gpus = gpu == null ? Array.Empty<GpuAdapterInfo>() : new[] { gpu },
        Backend = ChatBackend.Cuda,
        GpuNotUsableReason = motivo
    };

    static GgufHeader? Cabecera(string relativo, out long bytes)
    {
        string ruta = Path.Combine(Repo, relativo);
        bytes = File.Exists(ruta) ? new FileInfo(ruta).Length : 0;
        return File.Exists(ruta) ? GgufInspector.TryReadFileFull(ruta) : null;
    }

    static void CasoRecomendacion()
    {
        Console.WriteLine("\ncómo correr Qwen3.5-9B Q5_K_S en distintas PCs");
        var h = Cabecera(Medidos[2].Archivo, out long bytes);
        if (h == null) { Console.WriteLine("  --   el modelo no está en esta máquina, se omite"); return; }

        var r16 = ModelRecommender.RecommendRun(h, bytes, Pc(Placa("RTX 5060 Ti", HardwareInfoService.VendorNvidia, 16050), 32));
        Check("16 GB de placa: entero y con 32k", r16.FullyOnGpu && r16.ContextTokens == 32768, r16.Summary);
        Console.WriteLine($"  · {r16.Explanation}");

        var r8 = ModelRecommender.RecommendRun(h, bytes, Pc(Placa("RTX 4060", HardwareInfoService.VendorNvidia, 8188), 16));
        Check("8 GB de placa: entero, con menos contexto", r8.FullyOnGpu && r8.ContextTokens is >= 8192 and < 32768, r8.Summary);

        var r6 = ModelRecommender.RecommendRun(h, bytes, Pc(Placa("RTX 2060", HardwareInfoService.VendorNvidia, 6144), 16));
        Check("6 GB de placa: repartido", r6.Fits && !r6.FullyOnGpu && r6.GpuLayers is > 0 and < 32, r6.Summary);
        Check("y lo repartido entra en la placa", r6.GpuBytes <= ModelRecommender.VramBudget(Pc(Placa("x", 0, 6144), 16)));
        Console.WriteLine($"  · {r6.Explanation}");

        var amd = ModelRecommender.RecommendRun(h, bytes,
            Pc(null, 16, 6, "AMD Radeon RX 7600 no se usa: el motor de chat instalado es para CUDA (NVIDIA), que no la soporta. Corre en CPU"));
        Check("AMD con el motor CUDA: CPU", amd.CpuOnly, amd.Summary);
        Check("el CPU usa un hilo por núcleo físico", amd.Threads == 6, amd.Threads.ToString());
        Check("y la explicación dice por qué no usa la placa", amd.Explanation.Contains("CUDA"), amd.Explanation);

        var chica = ModelRecommender.RecommendRun(h, bytes, Pc(null, 8));
        Check("8 GB de RAM sin placa: no entra", !chica.Fits, chica.Summary);
        Check("y dice qué hacer", chica.Explanation.Contains("más chica"), chica.Explanation);
    }

    static void CasoCuantizacion()
    {
        Console.WriteLine("\nqué cuantización conviene, con las de un repositorio real");
        var h = Cabecera(Medidos[2].Archivo, out long bytes);
        if (h == null) { Console.WriteLine("  --   el modelo no está en esta máquina, se omite"); return; }

        // Tamaños de unsloth/Qwen3.5-9B-GGUF
        var tamaños = new List<(string, long)>
        {
            ("Q2_K", 3_771_000_000), ("Q3_K_M", 4_640_000_000), ("Q4_K_M", 5_680_000_000),
            ("Q5_K_M", 6_580_000_000), ("Q6_K", 7_460_000_000), ("Q8_0", 9_530_000_000),
            ("BF16", 17_900_000_000)
        };
        string Elegida(HardwareInfo pc) =>
            ModelRecommender.PickQuantization(tamaños, h, bytes, pc) is int i and >= 0 ? tamaños[i].Item1 : "ninguna";

        var nvidia = (int mb) => Placa("NVIDIA", HardwareInfoService.VendorNvidia, mb);
        Check("16 GB: Q8_0, nunca BF16", Elegida(Pc(nvidia(16050), 32)) == "Q8_0", Elegida(Pc(nvidia(16050), 32)));
        // Q5_K_M: 5.575 MiB de pesos + 457 de caché y estado a 8k + 900 = 6.932 de 7.164
        Check("8 GB: la más grande que entra entera", Elegida(Pc(nvidia(8188), 16)) == "Q5_K_M", Elegida(Pc(nvidia(8188), 16)));
        // Ahí solo Q2_K entra entera, y no se elige por eso: Q4_K_M repartida rinde mejor
        Check("6 GB: Q4_K_M repartida antes que Q2_K entera", Elegida(Pc(nvidia(6144), 16)) == "Q4_K_M", Elegida(Pc(nvidia(6144), 16)));
        Check("4 GB: Q4_K_M repartida", Elegida(Pc(nvidia(4096), 16)) == "Q4_K_M", Elegida(Pc(nvidia(4096), 16)));
        Check("sin placa, 16 GB de RAM: Q4_K_M en CPU", Elegida(Pc(null, 16)) == "Q4_K_M", Elegida(Pc(null, 16)));
        Check("sin placa, 8 GB de RAM: Q2_K como último recurso", Elegida(Pc(null, 8)) == "Q2_K", Elegida(Pc(null, 8)));
        Check("y Q2_K se reconoce como de baja calidad", ModelRecommender.IsLowQuality("Q2_K") && ModelRecommender.IsLowQuality("UD-IQ2_XXS")
              && !ModelRecommender.IsLowQuality("Q3_K_M") && !ModelRecommender.IsLowQuality("IQ4_XS"));
        var sinNada = new List<(string, long)> { ("Q4_K_M", 5_680_000_000) };
        Check("si nada entra: ninguna", ModelRecommender.PickQuantization(sinNada, h, bytes, Pc(null, 8)) == -1);
    }
}
