using System;
using System.IO;
using System.Linq;
using PRImager.AIAssistant.Services;
using PRImager.AIAssistant.Services.Models;
using PRImager.AIAssistant.ViewModels;

// Banco de prueba para 9.1 y 9.2: la pantalla de seleccion de modo debe ofrecer
// los modelos descargados y respetar el modelo guardado.
//
// Ejerce el codigo de produccion: instancia ModeSelectionViewModel contra el DLL
// real de la aplicacion. Lo que se verifica es el recorrido del usuario
// (elegir, guardar, reabrir), no las unidades por separado: esa es la leccion
// de la seccion 8 del documento maestro.

class Banco
{
    static int _ok, _fail;

    static void Check(string nombre, bool cond, string detalle = "")
    {
        if (cond) { _ok++; Console.WriteLine($"  OK   {nombre}"); }
        else { _fail++; Console.WriteLine($"  FALLA {nombre}   {detalle}"); }
    }

    // Cada caso corre en su propia carpeta: settings.json, catalogo y descargas.
    static string NuevoCaso(string nombre)
    {
        string raiz = Path.Combine(Path.GetTempPath(), "BancoModelos", nombre);
        if (Directory.Exists(raiz)) Directory.Delete(raiz, true);
        Directory.CreateDirectory(Path.Combine(raiz, "Models"));
        Directory.CreateDirectory(Path.Combine(raiz, "Descargas"));

        // Catalogo con los dos Gemma que se distribuyen.
        File.WriteAllText(Path.Combine(raiz, "model_catalog.json"), """
        {
          "version": 2,
          "models": [
            { "id": "e2b", "display_name": "Gemma 4 E2B", "model_file_name": "gemma-4-E2B-it-Q4_K_M.gguf",
              "mmproj_file_name": "mmproj-E2B-BF16.gguf", "requires_gpu": true, "hardware_tag": "GPU", "capabilities": ["Vision"] },
            { "id": "e4b", "display_name": "Gemma 4 E4B", "model_file_name": "gemma-4-E4B-it-Q4_K_M.gguf",
              "mmproj_file_name": "mmproj-E4B-BF16.gguf", "requires_gpu": true, "hardware_tag": "GPU", "capabilities": ["Vision"] }
          ],
          "execution_profiles": [
            { "id": "balanced", "display_name": "Equilibrado", "gpu_layers": 99, "ctx_size": 32768, "threads": 4 }
          ]
        }
        """);

        // Los .gguf del catalogo, presentes en disco para que cuenten como disponibles.
        File.WriteAllText(Path.Combine(raiz, "Models", "gemma-4-E2B-it-Q4_K_M.gguf"), "x");
        File.WriteAllText(Path.Combine(raiz, "Models", "gemma-4-E4B-it-Q4_K_M.gguf"), "x");
        return raiz;
    }

    static (ConfigManager, ModelProfileService) Servicios(string raiz)
        => (new ConfigManager(Path.Combine(raiz, "settings.json")),
            new ModelProfileService(Path.Combine(raiz, "model_catalog.json"), Path.Combine(raiz, "Models")));

    static void GuardarSettings(string raiz, AppSettings s)
        => new ConfigManager(Path.Combine(raiz, "settings.json")).Save(s);

    static AppSettings LeerSettings(string raiz)
        => new ConfigManager(Path.Combine(raiz, "settings.json")).Load();

    static AppSettings Base(string raiz) => new AppSettings
    {
        ModelPath = Path.Combine(raiz, "Models"),
        ModelDownloadPath = Path.Combine(raiz, "Descargas")
    };

    [STAThread]
    static int Main()
    {
        Console.WriteLine($"Banco de modelos — configuracion {Config()}\n");

        // Cada caso aislado: contra el arbol anterior al arreglo varios fallan, y
        // un caso que explota no debe ocultar el resultado de los que siguen.
        foreach (var caso in new Action[]
                 {
                     CasoDescubreDescargado, CasoRespetaGuardado, CasoNoPisaGuardado,
                     CasoPersisteRutaCompleta, CasoModeloDeCatalogoSinRegresion,
                     CasoExcluyeMmproj, CasoCarpetaInexistente, CasoParidadConChat
                 })
        {
            try { caso(); }
            catch (Exception ex)
            {
                _fail++;
                Console.WriteLine($"  FALLA {caso.Method.Name} lanzo {ex.GetType().Name}: {ex.Message}");
            }
        }

        Console.WriteLine($"\n{_ok} aserciones OK, {_fail} fallas");
        return _fail == 0 ? 0 : 1;
    }

    // Se deriva del binario que se esta ejerciendo, no de un simbolo del banco:
    // asi el encabezado no puede mentir sobre cual DLL se cargo.
    static string Config() => EscaneaDescargas() ? "con descargas (comercial)" : "Forense";

