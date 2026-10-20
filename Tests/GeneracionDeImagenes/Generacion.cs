using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PRImager.AIAssistant.Services;
using PRImager.AIAssistant.Services.ImageGen;
using PRImager.AIAssistant.ViewModels.ImageGen;

// Banco de E4.4: la vista de generación.
//
// La fase no agrega capacidades al motor —eso fue E4.1 a E4.3— sino que las cablea, así
// que lo que hay que verificar es distinto:
//
//   · que los valores de generación salgan del PAQUETE y no del motor;
//   · que la semilla que se guarda sea la que se usó, y nunca -1;
//   · que la metadata alcance para reproducir, incluidos los flags de ejecución;
//   · que el hilo de UI no se bloquee mientras genera, medido y no supuesto;
//   · que cancelar corte de verdad y que alternar diez veces no deje nada colgado.

/// <summary>
/// El detalle es opcional al comprobar, como en el resto del banco. Un
/// <c>Action&lt;string,bool,string&gt;</c> obliga a pasar los tres siempre.
/// </summary>
delegate void Comprobar(string nombre, bool cond, string detalle = "");

/// <summary>
/// Un SynchronizationContext de un solo hilo que cronometra cada continuación.
///
/// Es la herramienta que encontró el defecto 4.10 —la lectura de páginas corriendo sobre
/// el hilo de UI— y sirve igual acá: si alguna continuación de la generación tarda
/// cientos de milisegundos, en la aplicación real eso es la ventana congelada. Medirlo
/// es la única forma de saberlo; mirarlo en una máquina rápida no prueba nada.
/// </summary>
sealed class ContextoDeUnHilo : SynchronizationContext, IDisposable
{
    private readonly BlockingCollection<(SendOrPostCallback Cb, object? Estado)> _cola = new();
    private readonly Thread _hilo;

    public double MaxBloqueoMs { get; private set; }
    public double TotalBloqueoMs { get; private set; }
    public int Continuaciones { get; private set; }

    public ContextoDeUnHilo()
    {
        _hilo = new Thread(Bucle) { IsBackground = true, Name = "UI simulada" };
        _hilo.SetApartmentState(ApartmentState.STA);
        _hilo.Start();
    }

    private void Bucle()
    {
        SetSynchronizationContext(this);
        foreach (var (cb, estado) in _cola.GetConsumingEnumerable())
        {
            var cron = Stopwatch.StartNew();
            try { cb(estado); } catch (Exception) { /* lo informa el propio flujo */ }
            cron.Stop();

            Continuaciones++;
            TotalBloqueoMs += cron.Elapsed.TotalMilliseconds;
            if (cron.Elapsed.TotalMilliseconds > MaxBloqueoMs)
                MaxBloqueoMs = cron.Elapsed.TotalMilliseconds;
        }
    }

    public override void Post(SendOrPostCallback d, object? state) => _cola.Add((d, state));

    public override void Send(SendOrPostCallback d, object? state)
    {
        using var listo = new ManualResetEventSlim();
        _cola.Add((_ => { try { d(state); } finally { listo.Set(); } }, null));
        listo.Wait();
    }

    /// <summary>Corre algo EN el hilo simulado y espera el resultado.</summary>
    public T Invocar<T>(Func<T> f)
    {
        T r = default!;
        Send(_ => r = f(), null);
        return r;
    }

    public void Invocar(Action a) => Send(_ => a(), null);

    /// <summary>Lanza una tarea en el hilo simulado y devuelve la tarea real.</summary>
    public Task Lanzar(Func<Task> f)
    {
        var tcs = new TaskCompletionSource();
        Post(async _ =>
        {
            try { await f(); tcs.TrySetResult(); }
            catch (Exception ex) { tcs.TrySetException(ex); }
        }, null);
        return tcs.Task;
    }

    public void Dispose() => _cola.CompleteAdding();
}

static class BancoGeneracion
{
    static string NuevaCarpeta(string nombre)
    {
        var p = Path.Combine(Path.GetTempPath(), "BancoGeneracion", nombre);
        if (Directory.Exists(p)) Directory.Delete(p, true);
        Directory.CreateDirectory(p);
        return p;
    }

    static string Archivo(string carpeta, string nombre, int kb = 8)
    {
        var p = Path.Combine(carpeta, nombre);
        File.WriteAllBytes(p, new byte[kb * 1024]);
        return p;
    }

    /// <summary>
    /// ViewModel para las pruebas de lógica.
    ///
    /// La ruta del motor apunta a propósito a algo que no existe. Sin eso, con SD_RAIZ
    /// bien puesta estas pruebas levantarían sd-server de verdad contra los archivos de
    /// mentira del paquete y se quedarían un minuto esperando a que responda. Acá lo que
    /// se ejerce es el ViewModel, no el motor.
    /// </summary>
    static ImageGenerationViewModel NuevoVm(ImageModelService svc, string carpeta) =>
        new(svc, new ImageServerManager(), gpuInfo: null,
            enginePathResolver: () => Path.Combine(carpeta, "motor-que-no-existe.exe"),
            outputDirectory: Path.Combine(carpeta, "salidas"));

