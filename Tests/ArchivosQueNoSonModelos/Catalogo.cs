#if CATALOGO
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PRImager.AIAssistant.Services;
using PRImager.AIAssistant.Services.HuggingFace;
using PRImager.AIAssistant.ViewModels.HuggingFace;

partial class Banco
{
    const string Repo = "banco/Modelo-GGUF";

    // --- el catálogo, con el ViewModel real contra un HuggingFace de mentira ---
    //
    // Lo de mentira es solo el servidor, y responde lo que se midió del verdadero: el
    // detalle del repo, el árbol con tamaños, y Range sobre /resolve/. La lectura de los
    // archivos reales está en CasoHuggingFaceReal, con --con-red.

    static Dictionary<string, byte[]> ArchivosDePrueba() => new()
    {
        ["Modelo-Q4_K_M.gguf"] = Gguf.Modelo(),
        ["Modelo-Q5_K_M.gguf"] = Gguf.Modelo(),
        // Nombre de modelo, cabecera de borrador: solo la verificación previa lo frena
        ["Modelo-Q8_0.gguf"] = Gguf.Borrador(),
        ["Modelo-draft-Q8_0.gguf"] = Gguf.Borrador(),
        ["mmproj-Modelo-F16.gguf"] = Gguf.Proyector(),
        ["Modelo-Q2_K-00001-of-00002.gguf"] = Gguf.Modelo()
    };

    static void CasoCatalogo()
    {
        Console.WriteLine("\nel catálogo marca y no baja lo que no carga");

        var archivos = ArchivosDePrueba();
        var servidor = new HuggingFaceFalso(archivos);
        string raiz = NuevaCarpeta("catalogo");
        string destino = Path.Combine(raiz, "Descargas");
        var descargados = new List<string>();

        using var vm = new ModelCatalogViewModel(
            destino, 8192, Path.Combine(raiz, "Logs"),
            onModelDownloaded: p => descargados.Add(Path.GetFileName(p)),
            client: new HuggingFaceClient(new HttpClient(servidor)),
            downloader: new ModelDownloader(new HttpClient(servidor)));

        vm.SelectedRepo = new HfRepo { Id = Repo };
        Esperar(() => !vm.IsBusy && vm.Quantizations.Count == archivos.Count);

        QuantizationItem Fila(string f) => vm.Quantizations.Single(q => q.FileName == f);

        Check("la tabla lista los seis", vm.Quantizations.Count == 6, vm.Quantizations.Count.ToString());
        Check("el estado dice cuántos no cargan solos", vm.Status.Contains("3 archivos que no cargan solos"), vm.Status);
        Check("la cabeza con «draft» se marca", Fila("Modelo-draft-Q8_0.gguf").VramLabel == "No es un modelo");
        Check("y no se puede bajar", !Fila("Modelo-draft-Q8_0.gguf").CanDownload);
        Check("la parte se marca", Fila("Modelo-Q2_K-00001-of-00002.gguf").VramLabel == "Parte de un modelo");
        Check("el proyector se marca pero se puede bajar",
              Fila("mmproj-Modelo-F16.gguf").VramLabel == "Proyector de visión" && Fila("mmproj-Modelo-F16.gguf").CanDownload);
        Check("el modelo lleva semáforo", Fila("Modelo-Q4_K_M.gguf").VramLabel != "No es un modelo");

        vm.SelectedQuantization = Fila("Modelo-draft-Q8_0.gguf");
        Check("el botón de bajar se apaga", !vm.DownloadCommand.CanExecute(null));
        Check("la explicación dice qué es", vm.SelectedVramExplanation.Contains("cabeza de borrador"),
              vm.SelectedVramExplanation);

        // El disfrazado: el nombre no lo delata, la cabecera sí
        vm.SelectedQuantization = Fila("Modelo-Q8_0.gguf");
        Check("el disfrazado parece bajable", vm.DownloadCommand.CanExecute(null));
        int pedidosAntes = servidor.PedidosCompletos;
        Descargar(vm);
        Check("se pidió solo la cabecera, con Range", servidor.PedidosConRange.Contains("Modelo-Q8_0.gguf"));
        Check("no se bajó el archivo entero", servidor.PedidosCompletos == pedidosAntes);
        Check("no quedó en disco", !File.Exists(Path.Combine(destino, "Modelo-Q8_0.gguf")));
        Check("el error dice por qué", vm.IsError && vm.Status.Contains("65 capas"), vm.Status);

        // Y lo que sí es un modelo se baja como siempre
        vm.SelectedQuantization = Fila("Modelo-Q4_K_M.gguf");
        Descargar(vm);
        Check("el modelo de verdad se baja", File.Exists(Path.Combine(destino, "Modelo-Q4_K_M.gguf")), vm.Status);
        Check("y se avisa al selector", descargados.Contains("Modelo-Q4_K_M.gguf"));

        // El proyector se baja sin mirar la cabecera: ya se sabe qué es
        int rangeAntes = servidor.PedidosConRange.Count;
        vm.SelectedQuantization = Fila("mmproj-Modelo-F16.gguf");
        Descargar(vm);
        Check("el proyector se baja", File.Exists(Path.Combine(destino, "mmproj-Modelo-F16.gguf")), vm.Status);
        Check("sin pedir la cabecera", servidor.PedidosConRange.Count == rangeAntes);

        // Si la cabecera no se puede leer, no se bloquea: ante la duda se baja
        servidor.FallarRange = true;
        vm.SelectedQuantization = Fila("Modelo-Q5_K_M.gguf");
        Descargar(vm);
        Check("sin cabecera legible, el modelo se baja igual", File.Exists(Path.Combine(destino, "Modelo-Q5_K_M.gguf")), vm.Status);
    }

