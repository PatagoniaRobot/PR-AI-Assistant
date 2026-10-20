using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PRImager.AIAssistant.Services;

// Banco de la etapa E5: recomendar cuantización y parámetros según la PC.
//
// La PC de desarrollo es una sola —NVIDIA con 16 GB—, así que las reglas que dependen
// del hardware se ejercen con hardware descrito a mano: AMD con el motor CUDA, una
// integrada, una PC sin placa. La detección real se ejerce contra esta máquina.

partial class Banco
{
    static int _ok, _fail;

    static void Check(string nombre, bool cond, string detalle = "")
    {
        if (cond) { _ok++; Console.WriteLine($"  OK   {nombre}"); }
        else { _fail++; Console.WriteLine($"  FALLA {nombre}   {detalle}"); }
    }

    [STAThread]
    static int Main(string[] args)
    {
        Console.WriteLine("Banco de recomendación según la PC\n");

        var casos = new List<Action> { CasoEstaMaquina, CasoPlacaUtilizable, CasoBackend, CasoMemoriaMedida, CasoRecomendacion, CasoCuantizacion, CasoPerfilAutomatico };

        foreach (var caso in casos)
        {
            try { caso(); }
            catch (Exception ex)
            {
                _fail++;
                Console.WriteLine($"  FALLA {caso.Method.Name} lanzó {ex.GetType().Name}: {ex.Message}");
            }
        }

        Console.WriteLine($"\n{_ok} aserciones OK, {_fail} fallas");
        return _fail == 0 ? 0 : 1;
    }

    static string Tools => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\..\Tools"));

    // --- la detección real, contra esta máquina ---

    static void CasoEstaMaquina()
    {
        Console.WriteLine("esta máquina, detectada sin nvidia-smi");
        var hw = new HardwareInfoService(Tools).GetAsync().GetAwaiter().GetResult();

        Console.WriteLine($"  · {hw.Summary}");
        Console.WriteLine($"  · CPU: {hw.CpuName} · {hw.PhysicalCores} físicos / {hw.LogicalCores} lógicos · backend {hw.Backend}");
        foreach (var g in hw.Gpus) Console.WriteLine($"  · placa: {g.Name} [{g.Vendor}] {g.DedicatedVramMb} MB propios");

        Check("lee la RAM", hw.TotalRamMb > 4096, hw.TotalRamMb.ToString());
        Check("la libre es menor que la total", hw.AvailableRamMb > 0 && hw.AvailableRamMb < hw.TotalRamMb);
        Check("lee los núcleos físicos", hw.PhysicalCores > 0 && hw.PhysicalCores <= hw.LogicalCores,
              $"{hw.PhysicalCores}/{hw.LogicalCores}");
        Check("lee el nombre del procesador", hw.CpuName.Length > 0);
        Check("DXGI ve al menos una placa", hw.Gpus.Count > 0);
        Check("el motor de chat de Tools es CUDA", hw.Backend == ChatBackend.Cuda, hw.Backend.ToString());

        // Lo que se sabe de esta máquina: una RTX 5060 Ti de 16 GB
        var nvidia = hw.Gpus.FirstOrDefault(g => g.VendorId == HardwareInfoService.VendorNvidia);
        Check("la NVIDIA aparece con su memoria", nvidia != null && nvidia.DedicatedVramMb > 15000,
              nvidia?.DedicatedVramMb.ToString() ?? "no está");
        Check("y es la utilizable", hw.UsableGpu?.VendorId == HardwareInfoService.VendorNvidia);
        Check("nvidia-smi aporta la libre", hw.FreeVramMb is > 0);
    }

    // --- la regla, con hardware descrito ---

    static GpuAdapterInfo Placa(string nombre, uint vendor, int mb) =>
        new() { Name = nombre, VendorId = vendor, DedicatedVramMb = mb };

    static void CasoPlacaUtilizable()
    {
        Console.WriteLine("\nqué placa puede usar el motor de chat");
        var rtx = Placa("NVIDIA RTX 3060", HardwareInfoService.VendorNvidia, 12288);
        var rx = Placa("AMD Radeon RX 7800 XT", HardwareInfoService.VendorAmd, 16368);
        var uhd = Placa("Intel UHD Graphics 770", HardwareInfoService.VendorIntel, 128);
        var arc = Placa("Intel Arc A770", HardwareInfoService.VendorIntel, 16256);

        var (g, _) = HardwareInfoService.PickUsableGpu(new[] { uhd, rtx }, ChatBackend.Cuda);
        Check("NVIDIA con CUDA: se usa, aunque la integrada aparezca primero", g == rtx);

        var (g2, r2) = HardwareInfoService.PickUsableGpu(new[] { rx }, ChatBackend.Cuda);
        Check("AMD con el motor CUDA: no se usa", g2 == null);
        Check("y se dice por qué", r2 != null && r2.Contains("CUDA") && r2.Contains("CPU"), r2 ?? "");

        var (g3, _) = HardwareInfoService.PickUsableGpu(new[] { rx }, ChatBackend.Vulkan);
        Check("AMD con Vulkan: se usa", g3 == rx);

        var (g4, _) = HardwareInfoService.PickUsableGpu(new[] { arc, rtx }, ChatBackend.Vulkan);
        Check("con Vulkan, la de más memoria", g4 == arc);

        var (g5, r5) = HardwareInfoService.PickUsableGpu(new[] { uhd }, ChatBackend.Vulkan);
        Check("solo integrada: CPU", g5 == null && (r5 ?? "").Contains("integrada"), r5 ?? "");

        var (g6, r6) = HardwareInfoService.PickUsableGpu(Array.Empty<GpuAdapterInfo>(), ChatBackend.Cuda);
        Check("sin placa: CPU", g6 == null && (r6 ?? "").Contains("sin placa"), r6 ?? "");

        var (g7, _) = HardwareInfoService.PickUsableGpu(new[] { rx }, ChatBackend.Hip);
        Check("AMD con ROCm: se usa", g7 == rx);
    }

    static void CasoBackend()
    {
        Console.WriteLine("\nel backend sale de las .dll del motor");
        string raiz = Path.Combine(Path.GetTempPath(), "BancoRecomendacion", "backend");
        if (Directory.Exists(raiz)) Directory.Delete(raiz, true);
        Directory.CreateDirectory(raiz);
        Check("sin .dll de GPU: CPU", HardwareInfoService.DetectBackend(raiz) == ChatBackend.CpuOnly);
        File.WriteAllText(Path.Combine(raiz, "ggml-vulkan.dll"), "");
        Check("con ggml-vulkan.dll: Vulkan", HardwareInfoService.DetectBackend(raiz) == ChatBackend.Vulkan);
        Check("una carpeta inexistente no lanza",
              HardwareInfoService.DetectBackend(Path.Combine(raiz, "no")) == ChatBackend.CpuOnly);
    }
}
