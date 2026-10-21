#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PRImager.AIAssistant.Controls;
using PRImager.AIAssistant.Localization;
using PRImager.AIAssistant.Services;

// Banco de idiomas e íconos. PR-AIA pasó a bilingüe y a íconos propios el 20 de octubre
// de 2026 (pedido del usuario antes de publicar). Lo que vigila:
//   - español e inglés tienen las mismas claves, con los mismos datos {0}, {1}… y
//     formatos que no lanzan;
//   - toda clave que nombra el código existe, y en la edición Forense no viajan los
//     textos de búsqueda web, catálogo e imágenes (la misma barrera que el código);
//   - no quedó ningún emoji en el código: los íconos son el control Icon;
//   - cada ícono tiene dibujo y cae dentro de la grilla de 24;
//   - las pantallas en inglés se dibujan sin claves crudas a la vista.

class Banco
{
    static int _ok, _fail;

    static void Check(string nombre, bool cond, string detalle = "")
    {
        if (cond) { _ok++; Console.WriteLine($"  OK   {nombre}"); }
        else { _fail++; Console.WriteLine($"  FALLA {nombre}   {detalle}"); }
    }

    static bool EsForense => !typeof(ConfigManager).Assembly.GetTypes().Any(t => t.Name == "ImageGenerationViewModel");