    // --- archivos reales ---

    static void CasoHuggingFaceReal()
    {
        Console.WriteLine("\nlas cabeceras de archivos reales de HuggingFace");
        using var client = new HuggingFaceClient();

        (string repo, string archivo, GgufKind esperado)[] tabla =
        {
            // El de 4.22, y el modelo completo del mismo repositorio
            ("JonathanColetti/Qwen3.8-27B-Uncensored-GGUF", "Qwen3.8-27B-Uncensored-draft-Q8_0.gguf", GgufKind.DraftHead),
            ("JonathanColetti/Qwen3.8-27B-Uncensored-GGUF", "Qwen3.8-27B-Uncensored-Q4_K_M.gguf", GgufKind.Model),
            ("ggml-org/gemma-3-4b-it-GGUF", "mmproj-model-f16.gguf", GgufKind.Projector),
            ("ggml-org/gemma-3-4b-it-GGUF", "gemma-3-4b-it-Q4_K_M.gguf", GgufKind.Model),
            ("unsloth/Qwen3-235B-A22B-Instruct-2507-GGUF",
             "Q2_K_L/Qwen3-235B-A22B-Instruct-2507-Q2_K_L-00002-of-00002.gguf", GgufKind.SplitPart)
        };

        foreach (var (repo, archivo, esperado) in tabla)
        {
            var r = client.ProbeGgufAsync(repo, archivo).GetAwaiter().GetResult();
            if (!r.Success)
            {
                Check($"{archivo}: se leyó la cabecera", false, r.ErrorMessage ?? "");
                continue;
            }
            var v = GgufInspector.FromHeader(r.Value!);
            Check($"{archivo} → {esperado}", v.Kind == esperado,
                  $"{v.Kind}: {r.Value!.TensorCount} tensores, block_count {r.Value.BlockCount}");
        }
    }

    // --- utilidades ---

    static void Esperar(Func<bool> condicion, int segundos = 20)
    {
        var limite = DateTime.UtcNow.AddSeconds(segundos);
        while (!condicion())
        {
            if (DateTime.UtcNow > limite) throw new TimeoutException("la condición no se cumplió a tiempo");
            Thread.Sleep(50);
        }
    }

    // El comando es async void, como todo ICommand: para esperar el final se invoca el
    // método que envuelve. El nombre se busca por reflexión y, si cambia, el banco lo dice.
    static void Descargar(ModelCatalogViewModel vm)
    {
        var m = typeof(ModelCatalogViewModel).GetMethod("ExecuteDownloadAsync",
                    BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new MissingMethodException("ModelCatalogViewModel.ExecuteDownloadAsync");
        ((Task)m.Invoke(vm, null)!).GetAwaiter().GetResult();
    }

    sealed class HuggingFaceFalso : HttpMessageHandler
    {
        private readonly Dictionary<string, byte[]> _archivos;
        public readonly List<string> PedidosConRange = new();
        public int PedidosCompletos;
        public bool FallarRange;

        public HuggingFaceFalso(Dictionary<string, byte[]> archivos) => _archivos = archivos;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            string path = Uri.UnescapeDataString(req.RequestUri!.AbsolutePath);

            if (path == $"/api/models/{Repo}")
            {
                string siblings = string.Join(",", _archivos.Keys.Select(f => $"{{\"rfilename\":\"{f}\"}}"));
                return Json($"{{\"id\":\"{Repo}\",\"siblings\":[{siblings}]}}");
            }

            if (path == $"/api/models/{Repo}/tree/main")
            {
                string tree = string.Join(",", _archivos.Select(kv =>
                    $"{{\"path\":\"{kv.Key}\",\"lfs\":{{\"size\":{kv.Value.Length}}}}}"));
                return Json($"[{tree}]");
            }

            string prefijo = $"/{Repo}/resolve/main/";
            if (path.StartsWith(prefijo) && _archivos.TryGetValue(path.Substring(prefijo.Length), out var bytes))
            {
                string nombre = path.Substring(prefijo.Length);
                if (req.Headers.Range is { } range)
                {
                    PedidosConRange.Add(nombre);
                    if (FallarRange) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
                    var r = range.Ranges.First();
                    int hasta = (int)Math.Min(bytes.Length - 1, r.To ?? bytes.Length - 1);
                    var parte = new ByteArrayContent(bytes, 0, hasta + 1);
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = parte });
                }
                PedidosCompletos++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        static Task<HttpResponseMessage> Json(string s) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(s, Encoding.UTF8, "application/json")
            });
    }
}
#endif
