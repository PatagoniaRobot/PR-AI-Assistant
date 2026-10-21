using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PRImager.AIAssistant.Services.Enums;
using PRImager.AIAssistant.Services.ImageGen;

// Banco de prueba para E4.1: el motor de imágenes bajo control.
//
// Ejerce ImageServerManager e ImageGenClient contra el DLL real y, cuando el motor
// está instalado, contra sd-server de verdad. Lo que importa verificar no es que
// genere una imagen —eso ya se sabía desde E4.0— sino las tres cosas que la API NO
// da y hubo que resolver afuera:
//
//   · el progreso sale de stdout, y hay una barra tramposa que no es progreso
//   · cancelar en vuelo exige matar el proceso, esperando su salida real
//   · los fallos tienen que ser datos legibles, no excepciones

class Banco
{
    static int _ok, _fail, _omitidas;

    static void Check(string nombre, bool cond, string detalle = "")
    {
        if (cond) { _ok++; Console.WriteLine($"  OK    {nombre}"); }
        else { _fail++; Console.WriteLine($"  FALLA {nombre}   {detalle}"); }
    }

    static void Omitida(string nombre, string motivo)
    {
        _omitidas++;
        Console.WriteLine($"  OMIT  {nombre}   ({motivo})");
    }

    // Raíz de la instalación del motor: la variable de entorno SD_RAIZ, o una carpeta
    // «sd» al lado del repositorio.
    internal static string RaizMotor =>
        Environment.GetEnvironmentVariable("SD_RAIZ")
        ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\..\..\sd"));

    static string Exe => Path.Combine(RaizMotor, "bin", "sd-server.exe");
    static string Difusion => Path.Combine(RaizMotor, "models", "z_image_turbo-Q4_K.gguf");
    static string TextEncoder => Path.Combine(RaizMotor, "models", "Qwen3-4B-Instruct-2507-Q4_K_M.gguf");
    static string Vae => Path.Combine(RaizMotor, "models", "ae.safetensors");

    static bool HayMotor => File.Exists(Exe) && File.Exists(Difusion)
                            && File.Exists(TextEncoder) && File.Exists(Vae);

    /// <summary>
    /// El paquete instalado de verdad, para las pruebas que pasan por ImageModelService.
    /// Los pasos y el CFG son los de Z-Image Turbo, no los del motor.
    /// </summary>
    static ImageModelPackage PaqueteReal() => new()
    {
        Id = "z-image-turbo-real",
        DisplayName = "Z-Image Turbo Q4_K",
        Family = "z-image",
        DefaultSteps = 8,
        DefaultCfgScale = 1.0,
        DiffusionModelPath = Difusion,
        TextEncoderPath = TextEncoder,
        VaePath = Vae
    };

    static ImageServerConfig ConfigReal(int puerto = 1234) => new()
    {
        ServerExecutablePath = Exe,
        DiffusionModelPath = Difusion,
        TextEncoderPath = TextEncoder,
        VaePath = Vae,
        Port = puerto
    };

    static string CarpetaSalida()
    {
        var p = Path.Combine(Path.GetTempPath(), "BancoImagenes");
        Directory.CreateDirectory(p);
        return p;
    }

    static int ProcesosVivos() => Process.GetProcessesByName("sd-server").Length;

    static void MatarTodo()
    {
        foreach (var p in Process.GetProcessesByName("sd-server"))
        {
            try { p.Kill(true); p.WaitForExit(5000); } catch { }
        }
    }