    /// <summary>Un paquete completo con archivos de mentira, para lo que no toca el motor.</summary>
    static (ImageModelService Servicio, ImageModelPackage Paquete) ServicioConPaquete(
        string carpeta, int pasos = 8, double cfg = 1.0)
    {
        var svc = new ImageModelService(Path.Combine(carpeta, "image_models.json"));
        var p = new ImageModelPackage
        {
            Id = "prueba",
            DisplayName = "Paquete de prueba",
            Family = "z-image",
            DefaultSteps = pasos,
            DefaultCfgScale = cfg,
            DiffusionModelPath = Archivo(carpeta, "dif.gguf"),
            TextEncoderPath = Archivo(carpeta, "te.gguf"),
            VaePath = Archivo(carpeta, "ae.safetensors")
        };
        svc.Save(p);
        svc.Select(p.Id);
        return (svc, svc.SelectedPackage!);
    }

    // ==================================================================== lógica

    public static void Correr(Action<string, bool, string> check)
    {
        Comprobar Check = (n, c, d) => check(n, c, d);

        CasoProgresoEnLasDosUnidades(Check);
        CasoMetadata(Check);
        CasoNombresDeArchivo(Check);
        CasoTamanos(Check);
        CasoValoresDelPaquete(Check);
        CasoSemilla(Check);
        CasoCuandoSePuedeGenerar(Check);
    }

    // ------------------------------------------------------------------ progreso

    /// <summary>
    /// El motor cambia la unidad del progreso según la velocidad: <c>s/it</c> cuando una
    /// iteración pasa del segundo, <c>it/s</c> cuando no llega. El formato en
    /// <c>stable-diffusion.dll</c> es <c>%s %i/%i - %s</c>, con la unidad como cadena.
    ///
    /// El patrón original solo aceptaba <c>s/it</c>, así que andaba a 1024×1024 —donde
    /// E4.0 midió 1,85 s/it— y perdía el progreso a 512×512, que va por debajo del
    /// segundo. **A 1024 no se ve; hay que probar el tamaño chico.** La aserción vive acá
    /// y no contra el motor porque una prueba que dependa de generar en cada tamaño es
    /// lenta y esconde el caso entre otros veinte.
    /// </summary>
    static void CasoProgresoEnLasDosUnidades(Comprobar Check)
    {
        // Iteración lenta: la unidad es s/it.
        var lenta = "  |======>                                          | 3/8 - 1.84s/it";
        Check("interpreta el progreso en s/it",
            ImageServerManager.TryParseProgress(lenta, out var a) &&
            a.Step == 3 && a.TotalSteps == 8, lenta);
        Check("con s/it el número es segundos por iteración",
            Math.Abs(a.SecondsPerIteration - 1.84) < 0.001, $"{a.SecondsPerIteration}");

        // Iteración rápida: la MISMA barra, con la unidad invertida.
        var rapida = "  |======>                                          | 3/8 - 1.27it/s";
        Check("interpreta el progreso en it/s",
            ImageServerManager.TryParseProgress(rapida, out var b) &&
            b.Step == 3 && b.TotalSteps == 8, rapida);
        Check("con it/s el número se invierte a segundos por iteración",
            Math.Abs(b.SecondsPerIteration - (1.0 / 1.27)) < 0.001, $"{b.SecondsPerIteration}");
        Check("las dos unidades dan la misma fracción de avance",
            Math.Abs(a.Fraction - b.Fraction) < 0.0001);
        Check("con it/s también se puede estimar el tiempo restante",
            b.EstimatedSecondsRemaining > 0, $"{b.EstimatedSecondsRemaining}");

        // La trampa que la unidad existe para evitar: el motor dibuja una barra casi
        // idéntica para la CARGA de tensores, con cientos de pasos y unidad GB/s. Si se
        // colara, el avance saltaría de 386 a 8 y la barra de la interfaz iría y volvería.
        var carga = "  |==================================================| 108/386 - 2.34GB/s";
        Check("la barra de carga de tensores NO es progreso de generación",
            !ImageServerManager.TryParseProgress(carga, out _), carga);

        // Aceptar cualquier unidad sería el arreglo fácil y equivocado, así que se
        // comprueba que siga rechazando lo que no es del muestreo.
        Check("una barra con otra unidad tampoco se toma por progreso",
            !ImageServerManager.TryParseProgress("|====| 5/9 - 3.10MB/s", out _));

        // Y la tercera barra, capturada de la salida real: la decodificación del VAE en
        // tiles. Es 7x7 = 49 y usa it/s, igual que un muestreo rápido. **El parser no
        // puede distinguirla, y esta aserción existe para dejarlo escrito**: la línea es
        // legítima y se interpreta bien; lo que decide si es muestreo o no es el número
        // de pasos que se pidió, y eso lo sabe el llamador, no stdout. Ver BeginJob.
        var tiles = "  |===============================================>  | 47/49 - 14.08it/s";
        Check("la barra del VAE en tiles se parsea igual que el muestreo",
            ImageServerManager.TryParseProgress(tiles, out var v) && v.TotalSteps == 49,
            "si esto cambia, revisar por qué el filtro de BeginJob sigue haciendo falta");
        Check("una línea cualquiera no se toma por progreso",
            !ImageServerManager.TryParseProgress("sampling completed in 19.4s", out _));
        Check("un total de cero no pasa", !ImageServerManager.TryParseProgress("| 0/0 - 1.0s/it", out _));
    }

    // ------------------------------------------------------------------ metadata

