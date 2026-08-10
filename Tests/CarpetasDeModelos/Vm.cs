using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PRImager.AIAssistant.Services;
using PRImager.AIAssistant.Services.Folders;
#if APP_CON_IMAGENES
using PRImager.AIAssistant.Services.ImageGen;
#endif
using PRImager.AIAssistant.Services.Models;
using PRImager.AIAssistant.ViewModels;

// El ViewModel real, que es donde vive la decisión central: cambiar la carpeta REDIRIGE
// y no mueve. Si eso se rompiera, un cambio de carpeta pasaría a ser una operación de
// gigabytes que puede fallar a la mitad, disfrazada de preferencia.

sealed class DialogoFalso : IFileDialogService
{
    public string ProximaCarpeta;
    public string UltimoInicial = "";

    public string[] OpenFiles(string title, string filter) => null;
    public string[] OpenImages(string title) => null;
    public string[] OpenTextFiles(string title) => null;
    public string SaveFile(string title, string filter, string defaultFileName) => null;

    public string SelectFolder(string title, string initialFolder)
    {
        UltimoInicial = initialFolder;
        return ProximaCarpeta;
    }
}

static class BancoVm
{
    static int _ok, _fail;

    static void Check(string nombre, bool cond, string detalle = "")
    {
        if (cond) { _ok++; Console.WriteLine($"  OK   {nombre}"); }
        else { _fail++; Console.WriteLine($"  FALLA {nombre}   {detalle}"); }
    }

    static string NuevoCaso(string nombre)
    {
        string raiz = Path.Combine(Path.GetTempPath(), "BancoCarpetasVm", nombre);
        if (Directory.Exists(raiz)) Directory.Delete(raiz, true);
        Directory.CreateDirectory(raiz);
        return raiz;
    }

    static string Archivo(string carpeta, string nombre, int kb = 16)
    {
        Directory.CreateDirectory(carpeta);
        string p = Path.Combine(carpeta, nombre);
        File.WriteAllBytes(p, new byte[kb * 1024]);
        return p;
    }

#if APP_CON_IMAGENES
    static (AppFolderService svc, ImageModelService img, AppSettings cfg, int[] guardados)
        Servicio(string raiz, string descargas)
    {
        var cfg = new AppSettings { ModelPath = raiz, ModelDownloadPath = descargas };
        var guardados = new int[1];
        var svc = new AppFolderService(cfg, () => guardados[0]++);

        var img = new ImageModelService(Path.Combine(raiz, "image_models.json"));
        svc.UseImageModelService(img);

        return (svc, img, cfg, guardados);
    }
#else
    static (AppFolderService svc, object img, AppSettings cfg, int[] guardados)
        Servicio(string raiz, string descargas)
    {
        var cfg = new AppSettings { ModelPath = raiz, ModelDownloadPath = descargas };
        var guardados = new int[1];
        return (new AppFolderService(cfg, () => guardados[0]++), null, cfg, guardados);
    }
#endif

    public static (int ok, int fail) Correr()
    {
        // En la edición institucional NO hay ninguna carpeta que configurar: no descarga
        // modelos y no genera imágenes. Los casos del recorrido del usuario no aplican
        // —no hay fila que cambiar— y lo que hay que verificar es justamente eso.
        var casos = new List<Action>
#if APP_CON_IMAGENES
        {
            CasoCambiarNoMueve, CasoPersisteLaEleccion, CasoOfreceMoverLoQueQuedo,
            CasoMoverDesdeElViewModel, CasoDialogoAbreEnLaCarpetaActual,
            CasoReapuntaLosPaquetesDespuesDeMover, CasoNoReapuntaAlCambiarLaCarpeta,
            CasoNoReapuntaAUnaCarpetaSinLosArchivos
        };
#else
        {
            CasoForenseNoAdministraCarpetas
        };
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

        return (_ok, _fail);
    }

    // --- La decisión central ---

