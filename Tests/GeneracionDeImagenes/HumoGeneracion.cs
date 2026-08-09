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
/// Prueba de humo de la vista de generación (E4.4).
///
/// Las aserciones de lógica pueden pasar enteras sobre una ventana que se ve mal: el
/// defecto 4.13 salió con el color correcto y el texto equivocado, y en E4.3 el estado
/// del paquete incompleto salía cortado a la mitad porque el DataGrid recorta SIN poner
/// puntos suspensivos. Las dos veces lo encontró mirar la captura.
///
/// Por eso acá se abre la vista de verdad dentro de una ventana, se fuerza el layout, se
/// escuchan los errores de binding que WPF se traga en silencio, se lee el texto
/// EFECTIVAMENTE DIBUJADO y se mide el ancho natural de cada cadena contra el ancho real
/// de su control.
/// </summary>
static class HumoGeneracion
{
    static int _ok, _fail;

    static void Check(string nombre, bool cond, string detalle = "")
    {
        if (cond) { _ok++; Console.WriteLine($"  OK    {nombre}"); }
        else { _fail++; Console.WriteLine($"  FALLA {nombre}   {detalle}"); }
    }

    public static int Correr()
    {
        Console.WriteLine("\n-- Humo: se abre la vista de generación de verdad --");

        var traza = new StringWriter();
        var escucha = new System.Diagnostics.TextWriterTraceListener(traza);
        System.Diagnostics.PresentationTraceSources.Refresh();
        System.Diagnostics.PresentationTraceSources.DataBindingSource.Listeners.Add(escucha);
        System.Diagnostics.PresentationTraceSources.DataBindingSource.Switch.Level =
            System.Diagnostics.SourceLevels.Error;

        EntornoWpf.Preparar();

        var raiz = Path.Combine(Path.GetTempPath(), "BancoGeneracion", "humo");
        if (Directory.Exists(raiz)) Directory.Delete(raiz, true);
        Directory.CreateDirectory(raiz);

        // Un paquete completo con archivos de mentira: la vista tiene que quedar
        // utilizable sin que haya un motor instalado.
        var svc = new ImageModelService(Path.Combine(raiz, "image_models.json"));
        string Falso(string n) { var p = Path.Combine(raiz, n); File.WriteAllBytes(p, new byte[64 * 1024]); return p; }
        svc.Save(new ImageModelPackage
        {
            Id = "humo", DisplayName = "Z-Image de prueba", Family = "z-image",
            DefaultSteps = 8, DefaultCfgScale = 1.0,
            DiffusionModelPath = Falso("dif.gguf"),
            TextEncoderPath = Falso("te.gguf"),
            VaePath = Falso("ae.safetensors")
        });
        svc.Select("humo");

        using var vm = new ImageGenerationViewModel(
            svc, manager: null, gpuInfo: new GpuInfoService(),
            enginePathResolver: () => Path.Combine(raiz, "sd-server.exe"),
            outputDirectory: Path.Combine(raiz, "imagenes"));

        vm.InitializeAsync().GetAwaiter().GetResult();

        Window? ventana = null;
        Exception? fallo = null;

        try
        {
            var vista = new ImageGenerationView { DataContext = vm };
            ventana = new Window
            {
                Title = "Generación de imágenes — humo",
                Content = vista,
                Width = 1200,
                Height = 800,
                Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x2E)),
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -4000,
                Top = -4000,
                ShowInTaskbar = false
            };
            ventana.Show();
            ventana.UpdateLayout();
            ventana.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
        }
        catch (Exception ex) { fallo = ex; }

        Check("la vista abre sin lanzar", fallo is null, fallo?.Message ?? "");

        if (ventana is not null)
        {
            var generar = BuscarPorId(ventana, "GenerateButton") as System.Windows.Controls.Button;
            var cancelar = BuscarPorId(ventana, "GenCancelButton") as System.Windows.Controls.Button;
            var prompt = BuscarPorId(ventana, "GenPromptBox") as System.Windows.Controls.TextBox;

            Check("el campo de prompt existe", prompt is not null);
            Check("el botón de generar existe", generar is not null);

            // Sin prompt, deshabilitado. Con prompt, habilitado. El control positivo es
            // lo que hace que la primera aserción signifique algo: sin él podría estar
            // pasando porque el botón nunca se habilita.
            Check("el botón de generar arranca deshabilitado, sin prompt",
                generar is not null && !generar.IsEnabled);

            vm.Prompt = "un zorro en un bosque nevado";
            ventana.UpdateLayout();
            Check("el botón de generar se habilita al escribir un prompt",
                generar is not null && generar.IsEnabled);

            vm.Prompt = "   ";
            ventana.UpdateLayout();
            Check("y se vuelve a deshabilitar con un prompt en blanco",
                generar is not null && !generar.IsEnabled);

            vm.Prompt = "un zorro en un bosque nevado";
            ventana.UpdateLayout();

            // Se genera de a una por vez (decisión 5.7): el botón de cancelar solo
            // aparece cuando hay algo que cancelar, y no hay interfaz de cola.
            //
            // Se pregunta por IsVisible y no por Visibility, que es lo que uno escribe
            // primero y responde otra cosa: Visibility es la propiedad LOCAL del
            // control, así que un elemento adentro de un contenedor colapsado informa
            // Visible aunque no se dibuje. IsVisible tiene en cuenta a los ancestros,
            // que es la pregunta que importa: ¿lo ve el usuario?
            Check("el botón de cancelar está oculto mientras no se genera",
                cancelar is not null && !cancelar.IsVisible, $"{cancelar?.Visibility}");

            var barra = BuscarPorId(ventana, "GenProgressBar") as System.Windows.Controls.ProgressBar;
            Check("la barra de progreso está oculta mientras no se genera",
                barra is not null && !barra.IsVisible, $"{barra?.Visibility}");

            var textos = TodosLosTextos(ventana).ToList();

            Check("no hay ninguna lista ni posición de cola en pantalla",
                !textos.Any(t => t.Contains("cola", StringComparison.OrdinalIgnoreCase) ||
                                 t.Contains("En espera", StringComparison.OrdinalIgnoreCase)),
                string.Join(" | ", textos.Where(t => t.Contains("cola", StringComparison.OrdinalIgnoreCase))));

            // --- lo que tiene que estar DIBUJADO, no solo existir ---
            var paquete = BuscarPorId(ventana, "GenPackageText") as System.Windows.Controls.TextBlock;
            Check("la vista dice qué paquete de modelo está en uso",
                paquete is not null && paquete.Text.Contains("Z-Image de prueba"),
                paquete?.Text ?? "");
            Check("y con él los pasos y el CFG del paquete, no los del motor",
                paquete is not null && paquete.Text.Contains("8 pasos") && !paquete.Text.Contains("20 pasos"),
                paquete?.Text ?? "");

            var gpu = BuscarPorId(ventana, "GenGpuText") as System.Windows.Controls.TextBlock;
            Check("la vista informa el estado de la GPU",
                gpu is not null && gpu.Text.Length > 0, gpu?.Text ?? "");

            var vram = BuscarPorId(ventana, "GenVramLabel") as System.Windows.Controls.TextBlock;
            Check("el semáforo de VRAM se dibuja con una etiqueta",
                vram is not null && vram.Text.Length > 0 && vram.Text != "—", vram?.Text ?? "");

            // El tamaño tiene que seguir a la selección: es el defecto de forma de 4.13
            // —el control se ve bien y muestra otra cosa— llevado a esta pantalla.
            var tamano = BuscarPorId(ventana, "GenSizeText") as System.Windows.Controls.TextBlock;
            Check("el tamaño dibujado arranca en 1024 × 1024",
                tamano is not null && tamano.Text == "1024 × 1024", tamano?.Text ?? "");

            vm.SelectedSize = vm.SizeOptions.First(s => s.Side == 512);
            vm.SelectedOrientation = vm.Orientations.First(o => o.Value == ImageOrientation.Horizontal);
            ventana.UpdateLayout();
            Check("el tamaño dibujado sigue al selector",
                tamano is not null && tamano.Text == "640 × 448", tamano?.Text ?? "");

            vm.SelectedSize = vm.SizeOptions.First(s => s.Side == 1024);
            vm.SelectedOrientation = vm.Orientations.First(o => o.Value == ImageOrientation.Square);
            ventana.UpdateLayout();

            var marcador = BuscarPorId(ventana, "GenPlaceholder") as System.Windows.Controls.TextBlock;
            Check("sin imágenes todavía, el panel muestra la indicación",
                marcador is not null && marcador.IsVisible, $"{marcador?.Visibility}");

            var vista = BuscarPorId(ventana, "GenPreview") as System.Windows.Controls.Image;
            Check("y la imagen grande está oculta",
                vista is not null && !vista.IsVisible, $"{vista?.Visibility}");

            var galeria = BuscarPorId(ventana, "GenGallery") as System.Windows.Controls.ListBox;
            Check("la galería de la sesión existe y arranca vacía",
                galeria is not null && galeria.Items.Count == 0, $"{galeria?.Items.Count}");

            // La tira vacía dejaba una franja muerta con su encabezado desde el arranque.
            // Lo mostró la captura, no una aserción: el control existía y estaba bien.
            Check("la galería no ocupa lugar mientras está vacía",
                galeria is not null && !galeria.IsVisible, $"{galeria?.Visibility}");

            // --- los ajustes avanzados, plegados por defecto ---
            var avanzados = BuscarPorId(ventana, "GenAdvanced") as System.Windows.Controls.Expander;
            Check("los ajustes avanzados vienen plegados",
                avanzados is not null && avanzados.IsExpanded == false);

            if (avanzados is not null)
            {
                avanzados.IsExpanded = true;
                ventana.UpdateLayout();
                ventana.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);

                var pasos = BuscarPorId(ventana, "GenStepsValue") as System.Windows.Controls.TextBlock;
                Check("al desplegarlos, los pasos dibujados son los del paquete",
                    pasos is not null && pasos.Text == "8", pasos?.Text ?? "");

                var cfg = BuscarPorId(ventana, "GenCfgValue") as System.Windows.Controls.TextBlock;
                Check("y el CFG dibujado también", cfg is not null && cfg.Text == "1", cfg?.Text ?? "");

                var semilla = BuscarPorId(ventana, "GenSeedBox") as System.Windows.Controls.TextBox;
                Check("el campo de semilla está bloqueado mientras sea aleatoria",
                    semilla is not null && !semilla.IsEnabled);

                vm.UseRandomSeed = false;
                ventana.UpdateLayout();
                Check("y se habilita al pedir una semilla fija",
                    semilla is not null && semilla.IsEnabled);

                // Con los ajustes desplegados, el botón de generar se iba abajo del
                // scroll y había que buscarlo. IsVisible no alcanza para detectarlo: un
                // control desplazado fuera del área visible del ScrollViewer sigue
                // informando IsVisible true. Hay que mirar DÓNDE quedó.
                Check("el botón de generar sigue a la vista con los ajustes desplegados",
                    generar is not null && EstaDentro(ventana, generar),
                    generar is null ? "no está" : Ubicacion(ventana, generar));
            }

            // Ninguna cadena puede quedar recortada. Buscar puntos suspensivos NO sirve:
            // WPF corta sin ponerlos. Se mide el ancho natural del texto con
            // FormattedText contra el ancho real del control, que es lo único confiable.
            var cortados = TextBlocksDesbordados(ventana).ToList();
            Check("ningún texto queda cortado por su control", cortados.Count == 0,
                string.Join(" | ", cortados));

            ventana.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
            Capturar(ventana, Path.Combine(Path.GetTempPath(), "vista-de-generacion.png"));
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
    /// Rectángulo que ocupa un control dentro de la ventana. Es la única forma de saber
    /// si algo quedó fuera del área visible: un elemento desplazado por un ScrollViewer
    /// sigue informando IsVisible true.
    /// </summary>
    static Rect Rectangulo(Window ventana, FrameworkElement e)
    {
        var t = e.TransformToAncestor(ventana);
        return new Rect(t.Transform(new Point(0, 0)),
                        new Size(e.ActualWidth, e.ActualHeight));
    }

    static bool EstaDentro(Window ventana, FrameworkElement e)
    {
        var r = Rectangulo(ventana, e);
        return r.Width > 0 && r.Height > 0 &&
               r.Top >= -1 && r.Left >= -1 &&
               r.Bottom <= ventana.ActualHeight + 1 &&
               r.Right <= ventana.ActualWidth + 1;
    }

    static string Ubicacion(Window ventana, FrameworkElement e)
    {
        var r = Rectangulo(ventana, e);
        return $"ocupa {r.Left:N0},{r.Top:N0} a {r.Right:N0},{r.Bottom:N0} " +
               $"en una ventana de {ventana.ActualWidth:N0}×{ventana.ActualHeight:N0}";
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
