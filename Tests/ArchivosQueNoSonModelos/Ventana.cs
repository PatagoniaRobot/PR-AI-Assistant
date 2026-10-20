#if CATALOGO
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Windows;
using System.Windows.Media;
using PRImager.AIAssistant.Services.HuggingFace;
using PRImager.AIAssistant.ViewModels.HuggingFace;
using PRImager.AIAssistant.Views.HuggingFace;

partial class Banco
{
    // --- humo: la ventana del catálogo de verdad, con lo que dibuja ---
    //
    // Se dibuja con RenderTargetBitmap, por software. El 20 de octubre de 2026 en la
    // máquina de desarrollo toda ventana WPF salía en blanco en pantalla —también una
    // de prueba de cinco líneas— y por software se veía bien: la captura de pantalla
    // no servía para mirar nada.
    //
    // Va última: una Application por proceso.

    static void CasoVentana()
    {
        Console.WriteLine("\nhumo: la ventana del catálogo dibuja qué es cada archivo");

        var traza = new StringWriter();
        var escucha = new System.Diagnostics.TextWriterTraceListener(traza);
        System.Diagnostics.PresentationTraceSources.Refresh();
        System.Diagnostics.PresentationTraceSources.DataBindingSource.Listeners.Add(escucha);
        System.Diagnostics.PresentationTraceSources.DataBindingSource.Switch.Level =
            System.Diagnostics.SourceLevels.Error;

        // El ViewModel se carga antes de que exista la Application: después, las
        // continuaciones volverían al Dispatcher y esperar con Sleep las trabaría
        var servidor = new HuggingFaceFalso(ArchivosDePrueba());
        string raiz = NuevaCarpeta("ventana");
        using var vm = new ModelCatalogViewModel(
            Path.Combine(raiz, "Descargas"), 8192, Path.Combine(raiz, "Logs"),
            client: new HuggingFaceClient(new HttpClient(servidor)),
            downloader: new ModelDownloader(new HttpClient(servidor)));
        vm.Repos.Add(new HfRepo { Id = Repo, License = "apache-2.0", Downloads = 1234 });
        vm.SelectedRepo = vm.Repos[0];
        Esperar(() => !vm.IsBusy && vm.Quantizations.Count == 6);

        var app = new Application();
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/PR-AI-Assistant;component/Styles/ModernControls.xaml")
        });
        app.Resources["MainFont"] = new FontFamily("Segoe UI");
        app.Resources["MonoFont"] = new FontFamily("Consolas");

        var ventana = new ModelCatalogWindow
        {
            DataContext = vm,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -4000,
            Top = -4000,
            ShowInTaskbar = false
        };
        ventana.Show();
        vm.SelectedQuantization = vm.Quantizations.Single(q => q.FileName == "Modelo-draft-Q8_0.gguf");
        ventana.UpdateLayout();
        ventana.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);

        var textos = Textos(ventana).ToList();
        Check("se dibuja «No es un modelo»", textos.Contains("No es un modelo"));
        Check("se dibuja «Proyector de visión»", textos.Contains("Proyector de visión"));
        Check("se dibuja «Parte de un modelo»", textos.Contains("Parte de un modelo"));
        Check("se dibuja la explicación del seleccionado", textos.Any(t => t.Contains("cabeza de borrador")));

        string captura = Path.Combine(Path.GetTempPath(), "catalogo-no-modelos.png");
        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(
            (int)ventana.ActualWidth, (int)ventana.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(ventana);
        var png = new System.Windows.Media.Imaging.PngBitmapEncoder();
        png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
        using (var fs = new FileStream(captura, FileMode.Create)) png.Save(fs);
        Console.WriteLine($"  captura: {captura}");

        ventana.Close();
        escucha.Flush();
        System.Diagnostics.PresentationTraceSources.DataBindingSource.Listeners.Remove(escucha);
        string errores = traza.ToString();
        Check("WPF no reportó errores de binding", errores.Trim().Length == 0,
              errores.Length > 400 ? errores.Substring(0, 400) : errores);
        app.Shutdown();
    }

    static IEnumerable<string> Textos(DependencyObject raiz)
    {
        int n = VisualTreeHelper.GetChildrenCount(raiz);
        for (int i = 0; i < n; i++)
        {
            var hijo = VisualTreeHelper.GetChild(raiz, i);
            if (hijo is System.Windows.Controls.TextBlock tb && !string.IsNullOrEmpty(tb.Text))
                yield return tb.Text;
            foreach (var t in Textos(hijo)) yield return t;
        }
    }
}
#endif