    static void CasoMetadata(Comprobar Check)
    {
        var dir = NuevaCarpeta("metadata");
        var (_, paquete) = ServicioConPaquete(dir);

        var config = paquete.ToServerConfig(@"C:\sd\sd-server.exe");
        var request = new ImageGenRequest
        {
            Prompt = "un zorro en la nieve",
            NegativePrompt = "borroso",
            Width = 832, Height = 1216, Steps = 8, CfgScale = 1.0, Seed = 12345,
            SampleMethod = "euler"
        };

        var md = ImageGenMetadata.For(request, paquete, config, 19.4);

        Check("la metadata guarda el prompt y el negativo",
            md.Prompt == "un zorro en la nieve" && md.NegativePrompt == "borroso");
        Check("la metadata guarda tamaño, pasos, CFG y semilla",
            md.Width == 832 && md.Height == 1216 && md.Steps == 8 &&
            Math.Abs(md.CfgScale - 1.0) < 0.001 && md.Seed == 12345);
        Check("la metadata nombra los tres archivos del paquete",
            md.Model.DiffusionModel == "dif.gguf" && md.Model.TextEncoder == "te.gguf" &&
            md.Model.Vae == "ae.safetensors",
            $"{md.Model.DiffusionModel} | {md.Model.TextEncoder} | {md.Model.Vae}");

        // Los tres flags que E4.0 midió que CAMBIAN la imagen. Sin ellos la metadata
        // promete una reproducibilidad que no da: son flags de rendimiento, que uno
        // esperaría que no afectaran el resultado, y afectan.
        Check("la metadata registra --diffusion-fa", md.Engine.FlashAttention);
        Check("la metadata registra --vae-tiling", md.Engine.VaeTiling);
        Check("la metadata registra el backend", !string.IsNullOrWhiteSpace(md.Engine.Backend));
        Check("la metadata registra --offload-to-cpu", md.Engine.OffloadToCpu);

        // La línea entera es la red de seguridad: si mañana se agrega una palanca, queda
        // registrada aunque nadie se acuerde de sumarla a los campos de arriba.
        Check("la metadata guarda la línea de comandos entera",
            md.Engine.CommandLine.Contains("--diffusion-fa") &&
            md.Engine.CommandLine.Contains("--vae-tiling"),
            md.Engine.CommandLine);

        // --- el build del motor (pendiente 6.6) ---
        // Cambia los bytes de la imagen y no aparece en ningún flag: la línea de comandos
        // no lo atrapa, por eso va aparte.
        Check("sin build conocido, la metadata dice desconocido y no queda vacía",
            md.Engine.Build == ImageEngineVersion.Unknown, md.Engine.Build);
        var conBuild = ImageGenMetadata.For(request, paquete, config, 19.4, "bfbef5b");
        Check("la metadata registra el build del motor", conBuild.Engine.Build == "bfbef5b");

        Check("el commit se lee de la salida real de --version",
            ImageEngineVersion.ParseCommit("stable-diffusion.cpp version unknown, commit bfbef5b") == "bfbef5b");
        Check("el commit se lee aunque venga en mayúsculas o con más líneas",
            ImageEngineVersion.ParseCommit("algo\r\nversion master-841, commit 6B3EDAA\r\n") == "6b3edaa");
        Check("sin commit en la salida no se inventa uno",
            ImageEngineVersion.ParseCommit("stable-diffusion.cpp version unknown") is null &&
            ImageEngineVersion.ParseCommit("") is null && ImageEngineVersion.ParseCommit(null) is null);

        Check("un motor que no existe da desconocido, sin lanzar",
            ImageEngineVersion.ReadAsync(Path.Combine(dir, "no-esta.exe")).GetAwaiter().GetResult()
                == ImageEngineVersion.Unknown);

        var motor = Path.Combine(
            Environment.GetEnvironmentVariable("SD_RAIZ") ?? @"D:\PROYECTOS\PR-Tools\sd",
            "bin", "sd-server.exe");
        if (File.Exists(motor))
        {
            var build = ImageEngineVersion.ReadAsync(motor).GetAwaiter().GetResult();
            Check("el motor instalado informa su commit",
                build != ImageEngineVersion.Unknown && build.Length >= 7, build);
        }

        // --- ida y vuelta contra el disco ---
        var png = Path.Combine(dir, "imagen.png");
        File.WriteAllBytes(png, new byte[64]);
        var sidecar = md.WriteAsync(png).GetAwaiter().GetResult();

        Check("el sidecar se escribe al lado de la imagen",
            sidecar is not null && File.Exists(sidecar) &&
            Path.GetDirectoryName(sidecar) == Path.GetDirectoryName(png), sidecar ?? "null");
        Check("el sidecar es el .json del mismo nombre",
            sidecar == Path.Combine(dir, "imagen.json"), sidecar ?? "null");

        var releido = ImageGenMetadata.Read(sidecar!);
        Check("la metadata se puede volver a leer", releido is not null);
        Check("el prompt sobrevive al ida y vuelta con acentos",
            releido is not null && releido.Prompt == "un zorro en la nieve", releido?.Prompt ?? "");
        Check("los flags sobreviven al ida y vuelta",
            releido is not null && releido.Engine.FlashAttention && releido.Engine.VaeTiling);
        Check("la semilla sobrevive al ida y vuelta",
            releido is not null && releido.Seed == 12345, $"{releido?.Seed}");

        // Que el archivo no esté vacío ANTES de afirmar sobre su contenido: una aserción
        // negativa sobre una fuente vacía siempre pasa, y eso ya escondió un defecto en
        // el banco de la carga de archivos.
        var texto = File.ReadAllText(sidecar!);
        Check("el sidecar no está vacío", texto.Length > 200, $"{texto.Length} bytes");
        Check("el sidecar es legible a ojo y nombra los flags",
            texto.Contains("flash_attention") && texto.Contains("vae_tiling") &&
            texto.Contains("command_line"));
        var textoConBuild = File.ReadAllText(conBuild.WriteAsync(png).GetAwaiter().GetResult()!);
        Check("el build llega al archivo",
            textoConBuild.Contains("\"build\": \"bfbef5b\""), textoConBuild.Length.ToString());
        Check("el build sobrevive al ida y vuelta",
            ImageGenMetadata.Read(ImageGenMetadata.SidecarPathFor(png))?.Engine.Build == "bfbef5b");
    }

