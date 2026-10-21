using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using PRImager.AIAssistant.Services;
using PRImager.AIAssistant.Services.ImageGen;
using PRImager.AIAssistant.Services.Models;
using PRImager.AIAssistant.ViewModels;
using PRImager.AIAssistant.ViewModels.ImageGen;

// Banco de E4.5: integración.
//
// La fase no agrega capacidades: le da al usuario una forma de llegar a las que ya
// existen, y le pone la llave del perfil. Lo que hay que verificar es la llave, no la
// puerta:
//
//   · que el Modo Investigación apague la generación de imágenes, como a web y descargas;
//   · que la edición institucional la apague SIEMPRE, por compilación y no por
//     configuración — y eso se prueba contra el binario Forense de verdad;
//   · que la bandera sobreviva al guardado, al clonado y al reinicio;
//   · que la barra lateral refleje las dos barreras, cada una a su manera.

static class BancoIntegracion
{
    static string NuevaCarpeta(string nombre)
    {
        var p = Path.Combine(Path.GetTempPath(), "BancoIntegracion", nombre);
        if (Directory.Exists(p)) Directory.Delete(p, true);
        Directory.CreateDirectory(p);
        return p;
    }

    public static UsageProfile Perfil(bool imagenes) => new()
    {
        Id = "banco-imagenes",
        Name = "General de prueba",
        SystemPrompt = "Sos un asistente.",
        Temperature = 0.7,
        ImageGenEnabled = imagenes,
        FileUploadEnabled = true
    };

    /// <summary>
    /// Un MainViewModel real, sin arrancar llama-server: para saber si un botón está
    /// habilitado no hace falta inferencia. Lo comparte la prueba de humo.
    /// </summary>
    public static MainViewModel NuevoMainViewModel(string dir, OperationMode modo, UsageProfile perfil)
    {
        Directory.CreateDirectory(Path.Combine(dir, "Logs"));

        var config = new ConfigManager(Path.Combine(dir, "settings.json"));
        config.Save(new AppSettings { ModelPath = dir, LogsPath = Path.Combine(dir, "Logs") });

        var orq = new ChatOrchestrator(
            new LlamaServerManager(),
            new LlamaChatClient("http://127.0.0.1:59998"),
            new ForensicLogger(Path.Combine(dir, "Logs")),
            config);

        var perfiles = new UsageProfileService(Path.Combine(dir, "profiles.json"));
        perfiles.Load();

        var vm = new MainViewModel(orq, new DialogosFalsos(), config,
            new ModelProfileService(Path.Combine(dir, "catalogo.json"), dir), perfiles);

        vm.CurrentMode = modo;
        vm.SelectedUsageProfile = perfil;
        return vm;
    }

    public static string CarpetaParaHumo() => NuevaCarpeta("humo-barra");

    public static void Correr(Action<string, bool, string> check)
    {
        Comprobar Check = (n, c, d) => check(n, c, d);

        CasoPrecedencia(Check);
        CasoPerfilesDeFabrica(Check);
        CasoPersistencia(Check);
        CasoRutaDelMotor(Check);
        CasoViewModelDelChat(Check);
        EdicionForenseEnUnHijo(Check);
    }

    // ---------------------------------------------------- el hijo que mira Forense

