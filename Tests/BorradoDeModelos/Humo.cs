using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using PRImager.AIAssistant.Services.HuggingFace;
using PRImager.AIAssistant.ViewModels.HuggingFace;
using PRImager.AIAssistant.Views.HuggingFace;

/// <summary>
/// Prueba de humo de la ventana del catálogo con la sección nueva.
///
/// Un XAML que compila puede tirar la ventana al abrirla: fue el defecto 4.1, un
/// ProgressBar.Value bindeado TwoWay contra una propiedad de solo lectura. Acá se
/// abre la ventana de verdad, se la fuerza a hacer layout y se escuchan los
/// errores de binding, que WPF por defecto se traga en silencio.
/// </summary>
static class Humo
{
    static int _ok, _fail;

    static void Check(string nombre, bool cond, string detalle = "")
    {
        if (cond) { _ok++; Console.WriteLine($"  OK   {nombre}"); }
        else { _fail++; Console.WriteLine($"  FALLA {nombre}   {detalle}"); }
    }

    public static int Correr()
    {
        Console.WriteLine("\nprueba de humo: se abre la ventana del catalogo de verdad");

        // Los errores de binding van a Debug con nivel Error; se capturan acá.
        var traza = new StringWriter();
        var escucha = new System.Diagnostics.TextWriterTraceListener(traza);
        System.Diagnostics.PresentationTraceSources.Refresh();
        System.Diagnostics.PresentationTraceSources.DataBindingSource.Listeners.Add(escucha);
        System.Diagnostics.PresentationTraceSources.DataBindingSource.Switch.Level =
            System.Diagnostics.SourceLevels.Error;

        var app = new Application();
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/PR-AI-Assistant;component/Styles/ModernControls.xaml")
        });
        app.Resources["MainFont"] = new FontFamily("Segoe UI");
        app.Resources["MonoFont"] = new FontFamily("Consolas");

        string raiz = Path.Combine(Path.GetTempPath(), "BancoBorrado", "humo");
        if (Directory.Exists(raiz)) Directory.Delete(raiz, true);
        string desc = Path.Combine(raiz, "Descargas");
        string logs = Path.Combine(raiz, "Logs");
        Directory.CreateDirectory(desc);
        Directory.CreateDirectory(logs);

        File.WriteAllBytes(Path.Combine(desc, "Qwen3.5-9B-Q5_K_S.gguf"), new byte[64 * 1024]);
        File.WriteAllBytes(Path.Combine(desc, "Phi-4-Q4_K_M.gguf"), new byte[32 * 1024]);
        string activo = Path.Combine(desc, "Phi-4-Q4_K_M.gguf");

        var store = new DownloadedModelStore(desc, Array.Empty<string>(), () => activo);
        var vm = new ModelCatalogViewModel(
            destinationDirectory: desc,
            contextTokens: 32768,
            logDirectory: logs,
            store: store,
            confirmDeletion: _ => false);

        ModelCatalogWindow ventana = null;
        Exception fallo = null;

        try
        {
            ventana = new ModelCatalogWindow
            {
                DataContext = vm,
                // Fuera de pantalla: el banco no debe robar el foco
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -4000,
                Top = -4000,
                ShowInTaskbar = false
            };
            ventana.Show();
            ventana.UpdateLayout();

            // Que el Dispatcher procese el render pendiente
            ventana.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
        }
        catch (Exception ex)
        {
            fallo = ex;
        }

        Check("la ventana abre sin lanzar", fallo == null, fallo?.Message ?? "");

        if (ventana != null)
        {
            var grilla = BuscarPorId(ventana, "DownloadedGrid") as System.Windows.Controls.DataGrid;
            Check("la grilla de descargados existe en el arbol visual", grilla != null);
            Check("muestra los dos modelos", grilla != null && grilla.Items.Count == 2,
                  $"muestra {grilla?.Items.Count}");

            var botonBorrar = BuscarPorId(ventana, "DeleteDownloadedButton") as System.Windows.Controls.Button;
            Check("el boton de eliminar existe", botonBorrar != null);
            Check("arranca deshabilitado, sin seleccion", botonBorrar != null && !botonBorrar.IsEnabled);

            // Seleccionar el que NO es el activo debe habilitarlo
            vm.SelectedDownloaded = vm.DownloadedModels.First(m => !m.IsActive);
            ventana.UpdateLayout();
            Check("se habilita al seleccionar un modelo eliminable",
                  botonBorrar != null && botonBorrar.IsEnabled);

            // Seleccionar el activo debe volver a deshabilitarlo
            vm.SelectedDownloaded = vm.DownloadedModels.First(m => m.IsActive);
            ventana.UpdateLayout();
            Check("se deshabilita al seleccionar el modelo en uso",
                  botonBorrar != null && !botonBorrar.IsEnabled);

            var aviso = BuscarPorId(ventana, "DownloadedHint") as System.Windows.Controls.TextBlock;
            Check("el aviso del modelo en uso se muestra",
                  aviso != null && aviso.Visibility == Visibility.Visible,
                  $"visibilidad {aviso?.Visibility}");

            var resumen = BuscarPorId(ventana, "DownloadedSummary") as System.Windows.Controls.TextBlock;
            Check("el resumen informa el disco ocupado",
                  resumen != null && resumen.Text.Contains("2 modelos"), resumen?.Text ?? "");

            // El texto dibujado, no solo el color: un valor local en el XAML le gana
            // al DataTrigger y la celda se queda con el texto por defecto en verde.
            var textos = TodosLosTextos(ventana).ToList();
            Check("la celda del modelo en uso dice «en uso»",
                  textos.Any(t => t.Contains("en uso")),
                  "ninguna celda lo dice: revisar la precedencia del DataTrigger");
            Check("las demas filas quedan con el guion", textos.Count(t => t.Trim() == "—") == 1,
                  $"hay {textos.Count(t => t.Trim() == "—")} guiones");

            // Sin seleccion para la captura, que muestre el estado de apertura
            vm.SelectedDownloaded = null;
            ventana.UpdateLayout();
            ventana.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
            Capturar(ventana, Path.Combine(Path.GetTempPath(), "catalogo-con-descargados.png"));

            ventana.Close();
        }

        vm.Dispose();

        escucha.Flush();
        string errores = traza.ToString();
        System.Diagnostics.PresentationTraceSources.DataBindingSource.Listeners.Remove(escucha);

        Check("WPF no reporto errores de binding", errores.Trim().Length == 0,
              errores.Length > 400 ? errores.Substring(0, 400) : errores);

        app.Shutdown();

        Console.WriteLine($"\n{_ok} aserciones de humo OK, {_fail} fallas");
        return _fail;
    }

    /// <summary>Guarda la ventana como PNG para poder mirar el layout.</summary>
    static void Capturar(Window ventana, string destino)
    {
        try
        {
            var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(
                (int)ventana.ActualWidth, (int)ventana.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(ventana);

            var codificador = new System.Windows.Media.Imaging.PngBitmapEncoder();
            codificador.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));

            using var fs = new FileStream(destino, FileMode.Create);
            codificador.Save(fs);
            Console.WriteLine($"  captura: {destino}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  (no se pudo capturar: {ex.Message})");
        }
    }

    /// <summary>Todos los textos efectivamente dibujados en el árbol visual.</summary>
    static System.Collections.Generic.IEnumerable<string> TodosLosTextos(DependencyObject raiz)
    {
        if (raiz is System.Windows.Controls.TextBlock tb && !string.IsNullOrEmpty(tb.Text))
            yield return tb.Text;

        int n = VisualTreeHelper.GetChildrenCount(raiz);
        for (int i = 0; i < n; i++)
            foreach (string t in TodosLosTextos(VisualTreeHelper.GetChild(raiz, i)))
                yield return t;
    }

    static object BuscarPorId(DependencyObject raiz, string id)
    {
        if (raiz is UIElement e &&
            System.Windows.Automation.AutomationProperties.GetAutomationId(raiz) == id)
            return e;

        int n = VisualTreeHelper.GetChildrenCount(raiz);
        for (int i = 0; i < n; i++)
        {
            var hijo = VisualTreeHelper.GetChild(raiz, i);
            var r = BuscarPorId(hijo, id);
            if (r != null) return r;
        }
        return null;
    }
}
