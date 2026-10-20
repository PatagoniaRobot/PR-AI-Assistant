using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using PRImager.AIAssistant.Services;
using PRImager.AIAssistant.Services.Models;
using PRImager.AIAssistant.ViewModels;

// Banco del pendiente 6.5: el catálogo y el selector no deben ofrecer como modelo un
// .gguf que no carga solo —proyector, cabeza de borrador, adaptador, vocabulario,
// parte de un modelo partido—.
//
// El caso que lo abrió es el de 4.22 del maestro: una cabeza de borrador de 3 GB que
// el catálogo bajó y el selector listó como un modelo más.
//
// Las cabeceras se arman acá byte a byte con el formato GGUF, y con --con-red además
// se leen las de archivos reales de HuggingFace, incluida la de 4.22.

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
        bool conRed = args.Contains("--con-red");
        Console.WriteLine("Banco de archivos que no son modelos" +
#if CATALOGO
                          " — edición con catálogo" +
#else
                          " — edición Forense" +
#endif
                          (conRed ? ", con red" : "") + "\n");

        var casos = new List<Action>
        {
            CasoCabeceras, CasoCabeceraCortada, CasoNoEsGguf, CasoNombres, CasoSelector, CasoArchivoLocal
        };
#if CATALOGO
        casos.Add(CasoCatalogo);
        if (conRed) casos.Add(CasoHuggingFaceReal);
        casos.Add(CasoVentana);
