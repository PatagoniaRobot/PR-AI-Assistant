using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PRImager.AIAssistant.Services;
using PRImager.AIAssistant.Services.ImageGen;
using PRImager.AIAssistant.ViewModels.ImageGen;

// Banco de E4.3: paquetes de modelo de imagen, persistencia y descarga.
//
// Lo que importa verificar no es que guarde un JSON, sino las reglas que hacen que un
// paquete de tres archivos no se comporte como si fuera uno solo: que un paquete
// incompleto se vea y no se pueda usar, que la selección sobreviva al reinicio, y que
// una descarga a medias no quede registrada como utilizable.

sealed class DialogosFalsos : IFileDialogService
{
    /// <summary>Lo que va a devolver el próximo «Examinar…».</summary>
    public string[]? Proximo { get; set; }
    public List<string> TitulosVistos { get; } = new();

    public string[]? OpenFiles(string title, string filter) { TitulosVistos.Add(title); return Proximo; }
    public string[]? OpenImages(string title) { TitulosVistos.Add(title); return Proximo; }
    public string[]? OpenTextFiles(string title) { TitulosVistos.Add(title); return Proximo; }
    public string? SaveFile(string title, string filter, string defaultFileName) => null;

    /// <summary>Carpeta que va a devolver el próximo «Examinar…» de carpeta.</summary>
    public string? ProximaCarpeta { get; set; }
    public string? SelectFolder(string title, string initialFolder)
    {
        TitulosVistos.Add(title);
        return ProximaCarpeta;
    }
}