    /// <summary>
    /// Corre la comprobación del binario institucional **en otro proceso**.
    ///
    /// No es prolijidad: cargar el ensamblado Forense en este proceso rompe las pruebas
    /// de humo que vienen después. Los dos binarios se llaman `PR-AI-Assistant` y tienen
    /// la misma versión, así que con los dos en memoria WPF resuelve mal los pack URI y
    /// `InitializeComponent` falla con «No se encuentra el recurso
    /// views/imagegen/imagepackageselectorwindow.xaml» — que efectivamente no está en el
    /// Forense. El síntoma aparece en una ventana que no tiene nada que ver, y no señala
    /// la causa.
    ///
    /// Un AssemblyLoadContext colectible no alcanza: aísla los tipos, pero la descarga es
    /// diferida y WPF ya se confundió. Un proceso aparte sí, y es el mismo recurso que ya
    /// usa este banco para probar que no quedan huérfanos.
    /// </summary>
    static void EdicionForenseEnUnHijo(Comprobar Check)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = System.Diagnostics.Process.GetCurrentProcess().MainModule!.FileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };

        // El banco corre como .exe propio o vía "dotnet <dll>" según cómo se lo invoque.
        if (Path.GetFileNameWithoutExtension(psi.FileName)
                .Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            psi.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        }
        psi.ArgumentList.Add(ArgumentoDelHijo);

        try
        {
            using var hijo = System.Diagnostics.Process.Start(psi);
            if (hijo is null) { Check("se puede lanzar la prueba del binario Forense", false); return; }

            int leidas = 0;
            string? linea;
            while ((linea = hijo.StandardOutput.ReadLine()) is not null)
            {
                var partes = linea.Split('|');
                if (partes.Length < 2 || (partes[0] != "OK" && partes[0] != "FALLA")) continue;
                Check(partes[1], partes[0] == "OK", partes.Length > 2 ? partes[2] : "");
                leidas++;
            }
            hijo.WaitForExit(30000);

            // Si el hijo se cayó antes de comprobar nada, el silencio no puede leerse
            // como éxito: sin esta guarda, un hijo que explota al arrancar dejaría la
            // edición Forense sin verificar y el banco en verde.
            Check("el proceso hijo informó sus comprobaciones", leidas >= 5, $"{leidas} líneas");
        }
        catch (Exception ex)
        {
            Check("se puede lanzar la prueba del binario Forense", false, ex.Message);
        }
    }

    public const string ArgumentoDelHijo = "--hijo-forense";

    /// <summary>Punto de entrada del hijo: imprime una línea por comprobación.</summary>
    public static int CorrerComoHijo()
    {
        int fallas = 0;
        Comprobar Check = (nombre, cond, detalle) =>
        {
            if (!cond) fallas++;
            Console.WriteLine($"{(cond ? "OK" : "FALLA")}|{nombre}|{detalle.Replace('|', '/')}");
        };

        CasoEdicionForense(Check);
        Console.Out.Flush();
        return fallas == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------- precedencia

    static void CasoPrecedencia(Comprobar Check)
    {
        var conImagenes = Perfil(true);
        var sinImagenes = Perfil(false);

        var consulta = EffectiveProfile.Resolve(conImagenes, OperationMode.Consultation);
        Check("en Modo Consulta manda el perfil", consulta.ImageGenEnabled);

        // La misma regla que web y descargas, y por el mismo motivo: una herramienta
        // pericial que puede fabricar imágenes es un problema de credibilidad.
        var investigacion = EffectiveProfile.Resolve(conImagenes, OperationMode.Investigation);
        Check("el Modo Investigación apaga la generación de imágenes",
            !investigacion.ImageGenEnabled);
        Check("y la apaga igual que a la búsqueda web y las descargas",
            !investigacion.WebSearchEnabled && !investigacion.ModelDownloadEnabled);

        // No se contagia al resto: la carga de archivos SIGUE forzada a true en
        // Investigación, que es el && contra || de 1.2. Agregar una capacidad nueva es
        // justo cuando se rompería por simetría.
        Check("y no se lleva puesta la carga de archivos, que el modo habilita",
            investigacion.FileUploadEnabled);

        Check("un perfil sin la capacidad no la tiene ni en Consulta",
            !EffectiveProfile.Resolve(sinImagenes, OperationMode.Consultation).ImageGenEnabled);

        // El clon es la única vía para crear perfiles; si perdiera la capacidad, el
        // usuario la configuraría y desaparecería al clonar.
        var clon = conImagenes.CloneAsEditable("clon", "Clon");
        Check("el clon conserva la capacidad", clon.ImageGenEnabled);
        Check("y el clon nunca es de fábrica", !clon.IsFactory);
    }

    static void CasoPerfilesDeFabrica(Comprobar Check)
    {
        var dir = NuevaCarpeta("fabrica");
        var svc = new UsageProfileService(Path.Combine(dir, "profiles.json"));
        svc.Load();

        var forense = svc.Profiles.First(p => p.Id == UsageProfileService.ForensicProfileId);
        var general = svc.Profiles.First(p => p.Id == UsageProfileService.GeneralProfileId);

        Check("el perfil de fábrica General trae la generación de imágenes",
            general.ImageGenEnabled);

        // Apagada incluso fuera de Investigación, a diferencia de la carga de archivos:
        // el perfil Forense se usa cuando el trabajo es pericial.
        Check("el perfil de fábrica Forense NO la trae", !forense.ImageGenEnabled);
        Check("pero el Forense sí trae la carga de archivos", forense.FileUploadEnabled);

        Check("el Forense sigue sin web ni descargas",
            !forense.WebSearchEnabled && !forense.ModelDownloadEnabled);
    }

    static void CasoPersistencia(Comprobar Check)
    {
        var dir = NuevaCarpeta("persistencia");
        var ruta = Path.Combine(dir, "profiles.json");

        var svc = new UsageProfileService(ruta);
        svc.Load();

        var clon = svc.Clone(svc.Profiles.First(p => p.Id == UsageProfileService.GeneralProfileId), "Mi perfil");
        Check("el clon de General nace con imágenes habilitadas", clon.ImageGenEnabled);

        clon.ImageGenEnabled = false;
        Check("se puede apagar y guardar", svc.Update(clon));

        // Releer desde disco es lo que importa: una propiedad que no se persiste se ve
        // bien en memoria y vuelve a su default en el próximo arranque.
        var svc2 = new UsageProfileService(ruta);
        svc2.Load();
        var releido = svc2.Profiles.FirstOrDefault(p => p.Id == clon.Id);

        Check("el perfil sobrevive al reinicio", releido is not null);
        Check("y la capacidad apagada también", releido is not null && !releido.ImageGenEnabled);

        releido!.ImageGenEnabled = true;
        svc2.Update(releido);

        var svc3 = new UsageProfileService(ruta);
        svc3.Load();
        Check("volver a encenderla también persiste",
            svc3.Profiles.First(p => p.Id == clon.Id).ImageGenEnabled);

        // Que el JSON tenga la clave, y no que la propiedad exista solo en memoria.
        var json = File.ReadAllText(ruta);
        Check("la clave está en el archivo, en snake_case como el resto",
            json.Contains("\"image_gen_enabled\""),
            json.Length > 300 ? json.Substring(0, 300) : json);

        // Los de fábrica se reinyectan en cada Load: si alguien editó el JSON para
        // encenderle las imágenes al perfil Forense, se restaura.
        var texto = File.ReadAllText(ruta).Replace(
            "\"id\": \"forense\"", "\"id\": \"forense\"");
        File.WriteAllText(ruta, texto);
        var svc4 = new UsageProfileService(ruta);
        svc4.Load();
        Check("el perfil de fábrica Forense se reinyecta sin la capacidad",
            !svc4.Profiles.First(p => p.Id == UsageProfileService.ForensicProfileId).ImageGenEnabled);
    }

    // --------------------------------------------------------- la ruta del motor

    static void CasoRutaDelMotor(Comprobar Check)
    {
        var dir = NuevaCarpeta("motor");
        var ruta = Path.Combine(dir, "image_models.json");

        var svc = new ImageModelService(ruta);
        Check("sin configurar, la ruta del motor la descubre el localizador",
            svc.ResolveEnginePath() == ImageEngineLocator.ResolveExecutablePath(),
            svc.ResolveEnginePath());
        Check("y la configurada arranca vacía", svc.EngineExecutablePath == "");

        var falso = Path.Combine(dir, "sd-server.exe");
        File.WriteAllBytes(falso, new byte[16]);
        svc.EngineExecutablePath = falso;

        Check("la configurada le gana al descubrimiento", svc.ResolveEnginePath() == falso,
            svc.ResolveEnginePath());

        var svc2 = new ImageModelService(ruta);
        Check("y sobrevive al reinicio", svc2.ResolveEnginePath() == falso, svc2.ResolveEnginePath());

        // Vaciarla tiene que volver al descubrimiento automático, no dejar una cadena
        // vacía que después se pase como ruta al proceso.
        svc2.EngineExecutablePath = "   ";
        Check("vaciarla vuelve al descubrimiento",
            svc2.ResolveEnginePath() == ImageEngineLocator.ResolveExecutablePath(),
            svc2.ResolveEnginePath());

        Check("el localizador siempre devuelve una ruta, exista o no",
            !string.IsNullOrWhiteSpace(ImageEngineLocator.ResolveExecutablePath()));
        Check("y la ruta de instalación nombra Tools\\sd",
            ImageEngineLocator.InstalledPath.Contains(Path.Combine("Tools", "sd")),
            ImageEngineLocator.InstalledPath);

        // El aviso de la vista tiene que hablar de la ruta que se VA A USAR, no de la que
        // descubriría el localizador. Con el motor configurado a mano en otra carpeta, la
        // primera versión avisaba «el motor no está instalado» con el motor instalado: la
        // generación andaba y el cartel mentía. Lo encontró abrir la aplicación de verdad.
        var dirVm = NuevaCarpeta("motor-vm");
        var svcVm = new ImageModelService(Path.Combine(dirVm, "image_models.json"));
        string Falso(string n)
        {
            var r = Path.Combine(dirVm, n);
            File.WriteAllBytes(r, new byte[1024]);
            return r;
        }
        svcVm.Save(new ImageModelPackage
        {
            Id = "p", DisplayName = "Paquete", Family = "z-image",
            DiffusionModelPath = Falso("dif.gguf"),
            TextEncoderPath = Falso("te.gguf"),
            VaePath = Falso("ae.safetensors")
        });
        svcVm.Select("p");

        var motorReal = Path.Combine(dirVm, "sd-server.exe");
        File.WriteAllBytes(motorReal, new byte[16]);

        using (var conMotor = new ImageGenerationViewModel(svcVm, new ImageServerManager(),
                   gpuInfo: null, enginePathResolver: () => motorReal,
                   outputDirectory: Path.Combine(dirVm, "salidas")))
        {
            conMotor.InitializeAsync().GetAwaiter().GetResult();

            // Se exige que NO haya aviso ninguno, no que no diga cierta frase: la versión
            // anterior avisaba con otras palabras -«el motor no está instalado»- y una
            // aserción sobre el texto nuevo habría pasado sobre el defecto.
            Check("con el motor configurado en otra carpeta, no avisa nada",
                conMotor.StatusMessage == "", conMotor.StatusMessage);
        }

        // Control positivo: si de verdad no está, tiene que avisarlo y nombrar dónde miró.
        var inexistente = Path.Combine(dirVm, "no-esta", "sd-server.exe");
        using (var sinMotor = new ImageGenerationViewModel(svcVm, new ImageServerManager(),
                   gpuInfo: null, enginePathResolver: () => inexistente,
                   outputDirectory: Path.Combine(dirVm, "salidas")))
        {
            sinMotor.InitializeAsync().GetAwaiter().GetResult();
            Check("y si falta de verdad, avisa", sinMotor.StatusMessage.Contains("No se encontró el motor"),
                sinMotor.StatusMessage);
            Check("y dice en qué ruta miró", sinMotor.StatusMessage.Contains(inexistente),
                sinMotor.StatusMessage);
        }
    }

    // ------------------------------------------------- la barra lateral del chat

    static void CasoViewModelDelChat(Comprobar Check)
    {
        var dir = NuevaCarpeta("chat");
        var vm = NuevoMainViewModel(dir, OperationMode.Consultation, Perfil(true));

        // El binario contra el que corre este banco es el comercial.
        Check("el binario de prueba incluye la generación de imágenes", vm.IsImageGenCompiledIn);

        Check("con el perfil habilitado, la capacidad está disponible", vm.IsImageGenAvailable);
        Check("y el botón de generar imágenes se habilita",
            vm.OpenImageGenerationCommand.CanExecute(null));
        Check("y el de modelos de imagen también",
            vm.OpenImagePackagesCommand.CanExecute(null));

        // --- el mismo modo, perfil sin la capacidad ---
        vm.SelectedUsageProfile = Perfil(false);
        Check("con el perfil deshabilitado, la capacidad no está disponible",
            !vm.IsImageGenAvailable);
        Check("y los dos botones se deshabilitan",
            !vm.OpenImageGenerationCommand.CanExecute(null) &&
            !vm.OpenImagePackagesCommand.CanExecute(null));

        // --- Modo Investigación pisa al perfil ---
        vm.CurrentMode = OperationMode.Investigation;
        vm.SelectedUsageProfile = Perfil(true);
        Check("en Investigación no está disponible aunque el perfil la traiga",
            !vm.IsImageGenAvailable);
        Check("y el botón queda deshabilitado",
            !vm.OpenImageGenerationCommand.CanExecute(null));

        // El log de auditoría tiene que registrar la capacidad, como las otras dos.
        var logs = Directory.GetFiles(Path.Combine(dir, "Logs"));
        var contenido = logs.Length > 0 ? LeerAunqueEsteAbierto(logs[0]) : "";
        Check("el log de auditoría no está vacío", contenido.Length > 0,
            $"{logs.Length} archivos");
        Check("el log registra la capacidad de imágenes al aplicar el perfil",
            contenido.Contains("imagenes="), Recorte(contenido));
        Check("y la registra apagada en Investigación",
            contenido.Contains("imagenes=False"), Recorte(contenido));
    }

    /// <summary>
    /// El ForensicLogger mantiene el archivo abierto en escritura, así que
    /// File.ReadAllText choca con su handle y devuelve una IOException. Leerlo con
    /// FileShare.ReadWrite es lo que evita que una aserción sobre el log pase sobre una
    /// cadena vacía, que ya escondió un defecto en el banco de 9.3.
    /// </summary>
    static string LeerAunqueEsteAbierto(string ruta)
    {
        try
        {
            using var fs = new FileStream(ruta, FileMode.Open, FileAccess.Read,
                                          FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs);
            return sr.ReadToEnd();
        }
        catch (Exception)
        {
            return "";
        }
    }

    static string Recorte(string s) => s.Length > 300 ? s.Substring(s.Length - 300) : s;

    // ----------------------------------------------------- la edición Forense

    /// <summary>
    /// Prueba sobre el binario institucional DE VERDAD que la generación de imágenes no
    /// se puede encender por configuración.
    ///
    /// `UsageProfile` sí se compila en Forense —es configuración, no código de imágenes—,
    /// así que un profiles.json con `image_gen_enabled: true` llega igual a esa edición.
    /// Lo que no puede pasar es que `EffectiveProfile` lo informe como disponible.
    ///
    /// **El ensamblado se carga en su propio AssemblyLoadContext**, y no con
    /// Assembly.LoadFrom, porque el Forense y el Debug tienen la misma identidad —mismo
    /// nombre y misma versión— y el cargador habría devuelto el que ya está en memoria.
    /// La prueba habría corrido contra el binario comercial y pasado por el motivo
    /// equivocado. Se verifica explícitamente que el aislamiento haya funcionado.
    /// </summary>
    static void CasoEdicionForense(Comprobar Check)
    {
        var raiz = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var forense = Path.Combine(raiz, "bin", "Forense", "net10.0-windows", "PR-AI-Assistant.dll");

        if (!File.Exists(forense))
        {
            Check("hay un binario Forense para probar", false,
                $"no está {forense} — compilalo con: dotnet build -c Forense");
            return;
        }

        var contexto = new AssemblyLoadContext("forense-e45", isCollectible: true);
        Assembly asm;
        try { asm = contexto.LoadFromAssemblyPath(forense); }
        catch (Exception ex) { Check("se puede cargar el binario Forense", false, ex.Message); return; }

        // Control de que el aislamiento sirvió: si el cargador hubiera devuelto el
        // ensamblado ya cargado, esta ruta sería la de bin\Debug.
        Check("el ensamblado cargado es el Forense y no el que ya estaba en memoria",
            string.Equals(asm.Location, forense, StringComparison.OrdinalIgnoreCase),
            asm.Location);

        // Y control de que ES el Forense: ahí no existe el motor de imágenes.
        Check("en el binario Forense no existe ImageServerManager",
            asm.GetType("PRImager.AIAssistant.Services.ImageGen.ImageServerManager") is null);

        var tEffective = asm.GetType("PRImager.AIAssistant.Services.Models.EffectiveProfile");
        var tUsage = asm.GetType("PRImager.AIAssistant.Services.Models.UsageProfile");
        var tMode = asm.GetType("PRImager.AIAssistant.Services.Models.OperationMode");

        if (tEffective is null || tUsage is null || tMode is null)
        {
            Check("el binario Forense trae los tipos de perfil", false,
                "faltan EffectiveProfile, UsageProfile u OperationMode");
            return;
        }

        // La constante de compilación, leída del binario institucional.
        var constante = tEffective.GetField("ImageGenCompiledIn",
            BindingFlags.Public | BindingFlags.Static)?.GetRawConstantValue();
        Check("en Forense, ImageGenCompiledIn es false", constante is bool b && !b, $"{constante}");

        // Y el efecto: un perfil con la bandera encendida, en el modo más permisivo.
        var perfil = Activator.CreateInstance(tUsage)!;
        tUsage.GetProperty("ImageGenEnabled")!.SetValue(perfil, true);
        tUsage.GetProperty("Id")!.SetValue(perfil, "forzado");
        tUsage.GetProperty("Name")!.SetValue(perfil, "Forzado");

        var consulta = Enum.Parse(tMode, "Consultation");
        var efectivo = tEffective.GetMethod("Resolve", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, new[] { perfil, consulta })!;

        var resultado = (bool)tEffective.GetProperty("ImageGenEnabled")!.GetValue(efectivo)!;
        Check("en Forense la capacidad es false aunque el perfil la traiga en true",
            !resultado);

        // CONTROL POSITIVO. Sin esto, la aserción de arriba pasaría igual si Resolve
        // devolviera false siempre, o si la propiedad no existiera y algo devolviera el
        // default. El mismo camino, sobre el binario comercial, tiene que dar TRUE.
        var perfilComercial = new UsageProfile { Id = "x", Name = "X", ImageGenEnabled = true };
        Check("control positivo: en el binario comercial, el mismo perfil da true",
            EffectiveProfile.Resolve(perfilComercial, OperationMode.Consultation).ImageGenEnabled);

        // Y en Forense la carga de archivos sigue funcionando: la barrera es de imágenes,
        // no de todo.
        var archivos = (bool)tEffective.GetProperty("FileUploadEnabled")!.GetValue(
            tEffective.GetMethod("Resolve", BindingFlags.Public | BindingFlags.Static)!
                .Invoke(null, new[] { perfil, Enum.Parse(tMode, "Investigation") })!)!;
        Check("en Forense la carga de archivos sigue habilitada en Investigación", archivos);

        contexto.Unload();
    }
}
