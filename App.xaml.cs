using System.Configuration;
using System.Data;
using System.Windows;

namespace PRImager.AIAssistant;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
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