    [STAThread]
    static int Main(string[] args)
    {
        // Modo hijo: levanta el servidor y se queda esperando a que lo maten de
        // golpe. Lo usa CasoSinHuerfanos; ver allá por qué hace falta un proceso
        // aparte y no alcanza con Dispose.
        if (args.Length > 0 && args[0] == "--hijo-que-muere")
            return HijoQueMuere().GetAwaiter().GetResult();

        // Modo hijo: mira el binario Forense por reflexión. Va en otro proceso porque
        // cargarlo acá rompe la resolución de recursos XAML de las pruebas de humo.
        if (args.Length > 0 && args[0] == BancoIntegracion.ArgumentoDelHijo)
            return BancoIntegracion.CorrerComoHijo();

        Console.WriteLine("Banco del motor de generación de imágenes (E4.1)\n");
        MatarTodo();

        // El interruptor de la pantalla de inicio vive en la configuración real de esta PC
        // (MainViewModel no deja inyectar otra). Si quien corre el banco lo dejó apagado,
        // la barra lateral sale desactivada y el humo falla por el motivo equivocado: se
        // enciende mientras corre y se devuelve como estaba al salir.
        var interruptor = new ImageModelService();
        bool comoEstaba = interruptor.GenerationEnabled;
        interruptor.GenerationEnabled = true;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => new ImageModelService().GenerationEnabled = comoEstaba;

        Console.WriteLine("-- Configuración y línea de comandos --");
        CasoLineaDeComandos();
        CasoPalancasDeMemoria();
        CasoArchivosFaltantes();

        Console.WriteLine("\n-- Capacidades del motor (parser) --");
        CasoParseoDeCapacidades();
        CasoCapacidadesInvalidas();

        Console.WriteLine("\n-- Semáforo de VRAM (E4.2) --");
        BancoArbitro.Correr(Check);

        Console.WriteLine("\n-- Paquetes de modelo (E4.3) --");
        BancoPaquetes.Correr(Check);
        BancoPaquetes.CorrerDescarga(Check, args.Contains("--con-red")).GetAwaiter().GetResult();

        Console.WriteLine("\n-- La vista de generación (E4.4) --");
        BancoGeneracion.Correr(Check);

        Console.WriteLine("\n-- Integración y capacidad del perfil (E4.5) --");
        BancoIntegracion.Correr(Check);

        Console.WriteLine("\n-- Los fallos son datos, no excepciones --");
        CasoSinMotorInstalado().GetAwaiter().GetResult();
        CasoSinModelo().GetAwaiter().GetResult();
        CasoResolucionBloqueada().GetAwaiter().GetResult();

        Console.WriteLine("\n-- Contra el motor real --");
        if (!HayMotor)
        {
            Console.WriteLine($"  El motor no está en {RaizMotor}.");
            Console.WriteLine("  Se omiten las pruebas que lo necesitan. Poné SD_RAIZ si está en otro lado.");
            foreach (var n in new[] { "arranca y queda listo", "informa el modelo cargado",
                                      "genera una imagen real", "el progreso viene de stdout",
                                      "el progreso ignora la barra de carga", "prompt vacío con el motor listo",
                                      "la cancelación corta de verdad",
                                      "no deja sd-server huérfano", "puerto ocupado por otro modelo",
                                      "la vista genera de punta a punta", "el hilo de UI no se bloquea",
                                      "alternar diez veces no deja nada colgado" })
                Omitida(n, "sin motor instalado");
        }
        else
        {
            CasoGeneracionYProgreso().GetAwaiter().GetResult();
            CasoCancelacion().GetAwaiter().GetResult();
            CasoSinHuerfanos().GetAwaiter().GetResult();
            CasoPuertoOcupadoOtroModelo().GetAwaiter().GetResult();

            // E4.4 de punta a punta: el ViewModel real contra el motor real, midiendo el
            // bloqueo del hilo de UI. Las cancelaciones repetidas van aparte porque son
            // lentas y se pueden saltear con --sin-alternancias.
            Console.WriteLine("\n-- La vista contra el motor (E4.4) --");
            MatarTodo();
            BancoGeneracion.CorrerContraElMotor(Check, Exe, PaqueteReal()).GetAwaiter().GetResult();

            if (args.Contains("--sin-alternancias"))
            {
                Omitida("alternar diez veces no degrada ni deja procesos colgados", "--sin-alternancias");
            }
            else
            {
                Console.WriteLine("\n-- Cancelar diez veces seguidas (criterio arrastrado de E4.2) --");
                MatarTodo();
                BancoGeneracion.CorrerCancelaciones(Check, Exe, PaqueteReal()).GetAwaiter().GetResult();
            }
        }

        MatarTodo();

        // El humo va al final: abre ventanas WPF de verdad y deja capturas, y conviene
        // que no compita con el motor por la GPU mientras tanto.
        int fallasHumo = HumoPaquetes.Correr() + HumoGeneracion.Correr() + HumoBarraLateral.Correr();

        Console.WriteLine($"\n{_ok} OK, {_fail} fallas, {_omitidas} omitidas, {fallasHumo} fallas de humo");
        return _fail == 0 && fallasHumo == 0 ? 0 : 1;
    }

    // ---------------------------------------------------------------- configuración

