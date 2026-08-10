using System;
using System.IO;
using System.Linq;
using PRImager.AIAssistant.Services;
using PRImager.AIAssistant.Services.Folders;
using PRImager.AIAssistant.Services.Models;
using PRImager.AIAssistant.ViewModels;

/// <summary>
/// El cableado: un <c>MainViewModel</c> real, que arma sus propias piezas.
///
/// Existe por la lección del defecto 4.19. E4.5 cerró con 283 aserciones y tres capturas en
/// verde, y al abrir la aplicación la vista avisaba que faltaba el motor con el motor
/// instalado: el banco siempre le pasaba la ruta al ViewModel, así que ejercía el camino
/// bueno por construcción y el defecto vivía en la costura entre lo configurado y lo
/// descubierto. Acá pasa lo mismo con el servicio de carpetas: en todo el resto del banco se
/// lo construye a mano con la instancia de imágenes ya conectada. Quien lo arma de verdad es
/// MainViewModel, y esa costura solo se ejerce instanciándolo.
/// </summary>
static class Cableado
{
    static int _ok, _fail;

    static void Check(string nombre, bool cond, string detalle = "")
    {
        if (cond) { _ok++; Console.WriteLine($"  OK   {nombre}"); }
        else { _fail++; Console.WriteLine($"  FALLA {nombre}   {detalle}"); }
    }

    public static (int ok, int fail) Correr()
    {
        Console.WriteLine("\n-- Cableado: MainViewModel arma sus propias piezas --");

        string raiz = Path.Combine(Path.GetTempPath(), "BancoCarpetasCableado");
        if (Directory.Exists(raiz)) Directory.Delete(raiz, true);
        Directory.CreateDirectory(raiz);

        string descargas = Path.Combine(raiz, "Descargas");
        Directory.CreateDirectory(descargas);

        try
        {
            var config = new ConfigManager(Path.Combine(raiz, "settings.json"));
            config.Save(new AppSettings
            {
                ModelPath = raiz,
                ModelDownloadPath = descargas,
                LogsPath = Path.Combine(raiz, "Logs")
            });

            var logger = new ForensicLogger(Path.Combine(raiz, "Logs"));
            var orq = new ChatOrchestrator(
                new LlamaServerManager(),
                new LlamaChatClient("http://127.0.0.1:59999"),
                logger,
                config);

            var perfiles = new UsageProfileService(Path.Combine(raiz, "profiles.json"));
            perfiles.Load();

            var vm = new MainViewModel(
                orq, new DialogoFalso(), config,
                new ModelProfileService(Path.Combine(raiz, "catalogo.json"), raiz),
                perfiles);

#if APP_CON_IMAGENES
            Check("la aplicación declara que hay carpetas configurables",
                vm.AreFoldersConfigurable,
                "es lo que decide si la entrada de la barra lateral se dibuja");
            Check("el comando de abrir la ventana se puede ejecutar",
                vm.OpenFolderSettingsCommand.CanExecute(null));
#else
            Check("la edición institucional no declara carpetas configurables",
                !vm.AreFoldersConfigurable,
                "la entrada de la barra lateral no debe dibujarse");
            Check("el comando queda inerte",
                !vm.OpenFolderSettingsCommand.CanExecute(null));
#endif

            orq.Dispose();
        }
        catch (Exception ex)
        {
            _fail++;
            Console.WriteLine($"  FALLA armar el MainViewModel lanzó {ex.GetType().Name}: {ex.Message}");
        }

        return (_ok, _fail);
    }
}
