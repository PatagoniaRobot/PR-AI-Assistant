using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PRImager.AIAssistant.Services.HuggingFace;
using PRImager.AIAssistant.ViewModels.HuggingFace;

// Banco de prueba para 9.4: eliminar modelos descargados.
//
// Ejerce DownloadedModelStore y ModelCatalogViewModel contra el DLL real. Lo que
// importa verificar no es que borre, sino que NO borre lo que no debe: el modelo
// activo, los que vienen con el producto, y nada fuera de la carpeta de descargas.

class Banco
{
    static int _ok, _fail;

    static void Check(string nombre, bool cond, string detalle = "")
    {
        if (cond) { _ok++; Console.WriteLine($"  OK   {nombre}"); }
        else { _fail++; Console.WriteLine($"  FALLA {nombre}   {detalle}"); }
    }

    const string GemmaCatalogo = "gemma-4-E2B-it-Q4_K_M.gguf";
    const string MmprojCatalogo = "mmproj-E2B-BF16.gguf";

    static string _activo = "";

    // Carpeta de descargas nueva por caso, mas la carpeta de logs.
    static (string desc, string logs) NuevoCaso(string nombre)
    {
        string raiz = Path.Combine(Path.GetTempPath(), "BancoBorrado", nombre);
        if (Directory.Exists(raiz)) Directory.Delete(raiz, true);
        string desc = Path.Combine(raiz, "Descargas");
        string logs = Path.Combine(raiz, "Logs");
        Directory.CreateDirectory(desc);
        Directory.CreateDirectory(logs);
        _activo = "";
        return (desc, logs);
    }

    // Archivo de tamaño creible sin gastar disco: se escriben unos pocos KB.
    static string Archivo(string carpeta, string nombre, int kb = 8)
    {
        string p = Path.Combine(carpeta, nombre);
        File.WriteAllBytes(p, new byte[kb * 1024]);
        return p;
    }

    static DownloadedModelStore Store(string desc) => new(
        downloadDirectory: desc,
        catalogFileNames: new[] { GemmaCatalogo, MmprojCatalogo },
        activeSelectionKey: () => _activo);

