using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PRImager.AIAssistant.Services;
using PRImager.AIAssistant.Services.Models;
using PRImager.AIAssistant.ViewModels;

// Banco de prueba para 9.3: carga de archivos en Modo General.
//
// Lo que hay que verificar acá no es que se pueda adjuntar, sino que un archivo
// adjuntado en Modo General NO se convierta en evidencia: no debe entrar al contexto
// forense, no debe aparecer en el panel de la derecha y no debe quedar en el
// historial. Y que el Modo Investigación siga comportándose exactamente como antes.

class Banco
{
    static int _ok, _fail;

    static void Check(string nombre, bool cond, string detalle = "")
    {
        if (cond) { _ok++; Console.WriteLine($"  OK   {nombre}"); }
        else { _fail++; Console.WriteLine($"  FALLA {nombre}   {detalle}"); }
    }

    // --- Doble del diálogo de archivos: devuelve rutas fijas y anota el título ---
    class DialogoFalso : IFileDialogService
    {
        public string[] ArchivosTexto = Array.Empty<string>();
        public string[] Imagenes = Array.Empty<string>();
        public string UltimoTituloTexto = "";
        public string UltimoTituloImagen = "";

        public string[] OpenTextFiles(string title) { UltimoTituloTexto = title; return ArchivosTexto; }
        public string[] OpenImages(string title) { UltimoTituloImagen = title; return Imagenes; }
        public string[] OpenFiles(string title, string filter) => Array.Empty<string>();
        public string SaveFile(string title, string filter, string defaultFileName) => null;
    }

    static string _raiz;

    static string NuevoCaso(string nombre)
    {
        _raiz = Path.Combine(Path.GetTempPath(), "BancoCarga", nombre);
        if (Directory.Exists(_raiz)) Directory.Delete(_raiz, true);
        Directory.CreateDirectory(_raiz);
        Directory.CreateDirectory(Path.Combine(_raiz, "Logs"));
        return _raiz;
    }

    static string Archivo(string nombre, string contenido)
    {
        string p = Path.Combine(_raiz, nombre);
        File.WriteAllText(p, contenido);
        return p;
    }

    // Arma un MainViewModel real. Nada de esto arranca llama-server: el banco no
    // necesita inferencia para saber a dónde fue a parar el archivo.
    static (MainViewModel vm, DialogoFalso dialogo, ChatOrchestrator orq) NuevoVm(
        OperationMode modo, UsageProfile perfil)
    {
        string settingsPath = Path.Combine(_raiz, "settings.json");
        var config = new ConfigManager(settingsPath);
        config.Save(new AppSettings { ModelPath = _raiz, LogsPath = Path.Combine(_raiz, "Logs") });

        var logger = new ForensicLogger(Path.Combine(_raiz, "Logs"));
        var orq = new ChatOrchestrator(
            new LlamaServerManager(),
            new LlamaChatClient("http://127.0.0.1:59999"),
            logger,
            config);

        var perfiles = new UsageProfileService(Path.Combine(_raiz, "profiles.json"));
        perfiles.Load();

        var dialogo = new DialogoFalso();
        var vm = new MainViewModel(
            orq, dialogo, config,
            new ModelProfileService(Path.Combine(_raiz, "catalogo.json"), _raiz),
            perfiles);

        vm.CurrentMode = modo;
        if (perfil != null) vm.SelectedUsageProfile = perfil;

        return (vm, dialogo, orq);
    }

    static UsageProfile PerfilConCarga(bool habilitada) => new()
    {
        Id = "banco-general",
        Name = "General de prueba",
        SystemPrompt = "Sos un asistente.",
        Temperature = 0.7,
        FileUploadEnabled = habilitada,
        WebSearchEnabled = false,
        ModelDownloadEnabled = false,
        MandatoryLog = false
    };

