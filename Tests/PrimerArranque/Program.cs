#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using PRImager.AIAssistant.Services;
using PRImager.AIAssistant.Services.Models;
using PRImager.AIAssistant.ViewModels;

// Banco del primer arranque: lo que ve quien abre PR-AIA recién bajado, sin modelos y
// sin configuración. PR-AIA se publica sin modelos (DISTRIBUCION.md).
//
// Lo que encontró la prueba a mano del 20 de octubre de 2026, y que esto vigila:
//   - el perfil de entrada era Forense, que no habilita descargas: el catálogo apagado y
//     ningún otro camino para conseguir un modelo;
//   - el chat decía «PR-AI Assistant listo» sin modelo, con enviar e iniciar apagados;
//   - la pantalla de inicio decía «no se pudo leer el modelo» sin que hubiera ninguno.

class Banco
{
    static int _ok, _fail;

    static void Check(string nombre, bool cond, string detalle = "")
    {
        if (cond) { _ok++; Console.WriteLine($"  OK   {nombre}"); }
        else { _fail++; Console.WriteLine($"  FALLA {nombre}   {detalle}"); }
    }

    static bool ConCatalogo => typeof(ConfigManager).Assembly.GetTypes().Any(t => t.Name == "ModelCatalogViewModel");

    [STAThread]
    static int Main()
    {
        Console.WriteLine($"Banco del primer arranque — edición {(ConCatalogo ? "estándar" : "Forense")}\n");
        foreach (var caso in new Action[] { CasoPerfilDeEntrada, CasoInicio, CasoChatSinModelo, CasoConModelo, CasoHumo })
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

    static string Carpeta(string nombre)
    {
        string raiz = Path.Combine(Path.GetTempPath(), "BancoPrimerArranque", nombre);
        if (Directory.Exists(raiz)) Directory.Delete(raiz, true);
        Directory.CreateDirectory(Path.Combine(raiz, "Models"));
        Directory.CreateDirectory(Path.Combine(raiz, "Logs"));
        File.WriteAllText(Path.Combine(raiz, "catalogo.json"),
            "{ \"version\": 2, \"models\": [], \"execution_profiles\": [] }");
        return raiz;
    }

    // --- el perfil con que se entra ---

    static void CasoPerfilDeEntrada()
    {
        Console.WriteLine("el perfil de uso con que arranca una instalación nueva");
        string raiz = Carpeta("perfil");
        var perfiles = new UsageProfileService(Path.Combine(raiz, "profiles.json"));
        perfiles.Load();
        string esperado = ConCatalogo ? UsageProfileService.GeneralProfileId : UsageProfileService.ForensicProfileId;
        Check($"sin profiles.json: «{esperado}»", perfiles.ActiveProfile.Id == esperado, perfiles.ActiveProfile.Id);

        // Quien ya eligió, conserva su elección
        perfiles.SetActiveProfile(UsageProfileService.ForensicProfileId);
        var otra = new UsageProfileService(Path.Combine(raiz, "profiles.json"));
        otra.Load();
        Check("un perfil ya elegido se conserva", otra.ActiveProfile.Id == UsageProfileService.ForensicProfileId, otra.ActiveProfile.Id);
    }

    // --- la pantalla de inicio ---

    static void CasoInicio()
    {
        Console.WriteLine("\nla pantalla de inicio sin modelos");
        string raiz = Carpeta("inicio");
        var cfg = new ConfigManager(Path.Combine(raiz, "settings.json"));
        cfg.Save(new AppSettings { ModelPath = Path.Combine(raiz, "Models") });
        var vm = new ModeSelectionViewModel(cfg, new ModelProfileService(Path.Combine(raiz, "catalogo.json"), Path.Combine(raiz, "Models")));
        Check("dice que no hay modelos", vm.SelectedModelContextInfo == AutoExecutionProfile.NoModelText, vm.SelectedModelContextInfo);
        Check("y no habla de un modelo ilegible", !vm.SelectedModelContextInfo.Contains("no se pudo leer"));
    }

    // --- el chat sin modelo ---

    static MainViewModel Principal(string raiz, out UsageProfileService perfiles)
    {
        var cfg = new ConfigManager(Path.Combine(raiz, "settings.json"));
        cfg.Save(new AppSettings { ModelPath = Path.Combine(raiz, "Models"), LogsPath = Path.Combine(raiz, "Logs") });
        var orq = new ChatOrchestrator(new LlamaServerManager(), new LlamaChatClient("http://127.0.0.1:59997"),
                                       new ForensicLogger(Path.Combine(raiz, "Logs")), cfg);
        perfiles = new UsageProfileService(Path.Combine(raiz, "profiles.json"));
        perfiles.Load();
        var vm = new MainViewModel(orq, new SinDialogos(), cfg,
            new ModelProfileService(Path.Combine(raiz, "catalogo.json"), Path.Combine(raiz, "Models")), perfiles);
        // Como NavigationViewModel: el modo primero, después la lista de perfiles
        vm.CurrentMode = OperationMode.Consultation;
        vm.InitializeUsageProfile();
        return vm;
    }

    static void CasoChatSinModelo()
    {
        Console.WriteLine("\nel chat sin ningún modelo");
        string raiz = Carpeta("chat");
        var vm = Principal(raiz, out var perfiles);

        Check("sabe que no hay modelo", !vm.HasAvailableModel);
        Check("el texto nombra la carpeta donde van", vm.FirstRunText.Contains(Path.Combine(raiz, "Models")), vm.FirstRunText);
        Check("abrir la carpeta siempre se puede", vm.OpenModelsFolderCommand.CanExecute(null));

        if (!ConCatalogo)
        {
            Check("Forense: no ofrece el catálogo, que no existe", !vm.CanSwitchToGeneralForDownload && !vm.IsModelDownloadAvailable);
            Check("Forense: dice que hay que copiar el archivo", vm.FirstRunText.StartsWith("Para usar el asistente, copiá"), vm.FirstRunText);
            return;
        }

        Check("con General, el catálogo está habilitado", vm.IsModelDownloadAvailable && vm.OpenModelCatalogCommand.CanExecute(null));
        Check("y el texto lo ofrece", vm.FirstRunText.StartsWith("Bajá uno desde el catálogo"), vm.FirstRunText);
        Check("sin el botón de cambiar de perfil", !vm.CanSwitchToGeneralForDownload);

        // Quien eligió Forense: el texto dice por qué no hay catálogo y ofrece la salida
        vm.SelectedUsageProfile = vm.AvailableUsageProfiles.First(p => p.Id == UsageProfileService.ForensicProfileId);
        Check("con Forense, el catálogo se apaga", !vm.OpenModelCatalogCommand.CanExecute(null));
        Check("y se ofrece pasar a General", vm.CanSwitchToGeneralForDownload && vm.UseGeneralProfileCommand.CanExecute(null));
        Check("explicando por qué", vm.FirstRunText.Contains("«Forense» no permite bajar modelos"), vm.FirstRunText);
        vm.UseGeneralProfileCommand.Execute(null);
        Check("el botón pasa a General", vm.SelectedUsageProfile?.Id == UsageProfileService.GeneralProfileId, vm.SelectedUsageProfile?.Id ?? "");
        Check("y el catálogo vuelve", vm.OpenModelCatalogCommand.CanExecute(null));

        // En Investigación el marco institucional apaga las descargas: ahí no se ofrece General
        vm.CurrentMode = OperationMode.Investigation;
        Check("en Investigación no se ofrece cambiar de perfil", !vm.CanSwitchToGeneralForDownload);
    }

    // --- con un modelo, la guía desaparece ---

    static void CasoConModelo()
    {
        Console.WriteLine("\ncon un modelo en la carpeta");
        string raiz = Carpeta("conmodelo");
        File.WriteAllText(Path.Combine(raiz, "Models", "Algo-Q4_K_M.gguf"), "x");
        var vm = Principal(raiz, out _);
        // La Forense no escanea descargas: lista solo los modelos del catálogo. En la
        // estándar la carpeta de descargas cae en la de modelos y el archivo aparece.
        Check(ConCatalogo ? "el modelo se encuentra" : "Forense: lo que no está en el catálogo no se lista",
              vm.HasAvailableModel == ConCatalogo, vm.HasAvailableModel.ToString());
        if (ConCatalogo)
            Check("y queda elegido", vm.SelectedModelItem?.Model.ModelFileName == "Algo-Q4_K_M.gguf");
    }

    // --- lo que se dibuja ---

    static void CasoHumo()
    {
        Console.WriteLine("\nhumo: la vista de chat sin modelo");
        string raiz = Carpeta("humo");
        var app = Application.Current ?? new Application();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/PR-AI-Assistant;component/Styles/ModernControls.xaml")
        });
        app.Resources["MainFont"] = new FontFamily("Segoe UI");
        app.Resources["MonoFont"] = new FontFamily("Consolas");

