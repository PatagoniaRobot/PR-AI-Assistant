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
        new Views.MainWindow().Show();
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
                $"Error inesperado:\n{e.Exception.Message}\n\nLa aplicación continuará ejecutándose.",
                "PR-AI Assistant — Error",
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

