using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using PRImager.AIAssistant.Services.Models;

/// <summary>
/// Prueba de humo de la entrada a la generación de imágenes en la barra lateral (E4.5).
///
/// Es la única forma de verificar lo que E4.5 realmente entrega: que el usuario pueda
/// llegar. La lógica de la capacidad ya la cubre el banco; acá lo que importa es que los
/// botones existan en el árbol visual, que reflejen el estado del perfil **dibujado** y
/// que el aviso de «tu perfil no lo permite» se lea entero.
///
/// El criterio de qué se esconde y qué se deshabilita no es cosmético:
///
///   · si el binario NO trae la capacidad (edición Forense), la sección entera no existe;
///   · si el binario la trae pero el perfil no la permite, los botones se ven
///     deshabilitados y hay un aviso que lo explica.
///
/// Ocultar en el segundo caso dejaría al usuario sin forma de descubrir que la capacidad
/// existe y que su perfil se la quita.
/// </summary>
static class HumoBarraLateral
{
    static int _ok, _fail;

    static void Check(string nombre, bool cond, string detalle = "")
    {
        if (cond) { _ok++; Console.WriteLine($"  OK    {nombre}"); }
        else { _fail++; Console.WriteLine($"  FALLA {nombre}   {detalle}"); }
    }

    public static int Correr()
    {
        Console.WriteLine("\n-- Humo: la entrada a imágenes en la barra lateral (E4.5) --");

        var traza = new StringWriter();
        var escucha = new System.Diagnostics.TextWriterTraceListener(traza);
        System.Diagnostics.PresentationTraceSources.Refresh();
        System.Diagnostics.PresentationTraceSources.DataBindingSource.Listeners.Add(escucha);
        System.Diagnostics.PresentationTraceSources.DataBindingSource.Switch.Level =
            System.Diagnostics.SourceLevels.Error;

        // Ver EntornoWpf: sin fijar el ShutdownMode, la ventana del humo anterior deja la
        // aplicación apagándose y esta se dibuja vacía.
        EntornoWpf.Preparar();

        var dir = BancoIntegracion.CarpetaParaHumo();
        var vm = BancoIntegracion.NuevoMainViewModel(
            dir, OperationMode.Consultation, BancoIntegracion.Perfil(true));

        Window? ventana = null;
        Exception? fallo = null;

        try
        {
            var vista = new PRImager.AIAssistant.Views.ChatView { DataContext = vm };
            ventana = new Window
            {
                Title = "Barra lateral — humo",
                Content = vista,
                Width = 1200,
                Height = 900,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -4000,
                Top = -4000,
                ShowInTaskbar = false,
                Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x2E))
            };
            ventana.Show();
            ventana.UpdateLayout();
            ventana.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
        }
        catch (Exception ex) { fallo = ex; }

        Check("la vista de chat abre sin lanzar", fallo is null, fallo?.Message ?? "");

        if (ventana is not null)
        {
            var generar = BuscarPorId(ventana, "OpenImageGenButton") as System.Windows.Controls.Button;
            var paquetes = BuscarPorId(ventana, "OpenImagePackagesButton") as System.Windows.Controls.Button;
            var aviso = BuscarPorId(ventana, "ImageGenDisabledNotice") as System.Windows.Controls.TextBlock;

            Check("el botón de generar imágenes está en la barra lateral", generar is not null);
            Check("el botón de modelos de imagen también", paquetes is not null);

            // La barra lateral arranca CERRADA en esta aplicación, así que sin abrirla
            // nada de lo que hay adentro se dibuja. La primera versión de este humo no lo
            // hacía y tres aserciones pasaban por el motivo equivocado: «no se muestra el
            // aviso» daba OK porque no se mostraba NADA.
            Check("la barra lateral arranca cerrada, como en la aplicación",
                generar is not null && !generar.IsVisible);

            vm.IsSidebarOpen = true;
            ventana.UpdateLayout();
            ventana.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);

            // Control positivo: recién con esto las comprobaciones de visibilidad de
            // abajo significan algo.
            Check("al abrirla, el botón de generar se dibuja de verdad",
                generar is not null && generar.IsVisible);

            // --- perfil que la habilita ---
            Check("con el perfil habilitado, el botón de generar está habilitado",
                generar is not null && generar.IsEnabled);
            Check("y el de modelos también", paquetes is not null && paquetes.IsEnabled);
            Check("y no se muestra ningún aviso de perfil",
                aviso is not null && !aviso.IsVisible, $"{aviso?.Visibility}");

            // --- perfil que no la habilita ---
            vm.SelectedUsageProfile = BancoIntegracion.Perfil(false);
            ventana.UpdateLayout();
            ventana.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);

            Check("con el perfil deshabilitado, el botón de generar se deshabilita",
                generar is not null && !generar.IsEnabled);
            Check("pero NO desaparece: el usuario tiene que poder descubrir la capacidad",
                generar is not null && generar.IsVisible);
            Check("y aparece el aviso que lo explica",
                aviso is not null && aviso.IsVisible, $"{aviso?.Visibility}");
            Check("el aviso nombra al perfil como el motivo",
                aviso is not null && aviso.Text.Contains("perfil"), aviso?.Text ?? "");

            // --- Modo Investigación ---
            vm.CurrentMode = OperationMode.Investigation;
            vm.SelectedUsageProfile = BancoIntegracion.Perfil(true);
            ventana.UpdateLayout();

            Check("en Modo Investigación el botón queda deshabilitado aunque el perfil la traiga",
                generar is not null && !generar.IsEnabled);

            vm.CurrentMode = OperationMode.Consultation;
            vm.SelectedUsageProfile = BancoIntegracion.Perfil(true);
            ventana.UpdateLayout();

            // El texto dibujado, no solo que el control exista. Es la lección de 4.13.
            var textos = TodosLosTextos(ventana).ToList();
            Check("la sección se titula IMÁGENES", textos.Any(t => t == "IMÁGENES"),
                string.Join(" | ", textos.Where(t => t.Length < 20).Take(15)));
            Check("el botón dice qué hace", textos.Any(t => t.Contains("Generar imágenes")),
                string.Join(" | ", textos.Where(t => t.Contains("magen")).Take(6)));
            Check("y el otro también", textos.Any(t => t.Contains("Modelos de imagen")));

            var cortados = TextBlocksDesbordados(ventana).ToList();
            Check("ningún texto de la barra lateral queda cortado", cortados.Count == 0,
                string.Join(" | ", cortados));

            ventana.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
            Capturar(ventana, Path.Combine(Path.GetTempPath(), "barra-lateral-imagenes.png"));
            ventana.Close();
        }

        escucha.Flush();
        var errores = traza.ToString();
        System.Diagnostics.PresentationTraceSources.DataBindingSource.Listeners.Remove(escucha);

        // Los bindings de la vista de chat son muchos; un error acá es un binding roto de
        // verdad, que WPF se traga en silencio.
        Check("WPF no reportó errores de binding", errores.Trim().Length == 0,
            errores.Length > 400 ? errores.Substring(0, 400) : errores);

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
            var cod = new System.Windows.Media.Imaging.PngBitmapEncoder();
            cod.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
            using var fs = new FileStream(destino, FileMode.Create);
            cod.Save(fs);
            Console.WriteLine($"  captura: {destino}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  (no se pudo capturar: {ex.Message})");
        }
    }

    static IEnumerable<string> TextBlocksDesbordados(DependencyObject raiz)
    {
        if (raiz is System.Windows.Controls.TextBlock tb &&
            !string.IsNullOrWhiteSpace(tb.Text) && tb.ActualWidth > 0)
        {
            var ft = new FormattedText(
                tb.Text, System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                new Typeface(tb.FontFamily, tb.FontStyle, tb.FontWeight, tb.FontStretch),
                tb.FontSize, Brushes.Black,
                VisualTreeHelper.GetDpi(tb).PixelsPerDip);

            // TextTrimming declarado es una decisión de diseño, no un accidente: quien lo
            // puso ya aceptó que el texto se recorte. Lo que se busca acá es el recorte
            // silencioso.
            if (tb.TextWrapping == TextWrapping.NoWrap &&
                tb.TextTrimming == TextTrimming.None &&
                ft.Width > tb.ActualWidth + 2)
            {
                yield return $"«{tb.Text}» necesita {ft.Width:N0}px y tiene {tb.ActualWidth:N0}px";
            }
        }

        int n = VisualTreeHelper.GetChildrenCount(raiz);
        for (int i = 0; i < n; i++)
            foreach (var t in TextBlocksDesbordados(VisualTreeHelper.GetChild(raiz, i)))
                yield return t;
    }

    static IEnumerable<string> TodosLosTextos(DependencyObject raiz)
    {
        if (raiz is System.Windows.Controls.TextBlock tb && !string.IsNullOrEmpty(tb.Text))
            yield return tb.Text;

        int n = VisualTreeHelper.GetChildrenCount(raiz);
        for (int i = 0; i < n; i++)
            foreach (var t in TodosLosTextos(VisualTreeHelper.GetChild(raiz, i)))
                yield return t;
    }

    static object? BuscarPorId(DependencyObject raiz, string id)
    {
        if (raiz is UIElement e &&
            System.Windows.Automation.AutomationProperties.GetAutomationId(raiz) == id)
            return e;

        int n = VisualTreeHelper.GetChildrenCount(raiz);
        for (int i = 0; i < n; i++)
        {
            var r = BuscarPorId(VisualTreeHelper.GetChild(raiz, i), id);
            if (r is not null) return r;
        }
        return null;
    }
}