#endif

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

    // --- cabeceras armadas a mano ---

    static void CasoCabeceras()
    {
        Console.WriteLine("la cabecera dice qué es el archivo");

        var modelo = GgufInspector.FromHeader(Leer(Gguf.Modelo()));
        Check("un modelo completo es modelo", modelo.IsModel, modelo.Reason);

        // Los números de 4.22: 65 capas declaradas, 18 tensores
        var h = Leer(Gguf.Borrador());
        var borrador = GgufInspector.FromHeader(h);
        Check("la cabeza de borrador no es modelo", borrador.Kind == GgufKind.DraftHead, borrador.Kind.ToString());
        Check("la razón da los números", borrador.Reason.Contains("65 capas") && borrador.Reason.Contains("18 tensores"),
              borrador.Reason);
        Check("se leyó la arquitectura", h.Architecture == "qwen35", h.Architecture);
        Check("se leyó block_count", h.BlockCount == 65, h.BlockCount?.ToString());

        Check("un proyector clip es proyector",
              GgufInspector.FromHeader(Leer(Gguf.Proyector())).Kind == GgufKind.Projector);
        Check("general.type=adapter es adaptador",
              GgufInspector.FromHeader(Leer(Gguf.Adaptador())).Kind == GgufKind.Adapter);
        Check("cero tensores es solo vocabulario",
              GgufInspector.FromHeader(Leer(Gguf.Vocabulario())).Kind == GgufKind.VocabOnly);

        var parte = GgufInspector.FromHeader(Leer(Gguf.Parte()));
        Check("split.count > 1 es parte", parte.Kind == GgufKind.SplitPart, parte.Kind.ToString());
        Check("la parte se numera desde 1", parte.Reason.Contains("parte 2 de 3"), parte.Reason);

        // Hay conversores que escriben block_count en u64 y otros en u32
        var u64 = Leer(Gguf.Modelo(blockCountU64: true));
        Check("block_count en u64 también se lee", u64.BlockCount == 32, u64.BlockCount?.ToString());

        // El block_count del proyector de visión no es el del modelo
        var multimodal = Leer(Gguf.ModeloConVisionAntes());
        Check("clip.vision.block_count no se toma como el del modelo", multimodal.BlockCount == 32,
              multimodal.BlockCount?.ToString());
    }

    static void CasoCabeceraCortada()
    {
        Console.WriteLine("\nuna cabecera cortada a mitad devuelve lo que alcanzó");
        byte[] completo = Gguf.Modelo(vocabulario: 50_000);
        // Se corta en medio del vocabulario, que va al final: lo importante ya pasó
        var h = GgufInspector.TryParse(completo.AsSpan(0, completo.Length - 1000));
        Check("se lee igual", h != null);
        Check("con block_count", h?.BlockCount == 32, h?.BlockCount?.ToString());
        Check("y sigue siendo modelo", h != null && GgufInspector.FromHeader(h).IsModel);
    }

    static void CasoNoEsGguf()
    {
        Console.WriteLine("\nlo que no es GGUF no tiene veredicto");
        Check("texto suelto", GgufInspector.TryParse(Encoding.ASCII.GetBytes("x")) == null);
        Check("magia equivocada", GgufInspector.TryParse(new byte[64]) == null);
        byte[] v1 = Gguf.Modelo();
        v1[4] = 1;
        Check("GGUF v1", GgufInspector.TryParse(v1) == null);
    }

    static void CasoNombres()
    {
        Console.WriteLine("\nel nombre, cuando no hay cabecera");
        (string archivo, GgufKind esperado)[] tabla =
        {
            ("Qwen3-8B-Q4_K_M.gguf", GgufKind.Model),
            ("gemma-4-E4B-it-UD-Q4_K_XL.gguf", GgufKind.Model),
            ("mmproj-model-f16.gguf", GgufKind.Projector),
            ("Qwen3.8-27B-Uncensored-draft-Q8_0.gguf", GgufKind.DraftHead),
            ("Qwen3-235B-Q2_K_L-00001-of-00002.gguf", GgufKind.SplitPart),
            ("Qwen3-235B-Q2_K_L-00002-of-00002.gguf", GgufKind.SplitPart),
            ("Llama-3-8B-lora-F16.gguf", GgufKind.Adapter),
            ("ggml-vocab-llama-bpe.gguf", GgufKind.VocabOnly),
            // Que la palabra aparezca adentro de otra no cuenta
            ("Drafter-Pro-7B-Q4_K_M.gguf", GgufKind.Model),
            ("Explora-8B-Q4_K_M.gguf", GgufKind.Model)
        };
        foreach (var (archivo, esperado) in tabla)
        {
            var v = GgufInspector.FromFileName(archivo);
            Check($"{archivo} → {esperado}", v.Kind == esperado, v.Kind.ToString());
        }
    }

    // --- el selector: el recorrido del usuario, con el ViewModel real ---

    static void CasoSelector()
    {
        Console.WriteLine("\nel selector no ofrece lo que no carga");
        string raiz = NuevaCarpeta("selector");
        string desc = Path.Combine(raiz, "Descargas");

        // El peor caso: una cabeza de borrador con nombre de modelo, que solo la
        // cabecera desenmascara
        File.WriteAllBytes(Path.Combine(desc, "Qwen3.8-27B-Q8_0.gguf"), Gguf.Borrador());
        File.WriteAllBytes(Path.Combine(desc, "Llama-3-8B-Q4_K_M.gguf"), Gguf.Modelo());
        File.WriteAllBytes(Path.Combine(desc, "mmproj-Llama-F16.gguf"), Gguf.Proyector());
        File.WriteAllBytes(Path.Combine(desc, "Grande-Q2_K-00001-of-00002.gguf"), Gguf.Modelo());
        // Sin cabecera legible decide el nombre, y ante la duda se lista
        File.WriteAllText(Path.Combine(desc, "Ilegible-Q4_K_M.gguf"), "x");

        var cfg = new ConfigManager(Path.Combine(raiz, "settings.json"));
        cfg.Save(new AppSettings { ModelPath = Path.Combine(raiz, "Models"), ModelDownloadPath = desc });
        var svc = new ModelProfileService(Path.Combine(raiz, "model_catalog.json"), Path.Combine(raiz, "Models"));
        var vm = new ModeSelectionViewModel(cfg, svc);

        var ofrecidos = vm.AvailableModelItems.Select(m => m.Model.ModelFileName).ToList();
        bool hayDescargas = typeof(ConfigManager).Assembly.GetTypes().Any(t => t.Name == "ModelCatalogViewModel");

        if (!hayDescargas)
        {
            // La Forense no escanea la carpeta de descargas: no hay nada que mirar ahí
            Check("Forense: no se ofrece ningún descargado", !ofrecidos.Any(f => f.StartsWith("Qwen") || f.StartsWith("Llama")));
            return;
        }

        Check("el modelo de verdad se ofrece", ofrecidos.Contains("Llama-3-8B-Q4_K_M.gguf"), string.Join(", ", ofrecidos));
        Check("la cabeza de borrador disfrazada no", !ofrecidos.Contains("Qwen3.8-27B-Q8_0.gguf"));
        Check("el proyector no", !ofrecidos.Contains("mmproj-Llama-F16.gguf"));
        Check("la parte de un modelo partido no", !ofrecidos.Contains("Grande-Q2_K-00001-of-00002.gguf"));
        Check("el ilegible sí, ante la duda", ofrecidos.Contains("Ilegible-Q4_K_M.gguf"));
    }

    static void CasoArchivoLocal()
    {
        Console.WriteLine("\nla cabecera se lee de disco sin cargar el archivo");
        string raiz = NuevaCarpeta("local");
        string ruta = Path.Combine(raiz, "grande.gguf");
        // 8 MB de cola: solo se leen los primeros 64 KB
        byte[] cabecera = Gguf.Borrador();
        using (var fs = File.Create(ruta))
        {
            fs.Write(cabecera);
            fs.SetLength(cabecera.Length + 8 * 1024 * 1024);
        }
        var h = GgufInspector.TryReadFile(ruta);
        Check("se lee", h?.TensorCount == 18, h?.TensorCount.ToString());
        Check("un archivo inexistente no lanza", GgufInspector.TryReadFile(Path.Combine(raiz, "no.gguf")) == null);

        // Mientras se descarga, el .part está abierto para escritura: leerlo no debe fallar
        using var abierto = new FileStream(ruta, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        Check("abierto por otro proceso, también", GgufInspector.TryReadFile(ruta)?.TensorCount == 18);
    }

    // --- utilidades ---

    static GgufHeader Leer(byte[] gguf) =>
        GgufInspector.TryParse(gguf) ?? throw new InvalidOperationException("la cabecera de prueba no se pudo leer");

    static string NuevaCarpeta(string nombre)
    {
        string raiz = Path.Combine(Path.GetTempPath(), "BancoNoModelos", nombre);
        if (Directory.Exists(raiz)) Directory.Delete(raiz, true);
        Directory.CreateDirectory(Path.Combine(raiz, "Models"));
        Directory.CreateDirectory(Path.Combine(raiz, "Descargas"));
        File.WriteAllText(Path.Combine(raiz, "model_catalog.json"),
            """{ "version": 2, "models": [], "execution_profiles": [] }""");
        return raiz;
    }
}