    static void CasoCambiarNoMueve()
    {
        Console.WriteLine("\nCambiar la carpeta NO mueve ningún archivo");
        string raiz = NuevoCaso("no-mueve");
        string vieja = Path.Combine(raiz, "vieja");
        string nueva = Path.Combine(raiz, "nueva");

        var origen = Archivo(vieja, "modelo.gguf", 64);
        var (svc, _, _, _) = Servicio(raiz, vieja);

        var dialogos = new DialogoFalso { ProximaCarpeta = nueva };
        var vm = new FolderSettingsViewModel(svc, dialogos);

        var fila = vm.Folders.FirstOrDefault(f => f.Kind == AppFolderKind.LanguageModels);
        Check("la carpeta de modelos de lenguaje está en la lista", fila != null);
        if (fila == null) return;

        vm.ChangeFolderCommand.Execute(fila);

        Check("el archivo SIGUE en la carpeta anterior", File.Exists(origen),
            "cambiar la carpeta es una redirección, no un movimiento");
        Check("no se creó nada en la carpeta nueva",
            !Directory.Exists(nueva) || Directory.GetFiles(nueva).Length == 0);

        var actualizada = vm.Folders.First(f => f.Kind == AppFolderKind.LanguageModels);
        Check("la carpeta nueva ya quedó configurada",
            string.Equals(Path.GetFullPath(actualizada.Path), Path.GetFullPath(nueva),
                StringComparison.OrdinalIgnoreCase),
            actualizada.Path);
    }

    static void CasoPersisteLaEleccion()
    {
        Console.WriteLine("\nLa carpeta elegida se persiste");
        string raiz = NuevoCaso("persiste");
        string nueva = Path.Combine(raiz, "nueva");

        var (svc, _, cfg, guardados) = Servicio(raiz, Path.Combine(raiz, "vieja"));
        var vm = new FolderSettingsViewModel(svc, new DialogoFalso { ProximaCarpeta = nueva });

        vm.ChangeFolderCommand.Execute(vm.Folders.First(f => f.Kind == AppFolderKind.LanguageModels));

        Check("escribió settings.json una vez", guardados[0] == 1, $"escribió {guardados[0]} veces");
        Check("quedó en AppSettings",
            string.Equals(Path.GetFullPath(cfg.ModelDownloadPath), Path.GetFullPath(nueva),
                StringComparison.OrdinalIgnoreCase),
            cfg.ModelDownloadPath);
    }

    static void CasoOfreceMoverLoQueQuedo()
    {
        Console.WriteLine("\nOfrece mover lo que quedó atrás, y solo si quedó algo");
        string raiz = NuevoCaso("ofrece");
        string vieja = Path.Combine(raiz, "vieja");
        string nueva = Path.Combine(raiz, "nueva");

        Archivo(vieja, "a.gguf", 32);
        Archivo(vieja, "b.gguf", 32);

        var (svc, _, _, _) = Servicio(raiz, vieja);
        var vm = new FolderSettingsViewModel(svc, new DialogoFalso { ProximaCarpeta = nueva });
        vm.ChangeFolderCommand.Execute(vm.Folders.First(f => f.Kind == AppFolderKind.LanguageModels));

        var fila = vm.Folders.First(f => f.Kind == AppFolderKind.LanguageModels);
        Check("queda un movimiento pendiente", fila.HasPendingMove);
        Check("dice cuántos archivos y cuánto pesan",
            fila.PendingText.Contains("2 archivos"), fila.PendingText);
        Check("recuerda la carpeta anterior",
            string.Equals(Path.GetFullPath(fila.PreviousFolder), Path.GetFullPath(vieja),
                StringComparison.OrdinalIgnoreCase));

        // Y ahora una carpeta anterior vacía: no debe ofrecer nada.
        string raiz2 = NuevoCaso("ofrece-vacia");
        string vacia = Path.Combine(raiz2, "vacia");
        Directory.CreateDirectory(vacia);
        var (svc2, _, _, _) = Servicio(raiz2, vacia);
        var vm2 = new FolderSettingsViewModel(svc2, new DialogoFalso { ProximaCarpeta = Path.Combine(raiz2, "otra") });
        vm2.ChangeFolderCommand.Execute(vm2.Folders.First(f => f.Kind == AppFolderKind.LanguageModels));

        Check("con la carpeta anterior vacía no ofrece mover",
            !vm2.Folders.First(f => f.Kind == AppFolderKind.LanguageModels).HasPendingMove);
    }

