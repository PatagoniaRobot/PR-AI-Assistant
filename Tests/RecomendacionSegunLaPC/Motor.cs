using System;
using System.IO;
using PRImager.AIAssistant.Services;
using PRImager.AIAssistant.Services.Models;

partial class Banco
{
    // --- qué llama-server se usa: CUDA o Vulkan, según la placa ---

    static void CasoMotor()
    {
        Console.WriteLine("\nqué motor de chat se elige según la placa");
        string raiz = Path.Combine(Path.GetTempPath(), "BancoRecomendacion", "motor");
        if (Directory.Exists(raiz)) Directory.Delete(raiz, true);
        string cuda = Instalar(Path.Combine(raiz, "Tools"), "ggml-cuda.dll");
        string vulkan = Instalar(Path.Combine(raiz, "Tools", "vulkan"), "ggml-vulkan.dll");

        var rtx = Placa("NVIDIA RTX 3060", HardwareInfoService.VendorNvidia, 12288);
        var rx = Placa("AMD Radeon RX 7800 XT", HardwareInfoService.VendorAmd, 16368);
        var arc = Placa("Intel Arc A770", HardwareInfoService.VendorIntel, 16256);
        var uhd = Placa("Intel UHD Graphics 770", HardwareInfoService.VendorIntel, 128);
        string Elegido(params GpuAdapterInfo[] g) => ChatEngineSelector.Resolve(new AppSettings(), raiz, g);

        Check("NVIDIA: CUDA, que es más rápida", Elegido(rtx) == cuda, Elegido(rtx));
        Check("AMD: Vulkan", Elegido(rx) == vulkan, Elegido(rx));
        Check("Intel Arc: Vulkan", Elegido(arc) == vulkan, Elegido(arc));
        Check("integrada sola: la principal, en CPU", Elegido(uhd) == cuda, Elegido(uhd));
        Check("sin placa: la principal", Elegido() == cuda, Elegido());
        Check("AMD y NVIDIA: la de más memoria, con su motor", Elegido(rtx, rx) == vulkan, Elegido(rtx, rx));

        var propio = new AppSettings { LlamaServerExecutable = @"C:\otro\llama-server.exe" };
        Check("una ruta configurada se respeta", ChatEngineSelector.Resolve(propio, raiz, new[] { rx }) == @"C:\otro\llama-server.exe");

        // Un paquete que trae solo Vulkan —el liviano— la usa con cualquier placa
        File.Delete(cuda);
        Check("solo Vulkan instalada: Vulkan, también con NVIDIA", Elegido(rtx) == vulkan, Elegido(rtx));
        File.Delete(vulkan);
        Check("ninguna: la ruta de siempre, para que el error la nombre", Elegido(rtx) == cuda, Elegido(rtx));

        // Y en esta máquina, con el Tools\ del repositorio y la placa real
        string real = ChatEngineSelector.Resolve(new AppSettings(), Repo, HardwareInfoService.ReadGpus());
        Check("esta máquina usa CUDA", real == Path.Combine(Repo, ChatEngineSelector.DefaultExecutable), real);
        bool hayVulkan = File.Exists(Path.Combine(Repo, ChatEngineSelector.VulkanExecutable));
        Check("y tiene la variante Vulkan al lado", hayVulkan);
        if (hayVulkan)
            Check("la variante Vulkan usaría la NVIDIA",
                  HardwareInfoService.PickUsableGpu(HardwareInfoService.ReadGpus(),
                      HardwareInfoService.DetectBackend(Path.Combine(Repo, "Tools", "vulkan"))).gpu != null);
    }

    static string Instalar(string carpeta, string dll)
    {
        Directory.CreateDirectory(carpeta);
        File.WriteAllText(Path.Combine(carpeta, dll), "");
        string exe = Path.Combine(carpeta, "llama-server.exe");
        File.WriteAllText(exe, "");
        return exe;
    }
}

#if CATALOGO
partial class Banco
{
    // --- y el de imágenes, con la misma regla ---

    static void CasoMotorImagenes()
    {
        Console.WriteLine("\nqué motor de imágenes se elige según la placa");
        string raiz = Path.Combine(Path.GetTempPath(), "BancoRecomendacion", "sd");
        if (Directory.Exists(raiz)) Directory.Delete(raiz, true);
        string cuda = Instalar(Path.Combine(raiz, "bin"), "ggml-cuda.dll").Replace("llama-server.exe", "sd-server.exe");
        File.Move(Path.Combine(raiz, "bin", "llama-server.exe"), cuda);
        string vulkan = Instalar(Path.Combine(raiz, "bin-vulkan"), "ggml-vulkan.dll").Replace("llama-server.exe", "sd-server.exe");
        File.Move(Path.Combine(raiz, "bin-vulkan", "llama-server.exe"), vulkan);

        string antes = Environment.GetEnvironmentVariable(PRImager.AIAssistant.Services.ImageGen.ImageEngineLocator.RootEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(PRImager.AIAssistant.Services.ImageGen.ImageEngineLocator.RootEnvironmentVariable, raiz);
            string Elegido(params GpuAdapterInfo[] g) =>
                PRImager.AIAssistant.Services.ImageGen.ImageEngineLocator.ResolveExecutablePath(g);

            Check("NVIDIA: CUDA", Elegido(Placa("RTX", HardwareInfoService.VendorNvidia, 12288)) == cuda);
            Check("AMD: Vulkan", Elegido(Placa("RX", HardwareInfoService.VendorAmd, 16368)) == vulkan);
            File.Delete(cuda);
            Check("sin la de CUDA, Vulkan también con NVIDIA",
                  Elegido(Placa("RTX", HardwareInfoService.VendorNvidia, 12288)) == vulkan);
        }
        finally
        {
            Environment.SetEnvironmentVariable(PRImager.AIAssistant.Services.ImageGen.ImageEngineLocator.RootEnvironmentVariable, antes);
        }
    }
}
#endif