    [STAThread]
    static int Main()
    {
        Console.WriteLine("Banco de carga de archivos (9.3)\n");

        foreach (var caso in new Action[]
                 {
                     CasoPrecedencia, CasoPerfilesDeFabrica, CasoBloqueDeAdjuntos,
                     CasoGeneralNoEsEvidencia, CasoInvestigacionSinCambios,
                     CasoTextoDelDialogo, CasoAuditoria, CasoQuitarAdjunto,
                     CasoNuevaSesion, CasoPerfilSinCapacidad
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

        // La UI al final: monta una Application y no conviene mezclarla con lo demás.
        NuevoCaso("humo");
        int fallasHumo = Humo.Correr(
            (modo, perfil) =>
            {
                var (vm, dlg, orq) = NuevoVm(modo, perfil);
                return (vm, dlg, orq);
            },
            (nombre, contenido) => Archivo(nombre, contenido));

        return _fail == 0 && fallasHumo == 0 ? 0 : 1;
    }

    // --- La regla de precedencia, que es el corazón de la decisión ---

    static void CasoPrecedencia()
    {
        Console.WriteLine("precedencia: el modo habilita la carga, no la quita");

        var sinCarga = PerfilConCarga(false);
        var conCarga = PerfilConCarga(true);

        var invSin = EffectiveProfile.Resolve(sinCarga, OperationMode.Investigation);
        var invCon = EffectiveProfile.Resolve(conCarga, OperationMode.Investigation);
        var genSin = EffectiveProfile.Resolve(sinCarga, OperationMode.Consultation);
        var genCon = EffectiveProfile.Resolve(conCarga, OperationMode.Consultation);

        Check("Investigación fuerza la carga aunque el perfil la niegue", invSin.FileUploadEnabled);
        Check("Investigación la mantiene si el perfil la da", invCon.FileUploadEnabled);
        Check("fuera de Investigación manda el perfil (niega)", !genSin.FileUploadEnabled);
        Check("fuera de Investigación manda el perfil (concede)", genCon.FileUploadEnabled);

        Check("en Investigación lo cargado es evidencia", invCon.FileUploadIsForensic);
        Check("en Consulta lo cargado NO es evidencia", !genCon.FileUploadIsForensic);

        // La capacidad nueva no debe haber alterado las otras dos reglas
        Check("web sigue pisada a false en Investigación",
              !EffectiveProfile.Resolve(new UsageProfile { WebSearchEnabled = true }, OperationMode.Investigation).WebSearchEnabled);
        Check("descargas siguen pisadas a false en Investigación",
              !EffectiveProfile.Resolve(new UsageProfile { ModelDownloadEnabled = true }, OperationMode.Investigation).ModelDownloadEnabled);
    }

    static void CasoPerfilesDeFabrica()
    {
        Console.WriteLine("\nlos perfiles de fábrica traen la capacidad");
        NuevoCaso("fabrica");

        var svc = new UsageProfileService(Path.Combine(_raiz, "profiles.json"));
        svc.Load();

        foreach (var p in svc.Profiles.Where(p => p.IsFactory))
            Check($"el perfil de fábrica «{p.Name}» permite adjuntar", p.FileUploadEnabled);

        // Un perfil clonado hereda la capacidad
        var clon = svc.Profiles.First(p => p.IsFactory).CloneAsEditable("clon", "Clon");
        Check("el clon hereda la capacidad", clon.FileUploadEnabled);
    }

    // --- El bloque que ve el modelo ---

    static void CasoBloqueDeAdjuntos()
    {
        Console.WriteLine("\nel bloque de adjuntos que recibe el modelo");

        Check("sin adjuntos no se antepone nada",
              AttachmentPrompt.Build(new List<string>()).Length == 0);
        Check("una lista de vacíos tampoco produce bloque",
              AttachmentPrompt.Build(new[] { "", null }).Length == 0);

        string bloque = AttachmentPrompt.Build(new[] { "CONTENIDO-UNO", "CONTENIDO-DOS" });

        Check("abre con el marcador", bloque.StartsWith(AttachmentPrompt.OpeningMarker));
        Check("cierra con el marcador", bloque.Contains(AttachmentPrompt.ClosingMarker));
        Check("incluye el primer archivo", bloque.Contains("CONTENIDO-UNO"));
        Check("incluye el segundo archivo", bloque.Contains("CONTENIDO-DOS"));
        Check("no se anuncia como contexto forense",
              !bloque.Contains("FORENSE", StringComparison.OrdinalIgnoreCase),
              "un adjunto de Modo General no es evidencia y no debe nombrarse así");
    }

    // --- Lo esencial: en General el archivo no es evidencia ---

    static void CasoGeneralNoEsEvidencia()
    {
        Console.WriteLine("\nen Modo General el archivo adjunto NO es evidencia");
        NuevoCaso("general");
        string archivo = Archivo("notas.txt", "TEXTO-DEL-ARCHIVO-ADJUNTO");

        var (vm, dialogo, orq) = NuevoVm(OperationMode.Consultation, PerfilConCarga(true));
        dialogo.ArchivosTexto = new[] { archivo };

        string promptAntes = orq.SystemPrompt;

        vm.LoadFilesCommand.Execute(null);

        Check("el adjunto aparece en la tira", vm.PendingAttachments.Count == 1,
              $"hay {vm.PendingAttachments.Count}");
        Check("la tira se hace visible", vm.HasPendingAttachments);
        Check("NO entra al panel de contexto forense", vm.LoadedArtifactsList.Count == 0,
              $"entraron {vm.LoadedArtifactsList.Count}");
        Check("NO se inyecta en el system prompt",
              orq.SystemPrompt == promptAntes,
              "el contenido del archivo se filtró al prompt de sistema");
        Check("el panel derecho sigue oculto en General", !vm.ShowContextPanel);
        Check("los botones de carga SÍ se ven", vm.ShowFileButtons);
        Check("el contenido quedó guardado para el turno",
              vm.PendingAttachments[0].AttachedContent.Contains("TEXTO-DEL-ARCHIVO-ADJUNTO"));

        orq.Dispose();
    }

    static void CasoInvestigacionSinCambios()
    {
        Console.WriteLine("\nen Investigación todo sigue como antes: es evidencia");
        NuevoCaso("investigacion");
        string archivo = Archivo("evidencia.txt", "TEXTO-DE-LA-EVIDENCIA");

        var (vm, dialogo, orq) = NuevoVm(OperationMode.Investigation, PerfilConCarga(false));
        dialogo.ArchivosTexto = new[] { archivo };

        vm.LoadFilesCommand.Execute(null);

        Check("entra al panel de contexto forense", vm.LoadedArtifactsList.Count == 1,
              $"hay {vm.LoadedArtifactsList.Count}");
        Check("NO va a la tira de adjuntos", vm.PendingAttachments.Count == 0);
        Check("se inyecta en el system prompt",
              orq.SystemPrompt.Contains("TEXTO-DE-LA-EVIDENCIA"));
        Check("el panel derecho se muestra", vm.ShowContextPanel);
        Check("los botones se ven aunque el perfil no dé la capacidad", vm.ShowFileButtons,
              "el marco institucional debe habilitarla");

        orq.Dispose();
    }

    static void CasoTextoDelDialogo()
    {
        Console.WriteLine("\nel diálogo no nombra evidencia fuera de una investigación");

        // Cada VM en su propia carpeta: dos ForensicLogger sobre el mismo archivo se
        // pelean por el handle, y eso es del banco, no del producto.
        NuevoCaso("dialogo-general");
        var (vmG, dlgG, orqG) = NuevoVm(OperationMode.Consultation, PerfilConCarga(true));
        dlgG.ArchivosTexto = new[] { Archivo("x.txt", "x") };
        vmG.LoadFilesCommand.Execute(null);

        NuevoCaso("dialogo-investigacion");
        var (vmI, dlgI, orqI) = NuevoVm(OperationMode.Investigation, PerfilConCarga(true));
        dlgI.ArchivosTexto = new[] { Archivo("x.txt", "x") };
        vmI.LoadFilesCommand.Execute(null);

        Check("en Investigación dice contexto forense",
              dlgI.UltimoTituloTexto.Contains("forense"), dlgI.UltimoTituloTexto);
        Check("en General NO dice forense",
              !dlgG.UltimoTituloTexto.Contains("forense", StringComparison.OrdinalIgnoreCase),
              dlgG.UltimoTituloTexto);
        Check("en General habla de adjuntar al mensaje",
              dlgG.UltimoTituloTexto.Contains("adjuntar"), dlgG.UltimoTituloTexto);

        orqG.Dispose();
        orqI.Dispose();
    }

    // --- Auditoría: el hecho sí, el contenido no ---

    static void CasoAuditoria()
    {
        Console.WriteLine("\nse registra el hecho de la carga, nunca el contenido");
        NuevoCaso("auditoria");
        string archivo = Archivo("secreto.txt", "CONTENIDO-QUE-NO-DEBE-QUEDAR-EN-EL-LOG");

        var (vm, dialogo, orq) = NuevoVm(OperationMode.Consultation, PerfilConCarga(true));
        dialogo.ArchivosTexto = new[] { archivo };
        vm.LoadFilesCommand.Execute(null);

        string logs = LeerLogs(Path.Combine(_raiz, "Logs"));

        Check("queda constancia de la carga", logs.Contains("FILE_ATTACHED"), "no se registró nada");
        Check("consta el nombre del archivo", logs.Contains("secreto.txt"));
        Check("consta que no es forense", logs.Contains("forense=false"));
        Check("el CONTENIDO no está en el log",
              !logs.Contains("CONTENIDO-QUE-NO-DEBE-QUEDAR-EN-EL-LOG"),
              "se filtró el contenido del archivo al log de auditoría");

        orq.Dispose();
    }

    /// <summary>
    /// Lee el log con el escritor todavía abierto.
    ///
    /// File.ReadAllText no sirve acá: el logger mantiene el archivo abierto en Write y
    /// una apertura con FileShare.Read choca. Tragarse esa IOException hacía que el
    /// banco leyera cadena vacía, con lo cual la aserción «el contenido no está en el
    /// log» pasaba porque no había log. Si no se puede leer, hay que fallar, no
    /// devolver vacío.
    /// </summary>
    static string LeerLogs(string carpeta)
    {
        if (!Directory.Exists(carpeta))
            throw new DirectoryNotFoundException($"No existe la carpeta de logs: {carpeta}");

        var archivos = Directory.GetFiles(carpeta, "*", SearchOption.AllDirectories);
        if (archivos.Length == 0)
            throw new FileNotFoundException($"El logger no creó ningún archivo en {carpeta}");

        var sb = new System.Text.StringBuilder();
        foreach (string f in archivos)
        {
            using var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs);
            sb.AppendLine(sr.ReadToEnd());
        }
        return sb.ToString();
    }

    // --- Manejo de la tira ---

    static void CasoQuitarAdjunto()
    {
        Console.WriteLine("\nel usuario puede quitar un adjunto antes de enviar");
        NuevoCaso("quitar");
        string a = Archivo("uno.txt", "UNO");
        string b = Archivo("dos.txt", "DOS");

        var (vm, dialogo, orq) = NuevoVm(OperationMode.Consultation, PerfilConCarga(true));
        dialogo.ArchivosTexto = new[] { a, b };
        vm.LoadFilesCommand.Execute(null);

        Check("se cargaron los dos", vm.PendingAttachments.Count == 2);

        var primero = vm.PendingAttachments[0];
        primero.RemoveCommand.Execute(null);

        Check("queda uno solo", vm.PendingAttachments.Count == 1);
        Check("quedó el que no se quitó", vm.PendingAttachments[0].FileName == "dos.txt",
              vm.PendingAttachments[0].FileName);

        vm.PendingAttachments[0].RemoveCommand.Execute(null);
        Check("al quitar el último la tira se oculta", !vm.HasPendingAttachments);

        orq.Dispose();
    }

    static void CasoNuevaSesion()
    {
        Console.WriteLine("\nnueva sesión no deja adjuntos colgados del turno anterior");
        NuevoCaso("nuevasesion");
        string archivo = Archivo("pendiente.txt", "PENDIENTE");

        var (vm, dialogo, orq) = NuevoVm(OperationMode.Consultation, PerfilConCarga(true));
        dialogo.ArchivosTexto = new[] { archivo };
        vm.LoadFilesCommand.Execute(null);
        Check("hay un adjunto cargado", vm.PendingAttachments.Count == 1);

        vm.ClearHistoryCommand.Execute(null);

        Check("la tira quedó vacía", vm.PendingAttachments.Count == 0);
        Check("y oculta", !vm.HasPendingAttachments);

        orq.Dispose();
    }

    static void CasoPerfilSinCapacidad()
    {
        Console.WriteLine("\nun perfil que no da la capacidad esconde los botones");
        NuevoCaso("sincapacidad");

        var (vm, _, orq) = NuevoVm(OperationMode.Consultation, PerfilConCarga(false));

        Check("los botones no se ven", !vm.ShowFileButtons);
        Check("la capacidad efectiva es false", !vm.IsFileUploadAvailable);

        orq.Dispose();
    }
}