    [STAThread]
    static int Main()
    {
        Console.WriteLine($"Banco de idiomas e íconos — edición {(EsForense ? "Forense" : "estándar")}\n");
        foreach (var caso in new Action[] { CasoParidad, CasoClavesUsadas, CasoBarrera, CasoSinEmojis, CasoIconos, CasoReporte, CasoPantallasEnIngles })
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

    /// <summary>La raíz del repositorio: donde está PR-AI-Assistant.csproj.</summary>
    static string Raiz()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "PR-AI-Assistant.csproj")))
            d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("no se encontró la raíz del repositorio");
    }

    static readonly Regex Marcador = new(@"\{(\d+)(?:[,:][^}]*)?\}");

    static SortedSet<int> Indices(string s) => new(Marcador.Matches(s).Select(m => int.Parse(m.Groups[1].Value)));

    // --- español e inglés dicen lo mismo ---

    static void CasoParidad()
    {
        Console.WriteLine("español e inglés: mismas claves, mismos datos");
        var es = Loc.All(Loc.Spanish);
        var en = Loc.All(Loc.English);

        Check($"hay textos en español ({es.Count})", es.Count > 100);
        var soloEs = es.Keys.Except(en.Keys).ToList();
        var soloEn = en.Keys.Except(es.Keys).ToList();
        Check("toda clave en español está en inglés", soloEs.Count == 0, string.Join(", ", soloEs.Take(10)));
        Check("toda clave en inglés está en español", soloEn.Count == 0, string.Join(", ", soloEn.Take(10)));

        var distintos = es.Keys.Where(en.ContainsKey)
            .Where(k => !Indices(es[k]).SetEquals(Indices(en[k]))).ToList();
        Check("los mismos {n} en los dos idiomas", distintos.Count == 0, string.Join(", ", distintos.Take(10)));

        var rotos = new List<string>();
        foreach (var (lang, d) in new[] { ("es", es), ("en", en) })
            foreach (var (k, v) in d)
            {
                var idx = Indices(v);
                var args = Enumerable.Repeat((object)1.5, idx.Count == 0 ? 0 : idx.Max + 1).ToArray();
                try { _ = string.Format(CultureInfo.InvariantCulture, v, args); }
                catch (FormatException) { rotos.Add($"{lang}:{k}"); }
            }
        Check("ningún formato lanza", rotos.Count == 0, string.Join(", ", rotos.Take(10)));

        var vacios = es.Concat(en).Where(kv => string.IsNullOrWhiteSpace(kv.Value)).Select(kv => kv.Key).ToList();
        Check("ningún texto vacío", vacios.Count == 0, string.Join(", ", vacios.Take(10)));

        // Una clave repetida dentro de un archivo se pisaría en silencio
        var repetidas = new List<string>();
        foreach (var f in Directory.GetFiles(Path.Combine(Raiz(), "Localization"), "*.json", SearchOption.AllDirectories))
        {
            var vistas = new HashSet<string>();
            foreach (Match m in Regex.Matches(File.ReadAllText(f), "^\\s*\"([^\"]+)\"\\s*:", RegexOptions.Multiline))
                if (!vistas.Add(m.Groups[1].Value)) repetidas.Add($"{Path.GetFileName(f)}:{m.Groups[1].Value}");
        }
        Check("ninguna clave repetida en un archivo", repetidas.Count == 0, string.Join(", ", repetidas));
    }

    // --- lo que nombra el código existe ---

    static void CasoClavesUsadas()
    {
        Console.WriteLine("\ntoda clave que nombra el código existe");
        var es = Loc.All(Loc.Spanish);
        string raiz = Raiz();
        var usadas = new Dictionary<string, string>();
        var tf = new Regex("Loc\\.(?:T|F)\\(\\s*\"([^\"]+)\"");
        var p = new Regex("Loc\\.P\\(\\s*\"([^\"]+)\"");
        var x = new Regex(@"\{l:T ([A-Za-z0-9_.]+)\}");

        foreach (var f in Fuentes(raiz, "*.cs").Concat(Fuentes(raiz, "*.xaml")))
        {
            string t = File.ReadAllText(f);
            string rel = Path.GetRelativePath(raiz, f);
            foreach (Match m in tf.Matches(t)) usadas.TryAdd(m.Groups[1].Value, rel);
            foreach (Match m in x.Matches(t)) usadas.TryAdd(m.Groups[1].Value, rel);
            foreach (Match m in p.Matches(t))
            {
                usadas.TryAdd(m.Groups[1].Value + ".one", rel);
                usadas.TryAdd(m.Groups[1].Value + ".many", rel);
            }
        }

        // Contra el español completo de la fuente, no contra el DLL: en Forense faltan a
        // propósito los textos de las capacidades que no compila.
        var definidas = LeerFuente(raiz, Loc.Spanish);
        var faltan = usadas.Where(kv => !definidas.Contains(kv.Key)).Select(kv => $"{kv.Key} ({kv.Value})").ToList();
        Check($"las {usadas.Count} claves usadas están definidas", faltan.Count == 0, string.Join(", ", faltan.Take(10)));
        Check("el DLL trae los textos que tiene que traer", usadas.Keys.Count(k => es.ContainsKey(k)) > 300);
    }

    static IEnumerable<string> Fuentes(string raiz, string patron) =>
        Directory.GetFiles(raiz, patron, SearchOption.AllDirectories)
            .Where(f =>
            {
                string rel = Path.GetRelativePath(raiz, f).Replace('\\', '/');
                return !rel.StartsWith("bin/") && !rel.StartsWith("obj/") && !rel.StartsWith("Tests/")
                       && !rel.StartsWith(".") && !rel.StartsWith("Documentacion/")
                       && !rel.StartsWith("Publish") && !rel.Contains("/obj/") && !rel.Contains("/bin/");
            });

    static HashSet<string> LeerFuente(string raiz, string idioma)
    {
        var claves = new HashSet<string>();
        foreach (var f in Directory.GetFiles(Path.Combine(raiz, "Localization", idioma), "*.json"))
            foreach (Match m in Regex.Matches(File.ReadAllText(f), "^\\s*\"([^\"]+)\"\\s*:", RegexOptions.Multiline))
                claves.Add(m.Groups[1].Value);
        return claves;
    }

    // --- la barrera de compilación también vale para los textos ---

    static void CasoBarrera()
    {
        Console.WriteLine("\nlos textos siguen a la barrera de la edición");
        var es = Loc.All(Loc.Spanish);
        string[] deCapacidades = { "imagegen.window.title", "catalog.window.title", "searx.notConfigured", "licenses.angleSharp" };
        foreach (var k in deCapacidades)
        {
            if (EsForense)
                Check($"Forense no trae «{k}»", !es.ContainsKey(k));
            else
                Check($"la estándar trae «{k}»", es.ContainsKey(k));
        }
        Check("las dos traen los textos comunes", es.ContainsKey("chat.send") && es.ContainsKey("mode.investigation.title"));
    }

    // --- ningún emoji ---

    static void CasoSinEmojis()
    {
        Console.WriteLine("\nningún emoji en el código ni en las pantallas");
        string raiz = Raiz();
        // Tipografía que sí queda: viñetas, cursor del streaming, líneas del reporte,
        // flechas y signos dentro de textos al modelo
        var permitidos = new HashSet<int> { '•', '▌', '═', '─', '→', '≠', '●', '·', '…', '—', '–', '×', '«', '»', '©', '°', '¿', '¡', '“', '”', '‘', '’', '│', '├', '└' };
        var hallados = new List<string>();
        foreach (var f in Fuentes(raiz, "*.cs").Concat(Fuentes(raiz, "*.xaml")).Concat(Fuentes(raiz, "*.json")))
        {
            if (f.Contains("Config")) continue;
            int n = 0;
            foreach (var linea in File.ReadLines(f))
            {
                n++;
                string s = linea.TrimStart();
                if (s.StartsWith("//") || s.StartsWith("<!--")) continue;
                foreach (var r in s.EnumerateRunes())
                {
                    bool emoji = r.Value is >= 0x1F300 and <= 0x1FAFF or >= 0x2600 and <= 0x27BF or >= 0x2B00 and <= 0x2BFF
                                 or >= 0x2190 and <= 0x21FF or >= 0x2300 and <= 0x23FF or >= 0x25A0 and <= 0x25FF
                                 or 0xFE0F;
                    if (emoji && !permitidos.Contains(r.Value))
                        hallados.Add($"{Path.GetRelativePath(raiz, f)}:{n} U+{r.Value:X4}");
                }
            }
        }
        Check("no queda ninguno", hallados.Count == 0, string.Join(", ", hallados.Take(10)));
    }

    // --- los íconos ---

    static void CasoIconos()
    {
        Console.WriteLine("\nlos íconos");
        var todos = Enum.GetValues<IconKind>().Where(k => k != IconKind.None).ToList();
        var sinDibujo = todos.Where(k => IconCatalog.Get(k).Count == 0).ToList();
        Check($"los {todos.Count} tienen dibujo", sinDibujo.Count == 0, string.Join(", ", sinDibujo));

        var fuera = new List<string>();
        foreach (var k in todos)
            foreach (var capa in IconCatalog.Get(k))
            {
                var b = capa.Geometry.Bounds;
                if (b.IsEmpty || b.Left < -0.5 || b.Top < -0.5 || b.Right > 24.5 || b.Bottom > 24.5)
                    fuera.Add($"{k} {b}");
            }
        Check("todos caen dentro de la grilla de 24", fuera.Count == 0, string.Join("; ", fuera.Take(5)));
    }

    // --- el reporte exportado ---

    static void CasoReporte()
    {
        Console.WriteLine("\nel reporte de la sesión, en los dos idiomas");
        var datos = new PRImager.AIAssistant.Services.Models.SessionExportData
        {
            Session = new PRImager.AIAssistant.Services.Models.InvestigationSession
            {
                PeritoName = "Ana <b>Pérez</b>", PeritoBadge = "LP-1", CaseNumber = "C-7",
                Court = "Juzgado 1", Description = "Prueba"
            },
            Messages = new() { new() { Role = "user", Content = "hola", Timestamp = DateTime.UtcNow } },
            Artifacts = new(),
            ModelName = "modelo-x"
        };

        foreach (var (idioma, titulo) in new[] { (Loc.Spanish, "REPORTE DE SESIÓN"), (Loc.English, "SESSION REPORT") })
        {
            Loc.Initialize(idioma);
            string dir = Path.Combine(Path.GetTempPath(), "BancoIdiomas");
            Directory.CreateDirectory(dir);

            string txt = Path.Combine(dir, $"reporte-{idioma}.txt");
            var r = new SessionExporter().Export(datos, txt, PRImager.AIAssistant.Services.Models.ExportFormat.Txt);
            string contenido = File.ReadAllText(txt);
            Check($"{idioma}: el texto se exporta y se titula «{titulo}»", r.Success && contenido.Contains(titulo));

            // El hash del pie es el del contenido que lo precede: tiene que seguir cerrando
            int corte = contenido.LastIndexOf('\n', contenido.IndexOf(r.FileHash!, StringComparison.Ordinal));
            string sinPie = contenido[..corte];
            string calculado = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(sinPie))).ToLowerInvariant();
            Check($"{idioma}: el hash del pie corresponde al contenido", calculado == r.FileHash, $"{calculado} vs {r.FileHash}");

            string html = Path.Combine(dir, $"reporte-{idioma}.html");
            var h = new SessionExporter().Export(datos, html, PRImager.AIAssistant.Services.Models.ExportFormat.Html);
            string doc = File.ReadAllText(html);
            Check($"{idioma}: el HTML declara su idioma", h.Success && doc.Contains($"<html lang=\"{idioma}\">"));
            Check($"{idioma}: un nombre con «<b>» queda escapado", doc.Contains("Ana &lt;b&gt;P") && !doc.Contains("Ana <b>"));
        }
        Loc.Initialize(Loc.Spanish);
    }

    // --- las pantallas, en inglés ---

    static readonly Regex PareceClave = new(@"^[a-z]+(\.[A-Za-z0-9]+)+$");

    static void CasoPantallasEnIngles()
    {
        Console.WriteLine("\nlas pantallas en inglés");
        Loc.Initialize(Loc.English);
        Check("el idioma quedó en inglés", Loc.IsEnglish && Loc.T("chat.send") == "Send", Loc.T("chat.send"));

        var pantallas = new List<(string nombre, string tipo, string esperado)>
        {
            ("inicio", "PRImager.AIAssistant.Views.ModeSelectionView", "Investigation Mode"),
            ("chat", "PRImager.AIAssistant.Views.ChatView", "SERVER STATUS"),
            ("formulario", "PRImager.AIAssistant.Views.InvestigationFormView", "Examiner name *"),
            ("perfiles", "PRImager.AIAssistant.Views.ProfileManagerWindow", "Usage profiles"),
        };
        if (!EsForense)
        {
            pantallas.Add(("catálogo", "PRImager.AIAssistant.Views.HuggingFace.ModelCatalogWindow", "AVAILABLE QUANTIZATIONS"));
            pantallas.Add(("imágenes", "PRImager.AIAssistant.Views.ImageGen.ImageGenerationView", "Generate image"));
        }

        // Los recursos que en la aplicación pone App.xaml
        var app = Application.Current ?? new Application();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/PR-AI-Assistant;component/Styles/ModernControls.xaml")
        });
        app.Resources["MainFont"] = new FontFamily("Segoe UI");
        app.Resources["MonoFont"] = new FontFamily("Consolas");

        var asm = typeof(ConfigManager).Assembly;
        foreach (var (nombre, tipo, esperado) in pantallas)
        {
            var t = asm.GetType(tipo);
            if (t == null) { Check($"{nombre}: existe {tipo}", false); continue; }

            object obj;
            try { obj = Activator.CreateInstance(t)!; }
            catch (System.Reflection.TargetInvocationException ex)
            {
                Check($"{nombre}: se crea", false, ex.InnerException?.Message ?? ex.Message);
                continue;
            }
            Window ventana;
            if (obj is Window w) ventana = w;
            else ventana = new Window { Content = obj };
            ventana.Width = 1200; ventana.Height = 820;
            ventana.WindowStartupLocation = WindowStartupLocation.Manual;
            ventana.Left = -4000; ventana.Top = 0;
            ventana.ShowInTaskbar = false;
            ventana.Show();
            ventana.UpdateLayout();
            ventana.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);

            var textos = Textos(ventana).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
            var crudas = textos.Where(s => PareceClave.IsMatch(s.Trim())).Distinct().ToList();
            Check($"{nombre}: dice «{esperado}»", textos.Any(s => s.Contains(esperado)));
            Check($"{nombre}: ninguna clave cruda a la vista", crudas.Count == 0, string.Join(", ", crudas.Take(5)));

            Capturar(ventana, Path.Combine(Path.GetTempPath(), $"idiomas-{nombre}.png"));
            ventana.Close();
        }
    }

    static IEnumerable<string> Textos(DependencyObject raiz)
    {
        switch (raiz)
        {
            case TextBlock tb: yield return tb.Text; break;
            case ContentPresenter cp when cp.Content is string s: yield return s; break;
            case HeaderedContentControl h when h.Header is string hs: yield return hs; break;
        }
        int n = VisualTreeHelper.GetChildrenCount(raiz);
        for (int i = 0; i < n; i++)
            foreach (var s in Textos(VisualTreeHelper.GetChild(raiz, i)))
                yield return s;
    }

    static void Capturar(Window ventana, string destino)
    {
        try
        {
            var raiz = (FrameworkElement)ventana.Content;
            int w = (int)Math.Max(1, raiz.ActualWidth), h = (int)Math.Max(1, raiz.ActualHeight);
            var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(raiz);
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
}
