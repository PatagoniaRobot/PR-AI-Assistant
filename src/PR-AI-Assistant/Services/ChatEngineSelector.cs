using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PRImager.AIAssistant.Services.Models;

namespace PRImager.AIAssistant.Services;

/// <summary>
/// Elige qué llama-server usar según la placa (etapa E5, «que funcione sin NVIDIA»).
///
/// La aplicación puede traer dos variantes de la misma release de llama.cpp:
///
///   Tools\llama-server.exe          CUDA: solo NVIDIA, la más rápida ahí
///   Tools\vulkan\llama-server.exe   Vulkan: cualquier marca, 54 MB contra ~600 de CUDA
///
/// Medido el 20 de octubre de 2026 en la RTX 5060 Ti con Qwen3.5-9B Q5_K_S, b8671: escribe
/// igual (59,9 contra 59,3 tok/s) y lee un 17 % más lento por Vulkan (2.712 contra 2.261
/// tok/s). Con NVIDIA se prefiere CUDA; con AMD o Intel, Vulkan es la única que usa la placa.
///
/// Se elige la variante que pueda usar la placa de más memoria; si ninguna puede, la
/// principal, que corre en CPU igual que cualquiera. Si el usuario configuró otra ruta en
/// <c>llama_server_executable</c>, se respeta tal cual.
///
/// La decide la placa, no el backend instalado: por eso no puede depender de
/// <see cref="HardwareInfoService"/> sobre una carpeta, que es justamente lo que se elige.
/// </summary>
public static class ChatEngineSelector
{
    public const string DefaultExecutable = @"Tools\llama-server.exe";
    public const string VulkanExecutable = @"Tools\vulkan\llama-server.exe";

    private static readonly Lazy<IReadOnlyList<GpuAdapterInfo>> Gpus = new(HardwareInfoService.ReadGpus);

    /// <summary>Las placas de esta PC, leídas una vez por sesión. Las usa también el motor de imágenes.</summary>
    public static IReadOnlyList<GpuAdapterInfo> DetectedGpus => Gpus.Value;

    /// <summary>La ruta del llama-server con el que se va a arrancar.</summary>
    public static string Resolve(AppSettings settings) =>
        Resolve(settings, AppDomain.CurrentDomain.BaseDirectory, Gpus.Value);

    public static string Resolve(AppSettings settings, string baseDir, IReadOnlyList<GpuAdapterInfo> gpus)
    {
        string configured = settings.LlamaServerExecutable;
        bool isDefault = string.IsNullOrWhiteSpace(configured) ||
                         string.Equals(configured.Replace('/', '\\'), DefaultExecutable, StringComparison.OrdinalIgnoreCase);
        if (!isDefault)
            return Path.IsPathRooted(configured) ? configured : Path.Combine(baseDir, configured);

        return Choose(Path.Combine(baseDir, DefaultExecutable), Path.Combine(baseDir, VulkanExecutable), gpus);
    }

    /// <summary>
    /// La regla, sobre dos variantes cualesquiera de un motor —la del chat y la de
    /// imágenes usan la misma—. Pública para ejercerla sin hardware.
    /// </summary>
    public static string Choose(string main, string vulkan, IReadOnlyList<GpuAdapterInfo> gpus)
    {
        var candidates = new[] { main, vulkan }.Where(File.Exists).ToList();
        if (candidates.Count == 0) return main; // que el error de arranque nombre la ruta de siempre

        GpuAdapterInfo? Usable(string exe) =>
            HardwareInfoService.PickUsableGpu(gpus, HardwareInfoService.DetectBackend(Path.GetDirectoryName(exe)!)).gpu;

        // OrderBy es estable: a igual placa gana la principal, que va primera
        var best = candidates
            .Select(exe => (exe, gpu: Usable(exe)))
            .Where(c => c.gpu != null)
            .OrderByDescending(c => c.gpu!.DedicatedVramMb)
            .FirstOrDefault();

        return best.exe ?? candidates[0];
    }
}
