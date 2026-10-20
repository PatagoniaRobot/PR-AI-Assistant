#if CATALOGO
using System;
using System.IO;
using System.Linq;
using System.Threading;
using PRImager.AIAssistant.Services;
using PRImager.AIAssistant.Services.HuggingFace;
using PRImager.AIAssistant.ViewModels.HuggingFace;

partial class Banco
{
    // --- el catálogo, contra un repositorio real, con tres PCs descritas ---

    static void CasoCatalogoReal()
    {
        Console.WriteLine("\nel catálogo recomienda una cuantización de unsloth/Qwen3.5-9B-GGUF");
        var nvidia = (int mb) => Placa("NVIDIA", HardwareInfoService.VendorNvidia, mb);

        // La lectura de la cabecera llega hasta la tabla de tensores y corta ahí
        using (var client = new HuggingFaceClient())
        {
            // El tamaño tiene que ser el real: la última entrada de la tabla se mide contra él
            var detalle = client.GetRepoDetailAsync("unsloth/Qwen3.5-9B-GGUF").GetAwaiter().GetResult();
            long tamaño = detalle.Value?.GgufFiles.FirstOrDefault(f => f.FileName == "Qwen3.5-9B-Q4_K_M.gguf")?.SizeBytes ?? 0;
            var cron = System.Diagnostics.Stopwatch.StartNew();
            var r = client.ProbeGgufAsync("unsloth/Qwen3.5-9B-GGUF", "Qwen3.5-9B-Q4_K_M.gguf",
                                          fileLength: tamaño).GetAwaiter().GetResult();
            cron.Stop();
            Check("la lectura remota llega a la tabla de tensores", r.Success && r.Value!.Tensors != null, r.ErrorMessage ?? "");
            if (r.Success && r.Value!.Tensors is { } t)
                Console.WriteLine($"  · {cron.Elapsed.TotalSeconds:F1} s; en la RAM quedan " +
                                  $"{t.CpuResidentBytes / 1048576.0:F0} MiB de {t.TotalBytes / 1048576.0:F0}");
        }

        var (q16, fila16, estado16) = Recomendada(Pc(nvidia(16050), 32));
        Console.WriteLine($"  · 16 GB: {estado16}");
        // UD-Q8_K_XL es el Q8 de unsloth con algunas capas sin cuantizar: sigue siendo 8 bits,
        // pesa un poco más y es un poco mejor. Lo que no debe salir nunca es BF16.
        Check("16 GB de placa: una de 8 bits, nunca BF16", q16 is "Q8_0" or "UD-Q8_K_XL", q16);
        Check("entera en la placa", fila16?.Run?.FullyOnGpu == true);
        Check("la fila lo marca", fila16?.VramLabel.StartsWith("★ Recomendada") == true, fila16?.VramLabel ?? "");

        var (q8, fila8, estado8) = Recomendada(Pc(nvidia(8188), 16));
        Console.WriteLine($"  · 8 GB:  {estado8}");
        Check("8 GB de placa: entera, de 4 bits o más", fila8?.Run?.FullyOnGpu == true && !ModelRecommender.IsLowQuality(q8), q8);
        Check("y más chica que la de 16 GB", fila8 != null && fila16 != null && fila8.File.SizeBytes < fila16.File.SizeBytes);

        var (qc, filac, estadoc) = Recomendada(Pc(null, 16, 6, "sin placa de video: corre en CPU"));
        Console.WriteLine($"  · CPU:   {estadoc}");
        Check("sin placa: Q4_K_M en CPU", qc.Contains("Q4_K_M") && filac?.Run?.CpuOnly == true, qc);
        Check("la explicación da los parámetros", filac?.VramExplanation.Contains("6 hilos") == true, filac?.VramExplanation ?? "");

        // El encabezado de la ventana describe la PC, no solo la placa
        using var vm = Catalogo(Pc(null, 16, 6, "sin placa de video: corre en CPU"));
        Esperar(() => vm.GpuText.StartsWith("Tu PC:"));
        Check("el encabezado describe la PC sin placa", vm.GpuText.Contains("16 GB de RAM") && vm.GpuText.Contains("CPU"), vm.GpuText);
    }

    static ModelCatalogViewModel Catalogo(HardwareInfo pc)
    {
        string logs = Path.Combine(Path.GetTempPath(), "BancoRecomendacion", "logs");
        return new ModelCatalogViewModel(Path.Combine(Path.GetTempPath(), "BancoRecomendacion", "desc"), 8192, logs,
                                         hardware: HardwareInfoService.Fixed(pc));
    }

    static (string quant, QuantizationItem? fila, string estado) Recomendada(HardwareInfo pc)
    {
        using var vm = Catalogo(pc);
        vm.SelectedRepo = new HfRepo { Id = "unsloth/Qwen3.5-9B-GGUF" };
        Esperar(() => !vm.IsBusy && vm.Quantizations.Count > 0, 60);
        var fila = vm.Quantizations.FirstOrDefault(q => q.IsRecommended);
        return (fila?.Quantization ?? "ninguna", fila, vm.Status);
    }

    static void Esperar(Func<bool> condicion, int segundos = 20)
    {
        var limite = DateTime.UtcNow.AddSeconds(segundos);
        while (!condicion())
        {
            if (DateTime.UtcNow > limite) throw new TimeoutException("la condición no se cumplió a tiempo");
            Thread.Sleep(50);
        }
    }
}
#endif
