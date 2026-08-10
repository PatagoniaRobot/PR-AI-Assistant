using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PRImager.AIAssistant.Services.Folders;

// Banco de prueba para la configuración de carpetas de datos.
//
// Lo que importa verificar no es que mueva archivos, sino las tres cosas que pueden
// perder trabajo del usuario y que están puestas a propósito:
//
//   1. Cambiar la carpeta NO mueve nada. Es una redirección instantánea que no puede
//      fallar; mover es una acción aparte. Si alguna vez se juntan, un movimiento
//      cortado a la mitad dejaría la configuración a medias.
//   2. Entre volúmenes se copia, se VERIFICA y recién ahí se borra el origen. Mientras
//      el destino no esté completo y promovido, el original sigue intacto.
//   3. Un archivo que ya existe en el destino no se pisa nunca.
//
// El camino entre volúmenes se ejerce sin necesitar dos discos: FolderMigrator decide
// por la raíz de la ruta, así que se lo fuerza con una unidad virtual (subst).

class Banco
{
    static int _ok, _fail;

    static void Check(string nombre, bool cond, string detalle = "")
    {
        if (cond) { _ok++; Console.WriteLine($"  OK   {nombre}"); }
        else { _fail++; Console.WriteLine($"  FALLA {nombre}   {detalle}"); }
    }

    static string NuevoCaso(string nombre)
    {
        string raiz = Path.Combine(Path.GetTempPath(), "BancoCarpetas", nombre);
        if (Directory.Exists(raiz)) Directory.Delete(raiz, true);
        Directory.CreateDirectory(raiz);
        return raiz;
    }

    // Archivo con contenido distinto por nombre, para que el hash detecte un cruce.
    static string Archivo(string carpeta, string nombre, int kb = 64)
    {
        Directory.CreateDirectory(carpeta);
        string p = Path.Combine(carpeta, nombre);
        var datos = new byte[kb * 1024];
        var rnd = new Random(nombre.GetHashCode());
        rnd.NextBytes(datos);
        File.WriteAllBytes(p, datos);
        return p;
    }

    static readonly string[] Gguf = { "*.gguf", "*.gguf.part" };

