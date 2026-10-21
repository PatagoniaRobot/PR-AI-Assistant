using System;
using System.IO;
using System.Linq;
using PRImager.AIAssistant.Services;
using PRImager.AIAssistant.Services.Models;
using PRImager.AIAssistant.ViewModels;

partial class Banco
{
    // --- el perfil «Automático», con la pantalla de selección real ---

    static void CasoPerfilAutomatico()
    {
        Console.WriteLine("\nel perfil Automático en la pantalla de selección");
        // Gemma 4 E4B, del catálogo y con su proyector de visión al lado: así vale para
        // las dos ediciones —la Forense no mira la carpeta de descargas— y el proyector
        // entra en la cuenta
        string modelos = Path.Combine(Repo, "Models");
        string gemma = Path.Combine(modelos, "gemma-4-E4B-it-Q4_K_M.gguf");
        if (!File.Exists(gemma)) { Console.WriteLine("  --   el modelo no está en esta máquina, se omite"); return; }

        string raiz = Path.Combine(Path.GetTempPath(), "BancoRecomendacion", "perfil");
        if (Directory.Exists(raiz)) Directory.Delete(raiz, true);
        Directory.CreateDirectory(raiz);
        // El catálogo de fábrica viene sin modelos desde que la aplicación se publica sin
        // ellos: se le agrega el Gemma acá, con los perfiles de ejecución reales.
        var catalogo = System.Text.Json.Nodes.JsonNode.Parse(
            File.ReadAllText(Path.Combine(Repo, "Config", "model_catalog.json")))!;
        catalogo["models"] = System.Text.Json.Nodes.JsonNode.Parse("""
            [{ "id": "e4b", "display_name": "Gemma 4 E4B", "model_file_name": "gemma-4-E4B-it-Q4_K_M.gguf",
               "mmproj_file_name": "mmproj-E4B-BF16.gguf", "requires_gpu": true, "capabilities": ["Vision"] }]
            """);
        File.WriteAllText(Path.Combine(raiz, "model_catalog.json"), catalogo.ToJsonString());

        // El motor de chat es el de Tools\ del repositorio: el backend sale de sus .dll, y
        // la carpeta del banco no las tiene. Sin esto, la PC «no tiene placa».
        // Un settings.json de antes de E5: sin ExecutionProfileId, con los valores de «balanced»
        string settings = Path.Combine(raiz, "settings.json");
        File.WriteAllText(settings, $$"""
        {
          "model_path": "{{modelos.Replace("\\", "\\\\")}}",
          "selected_model": "gemma-4-E4B-it-Q4_K_M.gguf",
          "llama_server_executable": "{{Path.Combine(Repo, "Tools", "llama-server.exe").Replace("\\", "\\\\")}}",
          "ctx_size": 32768, "gpu_layers": 99, "threads": 4
        }
        """);

        var cfg = new ConfigManager(settings);
        var svc = new ModelProfileService(Path.Combine(raiz, "model_catalog.json"), modelos);
        var vm = new ModeSelectionViewModel(cfg, svc);

        Check("Automático es el primer botón", vm.AvailableExecutionProfiles.FirstOrDefault()?.DisplayName == "Automático");
        Check("los fijos siguen estando", vm.AvailableExecutionProfiles.Any(p => p.Profile.Id == "balanced"));
        Check("una configuración anterior cae en Automático", AutoExecutionProfile.IsAuto(vm.SelectedExecutionProfile?.Profile),
              vm.SelectedExecutionProfile?.Profile.Id ?? "ninguno");
        Check("con el Gemma 4 E4B elegido", vm.SelectedModelItem?.Model.ModelFileName == Path.GetFileName(gemma),
              vm.SelectedModelItem?.Model.ModelFileName ?? "ninguno");

        var s = cfg.Load();
        var hw = AutoExecutionProfile.Hardware(AutoExecutionProfile.ServerDirectory(s));
        Console.WriteLine($"  · {vm.SelectedModelContextInfo}");
        var rec = AutoExecutionProfile.Recommend(gemma, Path.Combine(modelos, "mmproj-E4B-BF16.gguf"), hw);
        var sinProyector = AutoExecutionProfile.Recommend(gemma, null, hw);
        Check("el proyector de visión se suma a la placa",
              rec != null && sinProyector != null && rec.GpuBytes - sinProyector.GpuBytes == new FileInfo(Path.Combine(modelos, "mmproj-E4B-BF16.gguf")).Length,
              $"{rec?.GpuBytes} contra {sinProyector?.GpuBytes}");
        Check("el texto lo dice", vm.SelectedModelContextInfo.StartsWith("automático: todo en la placa"), vm.SelectedModelContextInfo);
        Check("en esta máquina: todo a la placa", s.GpuLayers == RunRecommendation.AllLayers, s.GpuLayers.ToString());
        Check("con 32k", s.CtxSize == 32768, s.CtxSize.ToString());
        Check("y un hilo por núcleo físico", s.Threads == hw.PhysicalCores, $"{s.Threads} contra {hw.PhysicalCores}");
        Check("se guarda que es automático", s.ExecutionProfileId == AutoExecutionProfile.Id, s.ExecutionProfileId);
        Check("con caché normal: en 16 GB sobra", s.KvCacheType == "f16", s.KvCacheType);

        // Elegir uno fijo se respeta y se recuerda
        vm.SelectedExecutionProfile = vm.AvailableExecutionProfiles.First(p => p.Profile.Id == "performance");
        s = cfg.Load();
        Check("Rendimiento pone sus valores", s.GpuLayers == 0 && s.CtxSize == 8192 && s.KvCacheType == "f16",
              $"{s.GpuLayers}/{s.CtxSize}/{s.KvCacheType}");

        // Lo que llega a llama-server
        var q8 = new LlamaServerConfig { ServerExecutablePath = "llama-server.exe", ModelPath = "m.gguf", KvCacheType = "q8_0" }.ToCommandLineArgs();
        var f16 = new LlamaServerConfig { ServerExecutablePath = "llama-server.exe", ModelPath = "m.gguf" }.ToCommandLineArgs();
        Check("comprimida: llama-server recibe -ctk q8_0 -ctv q8_0", q8.Contains("-ctk q8_0 -ctv q8_0"), q8);
        Check("normal: no se pasa nada, queda la de llama.cpp", !f16.Contains("-ctk"), f16);
        var vm2 = new ModeSelectionViewModel(cfg, svc);
        Check("y al volver a abrir sigue elegido", vm2.SelectedExecutionProfile?.Profile.Id == "performance",
              vm2.SelectedExecutionProfile?.Profile.Id ?? "ninguno");
        Check("sin que el automático lo pise", cfg.Load().GpuLayers == 0);

        // Volver a Automático recalcula
        vm2.SelectedExecutionProfile = vm2.AvailableExecutionProfiles.First(p => AutoExecutionProfile.IsAuto(p.Profile));
        Check("volver a Automático recalcula", cfg.Load().GpuLayers == RunRecommendation.AllLayers && cfg.Load().CtxSize == 32768);
    }
}