    static void CasoNombresDeArchivo(Comprobar Check)
    {
        var dir = NuevaCarpeta("nombres");
        var momento = new DateTime(2026, 8, 9, 14, 30, 15);

        var a = ImageOutputFolder.BuildPath(dir, 777, momento);
        Check("el nombre lleva fecha, hora y semilla",
            Path.GetFileName(a) == "20260809-143015-777.png", Path.GetFileName(a));

        File.WriteAllBytes(a, new byte[16]);
        var b = ImageOutputFolder.BuildPath(dir, 777, momento);
        Check("dos imágenes en el mismo segundo no se pisan", a != b, $"{a} == {b}");

        File.WriteAllBytes(b, new byte[16]);
        var c = ImageOutputFolder.BuildPath(dir, 777, momento);
        Check("y una tercera tampoco", c != a && c != b, Path.GetFileName(c));

        // El prompt no entra en el nombre: puede ser larguísimo y traer caracteres que
        // Windows rechaza. Está en el sidecar, que es donde se lo busca.
        Check("el nombre no depende del prompt", !Path.GetFileName(a).Contains(' '));
    }

    // ------------------------------------------------------------------ tamaños

    static void CasoTamanos(Comprobar Check)
    {
        var dir = NuevaCarpeta("tamanos");
        var (svc, _) = ServicioConPaquete(dir);
        using var vm = NuevoVm(svc, dir);

        var combinaciones = (from t in vm.SizeOptions
                             from o in vm.Orientations
                             select (t, o)).ToList();

        Check("ofrece tres tamaños y tres orientaciones", combinaciones.Count == 9,
            $"{combinaciones.Count}");

        bool todosMultiplosDe64 = true, todosBajoElTope = true;
        foreach (var (t, o) in combinaciones)
        {
            vm.SelectedSize = t;
            vm.SelectedOrientation = o;
            if (vm.Width % 64 != 0 || vm.Height % 64 != 0) todosMultiplosDe64 = false;
            if (Math.Max(vm.Width, vm.Height) > ImageVramArbiter.MaxSafeDimension) todosBajoElTope = false;
        }

        Check("todos los tamaños son múltiplos de 64", todosMultiplosDe64);

        // Ninguno puede acercarse a 4096, que mata el proceso con un error de CUDA y
        // pasa igual con la GPU vacía. No hay campo libre, así que la interfaz no puede
        // llegar ahí; el cliente lo comprueba igual, que es lo que verifica E4.1.
        Check("ningún preset supera el tope de 3072", todosBajoElTope);

        vm.SelectedSize = vm.SizeOptions.First(s => s.Side == 1024);
        vm.SelectedOrientation = vm.Orientations.First(o => o.Value == ImageOrientation.Square);
        Check("cuadrada a 1024 da 1024×1024", vm.Width == 1024 && vm.Height == 1024, vm.SizeText);

        vm.SelectedOrientation = vm.Orientations.First(o => o.Value == ImageOrientation.Vertical);
        var (av, hv) = (vm.Width, vm.Height);
        Check("vertical es más alta que ancha", hv > av, vm.SizeText);

        vm.SelectedOrientation = vm.Orientations.First(o => o.Value == ImageOrientation.Horizontal);
        Check("horizontal es el espejo de vertical", vm.Width == hv && vm.Height == av, vm.SizeText);

        Check("el texto del tamaño acompaña a la selección", vm.SizeText == $"{vm.Width} × {vm.Height}",
            vm.SizeText);

        // El tamaño es una decisión de tiempo, no de memoria: con tiling, 512 y 1024
        // difieren en 216 MiB de VRAM y en el triple de tiempo. La interfaz tiene que
        // decirlo, porque la intuición del usuario dice lo contrario.
        Check("la ayuda del tamaño habla de tiempo y no solo de memoria",
            vm.SizeHint.Contains("tiempo"), vm.SizeHint);
    }

    // --------------------------------------------------- los valores del paquete

