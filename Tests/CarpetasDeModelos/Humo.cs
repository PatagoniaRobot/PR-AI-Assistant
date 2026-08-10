using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using PRImager.AIAssistant.Services.Folders;
#if APP_CON_IMAGENES
using PRImager.AIAssistant.Services.ImageGen;
#endif
using PRImager.AIAssistant.Services.Models;
using PRImager.AIAssistant.ViewModels;
using PRImager.AIAssistant.Views;

/// <summary>
/// Prueba de humo: abre la ventana de carpetas de verdad.
///
/// En este proyecto, las cuatro veces que apareció un defecto de dibujo lo encontró la
/// captura y no una aserción: la fila con el texto equivocado (4.13), el estado cortado del
/// selector de paquetes, el botón de generar abajo del scroll y la galería vacía dejando una
/// franja muerta. Por eso acá se abre la ventana, se fuerza el layout, se escuchan los
/// errores de binding que WPF se traga, se mide **dónde quedó** el botón del pie —no si está
/// visible, que un control fuera del área de un ScrollViewer informa true igual— y se deja
/// una captura para mirar.
/// </summary>
static class Humo
{
    static int _ok, _fail;

    static void Check(string nombre, bool cond, string detalle = "")
    {
        if (cond) { _ok++; Console.WriteLine($"  OK   {nombre}"); }
        else { _fail++; Console.WriteLine($"  FALLA {nombre}   {detalle}"); }
    }

    public static (int ok, int fail) Correr()
    {
        Console.WriteLine("\n-- Humo: se abre la ventana de carpetas de verdad --");

        var traza = new StringWriter();
        var escucha = new System.Diagnostics.TextWriterTraceListener(traza);
        System.Diagnostics.PresentationTraceSources.Refresh();
        System.Diagnostics.PresentationTraceSources.DataBindingSource.Listeners.Add(escucha);
        System.Diagnostics.PresentationTraceSources.DataBindingSource.Switch.Level =
            System.Diagnostics.SourceLevels.Error;

        var app = Application.Current ?? new Application();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        try
        {
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/PR-AI-Assistant;component/Styles/ModernControls.xaml")
            });
        }
        catch (Exception) { }
        app.Resources["MainFont"] = new FontFamily("Segoe UI");
        app.Resources["MonoFont"] = new FontFamily("Consolas");

        string raiz = Path.Combine(Path.GetTempPath(), "BancoCarpetasHumo");
        if (Directory.Exists(raiz)) Directory.Delete(raiz, true);
        Directory.CreateDirectory(raiz);

        // A propósito adentro del directorio de salida: es el caso que dispara el aviso.
        string enBin = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ModelosDescargadosHumo");
        Directory.CreateDirectory(enBin);
        File.WriteAllBytes(Path.Combine(enBin, "modelo.gguf"), new byte[512 * 1024]);

        var cfg = new AppSettings { ModelPath = raiz, ModelDownloadPath = enBin };
        var svc = new AppFolderService(cfg, () => { });
#if APP_CON_IMAGENES
        var img = new ImageModelService(Path.Combine(raiz, "image_models.json"));
        img.ModelsDirectory = Path.Combine(raiz, "ModelosImagen");
        img.OutputDirectory = Path.Combine(raiz, "Imagenes");
        svc.UseImageModelService(img);
#endif

        var dialogos = new DialogoFalso();
        var vm = new FolderSettingsViewModel(svc, dialogos);

        FolderSettingsWindow ventana = null;
        Exception fallo = null;

        try
        {
            ventana = new FolderSettingsWindow(vm)
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -4000, Top = -4000, ShowInTaskbar = false
            };
            ventana.Show();
            ventana.UpdateLayout();
            ventana.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
        }
        catch (Exception ex) { fallo = ex; }

        Check("la ventana abre sin lanzar", fallo is null, fallo?.Message ?? "");

#if !APP_CON_IMAGENES
        // Edición institucional: no hay ninguna carpeta que administrar, y la ventana
        // tiene que decirlo en lugar de dibujar una lista vacía sin explicación.
        if (ventana != null)
        {
            var listaVacia = BuscarPorId(ventana, "FolderList") as System.Windows.Controls.ItemsControl;
            Check("no dibuja ninguna carpeta", listaVacia != null && listaVacia.Items.Count == 0,
                $"dibuja {listaVacia?.Items.Count}");

            var vacio = BuscarPorId(ventana, "FolderEmptyText") as System.Windows.Controls.TextBlock;
            Check("explica que esta edición no administra carpetas",
                vacio != null && vacio.IsVisible, "IsVisible, no Visibility: importa el ancestro");

            Capturar(ventana, Path.Combine(Path.GetTempPath(), "ventana-de-carpetas-forense.png"));
            ventana.Close();
        }