/// <summary>Escribe cabeceras GGUF v3 con el formato real, para no depender de archivos de GB.</summary>
static class Gguf
{
    public static byte[] Modelo(bool blockCountU64 = false, int vocabulario = 0) =>
        Armar(tensores: 291, kv =>
        {
            kv.Str("general.architecture", "llama");
            kv.Str("general.type", "model");
            kv.Str("general.name", "Llama 3 8B");
            if (blockCountU64) kv.U64("llama.block_count", 32); else kv.U32("llama.block_count", 32);
            kv.U32("llama.context_length", 8192);
            kv.ArrF32("llama.rope.freqs", 64);
            if (vocabulario > 0) kv.ArrStr("tokenizer.ggml.tokens", vocabulario);
        });

    public static byte[] ModeloConVisionAntes() =>
        Armar(tensores: 291, kv =>
        {
            kv.Str("general.architecture", "llama");
            kv.U32("clip.vision.block_count", 27);
            kv.U32("llama.block_count", 32);
        });

    public static byte[] Borrador() =>
        Armar(tensores: 18, kv =>
        {
            kv.Str("general.architecture", "qwen35");
            kv.Str("general.type", "model");
            kv.Str("general.size_label", "3.0B");
            kv.U32("qwen35.block_count", 65);
            kv.U32("qwen35.nextn_predict_layers", 1);
        });

    public static byte[] Proyector() =>
        Armar(tensores: 439, kv =>
        {
            kv.Str("general.architecture", "clip");
            kv.U32("clip.vision.block_count", 27);
        });

    public static byte[] Adaptador() =>
        Armar(tensores: 128, kv =>
        {
            kv.Str("general.architecture", "llama");
            kv.Str("general.type", "adapter");
            kv.U32("llama.block_count", 32);
        });

    public static byte[] Vocabulario() =>
        Armar(tensores: 0, kv =>
        {
            kv.Str("general.architecture", "llama");
            kv.ArrStr("tokenizer.ggml.tokens", 100);
        });

    public static byte[] Parte() =>
        Armar(tensores: 473, kv =>
        {
            kv.U16("split.no", 1);
            kv.I32("split.tensors.count", 1131);
            kv.U16("split.count", 3);
        });

    sealed class Kv
    {
        public readonly MemoryStream Ms = new();
        public int Count;
        readonly BinaryWriter _w;
        public Kv() { _w = new BinaryWriter(Ms); }

        void Key(string k, uint tipo) { S(k); _w.Write(tipo); Count++; }
        void S(string s) { var b = Encoding.UTF8.GetBytes(s); _w.Write((ulong)b.Length); _w.Write(b); }

        public void Str(string k, string v) { Key(k, 8); S(v); }
        public void U16(string k, ushort v) { Key(k, 2); _w.Write(v); }
        public void U32(string k, uint v) { Key(k, 4); _w.Write(v); }
        public void I32(string k, int v) { Key(k, 5); _w.Write(v); }
        public void U64(string k, ulong v) { Key(k, 10); _w.Write(v); }
        public void ArrF32(string k, int n) { Key(k, 9); _w.Write(6u); _w.Write((ulong)n); for (int i = 0; i < n; i++) _w.Write(1f); }
        public void ArrStr(string k, int n) { Key(k, 9); _w.Write(8u); _w.Write((ulong)n); for (int i = 0; i < n; i++) S("tok" + i); }
    }

    static byte[] Armar(ulong tensores, Action<Kv> llenar)
    {
        var kv = new Kv();
        llenar(kv);
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write(Encoding.ASCII.GetBytes("GGUF"));
        w.Write(3u);
        w.Write(tensores);
        w.Write((ulong)kv.Count);
        w.Write(kv.Ms.ToArray());
        // Algo de relleno donde irían la tabla de tensores y los pesos
        w.Write(new byte[256]);
        return ms.ToArray();
    }
}
