using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using PRImager.AIAssistant.Services;
using PRImager.AIAssistant.Services.ImageGen;
using PRImager.AIAssistant.ViewModels.ImageGen;
using PRImager.AIAssistant.Views.ImageGen;

/// <summary>
/// Prueba de humo del selector de paquetes.
///
/// Un XAML que compila puede tirar la ventana al abrirla —fue el defecto 4.1— o
/// dibujarse mal sin que nadie se entere: el defecto 4.13 salió con el color correcto
/// y el texto equivocado, y lo encontró mirar la captura, no el compilador.
///
/// Por eso acá se abre la ventana de verdad, se fuerza el layout, se escuchan los
/// errores de binding que WPF se traga en silencio, y se lee el texto EFECTIVAMENTE
/// DIBUJADO en el árbol visual.
/// </summary>
static class HumoPaquetes
{
    static int _ok, _fail;

    static void Check(string nombre, bool cond, string detalle = "")
    {
        if (cond) { _ok++; Console.WriteLine($"  OK    {nombre}"); }
        else { _fail++; Console.WriteLine($"  FALLA {nombre}   {detalle}"); }
    }

    public static int Correr()
    {
        Console.WriteLine("\n-- Humo: se abre el selector de paquetes de verdad --");

        var traza = new StringWriter();
        var escucha = new System.Diagnostics.TextWriterTraceListener(traza);
        System.Diagnostics.PresentationTraceSources.Refresh();
        System.Diagnostics.PresentationTraceSources.DataBindingSource.Listeners.Add(escucha);
        System.Diagnostics.PresentationTraceSources.DataBindingSource.Switch.Level =
            System.Diagnostics.SourceLevels.Error;

        // Ver EntornoWpf: si esta ventana se cierra con el ShutdownMode de fábrica, la
        // aplicación empieza a apagarse y el humo siguiente encuentra un árbol vacío.
        EntornoWpf.Preparar();

        var raiz = Path.Combine(Path.GetTempPath(), "BancoPaquetes", "humo");
        if (Directory.Exists(raiz)) Directory.Delete(raiz, true);
        Directory.CreateDirectory(raiz);

        var svc = new ImageModelService(Path.Combine(raiz, "image_models.json"));

        // Un paquete completo y el de fábrica incompleto: el selector tiene que
        // distinguirlos a la vista, que es el criterio de esta fase.
        var modelos = Path.Combine(raiz, "modelos");
        Directory.CreateDirectory(modelos);
        string Falso(string n) { var p = Path.Combine(modelos, n); File.WriteAllBytes(p, new byte[64 * 1024]); return p; }

        svc.Save(new ImageModelPackage
        {
            Id = "completo", DisplayName = "Paquete completo de prueba", Family = "z-image",
            DiffusionModelPath = Falso("dif.gguf"),
            TextEncoderPath = Falso("te.gguf"),
            VaePath = Falso("ae.safetensors")
        });

        // Uno armado a mano al que le faltan los tres: es el que tiene que decir qué falta.
        // Los del catálogo sin bajar dicen otra cosa: «Sin descargar» y cuánto pesa.
        svc.Save(new ImageModelPackage
        {
            Id = "incompleto", DisplayName = "Paquete a mano incompleto", Family = "z-image",
            DiffusionModelPath = Path.Combine(modelos, "no-dif.gguf"),
            TextEncoderPath = Path.Combine(modelos, "no-te.gguf"),
            VaePath = Path.Combine(modelos, "no-ae.safetensors")
        });

        var dialogos = new DialogosFalsos();
        var vm = new ImagePackageSelectorViewModel(svc, dialogos, new GpuInfoService());
        vm.InitializeAsync().GetAwaiter().GetResult();

        ImagePackageSelectorWindow? ventana = null;
        Exception? fallo = null;

        try
        {
            ventana = new ImagePackageSelectorWindow(vm)
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

        if (ventana is not null)
        {
            var grilla = BuscarPorId(ventana, "PkgGrid") as System.Windows.Controls.DataGrid;
            Check("la grilla de paquetes existe en el árbol visual", grilla is not null);
            Check("muestra los tres del catálogo y los dos a mano", grilla is not null && grilla.Items.Count == 5,
                $"muestra {grilla?.Items.Count}");

            var usar = BuscarPorId(ventana, "PkgUseButton") as System.Windows.Controls.Button;
            Check("el botón «usar este paquete» existe", usar is not null);

            // Con el incompleto seleccionado el botón tiene que estar deshabilitado.
            vm.SelectedRow = vm.Packages.First(p => !p.IsComplete);
            ventana.UpdateLayout();
            Check("se deshabilita con un paquete incompleto seleccionado",
                usar is not null && !usar.IsEnabled);

            vm.SelectedRow = vm.Packages.First(p => p.IsComplete);
            ventana.UpdateLayout();
            Check("se habilita con uno completo", usar is not null && usar.IsEnabled);

            // --- el texto dibujado, no solo que el control exista ---
            var textos = TodosLosTextos(ventana).ToList();

            Check("la fila del paquete incompleto dice qué le falta",
                textos.Any(t => t.Contains("falta")),
                "ninguna celda lo dice: " + string.Join(" | ", textos.Take(20)));

            Check("los del catálogo sin bajar dicen cuánto pesan",
                textos.Count(t => t.StartsWith("Sin descargar") && t.Contains("GB")) == 3,
                string.Join(" | ", textos.Where(t => t.StartsWith("Sin descargar"))));

            Check("uno solo está marcado como recomendado para esta PC",
                textos.Count(t => t == "Recomendado") == 1,
                string.Join(" | ", textos.Where(t => t.StartsWith("Recom"))));

            Check("la fila del paquete completo dice que está listo",
                textos.Any(t => t.Contains("Listo para usar")),
                string.Join(" | ", textos.Take(20)));

            // Ninguna celda puede quedar cortada. La primera versión de esta aserción
            // buscaba puntos suspensivos y pasaba mientras el texto SÍ estaba cortado:
            // el DataGrid recorta sin agregarlos. Ahora se mide el ancho natural del
            // texto contra el ancho real del control, que es la única forma de saberlo.
            var cortados = TextBlocksDesbordados(ventana).ToList();
            Check("ningún texto queda cortado por su columna", cortados.Count == 0,
                string.Join(" | ", cortados));

            Check("el faltante nombra los tres componentes",
                textos.Any(t => t.Contains("difusión") && t.Contains("text encoder") && t.Contains("VAE")),
                string.Join(" | ", textos.Where(t => t.Contains("falta"))));

            var detalle = BuscarPorId(ventana, "PkgDetail") as System.Windows.Controls.TextBlock;
            Check("el detalle del seleccionado se dibuja",
                detalle is not null && detalle.Text.Length > 0, detalle?.Text ?? "");

            // Las tres rutas del paquete completo tienen que verse en sus campos.
            var rutaDif = BuscarPorId(ventana, "PkgDiffusionPath") as System.Windows.Controls.TextBox;
            var rutaTe = BuscarPorId(ventana, "PkgTextEncoderPath") as System.Windows.Controls.TextBox;
            var rutaVae = BuscarPorId(ventana, "PkgVaePath") as System.Windows.Controls.TextBox;
            Check("se dibujan las tres rutas del paquete",
                rutaDif is not null && rutaDif.Text.EndsWith("dif.gguf") &&
                rutaTe is not null && rutaTe.Text.EndsWith("te.gguf") &&
                rutaVae is not null && rutaVae.Text.EndsWith("ae.safetensors"),
                $"{rutaDif?.Text} | {rutaTe?.Text} | {rutaVae?.Text}");

            // Con el completo seleccionado no se ofrece bajar nada: el defecto del 21/10/2026.
            var descargar = BuscarPorId(ventana, "PkgDownloadButton") as System.Windows.Controls.Button;
            var textoDescarga = BuscarPorId(ventana, "PkgDownloadText") as System.Windows.Controls.TextBlock;
            Check("con un paquete bajado, el botón de descarga queda apagado",
                descargar is not null && !descargar.IsEnabled);
            Check("y el recuadro dice que ya está descargado",
                textoDescarga is not null && textoDescarga.Text.Contains("ya está descargado"), textoDescarga?.Text ?? "");

            vm.SelectedRow = vm.Packages.First(p => p.CanDownload);
            ventana.UpdateLayout();
            Check("con uno del catálogo sin bajar, el botón se habilita",
                descargar is not null && descargar.IsEnabled);
            Check("y el recuadro dice cuál y cuánto pesa",
                textoDescarga is not null && textoDescarga.Text.StartsWith("Descargar Z-Image") && textoDescarga.Text.Contains("GB"),
                textoDescarga?.Text ?? "");
            vm.SelectedRow = vm.Packages.First(p => p.IsComplete);
            ventana.UpdateLayout();

            var estadoDescarga = BuscarPorId(ventana, "PkgDownloadStatus") as System.Windows.Controls.TextBlock;
            Check("el estado de descarga está oculto mientras no se descarga",
                estadoDescarga is not null && estadoDescarga.Visibility != Visibility.Visible,
                $"{estadoDescarga?.Visibility}");

            // Usar el paquete completo tiene que reflejarse en el pie.
            usar!.Command.Execute(null);
            ventana.UpdateLayout();
            var activo = BuscarPorId(ventana, "PkgActiveText") as System.Windows.Controls.TextBlock;
            Check("el pie informa el paquete en uso",
                activo is not null && activo.Text.Contains("Paquete completo de prueba"),
                activo?.Text ?? "");

            ventana.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
            Capturar(ventana, Path.Combine(Path.GetTempPath(), "selector-de-paquetes.png"));
            ventana.Close();
        }

        escucha.Flush();
        var errores = traza.ToString();
        System.Diagnostics.PresentationTraceSources.DataBindingSource.Listeners.Remove(escucha);

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

    /// <summary>
    /// Devuelve los textos que NO entran en el ancho que les tocó, midiendo el ancho
    /// natural de la cadena con FormattedText y comparándolo contra el ancho real del
    /// control. Es la única forma confiable de detectar un recorte: WPF corta el texto
    /// sin dejar ninguna marca en la propiedad Text ni, por defecto, en pantalla.
    /// </summary>
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

            // Un par de píxeles de tolerancia por el redondeo del layout.
            if (tb.TextWrapping == TextWrapping.NoWrap && ft.Width > tb.ActualWidth + 2)
                yield return $"«{tb.Text}» necesita {ft.Width:N0}px y tiene {tb.ActualWidth:N0}px";
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