    // ¿El binario incluye la descarga de modelos? Se pregunta por reflexion en lugar
    // de por un #if para que el banco corra sin cambios contra cualquier arbol,
    // incluido el anterior al arreglo (control positivo), donde no existe
    // ModelDirectoryResolver.
    static bool EscaneaDescargas()
        => typeof(ConfigManager).Assembly.GetTypes()
               .Any(t => t.Name == "ModelCatalogViewModel");

    // Replica de la regla de resolucion. Sirve de referencia independiente: si la
    // aplicacion tiene el helper, se compara contra el mas abajo.
    static string CarpetaDescargas(AppSettings s)
    {
        if (!EscaneaDescargas()) return null;
        return string.IsNullOrWhiteSpace(s.ModelDownloadPath)
            ? (Path.IsPathRooted(s.ModelPath)
                ? s.ModelPath
                : Path.Combine(AppContext.BaseDirectory, s.ModelPath))
            : s.ModelDownloadPath;
    }

    // --- 9.2: el desplegable debe mostrar el modelo descargado ---
    static void CasoDescubreDescargado()
    {
        Console.WriteLine("9.2 — el desplegable ofrece los modelos descargados");
        string raiz = NuevoCaso("descubre");
        File.WriteAllText(Path.Combine(raiz, "Descargas", "Qwen3.5-9B-Q5_K_S.gguf"), "x");
        GuardarSettings(raiz, Base(raiz));

        var (cfg, svc) = Servicios(raiz);
        var vm = new ModeSelectionViewModel(cfg, svc);

        bool esperado = EscaneaDescargas();
        var qwen = vm.AvailableModelItems.FirstOrDefault(m => m.Model.ModelFileName == "Qwen3.5-9B-Q5_K_S.gguf");

        Check(esperado ? "el Qwen descargado aparece en el selector"
                       : "el Qwen descargado NO aparece (Forense)",
              (qwen != null) == esperado,
              esperado ? "no aparecio" : "aparecio y en Forense no debe");
        Check("los del catalogo siguen apareciendo",
              vm.AvailableModelItems.Count(m => m.Model.Id.StartsWith("e")) == 2);
    }

    // --- 9.1: el modelo guardado debe regir, no caer siempre en Gemma ---
    static void CasoRespetaGuardado()
    {
        Console.WriteLine("\n9.1 — el modelo guardado rige al abrir la pantalla");
        if (!EscaneaDescargas()) { Console.WriteLine("  (no aplica en Forense)"); return; }

        string raiz = NuevoCaso("respeta");
        string qwen = Path.Combine(raiz, "Descargas", "Qwen3.5-9B-Q5_K_S.gguf");
        File.WriteAllText(qwen, "x");

        var s = Base(raiz);
        s.SelectedModel = qwen;               // ruta absoluta, como la deja el chat
        GuardarSettings(raiz, s);

        var (cfg, svc) = Servicios(raiz);
        var vm = new ModeSelectionViewModel(cfg, svc);

        Check("SelectedModelItem es el Qwen, no el primer Gemma",
              vm.SelectedModelItem?.Model.ModelFileName == "Qwen3.5-9B-Q5_K_S.gguf",
              $"quedo en '{vm.SelectedModelItem?.Model.ModelFileName}'");
    }

    // --- el defecto de persistencia: la pantalla no debe pisar la eleccion ---
    static void CasoNoPisaGuardado()
    {
        Console.WriteLine("\nla pantalla no pisa el modelo guardado al abrirse");
        if (!EscaneaDescargas()) { Console.WriteLine("  (no aplica en Forense)"); return; }

        string raiz = NuevoCaso("nopisa");
        string qwen = Path.Combine(raiz, "Descargas", "Qwen3.5-9B-Q5_K_S.gguf");
        File.WriteAllText(qwen, "x");

        var s = Base(raiz);
        s.SelectedModel = qwen;
        GuardarSettings(raiz, s);

        var (cfg, svc) = Servicios(raiz);
        _ = new ModeSelectionViewModel(cfg, svc);

        Check("settings.json sigue con la ruta del Qwen tras abrir la pantalla",
              LeerSettings(raiz).SelectedModel == qwen,
              $"quedo '{LeerSettings(raiz).SelectedModel}'");
    }