    static void CasoLineaDeComandos()
    {
        var c = new ImageServerConfig
        {
            ServerExecutablePath = @"C:\sd\sd-server.exe",
            DiffusionModelPath = @"C:\m\dif.gguf",
            TextEncoderPath = @"C:\m\te.gguf",
            VaePath = @"C:\m\ae.safetensors",
            Port = 4321
        };
        var a = c.ToCommandLineArgs();

        Check("pasa el modelo de difusión", a.Contains("--diffusion-model \"C:\\m\\dif.gguf\""));
        Check("pasa el text encoder como --llm", a.Contains("--llm \"C:\\m\\te.gguf\""));
        Check("pasa el VAE", a.Contains("--vae \"C:\\m\\ae.safetensors\""));
        Check("respeta el puerto configurado", a.Contains("--listen-port 4321"));
        Check("escucha solo en loopback", a.Contains("--listen-ip 127.0.0.1"));
        Check("BaseUrl se arma con host y puerto", c.BaseUrl == "http://127.0.0.1:4321", c.BaseUrl);

        // Sin -v el motor no imprime el progreso por paso, y como la API tampoco lo
        // da, la generación quedaría sin ninguna señal de avance.
        Check("pide salida detallada, que es de donde sale el progreso", a.Contains(" -v"));
    }

    static void CasoPalancasDeMemoria()
    {
        var todas = new ImageServerConfig
        {
            ServerExecutablePath = "x", DiffusionModelPath = "d",
            TextEncoderPath = "t", VaePath = "v"
        };
        var a = todas.ToCommandLineArgs();

        // Los tres defaults salen de las mediciones de E4.0: tiling vale 6,4 GiB,
        // offload otros 3,5, y flash attention un 25% de tiempo.
        Check("flash attention viene puesta por defecto", a.Contains("--diffusion-fa"));
        Check("el tiling del VAE viene puesto por defecto", a.Contains("--vae-tiling"));
        Check("el offload a CPU viene puesto por defecto", a.Contains("--offload-to-cpu"));

        // --eager-load haría que sd-server retenga los pesos en VRAM en lugar de
        // liberarlos entre jobs, y con eso se acaba la convivencia con el modelo de
        // lenguaje. No debe aparecer nunca.
        Check("nunca pide --eager-load", !a.Contains("--eager-load"));

        var sin = todas with { FlashAttention = false, VaeTiling = false, OffloadToCpu = false };
        var b = sin.ToCommandLineArgs();
        Check("se pueden apagar las tres palancas",
            !b.Contains("--diffusion-fa") && !b.Contains("--vae-tiling") && !b.Contains("--offload-to-cpu"));
    }