#else
        if (ventana != null)
        {
            var lista = BuscarPorId(ventana, "FolderList") as System.Windows.Controls.ItemsControl;
            Check("la lista de carpetas existe en el árbol visual", lista != null);
            Check("dibuja las tres carpetas", lista != null && lista.Items.Count == 3,
                $"dibuja {lista?.Items.Count}");

            var textos = TodosLosTextos(ventana).ToList();

            Check("nombra las tres carpetas",
                textos.Any(t => t.Contains("Modelos de lenguaje")) &&
                textos.Any(t => t.Contains("Modelos de imagen")) &&
                textos.Any(t => t.Contains("Imágenes generadas")),
                string.Join(" | ", textos.Take(12)));

            Check("explica que cambiar la carpeta no mueve nada",
                textos.Any(t => t.Contains("archivos nuevos")),
                "el usuario tiene que saberlo ANTES de cambiar, no después");

            // El aviso de bin\: control positivo y negativo en la misma ventana.
            var avisos = textos.Where(t => t.Contains("limpieza de compilación")).ToList();
            Check("avisa que la carpeta está adentro del directorio de la aplicación",
                avisos.Count == 1, $"encontró {avisos.Count} avisos");
            Check("y NO lo avisa para las otras dos, que están afuera", avisos.Count <= 1,
                "sin este control, el aviso podría estar saliendo siempre");

            Check("informa el contenido de la carpeta con el aviso",
                textos.Any(t => t.Contains("1 archivo") && t.Contains("KB") || t.Contains("1 archivo · ")),
                string.Join(" | ", textos.Where(t => t.Contains("archivo"))));

            // Nada pendiente todavía: el panel de movimiento no puede estar ocupando lugar.
            var mover = BuscarTextoDeBoton(ventana, "Mover los archivos");
            Check("el botón de mover no se dibuja sin nada pendiente",
                mover is null || !mover.IsVisible,
                "una franja muerta desde el arranque es lo que dejaba la galería vacía");

            // La ruta completa tiene que verse, no quedar cortada contra el borde del campo.
            // Un TextBox no recorta el texto: lo desplaza, así que Text sigue entero y
            // ninguna aserción sobre el contenido lo nota. Hay que medir si ENTRA.
            //
            // Y se mide ACÁ, con la ruta larga en pantalla. Una primera versión lo hacía
            // después de cambiar la carpeta, cuando la que se dibuja es una ruta corta de
            // %TEMP% que entra de sobra: la aserción pasaba con TextWrapping en NoWrap, o
            // sea sobre el defecto exacto que existe para detectar. Es la misma forma de
            // trampa que el progreso medido solo a 1024×1024.
            var rutasCortadas = TextBoxesDesbordados(ventana).ToList();
            Check("las rutas largas se ven enteras en su campo", rutasCortadas.Count == 0,
                string.Join(" | ", rutasCortadas.Take(3)));

            // Se captura ANTES de cambiar la carpeta, que es cuando se ve el aviso de bin\.
            // Sin esta primera captura, la única que queda es la de después del cambio, y
            // justamente el aviso que motiva media pantalla no aparece en ninguna imagen.
            Capturar(ventana, Path.Combine(Path.GetTempPath(), "ventana-de-carpetas-aviso.png"));

            // Y ahora sí, con un movimiento pendiente — provocado por el camino real, no
            // fabricando el estado a mano. Una primera versión seteaba HasPendingMove
            // directamente y el botón salía deshabilitado: el banco estaba montando un
            // estado que la aplicación nunca produce así, y la aserción habría reportado
            // un defecto que no existe. Se ejerce el comando que aprieta el usuario.
            dialogos.ProximaCarpeta = Path.Combine(raiz, "ModelosMudados");
            vm.ChangeFolderCommand.Execute(
                vm.Folders.First(f => f.Kind == AppFolderKind.LanguageModels));
            ventana.UpdateLayout();
            ventana.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);

            mover = BuscarTextoDeBoton(ventana, "Mover los archivos");
            Check("con algo pendiente, el botón de mover SÍ se dibuja",
                mover != null && mover.IsVisible,
                "es el control positivo de la aserción anterior");
            Check("y está habilitado", mover != null && mover.IsEnabled);
            Check("se dibuja también el «dejarlos donde están»",
                BuscarTextoDeBoton(ventana, "Dejarlos donde están")?.IsVisible == true);

            // El pie no puede irse abajo del scroll: es el defecto que la captura encontró
            // en la vista de generación. IsVisible no alcanza para saberlo.
            var cerrar = BuscarPorId(ventana, "FolderCloseButton") as System.Windows.Controls.Button;
            Check("el botón de cerrar existe", cerrar != null);
            Check("el botón de cerrar queda DENTRO del área visible de la ventana",
                cerrar != null && EstaDentro(cerrar, ventana),
                "el pie tiene que estar fuera del ScrollViewer");

            var cortados = TextBlocksDesbordados(ventana).ToList();
            Check("ningún texto queda cortado", cortados.Count == 0,
                string.Join(" | ", cortados.Take(5)));


            ventana.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
            Capturar(ventana, Path.Combine(Path.GetTempPath(), "ventana-de-carpetas.png"));
            ventana.Close();
        }