    // --- elegir un descargado debe persistir la ruta completa ---
    static void CasoPersisteRutaCompleta()
    {
        Console.WriteLine("\nelegir un descargado persiste la ruta completa");
        if (!EscaneaDescargas()) { Console.WriteLine("  (no aplica en Forense)"); return; }

        string raiz = NuevoCaso("persiste");
        string qwen = Path.Combine(raiz, "Descargas", "Qwen3.5-9B-Q5_K_S.gguf");
        File.WriteAllText(qwen, "x");
        GuardarSettings(raiz, Base(raiz));

        var (cfg, svc) = Servicios(raiz);
        var vm = new ModeSelectionViewModel(cfg, svc);
        vm.SelectedModelItem = vm.AvailableModelItems.First(m => m.Model.ModelFileName == "Qwen3.5-9B-Q5_K_S.gguf");

        Check("se guardo la ruta absoluta, no el nombre suelto",
              LeerSettings(raiz).SelectedModel == qwen,
              $"quedo '{LeerSettings(raiz).SelectedModel}'");

        // Y al reabrir, se reencuentra: es el ciclo completo del usuario.
        var (cfg2, svc2) = Servicios(raiz);
        var vm2 = new ModeSelectionViewModel(cfg2, svc2);
        Check("al reabrir la pantalla sigue seleccionado el Qwen",
              vm2.SelectedModelItem?.Model.ModelFileName == "Qwen3.5-9B-Q5_K_S.gguf",
              $"quedo '{vm2.SelectedModelItem?.Model.ModelFileName}'");
    }

    // --- regresion: un modelo del catalogo se sigue guardando por nombre ---
    static void CasoModeloDeCatalogoSinRegresion()
    {
        Console.WriteLine("\nregresion: los modelos del catalogo se guardan por nombre");
        string raiz = NuevoCaso("catalogo");
        GuardarSettings(raiz, Base(raiz));

        var (cfg, svc) = Servicios(raiz);
        var vm = new ModeSelectionViewModel(cfg, svc);
        vm.SelectedModelItem = vm.AvailableModelItems.First(m => m.Model.Id == "e4b");

        Check("se guardo el nombre de archivo, sin ruta",
              LeerSettings(raiz).SelectedModel == "gemma-4-E4B-it-Q4_K_M.gguf",
              $"quedo '{LeerSettings(raiz).SelectedModel}'");
        Check("se guardo tambien el mmproj",
              LeerSettings(raiz).MmprojFileName == "mmproj-E4B-BF16.gguf");

        var (cfg2, svc2) = Servicios(raiz);
        var vm2 = new ModeSelectionViewModel(cfg2, svc2);
        Check("al reabrir sigue seleccionado el E4B",
              vm2.SelectedModelItem?.Model.Id == "e4b",
              $"quedo '{vm2.SelectedModelItem?.Model.Id}'");
    }

    // --- un mmproj no es un modelo cargable ---
    static void CasoExcluyeMmproj()
    {
        Console.WriteLine("\nlos proyectores multimodales no se ofrecen como modelo");
        string raiz = NuevoCaso("mmproj");
        File.WriteAllText(Path.Combine(raiz, "Descargas", "mmproj-Qwen-BF16.gguf"), "x");
        GuardarSettings(raiz, Base(raiz));

        var (cfg, svc) = Servicios(raiz);
        var vm = new ModeSelectionViewModel(cfg, svc);

        Check("el mmproj descargado no aparece en el selector",
              !vm.AvailableModelItems.Any(m => m.Model.ModelFileName.Contains("mmproj")
                                            && m.Model.IsDiscovered));
    }

    // --- una carpeta de descargas ausente no debe dejar sin modelos ---
    static void CasoCarpetaInexistente()
    {
        Console.WriteLine("\nuna carpeta de descargas ausente no rompe el selector");
        string raiz = NuevoCaso("sincarpeta");
        Directory.Delete(Path.Combine(raiz, "Descargas"));
        GuardarSettings(raiz, Base(raiz));

        var (cfg, svc) = Servicios(raiz);
        var vm = new ModeSelectionViewModel(cfg, svc);

        Check("siguen los dos modelos del catalogo", vm.AvailableModelItems.Count == 2);
        Check("hay uno seleccionado", vm.SelectedModelItem != null);
    }

    // --- la razon de ser del arreglo: las dos pantallas deben coincidir ---
    static void CasoParidadConChat()
    {
        Console.WriteLine("\nparidad: la pantalla de modo ofrece lo mismo que el chat");
        string raiz = NuevoCaso("paridad");
        File.WriteAllText(Path.Combine(raiz, "Descargas", "Qwen3.5-9B-Q5_K_S.gguf"), "x");
        File.WriteAllText(Path.Combine(raiz, "Descargas", "Phi-4-Q4.gguf"), "x");
        GuardarSettings(raiz, Base(raiz));

        var (cfg, svc) = Servicios(raiz);
        var vm = new ModeSelectionViewModel(cfg, svc);

        // El chat pide los modelos al servicio pasandole la carpeta de descargas;
        // eso es exactamente lo que hace MainViewModel.LoadProfiles.
        var delChat = svc.GetAvailableModels(CarpetaDescargas(LeerSettings(raiz)))
                         .Select(m => m.Model.SelectionKey).OrderBy(k => k).ToList();
        var deLaPantalla = vm.AvailableModelItems.Select(m => m.Model.SelectionKey).OrderBy(k => k).ToList();

        Check("ambos selectores ofrecen el mismo conjunto",
              delChat.SequenceEqual(deLaPantalla),
              $"chat={delChat.Count} pantalla={deLaPantalla.Count}");
    }
}