        var vm = Principal(raiz, out _);
        var ventana = new Window
        {
            Content = new PRImager.AIAssistant.Views.ChatView { DataContext = vm },
            Width = 1100,
            Height = 700,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -4000,
            Top = -4000,
            ShowInTaskbar = false,
            Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x2E))
        };
        ventana.Show();
        ventana.UpdateLayout();
        ventana.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);

        var textos = Textos(ventana).ToList();
        Check("se dibuja «Todavía no hay ningún modelo»", textos.Contains("Todavía no hay ningún modelo"));
        Check("no se dibuja «listo»", !textos.Any(t => t.Contains("PR-AI Assistant listo")));
        Check("la guía está visible", Buscar(ventana, "FirstRunGuide")?.IsVisible == true);
        Check(ConCatalogo ? "con el botón del catálogo" : "Forense: sin botón de catálogo",
              (Buscar(ventana, "FirstRunCatalogButton")?.IsVisible == true) == ConCatalogo);

        string captura = Path.Combine(Path.GetTempPath(), "primer-arranque.png");
        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(
            (int)ventana.ActualWidth, (int)ventana.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(ventana);
        var png = new System.Windows.Media.Imaging.PngBitmapEncoder();
        png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
        using (var fs = new FileStream(captura, FileMode.Create)) png.Save(fs);
        Console.WriteLine($"  captura: {captura}");
        ventana.Close();
    }

    static IEnumerable<string> Textos(DependencyObject raiz)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(raiz); i++)
        {
            var hijo = VisualTreeHelper.GetChild(raiz, i);
            if (hijo is System.Windows.Controls.TextBlock tb && tb.IsVisible)
            {
                if (!string.IsNullOrEmpty(tb.Text)) yield return tb.Text;
                else if (tb.Inlines.Count > 0)
                    yield return string.Concat(tb.Inlines.OfType<System.Windows.Documents.Run>().Select(r => r.Text));
            }
            foreach (var t in Textos(hijo)) yield return t;
        }
    }

    static FrameworkElement? Buscar(DependencyObject raiz, string id)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(raiz); i++)
        {
            var hijo = VisualTreeHelper.GetChild(raiz, i);
            if (hijo is FrameworkElement fe && System.Windows.Automation.AutomationProperties.GetAutomationId(fe) == id) return fe;
            var r = Buscar(hijo, id);
            if (r != null) return r;
        }
        return null;
    }
}

sealed class SinDialogos : IFileDialogService
{
    public string[]? OpenFiles(string title, string filter) => null;
    public string[]? OpenImages(string title) => null;
    public string[]? OpenTextFiles(string title) => null;
    public string? SaveFile(string title, string filter, string defaultFileName) => null;
    public string? SelectFolder(string title, string initialFolder) => null;
}