static class BancoPaquetes
{
    static string NuevaCarpeta(string nombre)
    {
        var p = Path.Combine(Path.GetTempPath(), "BancoPaquetes", nombre);
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

    static GpuVramInfo Gpu(int libre = 12000) => new()
    { Available = true, Name = "GPU de prueba", TotalMb = 16311, FreeMb = libre };

    public static void Correr(Action<string, bool, string> check)
    {
        void Check(string n, bool c, string d = "") => check(n, c, d);

        // ------------------------------------------------------- el paquete en sí
        var dir = NuevaCarpeta("paquete");
        var completo = new ImageModelPackage
        {
            Id = "p1", DisplayName = "Prueba",
            DiffusionModelPath = Archivo(dir, "dif.gguf"),
            TextEncoderPath = Archivo(dir, "te.gguf"),
            VaePath = Archivo(dir, "ae.safetensors")
        };
        Check("un paquete con los tres archivos está completo", completo.IsComplete);
        Check("suma el tamaño de los tres", completo.TotalSizeBytes == 3 * 8 * 1024,
            $"{completo.TotalSizeBytes}");

        var sinVae = completo with { VaePath = Path.Combine(dir, "no-esta.safetensors") };
        Check("detecta el paquete incompleto", !sinVae.IsComplete);
        Check("nombra el componente que falta, no el archivo",
            sinVae.MissingText == "falta el VAE", sinVae.MissingText);

        var soloDifusion = completo with
        { TextEncoderPath = "x", VaePath = "y" };
        Check("con dos faltantes los enumera",
            soloDifusion.MissingText.Contains("text encoder") && soloDifusion.MissingText.Contains("VAE"),
            soloDifusion.MissingText);

        // Los parámetros del paquete son los que pisan a los del motor.
        var pedido = completo.NewRequest("un zorro");
        Check("el pedido hereda los pasos del paquete", pedido.Steps == 8, $"{pedido.Steps}");
        Check("el pedido hereda el CFG del paquete", Math.Abs(pedido.CfgScale - 1.0) < 0.001,
            $"{pedido.CfgScale}");

        var cfg = completo.ToServerConfig(@"C:\sd\sd-server.exe", 4321);
        Check("arma la configuración del servidor con las tres rutas",
            cfg.DiffusionModelPath == completo.DiffusionModelPath &&
            cfg.TextEncoderPath == completo.TextEncoderPath &&
            cfg.VaePath == completo.VaePath);
        Check("respeta el puerto al armar la configuración", cfg.Port == 4321);

        // ------------------------------------------------------------- el servicio
        var cfgDir = NuevaCarpeta("servicio");
        var jsonPath = Path.Combine(cfgDir, "image_models.json");
        var svc = new ImageModelService(jsonPath);

        Check("crea el archivo de configuración al arrancar", File.Exists(jsonPath));
        Check("trae el paquete recomendado de fábrica",
            svc.Packages.Any(p => p.Id == ImageModelService.RecommendedPackageId));
        Check("el de fábrica viene marcado como tal",
            svc.Packages.First(p => p.Id == ImageModelService.RecommendedPackageId).IsFactory);
        Check("el de fábrica arranca incompleto si no se descargó nada",
            !svc.Packages.First(p => p.Id == ImageModelService.RecommendedPackageId).IsComplete);
        Check("sin selección, SelectedPackage es null", svc.SelectedPackage is null);

        // El catálogo: las tres cuantizaciones de Z-Image de 10.12, antes que las del
        // usuario y todas de fábrica. Compartir text encoder y VAE es lo que hace que la
        // segunda variante baje un solo archivo.
        var idsCatalogo = ImageModelService.Catalog.Select(c => c.Id).ToList();
        Check("trae los tres paquetes del catálogo, en orden",
            svc.Packages.Take(3).Select(p => p.Id).SequenceEqual(idsCatalogo),
            string.Join(", ", svc.Packages.Select(p => p.Id)));
        Check("los tres son de fábrica", svc.Packages.Take(3).All(p => p.IsFactory));
        Check("comparten text encoder y VAE",
            ImageModelService.Catalog.Select(c => c.Components.First(x => x.Role == "text encoder").FileName).Distinct().Count() == 1 &&
            ImageModelService.Catalog.Select(c => c.Components.First(x => x.Role == "VAE").FileName).Distinct().Count() == 1);
        Check("y cada uno tiene su propio archivo de difusión",
            ImageModelService.Catalog.Select(c => c.Components.First(x => x.Role == "modelo de difusión").FileName).Distinct().Count() == 3);
        Check("no deja borrar ninguno del catálogo", idsCatalogo.All(id => !svc.Remove(id)));

        // La recomendación según la placa (10.12): Q4_K por debajo de 20 GB, Q6_K arriba.
        GpuVramInfo Placa(int mb) => new() { Available = true, Name = "x", TotalMb = mb, FreeMb = mb };
        Check("con 8 GB recomienda Q4_K", ImageModelService.RecommendedIdFor(Placa(8192)) == "z-image-turbo-q4k");
        Check("con 16 GB recomienda Q4_K", ImageModelService.RecommendedIdFor(Placa(16311)) == "z-image-turbo-q4k");
        Check("con 24 GB recomienda Q6_K", ImageModelService.RecommendedIdFor(Placa(24564)) == "z-image-turbo-q6k");
        Check("sin datos de la placa recomienda Q4_K",
            ImageModelService.RecommendedIdFor(GpuVramInfo.Unavailable("sin placa")) == "z-image-turbo-q4k");

        svc.Save(completo);
        Check("guarda un paquete de usuario", svc.Packages.Any(p => p.Id == "p1"));
        Check("un paquete de usuario no puede declararse de fábrica",
            !svc.Packages.First(p => p.Id == "p1").IsFactory);

        Check("no deja seleccionar un paquete incompleto",
            !svc.Select(ImageModelService.RecommendedPackageId));
        Check("deja seleccionar uno completo", svc.Select("p1"));

        // La selección tiene que sobrevivir al reinicio: es el defecto 4.12, donde la
        // pantalla no reconocía lo guardado y lo sobrescribía en cada arranque.
        var svc2 = new ImageModelService(jsonPath);
        Check("la selección sobrevive a reabrir el servicio",
            svc2.SelectedPackageId == "p1", svc2.SelectedPackageId ?? "null");
        Check("y el paquete guardado se reencuentra entero",
            svc2.SelectedPackage is not null && svc2.SelectedPackage.IsComplete);

        Check("no deja borrar el paquete de fábrica",
            !svc2.Remove(ImageModelService.RecommendedPackageId));
        Check("deja borrar uno de usuario", svc2.Remove("p1"));
        Check("al borrar el seleccionado, la selección queda vacía",
            svc2.SelectedPackageId is null, svc2.SelectedPackageId ?? "null");

        // Quitar un paquete es configuración, no borrar modelos del disco.
        Check("quitar el paquete NO borra los archivos", File.Exists(completo.DiffusionModelPath));

        // Un archivo corrupto no debe impedir arrancar.
        File.WriteAllText(jsonPath, "{ esto no es json");
        var svc3 = new ImageModelService(jsonPath);
        Check("con el archivo corrupto regenera lo de fábrica",
            svc3.Packages.Any(p => p.Id == ImageModelService.RecommendedPackageId));

        // ------------------------------------------------ el descriptor de descarga
        var src = ImageModelService.RecommendedSource;
        Check("el paquete recomendado declara tres componentes", src.Components.Count == 3,
            $"{src.Components.Count}");
        Check("ninguno sale de un repositorio con restricción",
            src.Components.All(c => !c.RepoId.StartsWith("black-forest-labs")),
            string.Join(", ", src.Components.Select(c => c.RepoId)));
        Check("el VAE sale del espejo abierto",
            src.Components.Any(c => c.RepoId == "Comfy-Org/z_image_turbo"));
        Check("el VAE vive en una subcarpeta del repositorio",
            src.Components.First(c => c.Role == "VAE").FileName.Contains("/"));
        Check("pero se guarda en disco con el nombre suelto",
            src.Components.First(c => c.Role == "VAE").LocalFileName == "ae.safetensors");
        Check("informa el tamaño total antes de bajar nada",
            src.TotalExpectedBytes > 6_000_000_000L, $"{src.TotalExpectedBytes}");

        // ------------------------------------------------------- el ViewModel
        var vmDir = NuevaCarpeta("vm");
        var vmJson = Path.Combine(vmDir, "image_models.json");
        var svcVm = new ImageModelService(vmJson);
        var dialogos = new DialogosFalsos();
        var vm = new ImagePackageSelectorViewModel(svcVm, dialogos, new GpuInfoService());
        vm.Refresh();

        Check("el selector lista los paquetes del catálogo", vm.Packages.Count >= 3);
        var fila = vm.Packages.First(p => p.Id == ImageModelService.RecommendedPackageId);
        Check("el incompleto se muestra en gris", fila.NameColor == "#6B7280", fila.NameColor);
        Check("y dice que está sin descargar y cuánto pesa", fila.StatusText.Contains("Sin descargar") && fila.StatusText.Contains("GB"),
            fila.StatusText);
        Check("se puede bajar", fila.CanDownload);
        Check("sin nada en disco, bajarlo es el paquete entero",
            fila.PendingDownloadBytes == ImageModelService.RecommendedSource.TotalExpectedBytes, $"{fila.PendingDownloadBytes}");
        Check("el recuadro de descarga ofrece bajar el seleccionado",
            vm.SelectedRow?.Id == fila.Id && vm.DownloadText.Contains("Descargar") && vm.DownloadCommand.CanExecute(null),
            vm.DownloadText);
        Check("el incompleto no informa tamaño en disco", fila.SizeText == "—", fila.SizeText);
        Check("el incompleto no informa veredicto de VRAM", fila.VramLabel == "—", fila.VramLabel);

        // Completar el paquete a mano, que es el camino de 3.2.
        var manual = NuevaCarpeta("manual");
        vm.SelectedRow = fila;
        dialogos.Proximo = new[] { Archivo(manual, "dif.gguf") };
        vm.BrowseDiffusionCommand.Execute(null);
        dialogos.Proximo = new[] { Archivo(manual, "te.gguf") };
        vm.BrowseTextEncoderCommand.Execute(null);
        dialogos.Proximo = new[] { Archivo(manual, "ae.safetensors") };
        vm.BrowseVaeCommand.Execute(null);

        var completa = vm.Packages.First(p => p.Id == ImageModelService.RecommendedPackageId);
        Check("al apuntar los tres archivos el paquete queda completo", completa.IsComplete);
        Check("y deja de estar en gris", completa.NameColor != "#6B7280", completa.NameColor);
        Check("el diálogo dice qué archivo se está eligiendo",
            dialogos.TitulosVistos.Any(t => t.Contains("text encoder")),
            string.Join(" | ", dialogos.TitulosVistos));

        // La configuración se persiste enseguida: si se pierde al cerrar la ventana,
        // el usuario tiene que rehacerla cada vez.
        var svcVm2 = new ImageModelService(vmJson);
        Check("las rutas elegidas quedan persistidas",
            svcVm2.Packages.First(p => p.Id == ImageModelService.RecommendedPackageId).IsComplete);

        vm.SelectedRow = vm.Packages.First(p => p.Id == ImageModelService.RecommendedPackageId);
        vm.UseSelectedCommand.Execute(null);
        Check("«usar este paquete» lo deja como activo",
            vm.ActivePackageId == ImageModelService.RecommendedPackageId, vm.ActivePackageId ?? "null");

        // El defecto que vio el usuario el 21/10/2026: el recuadro ofrecía bajar el
        // paquete aunque ya estuviera bajado y en uso.
        Check("con el paquete completo, el recuadro dice que ya está bajado",
            vm.DownloadText.Contains("ya está descargado"), vm.DownloadText);
        Check("y no deja volver a bajarlo", !vm.DownloadCommand.CanExecute(null));

        // Q6_K comparte text encoder y VAE: con los dos en disco, falta solo su difusión.
        var q6 = ImageModelService.BuildPackage(ImageModelService.Catalog[1], manual);
        svcVm.Save(q6 with { TextEncoderPath = completa.Package.TextEncoderPath, VaePath = completa.Package.VaePath });
        vm.Refresh();
        var filaQ6 = vm.Packages.First(p => p.Id == "z-image-turbo-q6k");
        Check("con text encoder y VAE ya bajados, a la otra variante le falta solo la difusión",
            filaQ6.PendingDownloadBytes == ImageModelService.Catalog[1].Components[0].ExpectedBytes,
            $"{filaQ6.PendingDownloadBytes}");

        // Un paquete armado a mano no tiene de dónde bajarse.
        svcVm.Save(new ImageModelPackage { Id = "a-mano", DisplayName = "A mano", DiffusionModelPath = Path.Combine(manual, "no-existe.gguf") });
        vm.Refresh();
        vm.SelectedRow = vm.Packages.First(p => p.Id == "a-mano");
        Check("uno armado a mano no ofrece descarga", !vm.DownloadCommand.CanExecute(null) && vm.DownloadText.Contains("a mano"),
            vm.DownloadText);
        Check("y su estado dice qué archivo le falta", vm.SelectedRow.StatusText.Contains("falta"), vm.SelectedRow.StatusText);
    }

    /// <summary>
    /// La descarga contra la red real. Se baja SOLO el VAE —0,31 GB— y no los 6,5 GB
    /// del paquete entero: alcanza para ejercer el camino completo (URL con subcarpeta,
    /// streaming a .part, promoción al nombre final, armado del paquete) sin gastar
    /// media hora en cada corrida.
    /// </summary>
    public static async Task CorrerDescarga(Action<string, bool, string> check, bool conRed)
    {
        void Check(string n, bool c, string d = "") => check(n, c, d);

        if (!conRed)
        {
            Check("descarga real del VAE", true, "(omitida: usar --con-red)");
            return;
        }

        // Se usa el descriptor REAL de tres componentes, con los dos pesados ya
        // presentes como archivos de mentira. Así se ejerce el camino completo —salteo
        // de lo existente, descarga con subcarpeta, armado del paquete por rol— bajando
        // solo 0,31 GB en lugar de 6,5.
        //
        // Una versión anterior usaba un descriptor con un único componente, y eso
        // escondía el defecto: el armado mapeaba por posición y le asignaba el VAE al
        // hueco de la difusión. Un descriptor recortado no es el descriptor.
        var dir = NuevaCarpeta("descarga");
        var fuente = ImageModelService.RecommendedSource;
        foreach (var c in fuente.Components.Where(c => c.Role != "VAE"))
            File.WriteAllBytes(Path.Combine(dir, c.LocalFileName), new byte[16 * 1024]);

        using var d = new ImagePackageDownloader();
        var progresos = new List<PackageDownloadProgress>();
        d.ProgressChanged += (_, p) => progresos.Add(p);

        var r = await d.DownloadAsync(fuente, dir);

        Check("descarga el VAE desde el espejo abierto", r.Success, r.ErrorMessage ?? "");
        var destino = Path.Combine(dir, "ae.safetensors");
        Check("lo guarda con el nombre suelto, sin la subcarpeta", File.Exists(destino));
        if (File.Exists(destino))
        {
            Check("el archivo tiene el tamaño esperado",
                new FileInfo(destino).Length == 335_304_388, $"{new FileInfo(destino).Length}");
        }
        Check("no queda ningún .part", Directory.GetFiles(dir, "*.part").Length == 0);
        Check("informó progreso durante la descarga", progresos.Count > 0, $"{progresos.Count}");
        Check("el progreso nombra el componente",
            progresos.Count > 0 && progresos[0].ComponentRole == "VAE",
            progresos.Count > 0 ? progresos[0].ComponentRole : "");
        Check("saltea los componentes que ya estaban",
            r.AlreadyPresent.Count == 2, string.Join(",", r.AlreadyPresent));

        // El armado va por rol y no por posición: cada archivo tiene que caer en su
        // hueco. Mapear por índice daba un paquete cruzado que igual parecía completo.
        Check("el VAE queda en el hueco del VAE",
            r.Package is not null && r.Package.VaePath.EndsWith("ae.safetensors"),
            r.Package?.VaePath ?? "null");
        Check("la difusión queda en el hueco de la difusión",
            r.Package is not null && r.Package.DiffusionModelPath.EndsWith("z_image_turbo-Q4_K.gguf"),
            r.Package?.DiffusionModelPath ?? "null");
        Check("el text encoder queda en su hueco",
            r.Package is not null && r.Package.TextEncoderPath.Contains("Qwen3-4B"),
            r.Package?.TextEncoderPath ?? "null");
        Check("el paquete armado queda completo", r.Package is not null && r.Package.IsComplete);

        // Volver a bajarlo tiene que saltearlo, no fallar: el DownloadAsync de E3
        // rechaza un archivo existente, y sin este salteo un reintento tras una
        // cancelación quedaría trabado para siempre.
        var r2 = await d.DownloadAsync(fuente, dir);
        Check("un componente ya presente se saltea en vez de fallar", r2.Success, r2.ErrorMessage ?? "");
        Check("y se informa que ya estaba", r2.AlreadyPresent.Contains("VAE"),
            string.Join(",", r2.AlreadyPresent));

        // Cancelación: no debe dejar .part ni un paquete declarado utilizable.
        var dir2 = NuevaCarpeta("cancelada");
        foreach (var c in fuente.Components.Where(c => c.Role != "VAE"))
            File.WriteAllBytes(Path.Combine(dir2, c.LocalFileName), new byte[16 * 1024]);

        using var cts = new CancellationTokenSource();
        using var d2 = new ImagePackageDownloader();
        d2.ProgressChanged += (_, p) => { if (p.BytesDownloaded > 4_000_000) cts.Cancel(); };
        var r3 = await d2.DownloadAsync(fuente, dir2, cts.Token);

        Check("cancelar la descarga no la da por buena", !r3.Success);
        Check("cancelar deja el resultado marcado como cancelado", r3.WasCancelled, r3.ErrorMessage ?? "");
        Check("cancelar no deja .part", Directory.GetFiles(dir2, "*.part").Length == 0,
            string.Join(",", Directory.GetFiles(dir2, "*.part").Select(Path.GetFileName)));
        Check("cancelar no deja el archivo final a medias",
            !File.Exists(Path.Combine(dir2, "ae.safetensors")));
    }
}