    static void CasoValoresDelPaquete(Comprobar Check)
    {
        var dir = NuevaCarpeta("valores");
        var (svc, _) = ServicioConPaquete(dir, pasos: 8, cfg: 1.0);
        using var vm = NuevoVm(svc, dir);

        // Los defaults del MOTOR son 20 pasos y CFG 7.0, y con esos un Turbo destilado
        // produce una imagen mala. Los valores tienen que venir del paquete y pisar a
        // los del endpoint.
        Check("los pasos salen del paquete, no del motor", vm.Steps == 8, $"{vm.Steps}");
        Check("el CFG sale del paquete, no del motor", Math.Abs(vm.CfgScale - 1.0) < 0.001,
            $"{vm.CfgScale}");
        Check("no toma los 20 pasos del motor", vm.Steps != 20, $"{vm.Steps}");
        Check("no toma el CFG 7 del motor", Math.Abs(vm.CfgScale - 7.0) > 0.001, $"{vm.CfgScale}");

        // Un paquete de otra familia trae otros valores, y cambiarlo tiene que arrastrarlos:
        // dejar puestos los del anterior es un error silencioso que sale en la imagen.
        svc.Save(new ImageModelPackage
        {
            Id = "otro", DisplayName = "Otro paquete", Family = "sdxl",
            DefaultSteps = 30, DefaultCfgScale = 6.5,
            DiffusionModelPath = Archivo(dir, "dif2.gguf"),
            TextEncoderPath = Archivo(dir, "te2.gguf"),
            VaePath = Archivo(dir, "ae2.safetensors")
        });
        svc.Select("otro");
        vm.ApplyPackageDefaults();

        Check("cambiar de paquete arrastra sus pasos", vm.Steps == 30, $"{vm.Steps}");
        Check("cambiar de paquete arrastra su CFG", Math.Abs(vm.CfgScale - 6.5) < 0.001,
            $"{vm.CfgScale}");
        Check("el texto del paquete nombra el que está en uso",
            vm.PackageText.Contains("Otro paquete"), vm.PackageText);

        // El selector de paquetes es OTRA ventana y no le avisa a esta: si el usuario
        // cambia de paquete y vuelve, la vista usaría el modelo nuevo con los pasos del
        // viejo. Eso no da ningún error, sale en la imagen. Se ejerce sin motor a
        // propósito: la generación va a fallar por falta de sd-server, y lo que se
        // verifica es que los valores ya se hayan rehecho ANTES de llegar ahí.
        svc.Select("prueba");                 // vuelve al de 8 pasos, por afuera del VM
        vm.Prompt = "un zorro";
        vm.GenerateCommand.Execute(null);
        for (int i = 0; i < 100 && vm.IsGenerating; i++) Thread.Sleep(20);

        Check("cambiar de paquete por afuera rehace los pasos antes de generar",
            vm.Steps == 8, $"{vm.Steps}");
        Check("y el CFG también", Math.Abs(vm.CfgScale - 1.0) < 0.001, $"{vm.CfgScale}");
        Check("sin motor instalado la generación avisa en lugar de romper",
            vm.StatusMessage.Contains("motor"), vm.StatusMessage);

        // Los samplers arrancan con la opción vacía —el que elija el motor— y las listas
        // reales las trae /capabilities al arrancar, no una lista hardcodeada.
        Check("el sampler arranca en «el que elija el motor»", vm.SelectedSampler == "",
            vm.SelectedSampler);
        Check("la lista de samplers arranca con la opción vacía",
            vm.Samplers.Count == 1 && vm.Samplers[0] == "", $"{vm.Samplers.Count}");
    }

    // ------------------------------------------------------------------ semilla