    [STAThread]
    static int Main(string[] args)
    {
        Console.WriteLine("Banco de carpetas de datos\n");

        var casos = new List<Action>
        {
            CasoSurveyCuenta,
            CasoMueveEnElMismoVolumen,
            CasoNoPisaLoQueYaEstaEnElDestino,
            CasoOrigenInexistente,
            CasoMismaCarpeta,
            CasoDestinoAdentroDelOrigen,
            CasoSoloLosPatronesPedidos,
            CasoCancelarDejaMovidoLoMovido,
            CasoDetectaBinComoSalidaDeCompilacion
        };

        foreach (var caso in casos)
        {
            try { caso(); }
            catch (Exception ex)
            {
                _fail++;
                Console.WriteLine($"  FALLA {caso.Method.Name} lanzó {ex.GetType().Name}: {ex.Message}");
            }
        }

        CasoEntreVolumenes();

        var (okVm, failVm) = BancoVm.Correr();
        _ok += okVm;
        _fail += failVm;

        var (okCab, failCab) = Cableado.Correr();
        _ok += okCab;
        _fail += failCab;

        if (!args.Contains("--sin-humo"))
        {
            var (okHumo, failHumo) = Humo.Correr();
            _ok += okHumo;
            _fail += failHumo;
        }

        Console.WriteLine($"\n  {_ok} OK, {_fail} fallas");
        return _fail == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------------ medir

    static void CasoSurveyCuenta()
    {
        Console.WriteLine("Survey cuenta lo que hay para mover");
        string raiz = NuevoCaso("survey");
        string desde = Path.Combine(raiz, "vieja");

        Archivo(desde, "a.gguf", 64);
        Archivo(desde, "b.gguf", 32);
        Archivo(desde, "notas.txt", 8);          // no es de esta carpeta

        var (archivos, bytes) = FolderMigrator.Survey(desde, Gguf);

        Check("cuenta solo los archivos de la carpeta", archivos == 2, $"contó {archivos}");
        Check("suma los bytes de esos y no de los otros", bytes == 96 * 1024, $"sumó {bytes}");

        var (vacios, _) = FolderMigrator.Survey(Path.Combine(raiz, "no-existe"), Gguf);
        Check("una carpeta que no existe da cero, no una excepción", vacios == 0);
    }

    // ------------------------------------------------------------------ mover

    static void CasoMueveEnElMismoVolumen()
    {
        Console.WriteLine("\nMueve en el mismo volumen");
        string raiz = NuevoCaso("mismo-volumen");
        string desde = Path.Combine(raiz, "vieja");
        string hacia = Path.Combine(raiz, "nueva");

        var origen = Archivo(desde, "modelo.gguf", 128);
        var bytesOriginales = File.ReadAllBytes(origen);

        var r = Mover(desde, hacia, Gguf);

        Check("informa éxito", r.Success && !r.WasCancelled);
        Check("detecta que es el mismo volumen", r.WasSameVolume);
        Check("movió el archivo", r.Moved.Count == 1 && r.Moved[0] == "modelo.gguf");
        Check("el archivo ya no está en la carpeta anterior", !File.Exists(origen));

        var destino = Path.Combine(hacia, "modelo.gguf");
        Check("el archivo está en la carpeta nueva", File.Exists(destino));
        Check("el contenido es el mismo",
            File.Exists(destino) && File.ReadAllBytes(destino).SequenceEqual(bytesOriginales));
        Check("informa los bytes movidos", r.BytesMoved == 128 * 1024, $"informó {r.BytesMoved}");
    }

    static void CasoNoPisaLoQueYaEstaEnElDestino()
    {
        Console.WriteLine("\nNo pisa un archivo que ya está en el destino");
        string raiz = NuevoCaso("colision");
        string desde = Path.Combine(raiz, "vieja");
        string hacia = Path.Combine(raiz, "nueva");

        Archivo(desde, "modelo.gguf", 64);
        var yaEstaba = Archivo(hacia, "modelo.gguf", 32);
        var contenidoBueno = File.ReadAllBytes(yaEstaba);

        var r = Mover(desde, hacia, Gguf);

        Check("lo informa como salteado, no como movido",
            r.Skipped.Count == 1 && r.Moved.Count == 0);
        Check("NO pisó el archivo que ya estaba",
            File.ReadAllBytes(yaEstaba).SequenceEqual(contenidoBueno));
        Check("deja el de la carpeta anterior donde estaba",
            File.Exists(Path.Combine(desde, "modelo.gguf")),
            "saltear no puede borrar el original: sería perderlo sin haberlo copiado");
    }

    static void CasoOrigenInexistente()
    {
        Console.WriteLine("\nUna carpeta anterior que no existe no es un error");
        string raiz = NuevoCaso("sin-origen");

        var r = Mover(Path.Combine(raiz, "no-existe"), Path.Combine(raiz, "nueva"), Gguf);

        Check("informa éxito", r.Success);
        Check("no hizo nada", r.NothingToDo);
        Check("no devuelve un mensaje de error", r.ErrorMessage.Length == 0);
    }

    static void CasoMismaCarpeta()
    {
        Console.WriteLine("\nMover a la misma carpeta no toca nada");
        string raiz = NuevoCaso("misma");
        string carpeta = Path.Combine(raiz, "unica");
        var archivo = Archivo(carpeta, "modelo.gguf", 32);

        var r = Mover(carpeta, carpeta, Gguf);

        Check("informa éxito", r.Success);
        Check("el archivo sigue estando", File.Exists(archivo));
        Check("no informa nada movido", r.Moved.Count == 0);
    }

    static void CasoDestinoAdentroDelOrigen()
    {
        Console.WriteLine("\nRechaza mover a una carpeta adentro de la de origen");
        string raiz = NuevoCaso("anidada");
        string desde = Path.Combine(raiz, "vieja");
        string hacia = Path.Combine(desde, "adentro");
        var archivo = Archivo(desde, "modelo.gguf", 32);

        var r = Mover(desde, hacia, Gguf);

        Check("no lo intenta", !r.Success);
        Check("explica el motivo", r.ErrorMessage.Contains("adentro"), r.ErrorMessage);
        Check("no tocó el archivo", File.Exists(archivo));
    }

    static void CasoSoloLosPatronesPedidos()
    {
        Console.WriteLine("\nMueve solo los archivos de esa carpeta");
        string raiz = NuevoCaso("patrones");
        string desde = Path.Combine(raiz, "vieja");
        string hacia = Path.Combine(raiz, "nueva");

        Archivo(desde, "modelo.gguf", 16);
        Archivo(desde, "modelo.gguf.part", 8);
        var ajeno = Archivo(desde, "config.json", 4);

        var r = Mover(desde, hacia, Gguf);

        Check("mueve el .gguf y su .part", r.Moved.Count == 2);
        Check("no toca lo que no es suyo", File.Exists(ajeno));

        // Las imágenes generadas SÍ se llevan su .json, que es lo que las hace
        // reproducibles: el sidecar no puede quedarse atrás.
        Check("los patrones de imágenes incluyen el sidecar",
            AppFolderService.GeneratedImagePatterns.Contains("*.json") &&
            AppFolderService.GeneratedImagePatterns.Contains("*.png"));
        Check("los de modelos de imagen incluyen el VAE .safetensors",
            AppFolderService.ImageModelPatterns.Contains("*.safetensors"));
    }

    static void CasoCancelarDejaMovidoLoMovido()
    {
        Console.WriteLine("\nCancelar a la mitad deja movido lo ya movido");
        string raiz = NuevoCaso("cancelar");
        string desde = Path.Combine(raiz, "vieja");
        string hacia = Path.Combine(raiz, "nueva");

        for (int i = 0; i < 6; i++) Archivo(desde, $"m{i}.gguf", 32);

        var migrador = new FolderMigrator();
        var cts = new CancellationTokenSource();

        // Se corta después del segundo archivo terminado.
        migrador.ProgressChanged += (_, p) => { if (p.FilesDone >= 2) cts.Cancel(); };

        var r = migrador.MoveAsync(desde, hacia, Gguf, cts.Token).GetAwaiter().GetResult();

        Check("informa que fue cancelado", r.WasCancelled);
        Check("no lo informa como fallo", r.Success,
            "una cancelación es una decisión del usuario, no un error");
        Check("movió algo pero no todo",
            r.Moved.Count > 0 && r.Moved.Count < 6, $"movió {r.Moved.Count}");

        int enNueva = Directory.GetFiles(hacia, "*.gguf").Length;
        int enVieja = Directory.GetFiles(desde, "*.gguf").Length;

        Check("lo movido está en la carpeta nueva", enNueva == r.Moved.Count,
            $"{enNueva} en disco contra {r.Moved.Count} informados");
        Check("no se perdió ningún archivo", enNueva + enVieja == 6,
            $"{enNueva} + {enVieja}");
    }

    // ------------------------------------------------------------------ entre volúmenes

    /// <summary>
    /// El camino que puede perder un archivo. Se fuerza con una unidad virtual porque
    /// FolderMigrator decide por la raíz de la ruta: sin esto, un banco en una sola
    /// máquina no ejerce nunca el copiar-verificar-borrar y la parte con riesgo real
    /// queda sin probar. Es la misma clase de agujero que el doble recortado de E4.3.
    /// </summary>
    static void CasoEntreVolumenes()
    {
        Console.WriteLine("\nEntre volúmenes: copiar, verificar y recién ahí borrar");

        string raiz = NuevoCaso("volumenes");
        string real = Path.Combine(raiz, "montada");
        Directory.CreateDirectory(real);

        string letra = LetraLibre();
        if (letra is null)
        {
            Console.WriteLine("  OMITIDO  no hay letra de unidad libre para montar");
            return;
        }

        if (!Subst(letra, real))
        {
            Console.WriteLine($"  OMITIDO  no se pudo montar {letra} con subst");
            return;
        }

        try
        {
            string desde = Path.Combine(raiz, "vieja");
            string hacia = letra + @"\nueva";

            var origen = Archivo(desde, "modelo.gguf", 256);
            var bytesOriginales = File.ReadAllBytes(origen);

            var r = Mover(desde, hacia, Gguf);

            Check("detecta que son volúmenes distintos", !r.WasSameVolume,
                "si esto falla, el resto del caso no está probando el camino de copia");
            Check("informa éxito", r.Success);
            Check("movió el archivo", r.Moved.Count == 1);

            var destino = Path.Combine(hacia, "modelo.gguf");
            Check("el archivo llegó entero al destino",
                File.Exists(destino) && File.ReadAllBytes(destino).SequenceEqual(bytesOriginales));
            Check("borró el original recién después", !File.Exists(origen));
            Check("no dejó ningún .part",
                Directory.GetFiles(hacia, "*" + FolderMigrator.PartialExtension).Length == 0);

            // Cancelar durante la copia: el original NO se puede haber borrado.
            var origen2 = Archivo(desde, "grande.gguf", 4096);
            var migrador = new FolderMigrator();
            var cts = new CancellationTokenSource();
            migrador.ProgressChanged += (_, _) => cts.Cancel();

            var r2 = migrador.MoveAsync(desde, hacia, Gguf, cts.Token).GetAwaiter().GetResult();

            Check("cancelar durante la copia no borra el original", File.Exists(origen2),
                "es la única garantía que hace seguro el camino entre volúmenes");
            Check("cancelar no deja un .part a medias",
                Directory.GetFiles(hacia, "*" + FolderMigrator.PartialExtension).Length == 0);
            Check("no informa el archivo como movido", !r2.Moved.Contains("grande.gguf"));
        }
        finally
        {
            Subst(letra, null);
        }
    }

    static string LetraLibre()
    {
        var usadas = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
        foreach (char c in "YXWVU")
            if (!usadas.Contains(c)) return c + ":";
        return null;
    }

    static bool Subst(string letra, string carpeta)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("subst")
            {
                Arguments = carpeta is null ? $"{letra} /D" : $"{letra} \"{carpeta}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };
            using var p = System.Diagnostics.Process.Start(psi);
            p.WaitForExit(5000);
            return p.ExitCode == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // ------------------------------------------------------------------ el aviso de bin\

    static void CasoDetectaBinComoSalidaDeCompilacion()
    {
        Console.WriteLine("\nDetecta una carpeta adentro de la salida de compilación");

        string baseDir = AppDomain.CurrentDomain.BaseDirectory;

        Check("una carpeta adentro del directorio de la app se detecta",
            AppFolderService.IsInsideBuildOutput(Path.Combine(baseDir, "ModelosDescargados")),
            "es el pendiente 6.3: ahí adentro, una limpieza de build se lleva los modelos");

        Check("el propio directorio de la app se detecta",
            AppFolderService.IsInsideBuildOutput(baseDir));

        Check("una carpeta de %APPDATA% no se detecta",
            !AppFolderService.IsInsideBuildOutput(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "PatagoniaRobot", "PR-AIA", "ModelosDescargados")));

        Check("una ruta vacía no se detecta ni lanza",
            !AppFolderService.IsInsideBuildOutput(""));

        // Control negativo del prefijo: "bin-otro" empieza con "bin" pero no cuelga de él.
        Check("no confunde una carpeta hermana con el mismo prefijo",
            !AppFolderService.IsInsideBuildOutput(
                Path.TrimEndingDirectorySeparator(baseDir) + "-otra"));
    }

    // ------------------------------------------------------------------

    static FolderMigrationResult Mover(string desde, string hacia, string[] patrones) =>
        new FolderMigrator().MoveAsync(desde, hacia, patrones, CancellationToken.None)
            .GetAwaiter().GetResult();
}
