using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using PRImager.AIAssistant.Services.Models;
using PRImager.AIAssistant.ViewModels;

/// <summary>
/// Prueba de humo de la tira de adjuntos.
///
/// Se dibuja la vista de chat de verdad y se lee lo que quedó en el árbol visual. Un
/// XAML que compila puede fallar al dibujar (4.1) o dibujar el texto equivocado con
/// el color correcto (4.13); ninguna de las dos cosas la ve el compilador.
/// </summary>
static class Humo
{
    static int _ok, _fail;

    static void Check(string nombre, bool cond, string detalle = "")
    {
        if (cond) { _ok++; Console.WriteLine($"  OK   {nombre}"); }
        else { _fail++; Console.WriteLine($"  FALLA {nombre}   {detalle}"); }
    }

    public static int Correr(Func<OperationMode, UsageProfile, (MainViewModel, object, IDisposable)> armar,
                             Func<string, string, string> archivo)
    {
        Console.WriteLine("\nprueba de humo: la tira de adjuntos dibujada");

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

        var perfil = new UsageProfile
        {
            Id = "humo", Name = "Humo", SystemPrompt = "x",
            Temperature = 0.7, FileUploadEnabled = true
        };

        var (vm, dialogo, desechable) = armar(OperationMode.Consultation, perfil);

        // El doble del diálogo es de otra clase; se le pasan los archivos por reflexión
        // para no acoplar este archivo a su definición.
        var campo = dialogo.GetType().GetField("ArchivosTexto");
        campo.SetValue(dialogo, new[]
        {
            archivo("informe-tecnico.txt", "CONTENIDO"),
            archivo("planilla.csv", "a,b,c")
        });

        Exception fallo = null;
        Window ventana = null;

        try
        {
            var vista = new PRImager.AIAssistant.Views.ChatView { DataContext = vm };
            ventana = new Window
            {
                Content = vista,
                Width = 1100,
                Height = 760,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -4000,
                Top = -4000,
                ShowInTaskbar = false,
                Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x2E))
            };
            ventana.Show();
            ventana.UpdateLayout();

            // La tira arranca oculta: todavía no hay adjuntos
            var tiraVacia = Buscar(ventana, "PendingAttachmentsStrip") as UIElement;
            Check("la tira arranca oculta", tiraVacia == null || tiraVacia.Visibility != Visibility.Visible,
                  $"visibilidad {tiraVacia?.Visibility}");

            vm.LoadFilesCommand.Execute(null);
            ventana.UpdateLayout();
            ventana.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);

            var tira = Buscar(ventana, "PendingAttachmentsStrip") as UIElement;
            Check("la tira se muestra al adjuntar", tira != null && tira.Visibility == Visibility.Visible,
                  $"visibilidad {tira?.Visibility}");

            var textos = TodosLosTextos(ventana).ToList();
            Check("se ve el nombre del primer adjunto", textos.Any(t => t.Contains("informe-tecnico.txt")));
            Check("se ve el nombre del segundo adjunto", textos.Any(t => t.Contains("planilla.csv")));
            Check("se ve el ícono de archivo", textos.Any(t => t.Contains("📄")));
            Check("se ve el tamaño", textos.Any(t => t.EndsWith(" B") || t.EndsWith(" KB")));

            // Quitar desde la UI, no desde el ViewModel. Se busca dentro de la tira y
            // no en toda la vista: ChatView tiene otras ✕ que no son de los adjuntos.
            var equis = TodosLosBotones((DependencyObject)tira)
                .Where(b => (b.Content as string) == "✕").ToList();
            Check("hay una ✕ por adjunto", equis.Count == 2, $"hay {equis.Count}");

            if (equis.Count > 0)
            {
                var primera = equis[0];
                primera.Command.Execute(primera.CommandParameter);
                ventana.UpdateLayout();

                Check("al pulsar la ✕ queda un solo adjunto", vm.PendingAttachments.Count == 1,
                      $"quedan {vm.PendingAttachments.Count}");
                Check("el nombre borrado ya no se dibuja",
                      !TodosLosTextos(ventana).Any(t => t.Contains("informe-tecnico.txt")));
            }

            Capturar(ventana, Path.Combine(Path.GetTempPath(), "tira-de-adjuntos.png"));
            ventana.Close();
        }
        catch (Exception ex)
        {
            fallo = ex;
        }

        Check("la vista se dibuja sin lanzar", fallo == null, fallo?.ToString() ?? "");

        desechable?.Dispose();

        escucha.Flush();
        string errores = traza.ToString();
        System.Diagnostics.PresentationTraceSources.DataBindingSource.Listeners.Remove(escucha);

        Check("WPF no reportó errores de binding", errores.Trim().Length == 0,
              errores.Length > 500 ? errores.Substring(0, 500) : errores);

        app.Shutdown();

        Console.WriteLine($"\n{_ok} aserciones de humo OK, {_fail} fallas");
        return _fail;
    }

    static void Capturar(Window ventana, string destino)
    {
        try
        {
            var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(
                (int)ventana.ActualWidth, (int)ventana.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(ventana);
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
            using var fs = new FileStream(destino, FileMode.Create);
            enc.Save(fs);
            Console.WriteLine($"  captura: {destino}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  (no se pudo capturar: {ex.Message})");
        }
    }

    static object Buscar(DependencyObject raiz, string id)
    {
        if (raiz is UIElement e &&
            System.Windows.Automation.AutomationProperties.GetAutomationId(raiz) == id)
            return e;

        int n = VisualTreeHelper.GetChildrenCount(raiz);
        for (int i = 0; i < n; i++)
        {
            var r = Buscar(VisualTreeHelper.GetChild(raiz, i), id);
            if (r != null) return r;
        }
        return null;
    }

    static System.Collections.Generic.IEnumerable<string> TodosLosTextos(DependencyObject raiz)
    {
        if (raiz is System.Windows.Controls.TextBlock tb && !string.IsNullOrEmpty(tb.Text))
            yield return tb.Text;

        int n = VisualTreeHelper.GetChildrenCount(raiz);
        for (int i = 0; i < n; i++)
            foreach (string t in TodosLosTextos(VisualTreeHelper.GetChild(raiz, i)))
                yield return t;
    }

    static System.Collections.Generic.IEnumerable<System.Windows.Controls.Button> TodosLosBotones(
        DependencyObject raiz)
    {
        if (raiz is System.Windows.Controls.Button b) yield return b;

        int n = VisualTreeHelper.GetChildrenCount(raiz);
        for (int i = 0; i < n; i++)
            foreach (var x in TodosLosBotones(VisualTreeHelper.GetChild(raiz, i)))
                yield return x;
    }
}