    [STAThread]
    static int Main()
    {
        Console.WriteLine("Banco de borrado de modelos descargados\n");

        foreach (var caso in new Action[]
                 {
                     CasoListaSoloDescargados, CasoNoBorraElActivo, CasoNoBorraDelCatalogo,
                     CasoNoBorraFueraDeLaCarpeta, CasoBorraYLibera, CasoBorraParcialAsociado,
                     CasoMmprojHuerfano, CasoMmprojAcompanado, CasoRegistraLaBaja,
                     CasoLimpiaParciales, CasoCarpetaInexistente, CasoSinCarpeta,
                     CasoViewModelConfirmacion, CasoViewModelRefresca
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

        // La UI se ejerce al final: monta una Application y no conviene mezclarla
        // con los casos de disco.
        int fallasHumo = Humo.Correr();

        return _fail == 0 && fallasHumo == 0 ? 0 : 1;
    }

    // --- Lo que se ofrece para borrar ---

    static void CasoListaSoloDescargados()
    {
        Console.WriteLine("la lista excluye los modelos que vienen con el producto");
        var (desc, _) = NuevoCaso("lista");
        Archivo(desc, "Qwen3.5-9B-Q5_K_S.gguf");
        Archivo(desc, GemmaCatalogo);        // distribuido, comparte carpeta
        Archivo(desc, MmprojCatalogo);       // proyector del distribuido
        Archivo(desc, "mmproj-Qwen-BF16.gguf");
        Archivo(desc, "notas.txt");

        var lista = Store(desc).List();

        Check("aparece el Qwen descargado", lista.Any(m => m.FileName.StartsWith("Qwen")));
        Check("no aparece el Gemma distribuido", !lista.Any(m => m.FileName == GemmaCatalogo));
        Check("no aparece ningun mmproj", !lista.Any(m => m.FileName.Contains("mmproj")));
        Check("no aparecen archivos que no son .gguf", !lista.Any(m => m.FileName.EndsWith(".txt")));
        Check("solo queda un candidato", lista.Count == 1, $"quedaron {lista.Count}");
    }

    // --- Las tres barreras ---

    static void CasoNoBorraElActivo()
    {
        Console.WriteLine("\nel modelo activo no se puede eliminar");
        var (desc, _) = NuevoCaso("activo");
        string qwen = Archivo(desc, "Qwen3.5-9B-Q5_K_S.gguf");
        _activo = qwen;

        var store = Store(desc);
        var item = store.List().Single();

        Check("la UI lo marca en uso", item.IsActive);
        Check("la UI no habilita el borrado", !item.CanDelete);
        Check("el motivo se explica", item.BlockedReason.Contains("modelo activo"));

        var r = store.Delete(item);
        Check("el servicio tambien lo rechaza", !r.Success);
        Check("el archivo sigue en disco", File.Exists(qwen));

        // Un settings.json viejo puede traer solo el nombre, sin la ruta
        _activo = "Qwen3.5-9B-Q5_K_S.gguf";
        Check("se reconoce activo tambien por nombre suelto", Store(desc).List().Single().IsActive);
    }

    static void CasoNoBorraDelCatalogo()
    {
        Console.WriteLine("\nlos modelos del catalogo distribuido no se eliminan");
        var (desc, _) = NuevoCaso("catalogo");
        string gemma = Archivo(desc, GemmaCatalogo);
        Archivo(desc, "Qwen3.5-9B-Q5_K_S.gguf");

        // Se fuerza el borrado del distribuido salteando la lista, que ya lo excluye:
        // la barrera tiene que estar tambien en el servicio.
        var forzado = new DownloadedModelInfo
        {
            FileName = GemmaCatalogo,
            FullPath = gemma,
            SizeBytes = 8192
        };

        var r = Store(desc).Delete(forzado);
        Check("el servicio lo rechaza", !r.Success);
        Check("el mensaje nombra al catalogo distribuido",
              r.ErrorMessage != null && r.ErrorMessage.Contains("catálogo distribuido"),
              r.ErrorMessage ?? "");
        Check("el Gemma sigue en disco", File.Exists(gemma));
    }

    static void CasoNoBorraFueraDeLaCarpeta()
    {
        Console.WriteLine("\nno se borra nada fuera de la carpeta de descargas");
        var (desc, _) = NuevoCaso("afuera");
        string afuera = Path.Combine(Path.GetDirectoryName(desc), "ajeno.gguf");
        File.WriteAllBytes(afuera, new byte[1024]);

        var forzado = new DownloadedModelInfo
        {
            FileName = "ajeno.gguf",
            FullPath = afuera,
            SizeBytes = 1024
        };

        var r = Store(desc).Delete(forzado);
        Check("el servicio lo rechaza", !r.Success);
        Check("el archivo ajeno sigue en disco", File.Exists(afuera));
    }

    // --- El camino feliz ---

    static void CasoBorraYLibera()
    {
        Console.WriteLine("\nel modelo se borra y se informa el espacio liberado");
        var (desc, _) = NuevoCaso("borra");
        string qwen = Archivo(desc, "Qwen3.5-9B-Q5_K_S.gguf", kb: 64);

        var store = Store(desc);
        var r = store.Delete(store.List().Single());

        Check("la operacion informa exito", r.Success, r.ErrorMessage ?? "");
        Check("el archivo ya no esta", !File.Exists(qwen));
        Check("el tamaño informado es el real", r.Value != null && r.Value.SizeBytes == 64 * 1024,
              $"informo {r.Value?.SizeBytes}");
        Check("la lista queda vacia", store.List().Count == 0);
    }

    static void CasoBorraParcialAsociado()
    {
        Console.WriteLine("\nse borra el .part de una descarga interrumpida del mismo modelo");
        var (desc, _) = NuevoCaso("parcial");
        string qwen = Archivo(desc, "Qwen3.5-9B-Q5_K_S.gguf");
        string part = qwen + ".part";
        File.WriteAllBytes(part, new byte[2048]);

        var store = Store(desc);
        var item = store.List().Single();
        Check("la lista detecta el parcial", item.PartialPath != null);

        var r = store.Delete(item);
        Check("el .part se borro", !File.Exists(part));
        Check("queda asentado como accesorio",
              r.Value != null && r.Value.AlsoDeleted.Any(a => a.EndsWith(".part")));
    }

    static void CasoMmprojHuerfano()
    {
        Console.WriteLine("\nel proyector se borra cuando queda huerfano");
        var (desc, _) = NuevoCaso("huerfano");
        Archivo(desc, "Qwen3.5-9B-Q5_K_S.gguf");
        string mmproj = Archivo(desc, "mmproj-Qwen-BF16.gguf");

        var store = Store(desc);
        var r = store.Delete(store.List().Single());

        Check("el mmproj huerfano se borro", !File.Exists(mmproj));
        Check("queda asentado como accesorio",
              r.Value != null && r.Value.AlsoDeleted.Any(a => a.Contains("mmproj")));
    }

    static void CasoMmprojAcompanado()
    {
        Console.WriteLine("\nel proyector NO se borra si queda otro modelo que podria usarlo");
        var (desc, _) = NuevoCaso("acompanado");
        Archivo(desc, "Qwen3.5-9B-Q5_K_S.gguf");
        Archivo(desc, "Phi-4-Q4_K_M.gguf");
        string mmproj = Archivo(desc, "mmproj-Qwen-BF16.gguf");

        var store = Store(desc);
        var qwen = store.List().First(m => m.FileName.StartsWith("Qwen"));
        var r = store.Delete(qwen);

        Check("el borrado del Qwen funciono", r.Success, r.ErrorMessage ?? "");
        Check("el mmproj sigue en disco", File.Exists(mmproj));
        Check("no se lo declara borrado",
              r.Value != null && !r.Value.AlsoDeleted.Any(a => a.Contains("mmproj")));
    }

    // --- Trazabilidad ---

    static void CasoRegistraLaBaja()
    {
        Console.WriteLine("\nla baja queda registrada en model-downloads.jsonl");
        var (desc, logs) = NuevoCaso("registro");
        Archivo(desc, "Qwen3.5-9B-Q5_K_S.gguf", kb: 32);

        var log = new ModelDownloadLog(logs);

        // Un alta previa, para verificar que ambas clases de linea conviven
        log.Record(new ModelDownloadRecord
        {
            RepoId = "Qwen/Qwen3.5-9B-GGUF",
            FileName = "Qwen3.5-9B-Q5_K_S.gguf",
            SizeBytes = 32 * 1024,
            Sha256 = "abc",
            CompletedUtc = DateTime.UtcNow,
            DestinationPath = Path.Combine(desc, "Qwen3.5-9B-Q5_K_S.gguf")
        });

        var store = Store(desc);
        var r = store.Delete(store.List().Single());
        log.RecordDeletion(r.Value);

        var lineas = File.ReadAllLines(log.FilePath);
        Check("hay dos lineas", lineas.Length == 2, $"hay {lineas.Length}");
        Check("el alta se marca como download", lineas[0].Contains("\"event\":\"download\""), lineas[0]);
        Check("la baja se marca como deletion", lineas[1].Contains("\"event\":\"deletion\""), lineas[1]);
        Check("la baja trae el nombre del archivo", lineas[1].Contains("Qwen3.5-9B-Q5_K_S.gguf"));
        Check("la baja trae el tamaño liberado", lineas[1].Contains("32768"), lineas[1]);
    }

    static void CasoLimpiaParciales()
    {
        Console.WriteLine("\nse limpian los .part sueltos de descargas interrumpidas");
        var (desc, _) = NuevoCaso("limpia");
        File.WriteAllBytes(Path.Combine(desc, "Algo-Q4.gguf.part"), new byte[4096]);
        File.WriteAllBytes(Path.Combine(desc, "Otro-Q8.gguf.part"), new byte[2048]);
        string bueno = Archivo(desc, "Qwen3.5-9B-Q5_K_S.gguf");

        var (deleted, freed) = Store(desc).CleanPartials();

        Check("se borraron los dos parciales", deleted.Count == 2, $"borro {deleted.Count}");
        Check("se informan los bytes liberados", freed == 6144, $"informo {freed}");
        Check("el modelo bueno sigue intacto", File.Exists(bueno));
    }

    // --- Bordes ---

    static void CasoCarpetaInexistente()
    {
        Console.WriteLine("\nuna carpeta de descargas ausente no rompe");
        var (desc, _) = NuevoCaso("ausente");
        Directory.Delete(desc, true);

        var store = Store(desc);
        Check("la lista sale vacia", store.List().Count == 0);
        Check("limpiar parciales no rompe", store.CleanPartials().Deleted.Count == 0);
    }

    static void CasoSinCarpeta()
    {
        Console.WriteLine("\nsin carpeta de descargas no se gestiona nada");
        var store = new DownloadedModelStore(null, new List<string>(), () => "");

        Check("la lista sale vacia", store.List().Count == 0);
        var r = store.Delete(new DownloadedModelInfo { FileName = "x.gguf", FullPath = @"C:\x.gguf" });
        Check("el borrado se rechaza", !r.Success);
    }

    // --- El ViewModel ---

    static ModelCatalogViewModel NuevoVm(string desc, string logs, Func<DownloadedModelInfo, bool> confirmar,
                                         Action<ModelDeletionRecord> alBorrar = null)
        => new ModelCatalogViewModel(
            destinationDirectory: desc,
            contextTokens: 32768,
            logDirectory: logs,
            store: Store(desc),
            onModelDeleted: alBorrar,
            confirmDeletion: confirmar);

    static void CasoViewModelConfirmacion()
    {
        Console.WriteLine("\nsi el usuario no confirma, no se borra nada");
        var (desc, logs) = NuevoCaso("confirma");
        string qwen = Archivo(desc, "Qwen3.5-9B-Q5_K_S.gguf");

        bool preguntado = false;
        var vm = NuevoVm(desc, logs, m => { preguntado = true; return false; });
        vm.SelectedDownloaded = vm.DownloadedModels.Single();
        vm.DeleteDownloadedCommand.Execute(null);

        Check("se le pregunto al usuario", preguntado);
        Check("el archivo sigue en disco", File.Exists(qwen));
        Check("la lista lo sigue mostrando", vm.DownloadedModels.Count == 1);
        vm.Dispose();
    }

    static void CasoViewModelRefresca()
    {
        Console.WriteLine("\nal confirmar se borra, se refresca la lista y se avisa al chat");
        var (desc, logs) = NuevoCaso("refresca");
        string qwen = Archivo(desc, "Qwen3.5-9B-Q5_K_S.gguf", kb: 16);

        ModelDeletionRecord avisado = null;
        var vm = NuevoVm(desc, logs, _ => true, r => avisado = r);

        Check("el resumen informa lo que hay en disco",
              vm.DownloadedSummary.Contains("1 modelo"), vm.DownloadedSummary);

        vm.SelectedDownloaded = vm.DownloadedModels.Single();
        Check("el comando esta habilitado", vm.DeleteDownloadedCommand.CanExecute(null));

        vm.DeleteDownloadedCommand.Execute(null);

        Check("el archivo se borro", !File.Exists(qwen));
        Check("la lista quedo vacia", vm.DownloadedModels.Count == 0);
        Check("el resumen lo refleja", vm.DownloadedSummary.Contains("No hay modelos"), vm.DownloadedSummary);
        Check("se le aviso al chat para que refresque el selector", avisado != null);
        Check("la baja quedo en el jsonl",
              File.Exists(Path.Combine(logs, "model-downloads.jsonl")) &&
              File.ReadAllText(Path.Combine(logs, "model-downloads.jsonl")).Contains("deletion"));

        // Y el modelo activo tampoco se puede borrar desde la UI
        var (desc2, logs2) = NuevoCaso("refresca2");
        _activo = Archivo(desc2, "Qwen3.5-9B-Q5_K_S.gguf");
        var vm2 = NuevoVm(desc2, logs2, _ => true);
        vm2.SelectedDownloaded = vm2.DownloadedModels.Single();
        Check("el comando esta deshabilitado para el modelo activo",
              !vm2.DeleteDownloadedCommand.CanExecute(null));
        Check("la UI explica por que", vm2.SelectedDownloadedHint.Contains("modelo activo"),
              vm2.SelectedDownloadedHint);

        vm.Dispose();
        vm2.Dispose();
    }
}