    static void CasoMoverDesdeElViewModel()
    {
        Console.WriteLine("\nMover desde el ViewModel");
        string raiz = NuevoCaso("mover-vm");
        string vieja = Path.Combine(raiz, "vieja");
        string nueva = Path.Combine(raiz, "nueva");

        Archivo(vieja, "a.gguf", 32);
        Archivo(vieja, "b.gguf", 32);

        var (svc, _, _, _) = Servicio(raiz, vieja);
        var vm = new FolderSettingsViewModel(svc, new DialogoFalso { ProximaCarpeta = nueva });
        vm.ChangeFolderCommand.Execute(vm.Folders.First(f => f.Kind == AppFolderKind.LanguageModels));

        var fila = vm.Folders.First(f => f.Kind == AppFolderKind.LanguageModels);
        vm.MoveCommand.Execute(fila);

        // AsyncRelayCommand.Execute es async void; el movimiento de dos archivos chicos en
        // el mismo volumen es sincrónico en la práctica, pero se espera igual.
        Esperar(() => !vm.Folders.First(f => f.Kind == AppFolderKind.LanguageModels).IsMoving);

        Check("los archivos están en la carpeta nueva",
            Directory.GetFiles(nueva, "*.gguf").Length == 2);
        Check("no quedó ninguno en la anterior",
            Directory.GetFiles(vieja, "*.gguf").Length == 0);

        var despues = vm.Folders.First(f => f.Kind == AppFolderKind.LanguageModels);
        Check("ya no ofrece mover", !despues.HasPendingMove);
        Check("informa lo que hizo", despues.ResultText.Contains("2 archivos"), despues.ResultText);
        Check("la carpeta informa su nuevo contenido",
            despues.ContentsText.Contains("2 archivos"), despues.ContentsText);
    }

#if !APP_CON_IMAGENES
    /// <summary>
    /// La edición institucional no administra ninguna carpeta de datos del usuario, así
    /// que la entrada de la barra lateral no se muestra y la ventana no se abre.
    ///
    /// No es una barrera de compilación nueva —AppFolderService sí se compila ahí, porque
    /// no es código de red ni de imágenes— sino la consecuencia de que esa edición no tenga
    /// ninguna de las tres carpetas: las descargas están detrás de MODEL_DOWNLOAD y las dos
    /// de imagen detrás de IMAGE_GEN.
    /// </summary>
    static void CasoForenseNoAdministraCarpetas()
    {
        Console.WriteLine("\nLa edición institucional no administra ninguna carpeta");
        string raiz = NuevoCaso("forense");

        var (svc, _, _, _) = Servicio(raiz, Path.Combine(raiz, "descargas"));

        Check("no hay ninguna carpeta configurable", svc.List().Count == 0,
            $"informa {svc.List().Count}");

        var vm = new FolderSettingsViewModel(svc, new DialogoFalso());
        Check("el ViewModel se declara vacío", vm.IsEmpty);
        Check("no dibuja ninguna fila", vm.Folders.Count == 0);
        Check("resolver una carpeta de imagen devuelve vacío, no lanza",
            svc.Resolve(AppFolderKind.ImageModels).Length == 0);
    }
#endif

#if APP_CON_IMAGENES
    // --- Los paquetes de imagen ---