#endif

        try { Directory.Delete(enBin, true); } catch (Exception) { }

        escucha.Flush();
        var errores = traza.ToString();
        System.Diagnostics.PresentationTraceSources.DataBindingSource.Listeners.Remove(escucha);

        Check("WPF no reportó errores de binding", errores.Trim().Length == 0,
            errores.Length > 400 ? errores.Substring(0, 400) : errores);

        return (_ok, _fail);
    }

    /// <summary>
    /// Compara el rectángulo del control contra el de la ventana. Un control desplazado
    /// fuera del área de un ScrollViewer sigue informando IsVisible en true, así que la
    /// propiedad no responde la pregunta que importa.
    /// </summary>
    static bool EstaDentro(FrameworkElement e, Window ventana)
    {
        try
        {
            if (!e.IsVisible || e.ActualWidth <= 0) return false;

            var esquina = e.TransformToAncestor(ventana).Transform(new Point(0, 0));
            var rect = new Rect(esquina, new Size(e.ActualWidth, e.ActualHeight));
            var area = new Rect(0, 0, ventana.ActualWidth, ventana.ActualHeight);

            return area.Contains(rect);
        }
        catch (Exception)
        {
            return false;
        }
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

    static System.Windows.Controls.Button BuscarTextoDeBoton(DependencyObject raiz, string texto)
    {
        if (raiz is System.Windows.Controls.Button b &&
            b.Content is System.Windows.Controls.TextBlock tb && tb.Text == texto)
            return b;
        if (raiz is System.Windows.Controls.Button b2 && (b2.Content as string) == texto)
            return b2;

        int n = VisualTreeHelper.GetChildrenCount(raiz);
        for (int i = 0; i < n; i++)
        {
            var r = BuscarTextoDeBoton(VisualTreeHelper.GetChild(raiz, i), texto);
            if (r != null) return r;
        }
        return null;
    }

    static IEnumerable<string> TextBlocksDesbordados(DependencyObject raiz)
    {
        if (raiz is System.Windows.Controls.TextBlock tb &&
            !string.IsNullOrWhiteSpace(tb.Text) && tb.ActualWidth > 0 &&
            tb.TextWrapping == TextWrapping.NoWrap)
        {
            var ft = new FormattedText(
                tb.Text, System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                new Typeface(tb.FontFamily, tb.FontStyle, tb.FontWeight, tb.FontStretch),
                tb.FontSize, Brushes.Black,
                VisualTreeHelper.GetDpi(tb).PixelsPerDip);

            if (ft.Width > tb.ActualWidth + 2)
                yield return $"«{tb.Text}» necesita {ft.Width:N0}px y tiene {tb.ActualWidth:N0}px";
        }

        int n = VisualTreeHelper.GetChildrenCount(raiz);
        for (int i = 0; i < n; i++)
            foreach (var t in TextBlocksDesbordados(VisualTreeHelper.GetChild(raiz, i)))
                yield return t;
    }

    /// <summary>
    /// Campos de texto cuyo contenido no entra en el ancho disponible.
    ///
    /// Se mide contra el ancho de una sola línea cuando el campo NO envuelve; si envuelve,
    /// se comprueba que la altura alcance para las líneas que hacen falta. Un TextBox no
    /// deja ninguna marca cuando el texto no entra —lo desplaza—, así que la propiedad Text
    /// sigue completa y una aserción sobre el contenido pasa igual.
    /// </summary>
    static IEnumerable<string> TextBoxesDesbordados(DependencyObject raiz)
    {
        if (raiz is System.Windows.Controls.TextBox tb &&
            !string.IsNullOrWhiteSpace(tb.Text) && tb.ActualWidth > 0)
        {
            double disponible = tb.ActualWidth - tb.Padding.Left - tb.Padding.Right - 4;

            var ft = new FormattedText(
                tb.Text, System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                new Typeface(tb.FontFamily, tb.FontStyle, tb.FontWeight, tb.FontStretch),
                tb.FontSize, Brushes.Black,
                VisualTreeHelper.GetDpi(tb).PixelsPerDip);

            if (tb.TextWrapping == TextWrapping.NoWrap)
            {
                if (ft.Width > disponible)
                    yield return $"«{tb.Text}» necesita {ft.Width:N0}px y tiene {disponible:N0}px";
            }
            else
            {
                ft.MaxTextWidth = Math.Max(disponible, 1);
                if (ft.Height > tb.ActualHeight + 2)
                    yield return $"«{tb.Text}» necesita {ft.Height:N0}px de alto y tiene {tb.ActualHeight:N0}px";
            }
        }

        int n = VisualTreeHelper.GetChildrenCount(raiz);
        for (int i = 0; i < n; i++)
            foreach (var t in TextBoxesDesbordados(VisualTreeHelper.GetChild(raiz, i)))
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

    static object BuscarPorId(DependencyObject raiz, string id)
    {
        if (raiz is UIElement e &&
            System.Windows.Automation.AutomationProperties.GetAutomationId(raiz) == id)
            return e;

        int n = VisualTreeHelper.GetChildrenCount(raiz);
        for (int i = 0; i < n; i++)
        {
            var r = BuscarPorId(VisualTreeHelper.GetChild(raiz, i), id);
            if (r != null) return r;
        }
        return null;
    }
}
