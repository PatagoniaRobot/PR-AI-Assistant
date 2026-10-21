using System.Configuration;
using System.Data;
using System.Windows;

namespace PRImager.AIAssistant;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    /// <summary>
    /// Una sola copia por sesión de Windows. Con dos, la segunda se caía al arrancar —el
    /// registro forense abre el archivo del día en exclusiva para escribir— y dejaba una
    /// ventana de error; y aunque no se cayera, las dos pelearían por el puerto del motor.
    /// Se vio el 20 de octubre de 2026 probando el primer arranque; venía de la versión 1.0.
    /// </summary>
    private const string SingleInstanceName = @"Local\PRImager.AIAssistant";

    // Se guarda para que viva lo que vive el proceso: si el recolector lo libera, el
    // mutex se suelta y otra copia podría arrancar.
    private static System.Threading.Mutex? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstance = new System.Threading.Mutex(initiallyOwned: true, SingleInstanceName, out bool isFirst);
        if (!isFirst)
        {
            BringExistingToFront();
            Shutdown();
            return;
        }

        base.OnStartup(e);

        // El idioma, antes de crear la primera ventana: el XAML resuelve los textos al
        // cargarse. Se lee la configuración acá —MainWindow la vuelve a leer— porque es
        // lo único que hace falta antes de que exista.
        Localization.Loc.Initialize(new Services.ConfigManager(SettingsFile).Load().Language);

        new Views.MainWindow().Show();
    }

    /// <summary>Dónde vive settings.json. Lo usan App y MainWindow.</summary>
    public static string SettingsFile => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "PRImager", "AIAssistant", "settings.json");

    private static bool _restartRequested;

    /// <summary>
    /// Cierra y vuelve a abrir la aplicación: para cambiar el idioma. La copia nueva se
    /// lanza recién en <see cref="OnExit"/>, con los motores ya cerrados por la ventana
    /// principal y el mutex de copia única suelto; antes, la nueva encontraría a la vieja
    /// todavía abierta y se cerraría trayéndola al frente.
    /// </summary>
    public static void Restart()
    {
        _restartRequested = true;
        Current.MainWindow?.Close();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_restartRequested && Environment.ProcessPath is { } exe)
        {
            _singleInstance?.ReleaseMutex();
            _singleInstance?.Dispose();
            _singleInstance = null;
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = true });
        }
        base.OnExit(e);
    }

    /// <summary>Trae al frente la copia que ya está abierta, como hacen casi todos los programas.</summary>
    private static void BringExistingToFront()
    {
        var current = System.Diagnostics.Process.GetCurrentProcess();
        foreach (var other in System.Diagnostics.Process.GetProcessesByName(current.ProcessName))
        {
            if (other.Id == current.Id || other.MainWindowHandle == IntPtr.Zero) continue;
            ShowWindow(other.MainWindowHandle, SwRestore);
            SetForegroundWindow(other.MainWindowHandle);
            break;
        }
    }

    private const int SwRestore = 9;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    public App()
    {
        DispatcherUnhandledException += (s, e) =>
        {
            System.Diagnostics.Debug.WriteLine($"[UNHANDLED UI] {e.Exception}");
            System.Windows.MessageBox.Show(
                Localization.Loc.F("app.unexpectedError", e.Exception.Message),
                Localization.Loc.T("app.errorTitle"),
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
            e.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            if (e.ExceptionObject is Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[UNHANDLED DOMAIN] {ex}");
                try
                {
                    string logPath = System.IO.Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        "PRImager", "AIAssistant", "crash.log");
                    string? dir = System.IO.Path.GetDirectoryName(logPath);
                    if (!string.IsNullOrEmpty(dir) && !System.IO.Directory.Exists(dir))
                        System.IO.Directory.CreateDirectory(dir);
                    System.IO.File.AppendAllText(logPath,
                        $"[{DateTime.UtcNow:O}] {ex}\n---\n");
                }
                catch { }
            }
        };
    }
}