    static void CasoReapuntaLosPaquetesDespuesDeMover()
    {
        Console.WriteLine("\nDespués de mover, los paquetes apuntan a la carpeta nueva");
        string raiz = NuevoCaso("reapunta");
        string vieja = Path.Combine(raiz, "img-vieja");
        string nueva = Path.Combine(raiz, "img-nueva");

        var (svc, img, _, _) = Servicio(raiz, Path.Combine(raiz, "descargas"));
        img.ModelsDirectory = vieja;

        var difusion = Archivo(vieja, "z_image.gguf", 32);
        var encoder = Archivo(vieja, "qwen3.gguf", 32);
        var vae = Archivo(vieja, "ae.safetensors", 16);

        img.Save(new ImageModelPackage
        {
            Id = "prueba",
            DisplayName = "Paquete de prueba",
            DiffusionModelPath = difusion,
            TextEncoderPath = encoder,
            VaePath = vae
        });

        Check("el paquete arranca completo",
            img.Packages.First(p => p.Id == "prueba").IsComplete);

        var vm = new FolderSettingsViewModel(svc, new DialogoFalso { ProximaCarpeta = nueva });
        var fila = vm.Folders.First(f => f.Kind == AppFolderKind.ImageModels);
        vm.ChangeFolderCommand.Execute(fila);

        var trasCambiar = img.Packages.First(p => p.Id == "prueba");
        Check("cambiar la carpeta NO rompe el paquete", trasCambiar.IsComplete,
            "los archivos siguen en la carpeta vieja, así que el paquete debe seguir apuntando ahí");

        vm.MoveCommand.Execute(vm.Folders.First(f => f.Kind == AppFolderKind.ImageModels));
        Esperar(() => !vm.Folders.First(f => f.Kind == AppFolderKind.ImageModels).IsMoving);

        var trasMover = img.Packages.First(p => p.Id == "prueba");
        Check("después de mover el paquete sigue completo", trasMover.IsComplete,
            trasMover.MissingText);
        Check("y ahora apunta a la carpeta nueva",
            string.Equals(Path.GetDirectoryName(Path.GetFullPath(trasMover.DiffusionModelPath)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(nueva)),
                StringComparison.OrdinalIgnoreCase),
            trasMover.DiffusionModelPath);
        Check("los tres archivos, no solo el primero",
            trasMover.TextEncoderPath.StartsWith(nueva, StringComparison.OrdinalIgnoreCase) &&
            trasMover.VaePath.StartsWith(nueva, StringComparison.OrdinalIgnoreCase));

        // Persistió, no quedó solo en memoria.
        var releido = new ImageModelService(Path.Combine(raiz, "image_models.json"));
        Check("la reapuntada quedó guardada en image_models.json",
            releido.Packages.First(p => p.Id == "prueba").IsComplete);
    }

    static void CasoNoReapuntaAlCambiarLaCarpeta()
    {
        Console.WriteLine("\nUn paquete de otra carpeta no se toca nunca");
        string raiz = NuevoCaso("ajeno");
        string vieja = Path.Combine(raiz, "img-vieja");
        string nueva = Path.Combine(raiz, "img-nueva");
        string ajena = Path.Combine(raiz, "en-otro-lado");

        var (svc, img, _, _) = Servicio(raiz, Path.Combine(raiz, "descargas"));
        img.ModelsDirectory = vieja;

        var d = Archivo(ajena, "propio.gguf", 16);
        var e = Archivo(ajena, "propio-te.gguf", 16);
        var v = Archivo(ajena, "propio-vae.safetensors", 8);

        img.Save(new ImageModelPackage
        {
            Id = "armado-a-mano",
            DisplayName = "Armado a mano",
            DiffusionModelPath = d, TextEncoderPath = e, VaePath = v
        });

        int cambiados = img.RepointPackages(vieja, nueva);

        Check("no reapunta un paquete que nunca estuvo en esa carpeta", cambiados == 0);
        Check("las rutas siguen intactas",
            img.Packages.First(p => p.Id == "armado-a-mano").DiffusionModelPath == d);
    }