    static void CasoSemilla(Comprobar Check)
    {
        var dir = NuevaCarpeta("semilla");
        var (svc, _) = ServicioConPaquete(dir);
        using var vm = NuevoVm(svc, dir);

        Check("la semilla aleatoria viene puesta de fábrica", vm.UseRandomSeed);
        Check("con semilla aleatoria el campo no se edita", !vm.CanEditSeed);

        vm.UseRandomSeed = false;
        Check("al fijar la semilla el campo se habilita", vm.CanEditSeed);

        // El punto de todo esto: el motor acepta -1 para pedir semilla aleatoria pero NO
        // informa cuál usó —la respuesta del job no trae el campo—, así que la sortea la
        // aplicación. Se ejerce por reflexión porque el sorteo es privado: lo que
        // importa es que ningún camino produzca -1.
        var metodo = typeof(ImageGenerationViewModel).GetMethod("ResolverSemilla",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        if (metodo is null)
        {
            Check("se puede ejercer el sorteo de semilla", false, "no está ResolverSemilla");
            return;
        }

        vm.UseRandomSeed = true;
        var sorteadas = Enumerable.Range(0, 50).Select(_ => (long)metodo.Invoke(vm, null)!).ToList();

        Check("la semilla aleatoria nunca es -1", sorteadas.All(s => s != -1),
            string.Join(",", sorteadas.Where(s => s == -1).Take(3)));
        Check("la semilla aleatoria nunca es negativa", sorteadas.All(s => s >= 0));
        Check("la semilla aleatoria cambia entre generaciones",
            sorteadas.Distinct().Count() > 40, $"{sorteadas.Distinct().Count()} distintas de 50");

        vm.UseRandomSeed = false;
        vm.Seed = 4242;
        Check("con semilla fija se usa exactamente la pedida",
            (long)metodo.Invoke(vm, null)! == 4242);
    }

    // ------------------------------------------------- cuándo se puede generar

    static void CasoCuandoSePuedeGenerar(Comprobar Check)
    {
        var dir = NuevaCarpeta("puede");
        var svc = new ImageModelService(Path.Combine(dir, "image_models.json"));

        // --- sin paquete seleccionado ---
        using (var vm = NuevoVm(svc, dir))
        {
            vm.Prompt = "un zorro";
            Check("sin paquete seleccionado no se genera", !vm.CanGenerate);
            Check("y el motivo se dice en pantalla",
                vm.PackageText.Contains("paquete"), vm.PackageText);
        }

        // --- con paquete incompleto ---
        var incompleto = new ImageModelPackage
        {
            Id = "incompleto", DisplayName = "Incompleto",
            DiffusionModelPath = Archivo(dir, "dif.gguf"),
            TextEncoderPath = Path.Combine(dir, "no-esta.gguf"),
            VaePath = Path.Combine(dir, "tampoco.safetensors")
        };
        svc.Save(incompleto);
        Check("un paquete incompleto no se puede ni seleccionar", !svc.Select("incompleto"));

        // --- con paquete completo ---
        var dir2 = NuevaCarpeta("puede-completo");
        var (svc2, _) = ServicioConPaquete(dir2);
        using var vm2 = NuevoVm(svc2, dir2);

        // El control POSITIVO va primero: sin él, «prompt vacío no genera» podría estar
        // pasando porque nunca se puede generar, que es otra cosa. Es la trampa que ya
        // apareció tres veces en E4 —una aserción que pasa por el motivo equivocado— y
        // la forma de desarmarla es dejar un solo camino disponible.
        vm2.Prompt = "un zorro en la nieve";
        Check("con paquete completo y prompt sí se puede generar", vm2.CanGenerate);
        Check("y el comando está habilitado", vm2.GenerateCommand.CanExecute(null));

        vm2.Prompt = "";
        Check("prompt vacío no genera", !vm2.CanGenerate);
        Check("prompt en blanco tampoco genera", !SePuedeCon(vm2, "    "));
        Check("un salto de línea solo tampoco genera", !SePuedeCon(vm2, "\n\n"));
        Check("el botón de generar queda deshabilitado sin prompt",
            !vm2.GenerateCommand.CanExecute(null));

        vm2.Prompt = "vuelve a haber prompt";
        Check("y vuelve a habilitarse al escribir", vm2.CanGenerate);

        // Cancelar sin nada en curso no debe hacer nada ni romper.
        Check("cancelar sin generación en curso no está habilitado",
            !vm2.CancelCommand.CanExecute(null));

        static bool SePuedeCon(ImageGenerationViewModel vm, string prompt)
        {
            vm.Prompt = prompt;
            return vm.CanGenerate;
        }
    }

    // ============================================== contra el motor de verdad

    /// <summary>
    /// Genera una imagen a través del ViewModel, midiendo el bloqueo del hilo de UI.
    ///
    /// Corre sobre un SynchronizationContext de un solo hilo que cronometra cada
    /// continuación: es la única forma de verificar «la ventana no se congela durante la
    /// generación» sin creerle a la impresión de que se ve fluida.
    /// </summary>
    public static async Task CorrerContraElMotor(
        Action<string, bool, string> check, string exe, ImageModelPackage paqueteReal)
    {
        Comprobar Check = (n, c, d) => check(n, c, d);

        var dir = NuevaCarpeta("real");
        var salidas = Path.Combine(dir, "imagenes");
        var svc = new ImageModelService(Path.Combine(dir, "image_models.json"));
        svc.Save(paqueteReal);
        svc.Select(paqueteReal.Id);

        using var contexto = new ContextoDeUnHilo();

        // El ViewModel captura el contexto al construirse, así que hay que construirlo
        // EN el hilo simulado o el progreso no se marshalearía por donde se mide.
        var vm = contexto.Invocar(() => new ImageGenerationViewModel(
            svc, manager: null, gpuInfo: null,
            enginePathResolver: () => exe, outputDirectory: salidas));

        try
        {
            await contexto.Lanzar(() => vm.InitializeAsync());

            Check("la vista informa la GPU al inicializarse",
                !string.IsNullOrWhiteSpace(vm.GpuText), vm.GpuText);

            // --- una generación completa ---
            contexto.Invocar(() =>
            {
                vm.Prompt = "a red fox in a snowy forest, morning light, photorealistic";
                vm.UseRandomSeed = true;
                vm.SelectedSize = vm.SizeOptions.First(s => s.Side == 512);
                vm.SelectedOrientation = vm.Orientations.First(o => o.Value == ImageOrientation.Square);
            });

            var progresosVistos = new ConcurrentBag<double>();
            var textosDeProgreso = new ConcurrentBag<string>();
            void Espiar(object? _, System.ComponentModel.PropertyChangedEventArgs e)
            {
                if (e.PropertyName == nameof(vm.ProgressPercent)) progresosVistos.Add(vm.ProgressPercent);
                if (e.PropertyName == nameof(vm.ProgressText)) textosDeProgreso.Add(vm.ProgressText);
            }
            vm.PropertyChanged += Espiar;

            var cron = Stopwatch.StartNew();
            await contexto.Lanzar(() => EjecutarGenerar(vm));
            cron.Stop();
            vm.PropertyChanged -= Espiar;

            Check("genera una imagen desde la vista", vm.Gallery.Count == 1,
                $"{vm.Gallery.Count} en galería · {vm.StatusMessage}");

            if (vm.Gallery.Count == 1)
            {
                var item = vm.Gallery[0];

                Check("el PNG existe en la carpeta de salida", File.Exists(item.ImagePath),
                    item.ImagePath);
                Check("el PNG está en la carpeta configurada, no en otra",
                    Path.GetDirectoryName(item.ImagePath) == salidas,
                    Path.GetDirectoryName(item.ImagePath) ?? "");

                var bytes = File.Exists(item.ImagePath) ? File.ReadAllBytes(item.ImagePath) : Array.Empty<byte>();
                Check("es un PNG de verdad y no un archivo vacío",
                    bytes.Length > 50_000 && bytes[0] == 0x89 && bytes[1] == 0x50,
                    $"{bytes.Length} bytes");

                Check("la imagen queda como la actual", vm.CurrentImage == item && vm.HasCurrentImage);
                Check("la galería trae miniatura y vista previa",
                    item.Thumbnail is not null && item.Preview is not null);

                // --- la metadata, que es el criterio de la fase ---
                Check("el sidecar de metadata existe", File.Exists(item.SidecarPath), item.SidecarPath);
                var md = File.Exists(item.SidecarPath) ? ImageGenMetadata.Read(item.SidecarPath) : null;
                Check("la metadata se lee", md is not null);

                if (md is not null)
                {
                    Check("la metadata guarda el prompt que se pidió",
                        md.Prompt.Contains("red fox"), md.Prompt);
                    // El cableado del 6.6: que el ViewModel le pregunte el build al motor
                    // de verdad y no se quede con el valor por defecto.
                    Check("la metadata de la vista guarda el build del motor",
                        md.Engine.Build != ImageEngineVersion.Unknown && md.Engine.Build.Length >= 7,
                        md.Engine.Build);

                    // Lo que hace reproducible a la imagen: la semilla concreta. Con -1
                    // el archivo diría «aleatoria» y no habría forma de volver a sacarla.
                    Check("la metadata guarda una semilla concreta, nunca -1",
                        md.Seed >= 0, $"{md.Seed}");
                    Check("la semilla guardada es la que se usó de verdad",
                        Path.GetFileNameWithoutExtension(item.ImagePath).EndsWith(md.Seed.ToString()),
                        $"{Path.GetFileName(item.ImagePath)} vs {md.Seed}");

                    Check("la metadata guarda el tamaño realmente pedido",
                        md.Width == 512 && md.Height == 512, $"{md.Width}×{md.Height}");
                    Check("la metadata guarda los flags que cambian la imagen",
                        md.Engine.FlashAttention && md.Engine.VaeTiling &&
                        !string.IsNullOrWhiteSpace(md.Engine.Backend));
                    Check("la metadata mide cuánto tardó", md.ElapsedSeconds > 0,
                        $"{md.ElapsedSeconds}");
                }
            }

            // --- el progreso, que sale de stdout y no de la API ---
            var picos = progresosVistos.ToList();
            Check("la vista recibe progreso durante la generación", picos.Count > 0,
                $"{picos.Count} avisos");
            Check("el progreso avanza y llega cerca del final",
                picos.Count > 0 && picos.Max() > 80, $"máximo {picos.DefaultIfEmpty(0).Max():N0}%");
            Check("el progreso se expresa en pasos, no en porcentaje pelado",
                textosDeProgreso.Any(t => t.Contains("Paso")),
                string.Join(" | ", textosDeProgreso.Take(3)));

            // --- el hilo de UI, medido ---
            Console.WriteLine($"    bloqueo del hilo de UI: máximo {contexto.MaxBloqueoMs:N0} ms, " +
                              $"total {contexto.TotalBloqueoMs:N0} ms en {contexto.Continuaciones} " +
                              $"continuaciones, sobre {cron.Elapsed.TotalSeconds:N1} s de generación");

            // Windows marca una ventana como «no responde» al segundo sin atender el
            // mensaje. 250 ms deja margen y sigue siendo detectable: si la decodificación
            // de la miniatura volviera al hilo de UI, un PNG grande se pasa de largo.
            Check("el hilo de UI nunca se bloquea más de 250 ms",
                contexto.MaxBloqueoMs < 250, $"máximo {contexto.MaxBloqueoMs:N0} ms");
            Check("el hilo de UI está ocioso casi todo el tiempo",
                contexto.TotalBloqueoMs < cron.Elapsed.TotalMilliseconds * 0.10,
                $"{contexto.TotalBloqueoMs:N0} ms de {cron.Elapsed.TotalMilliseconds:N0} ms");

            // --- prompt vacío contra el motor listo ---
            // Con el servidor arriba, lo único que puede frenar la generación es la
            // validación: sin motor pasaría por el motivo equivocado.
            contexto.Invocar(() => vm.Prompt = "   ");
            Check("con el motor listo, un prompt vacío sigue sin generar", !vm.CanGenerate);
            contexto.Invocar(() => vm.Prompt = "a small stone house");
        }
        finally
        {
            await vm.ShutdownAsync();
            vm.Dispose();
        }
    }

    /// <summary>
    /// Ejecuta el comando y espera a que termine de verdad. AsyncRelayCommand.Execute es
    /// `async void`, así que sin esto la prueba seguiría de largo mientras genera.
    /// </summary>
    static async Task EjecutarGenerar(ImageGenerationViewModel vm)
    {
        vm.GenerateCommand.Execute(null);
        while (vm.IsGenerating) await Task.Delay(100);
    }

    /// <summary>
    /// Cancelar corta de verdad, y alternar diez veces no degrada ni deja procesos
    /// colgados.
    ///
    /// El criterio de las diez alternancias es de E4.2 y quedó sin cumplir: se había
    /// ejercido un ciclo suelto de cancelar y regenerar, no diez seguidos. Corresponde
    /// acá, que es cuando el usuario puede provocarlo apretando el botón.
    /// </summary>
    public static async Task CorrerCancelaciones(
        Action<string, bool, string> check, string exe, ImageModelPackage paqueteReal, int vueltas = 10)
    {
        Comprobar Check = (n, c, d) => check(n, c, d);

        var dir = NuevaCarpeta("cancelaciones");
        var salidas = Path.Combine(dir, "imagenes");
        var svc = new ImageModelService(Path.Combine(dir, "image_models.json"));
        svc.Save(paqueteReal);
        svc.Select(paqueteReal.Id);

        int vivosAntes = Process.GetProcessesByName("sd-server").Length;

        using var vm = new ImageGenerationViewModel(
            svc, manager: null, gpuInfo: null,
            enginePathResolver: () => exe, outputDirectory: salidas);

        await vm.InitializeAsync();

        vm.Prompt = "a red fox in a snowy forest";
        vm.SelectedSize = vm.SizeOptions.First(s => s.Side == 1024);
        vm.Steps = 20;   // largo a propósito: hay que llegar a cancelar en pleno muestreo

        var duraciones = new System.Collections.Generic.List<double>();
        bool todasCancelaron = true;
        bool ningunaEscribio = true;

        for (int i = 1; i <= vueltas; i++)
        {
            var cron = Stopwatch.StartNew();
            vm.GenerateCommand.Execute(null);

            // Se espera a estar generando de verdad antes de cancelar: cancelar mientras
            // el motor todavía arranca probaría otra cosa.
            var espera = Stopwatch.StartNew();
            while (vm.ProgressPercent <= 0 && espera.Elapsed.TotalSeconds < 90 && vm.IsGenerating)
                await Task.Delay(100);

            bool llegóAMuestrear = vm.ProgressPercent > 0;

            vm.CancelCommand.Execute(null);
            while (vm.IsGenerating) await Task.Delay(100);
            cron.Stop();

            duraciones.Add(cron.Elapsed.TotalSeconds);

            // Se exige el mensaje EXACTO de cancelación, no que contenga «cancel».
            //
            // La primera versión buscaba la subcadena y pasaba sobre «Error mientras se
            // generaba la imagen: The operation was canceled.», que contiene «cancel» y
            // es un error en inglés: la OperationCanceledException se escapaba al catch
            // genérico cuando el cancelar coincidía con la consulta de estado en vuelo.
            // Una aserción laxa dio por bueno exactamente el caso que tenía que delatar.
            if (vm.StatusMessage != "Generación cancelada.")
                todasCancelaron = false;
            if (vm.Gallery.Count > 0)
                ningunaEscribio = false;

            Console.WriteLine($"    vuelta {i,2}: {cron.Elapsed.TotalSeconds,5:N1} s" +
                              $"  muestreando={llegóAMuestrear}  «{vm.StatusMessage}»");
        }

        Check($"cancelar {vueltas} veces seguidas siempre cancela", todasCancelaron, vm.StatusMessage);
        Check("cancelar nunca deja una imagen a medias en la galería", ningunaEscribio,
            $"{vm.Gallery.Count} en galería");
        Check("cancelar nunca deja archivos .part",
            !Directory.Exists(salidas) || !Directory.EnumerateFiles(salidas, "*.part").Any());

        // No degrada: la última vuelta no puede tardar mucho más que la primera. Si el
        // motor fuera quedando lento —o quedaran servidores acumulados peleando por la
        // GPU— acá se vería.
        if (duraciones.Count >= 3)
        {
            var primeras = duraciones.Take(3).Average();
            var ultimas = duraciones.TakeLast(3).Average();
            Check("alternar diez veces no degrada el tiempo de cancelación",
                ultimas < primeras * 2 + 3,
                $"primeras {primeras:N1} s, últimas {ultimas:N1} s");
        }

        // Después de las diez, el motor tiene que seguir sirviendo.
        vm.Steps = 8;
        vm.SelectedSize = vm.SizeOptions.First(s => s.Side == 512);
        await EjecutarGenerar(vm);
        Check("después de diez cancelaciones todavía se puede generar",
            vm.Gallery.Count == 1, vm.StatusMessage);

        // --- cerrar la vista cancela y baja el motor ---
        vm.Prompt = "a stone house in the mountains";
        vm.Steps = 20;
        vm.GenerateCommand.Execute(null);

        var esperaCierre = Stopwatch.StartNew();
        while (vm.ProgressPercent <= 0 && esperaCierre.Elapsed.TotalSeconds < 90 && vm.IsGenerating)
            await Task.Delay(100);

        await vm.ShutdownAsync();

        Check("cerrar la vista con una generación en curso la corta", !vm.IsGenerating,
            vm.StatusMessage);

        // Que ShutdownAsync espere la cancelación no es un detalle: cancelar RELANZA el
        // servidor, así que parar sin esperar dejaría el relanzamiento ganando la carrera
        // y un sd-server vivo después de cerrar la vista.
        var cronMuerte = Stopwatch.StartNew();
        while (cronMuerte.Elapsed.TotalSeconds < 15 &&
               Process.GetProcessesByName("sd-server").Length > vivosAntes)
            await Task.Delay(250);

        Check("cerrar la vista no deja sd-server corriendo",
            Process.GetProcessesByName("sd-server").Length <= vivosAntes,
            $"antes {vivosAntes}, ahora {Process.GetProcessesByName("sd-server").Length}");

        Check("cerrar dos veces no rompe", await CerrarDeNuevo(vm));

        static async Task<bool> CerrarDeNuevo(ImageGenerationViewModel vm)
        {
            try { await vm.ShutdownAsync(); return true; }
            catch (Exception) { return false; }
        }
    }
}