    static void CasoArchivosFaltantes()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "BancoImagenesCfg");
        Directory.CreateDirectory(tmp);
        var d = Path.Combine(tmp, "d.gguf"); File.WriteAllBytes(d, new byte[16]);
        var t = Path.Combine(tmp, "t.gguf"); File.WriteAllBytes(t, new byte[16]);
        var v = Path.Combine(tmp, "v.safetensors"); File.WriteAllBytes(v, new byte[16]);

        var completo = new ImageServerConfig
        { ServerExecutablePath = "x", DiffusionModelPath = d, TextEncoderPath = t, VaePath = v };
        Check("con los tres archivos no falta nada", completo.FirstMissingModelFile() is null);

        Check("detecta que falta la difusión",
            (completo with { DiffusionModelPath = Path.Combine(tmp, "no.gguf") }).FirstMissingModelFile()!.EndsWith("no.gguf"));
        Check("detecta que falta el text encoder",
            (completo with { TextEncoderPath = Path.Combine(tmp, "no.gguf") }).FirstMissingModelFile()!.EndsWith("no.gguf"));
        Check("detecta que falta el VAE",
            (completo with { VaePath = Path.Combine(tmp, "no.st") }).FirstMissingModelFile()!.EndsWith("no.st"));

        Directory.Delete(tmp, true);
    }

    // ---------------------------------------------------------------- capacidades

    // Respuesta real de /sdcpp/v1/capabilities capturada en E4.0, recortada. Es un
    // doble, y para un parser puro es legítimo; la respuesta viva se verifica igual
    // en CasoGeneracionYProgreso cuando el motor está instalado.
    const string CapacidadesReales = """
    {
      "model": { "name": "z_image_turbo-Q4_K.gguf", "stem": "z_image_turbo-Q4_K" },
      "current_mode": "img_gen",
      "supported_modes": ["img_gen"],
      "samplers": ["euler","euler_a","heun","dpm2","dpm++2m","lcm","ddim_trailing","lms"],
      "schedulers": ["discrete","karras","exponential","simple","beta"],
      "limits": { "max_batch_count": 8, "max_height": 4096, "max_queue_size": 64,
                  "max_width": 4096, "min_height": 64, "min_width": 64 }
    }
    """;

    static void CasoParseoDeCapacidades()
    {
        var c = ImageGenClient.ParseCapabilities(CapacidadesReales);
        Check("interpreta la respuesta real del motor", c is not null);
        if (c is null) return;

        Check("saca el nombre del modelo cargado", c.ModelName == "z_image_turbo-Q4_K.gguf", c.ModelName);
        Check("lee la lista de samplers", c.Samplers.Count == 8, $"{c.Samplers.Count}");
        Check("lee la lista de schedulers", c.Schedulers.Count == 5, $"{c.Schedulers.Count}");
        Check("lee los límites de tamaño", c.MinWidth == 64 && c.MaxWidth == 4096,
            $"{c.MinWidth}-{c.MaxWidth}");
        Check("lee el tope de lote", c.MaxBatchCount == 8, $"{c.MaxBatchCount}");
    }

    static void CasoCapacidadesInvalidas()
    {
        Check("JSON roto devuelve null y no explota", ImageGenClient.ParseCapabilities("{ no es json") is null);
        Check("HTML en lugar de JSON devuelve null", ImageGenClient.ParseCapabilities("<html>502</html>") is null);
        Check("respuesta vacía devuelve null", ImageGenClient.ParseCapabilities("") is null);

        // Un JSON válido pero sin las claves esperadas no debe reventar: el motor
        // puede cambiar de forma entre releases.
        var c = ImageGenClient.ParseCapabilities("{\"otra_cosa\":1}");
        Check("JSON válido sin las claves esperadas no explota", c is not null && c.Samplers.Count == 0);
    }

    // ---------------------------------------------------------- fallos como datos

    static async Task CasoSinMotorInstalado()
    {
        using var m = new ImageServerManager();
        var cfg = new ImageServerConfig
        {
            ServerExecutablePath = @"C:\no\existe\sd-server.exe",
            DiffusionModelPath = @"C:\no\d.gguf",
            TextEncoderPath = @"C:\no\t.gguf",
            VaePath = @"C:\no\v.safetensors",
            Port = 1299
        };

        string? error = null;
        var explotó = false;
        try { error = await m.StartAsync(cfg); }
        catch (Exception) { explotó = true; }

        Check("sin motor instalado no lanza excepción", !explotó);
        Check("sin motor instalado devuelve un motivo", !string.IsNullOrWhiteSpace(error));
        Check("el motivo dice cómo instalarlo",
            error is not null && error.Contains("stable-diffusion.cpp"), error ?? "");
        Check("el estado no queda en Ready", m.CurrentStatus != ServerStatus.Ready);
    }

    static async Task CasoSinModelo()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "BancoImagenesSinModelo");
        Directory.CreateDirectory(tmp);
        var falsoExe = Path.Combine(tmp, "sd-server.exe");
        File.WriteAllBytes(falsoExe, new byte[16]);

        using var m = new ImageServerManager();
        var cfg = new ImageServerConfig
        {
            ServerExecutablePath = falsoExe,
            DiffusionModelPath = Path.Combine(tmp, "falta.gguf"),
            TextEncoderPath = Path.Combine(tmp, "t.gguf"),
            VaePath = Path.Combine(tmp, "v.safetensors"),
            Port = 1298
        };

        var error = await m.StartAsync(cfg);
        Check("con el motor pero sin modelo devuelve un motivo", !string.IsNullOrWhiteSpace(error));
        Check("el motivo nombra el archivo que falta",
            error is not null && error.Contains("falta.gguf"), error ?? "");
        Check("el motivo explica que son tres archivos",
            error is not null && error.Contains("tres archivos"), error ?? "");

        Directory.Delete(tmp, true);
    }

    /// <summary>
    /// El servidor acepta prompt vacío y crea el job igual (verificado en E4.0), así
    /// que la validación tiene que estar del lado del cliente.
    ///
    /// Corre CONTRA UN SERVIDOR LISTO a propósito. Una primera versión lo probaba sin
    /// servidor y la aserción pasaba por el motivo equivocado: al quitar la validación
    /// seguía sin generar, pero porque el motor no estaba listo. Una aserción que pasa
    /// por el motivo equivocado no prueba nada.
    /// </summary>
    static async Task CasoPromptVacio(ImageServerManager m)
    {
        using var c = new ImageGenClient(m);
        var ruta = Path.Combine(CarpetaSalida(), "no-deberia-existir.png");
        if (File.Exists(ruta)) File.Delete(ruta);

        var r = await c.GenerateAsync(new ImageGenRequest { Prompt = "   " }, ruta);

        Check("prompt vacío no genera, con el motor listo", !r.Success && !r.WasCancelled);
        Check("prompt vacío avisa qué hacer",
            r.ErrorMessage is not null && r.ErrorMessage.Contains("prompt"), r.ErrorMessage ?? "");
        Check("prompt vacío no escribe archivo", !File.Exists(ruta));
    }

    /// <summary>
    /// El límite de resolución se comprueba en el cliente y no solo en la interfaz,
    /// porque pasarse mata el servidor: un aviso que el llamador pueda ignorar no
    /// alcanza para algo que no se puede deshacer. Se verifica SIN servidor a
    /// propósito, para que quede claro que corta antes de llegar a la red.
    /// </summary>
    static async Task CasoResolucionBloqueada()
    {
        using var m = new ImageServerManager();
        using var c = new ImageGenClient(m);
        var ruta = Path.Combine(CarpetaSalida(), "no-4096.png");
        if (File.Exists(ruta)) File.Delete(ruta);

        var r = await c.GenerateAsync(new ImageGenRequest
        { Prompt = "algo", Width = 4096, Height = 4096 }, ruta);

        Check("el cliente rechaza 4096 antes de contactar al motor", !r.Success);
        Check("y explica el límite real",
            r.ErrorMessage is not null && r.ErrorMessage.Contains("3072"), r.ErrorMessage ?? "");
        Check("el rechazo de 4096 no es 'motor no listo'",
            r.ErrorMessage is not null && !r.ErrorMessage.Contains("no está listo"), r.ErrorMessage ?? "");
        Check("no escribe archivo", !File.Exists(ruta));
    }

    // ------------------------------------------------------------- contra el motor

    static async Task CasoGeneracionYProgreso()
    {
        using var m = new ImageServerManager();
        var progresos = new System.Collections.Concurrent.ConcurrentBag<ImageGenProgress>();
        m.ProgressReported += (_, p) => progresos.Add(p);

        var error = await m.StartAsync(ConfigReal());
        Check("arranca sin error", error is null, error ?? "");
        var listo = await m.WaitForReadyAsync(TimeSpan.FromSeconds(60));
        Check("arranca y queda listo", listo);
        if (!listo) return;

        var caps = await m.GetCapabilitiesAsync(m.BaseUrl);
        Check("informa el modelo cargado",
            caps is not null && caps.ModelName.Contains("z_image_turbo"), caps?.ModelName ?? "null");
        Check("informa samplers de verdad", caps is not null && caps.Samplers.Count > 10,
            $"{caps?.Samplers.Count}");

        using var cli = new ImageGenClient(m);
        var salida = Path.Combine(CarpetaSalida(), "generada.png");
        if (File.Exists(salida)) File.Delete(salida);

        const int pasos = 8;
        var r = await cli.GenerateAsync(new ImageGenRequest
        {
            Prompt = "a red fox in a snowy forest, morning light, photorealistic",
            Width = 1024, Height = 1024, Steps = pasos, CfgScale = 1.0, Seed = 11
        }, salida);

        Check("genera una imagen real", r.Success, r.ErrorMessage ?? "");
        Check("el archivo existe", File.Exists(salida));

        if (File.Exists(salida))
        {
            var bytes = File.ReadAllBytes(salida);
            // No alcanza con que exista: tiene que ser un PNG y tener contenido. Una
            // aserción sobre un archivo vacío pasaría igual.
            Check("es un PNG de verdad",
                bytes.Length > 4 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47);
            Check("el PNG no está vacío", bytes.Length > 100_000, $"{bytes.Length} bytes");
            Check("no queda el archivo parcial", !File.Exists(salida + ".part"));
        }

        // --- el progreso, que es lo que la API no da ---
        var lista = progresos.ToList();
        Check("el progreso viene de stdout", lista.Count > 0, $"{lista.Count} eventos");

        // La trampa: el motor dibuja una barra casi idéntica para la CARGA de tensores,
        // con cientos de pasos y unidad GB/s. Si se colara, el total no sería 8.
        Check("el progreso ignora la barra de carga de tensores",
            lista.Count > 0 && lista.All(p => p.TotalSteps == pasos),
            "totales vistos: " + string.Join(",", lista.Select(p => p.TotalSteps).Distinct()));

        Check("el progreso avanza hasta el final",
            lista.Any(p => p.Step == pasos));
        Check("la fracción queda entre 0 y 1",
            lista.All(p => p.Fraction > 0 && p.Fraction <= 1));
        Check("estima el tiempo restante",
            lista.Any(p => p.SecondsPerIteration > 0));

        // Con el servidor listo, lo único que puede frenar un prompt vacío es la
        // validación del cliente.
        await CasoPromptVacio(m);

        await m.StopAsync();
    }

    static async Task CasoCancelacion()
    {
        using var m = new ImageServerManager();
        var error = await m.StartAsync(ConfigReal());
        if (error is not null) { Check("la cancelación corta de verdad", false, error); return; }
        if (!await m.WaitForReadyAsync(TimeSpan.FromSeconds(60)))
        { Check("la cancelación corta de verdad", false, "no quedó listo"); return; }

        using var cli = new ImageGenClient(m);
        var salida = Path.Combine(CarpetaSalida(), "cancelada.png");
        if (File.Exists(salida)) File.Delete(salida);

        using var cts = new CancellationTokenSource();
        var cron = Stopwatch.StartNew();
        var tarea = cli.GenerateAsync(new ImageGenRequest
        {
            Prompt = "a red fox in a snowy forest",
            Width = 1024, Height = 1024, Steps = 8, CfgScale = 1.0, Seed = 11
        }, salida, cts.Token);

        // Se deja entrar en 'generating', que es donde la API ya no puede cancelar.
        await Task.Delay(TimeSpan.FromSeconds(5));
        cts.Cancel();
        var r = await tarea;
        cron.Stop();

        Check("la cancelación corta de verdad", r.WasCancelled, r.ErrorMessage ?? "");
        Check("cancelar no deja la imagen escrita", !File.Exists(salida));
        Check("cancelar tarda mucho menos que generar", cron.Elapsed.TotalSeconds < 15,
            $"{cron.Elapsed.TotalSeconds:N1}s");

        // Tras matar y relanzar, el servidor tiene que volver solo. Si el relanzamiento
        // no esperara la salida real del proceso, acá aparecería el puerto ocupado.
        var volvio = await m.WaitForReadyAsync(TimeSpan.FromSeconds(30));
        Check("el motor vuelve a estar listo después de cancelar", volvio);

        if (volvio)
        {
            var r2 = await cli.GenerateAsync(new ImageGenRequest
            {
                Prompt = "a small stone house", Width = 512, Height = 512,
                Steps = 8, CfgScale = 1.0, Seed = 3
            }, Path.Combine(CarpetaSalida(), "despues-de-cancelar.png"));
            Check("se puede volver a generar después de cancelar", r2.Success, r2.ErrorMessage ?? "");
        }

        await m.StopAsync();
    }

    /// <summary>
    /// Modo hijo: levanta el servidor y espera a que lo maten. Escribe una línea a
    /// stdout cuando el servidor ya está arriba, para que el padre no adivine.
    /// </summary>
    static async Task<int> HijoQueMuere()
    {
        var m = new ImageServerManager();
        var error = await m.StartAsync(ConfigReal(1237));
        if (error is not null) { Console.WriteLine("ERROR " + error); return 1; }
        if (!await m.WaitForReadyAsync(TimeSpan.FromSeconds(60))) { Console.WriteLine("ERROR sin arrancar"); return 1; }

        Console.WriteLine("LISTO");
        Console.Out.Flush();
        await Task.Delay(TimeSpan.FromMinutes(5));   // el padre lo mata antes
        return 0;
    }

    static async Task CasoSinHuerfanos()
    {
        MatarTodo();
        await Task.Delay(500);
        var antes = ProcesosVivos();

        // Dispose() NO sirve para probar esto: mata el proceso explícitamente, así que
        // la aserción pasaría aunque no hubiera JobObject. Para ejercer de verdad "matar
        // la app a lo bruto" hace falta un proceso aparte al que se le corte la
        // corriente sin darle oportunidad de limpiar nada.
        var exe = Process.GetCurrentProcess().MainModule!.FileName;
        var dll = System.Reflection.Assembly.GetExecutingAssembly().Location;
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };
        // El banco corre como .exe propio o vía "dotnet <dll>" según cómo se lo invoque.
        if (Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            psi.ArgumentList.Add(dll);
        }
        psi.ArgumentList.Add("--hijo-que-muere");

        using var hijo = Process.Start(psi);
        if (hijo is null) { Check("no deja procesos huérfanos", false, "no se pudo lanzar el hijo"); return; }

        var arrancó = false;
        var cron = Stopwatch.StartNew();
        while (cron.Elapsed.TotalSeconds < 90)
        {
            var linea = await hijo.StandardOutput.ReadLineAsync();
            if (linea is null) break;
            if (linea.StartsWith("LISTO")) { arrancó = true; break; }
            if (linea.StartsWith("ERROR")) { Check("no deja procesos huérfanos", false, linea); return; }
        }

        Check("el hijo levanta su propio sd-server", arrancó && ProcesosVivos() > antes,
            $"antes {antes}, ahora {ProcesosVivos()}");
        if (!arrancó) { try { hijo.Kill(true); } catch { } return; }

        // Kill SIN el árbol de procesos: se mata solo al hijo, igual que haría el
        // administrador de tareas. Si sd-server sobrevive a esto, es que no está
        // dentro de ningún JobObject.
        hijo.Kill(entireProcessTree: false);
        hijo.WaitForExit(10000);

        // Windows tarda un instante en cerrar el JobObject y matar lo que contiene.
        cron.Restart();
        while (cron.Elapsed.TotalSeconds < 15 && ProcesosVivos() > antes)
            await Task.Delay(250);

        Check("matar la aplicación a lo bruto no deja sd-server huérfano",
            ProcesosVivos() == antes, $"antes {antes}, ahora {ProcesosVivos()}");

        MatarTodo();
    }

    static async Task CasoPuertoOcupadoOtroModelo()
    {
        MatarTodo();
        using var primero = new ImageServerManager();
        var e1 = await primero.StartAsync(ConfigReal());
        if (e1 is not null) { Check("puerto ocupado por otro modelo", false, e1); return; }
        if (!await primero.WaitForReadyAsync(TimeSpan.FromSeconds(60)))
        { Check("puerto ocupado por otro modelo", false, "no quedó listo"); return; }

        // Mismo puerto, mismo modelo: se reutiliza en lugar de fallar.
        using var mismo = new ImageServerManager();
        var e2 = await mismo.StartAsync(ConfigReal());
        Check("reutiliza una instancia con el mismo modelo", e2 is null, e2 ?? "");
        Check("la instancia reutilizada queda lista", mismo.CurrentStatus == ServerStatus.Ready);

        // Mismo puerto, otro modelo de difusión: NO se puede reutilizar, porque el
        // usuario recibiría imágenes de un modelo distinto del que eligió.
        //
        // El otro modelo no se carga nunca —el puerto ocupado se detecta antes—, así que
        // alcanza con un archivo vacío que tenga otro nombre. Hasta el 20 de octubre de
        // 2026 se usaba el Q3_K real, y al borrarlo la prueba pasó a omitirse.
        var dirOtro = Path.Combine(Path.GetTempPath(), "banco-imagenes-otro-modelo");
        Directory.CreateDirectory(dirOtro);
        var otro = Path.Combine(dirOtro, "z_image_turbo-Q3_K.gguf");
        File.WriteAllBytes(otro, Array.Empty<byte>());
        try
        {
            using var distinto = new ImageServerManager();
            var e3 = await distinto.StartAsync(ConfigReal() with { DiffusionModelPath = otro });
            Check("puerto ocupado por otro modelo lo dice", !string.IsNullOrWhiteSpace(e3));
            Check("el motivo nombra los dos modelos",
                e3 is not null && e3.Contains("Q3_K") && e3.Contains("Q4_K"), e3 ?? "");
        }
        finally
        {
            try { Directory.Delete(dirOtro, recursive: true); } catch { }
        }

        await primero.StopAsync();
    }
}