    /// <summary>
    /// La invariante «cambiar la carpeta no rompe el paquete» está protegida por DOS cosas
    /// independientes: que no se reapunte al cambiar la carpeta, y que reapuntar exija que
    /// el archivo esté del otro lado. Una aserción sobre el resultado pasa mientras
    /// cualquiera de las dos siga en pie —se comprobó mutando: poner la reapuntada en
    /// Set() no hizo fallar nada—, así que hace falta una que mire el mecanismo.
    ///
    /// Es la forma de trampa que la sección 8 del maestro describe: verificar que algo NO
    /// ocurra cuando hay más de un camino por el que puede no ocurrir.
    /// </summary>
    static void CasoNoReapuntaAUnaCarpetaSinLosArchivos()
    {
        Console.WriteLine("\nNo reapunta a una carpeta donde el archivo todavía no está");
        string raiz = NuevoCaso("sin-archivos");
        string vieja = Path.Combine(raiz, "img-vieja");
        string nueva = Path.Combine(raiz, "img-nueva");
        Directory.CreateDirectory(nueva);          // existe, pero está vacía

        var (_, img, _, _) = Servicio(raiz, Path.Combine(raiz, "descargas"));
        img.ModelsDirectory = vieja;

        var d = Archivo(vieja, "dif.gguf", 16);
        var e = Archivo(vieja, "te.gguf", 16);
        var v = Archivo(vieja, "vae.safetensors", 8);

        img.Save(new ImageModelPackage
        {
            Id = "intacto", DisplayName = "Intacto",
            DiffusionModelPath = d, TextEncoderPath = e, VaePath = v
        });

        int cambiados = img.RepointPackages(vieja, nueva);

        Check("no reapunta nada si los archivos no están del otro lado", cambiados == 0,
            $"reapuntó {cambiados}");
        Check("el paquete sigue completo",
            img.Packages.First(p => p.Id == "intacto").IsComplete,
            "reapuntar a una carpeta vacía deja los tres componentes faltando");

        // Y el control positivo: con el archivo presente, SÍ reapunta. Sin esto, la
        // aserción de arriba pasaría igual si RepointPackages no hiciera nada nunca.
        File.Copy(d, Path.Combine(nueva, "dif.gguf"));
        int cambiados2 = img.RepointPackages(vieja, nueva);

        Check("con el archivo presente sí reapunta", cambiados2 == 1, $"reapuntó {cambiados2}");
        Check("y reapunta solo el que está, no los tres",
            img.Packages.First(p => p.Id == "intacto").TextEncoderPath == e);
    }
#endif

    static void CasoDialogoAbreEnLaCarpetaActual()
    {
        Console.WriteLine("\nEl diálogo se abre en la carpeta actual");
        string raiz = NuevoCaso("dialogo");
        string actual = Path.Combine(raiz, "actual");
        Directory.CreateDirectory(actual);

        var (svc, _, _, _) = Servicio(raiz, actual);
        var dialogos = new DialogoFalso { ProximaCarpeta = null };   // el usuario cancela
        var vm = new FolderSettingsViewModel(svc, dialogos);

        var fila = vm.Folders.First(f => f.Kind == AppFolderKind.LanguageModels);
        vm.ChangeFolderCommand.Execute(fila);

        Check("le pasa la carpeta actual como punto de partida",
            string.Equals(Path.GetFullPath(dialogos.UltimoInicial), Path.GetFullPath(actual),
                StringComparison.OrdinalIgnoreCase),
            dialogos.UltimoInicial);
        Check("cancelar el diálogo no cambia nada",
            string.Equals(Path.GetFullPath(vm.Folders.First(f => f.Kind == AppFolderKind.LanguageModels).Path),
                Path.GetFullPath(actual), StringComparison.OrdinalIgnoreCase));
    }

    static void Esperar(Func<bool> hasta, int msTope = 15000)
    {
        var reloj = System.Diagnostics.Stopwatch.StartNew();
        while (!hasta() && reloj.ElapsedMilliseconds < msTope)
            System.Threading.Thread.Sleep(20);
    }
}
