using System;
using System.IO;
using System.Windows;
using PRImager.AIAssistant.Services;
using PRImager.AIAssistant.ViewModels;
using System.Runtime.InteropServices;

namespace PRImager.AIAssistant.Views;

/// <summary>
/// Servicio concreto para diálogos de archivo (WPF).
/// </summary>
public class FileDialogService : IFileDialogService
{
    public string[]? OpenFiles(string title, string filter)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = title,
            Filter = filter,
            Multiselect = true
        };
        return dialog.ShowDialog() == true ? dialog.FileNames : null;
    }

    public string[]? OpenImages(string title)
    {
        return OpenFiles(title, Loc.T("files.filter.images"));
    }

    public string[]? OpenTextFiles(string title)
    {
        return OpenFiles(title, Loc.T("files.filter.text"));
    }

    public string? SaveFile(string title, string filter, string defaultFileName)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = title,
            Filter = filter,
            FileName = defaultFileName
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? SelectFolder(string title, string initialFolder)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = title,
            Multiselect = false
        };

        // Abrir el diálogo en la carpeta actual le ahorra al usuario navegar hasta ella
        // para elegir la de al lado. Si ya no existe, se abre donde el sistema decida.
        try
        {
            if (!string.IsNullOrWhiteSpace(initialFolder) && System.IO.Directory.Exists(initialFolder))
                dialog.InitialDirectory = initialFolder;
        }
        catch (Exception)
        {
        }

        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }
}

/// <summary>
/// MainWindow actúa como contenedor de navegación.
/// Crea los servicios compartidos una sola vez y delega la navegación a NavigationViewModel.
/// </summary>
public partial class MainWindow : Window
{
    private readonly ConfigManager _configManager;
    private readonly ForensicLogger _logger;
    private readonly LlamaServerManager _serverManager;
    private readonly LlamaChatClient _chatClient;
    private readonly NavigationViewModel _navigationVm;

    public MainWindow()
    {
        InitializeComponent();

        // Inicializando servicios base que conforman la app forense standalone (una sola vez)
        string configFile = App.SettingsFile;
        string appDataFolder = Path.GetDirectoryName(configFile)!;
        string logsDir = Path.Combine(appDataFolder, "Logs");

        _configManager = new ConfigManager(configFile);
        _logger = new ForensicLogger(logsDir);
        _serverManager = new LlamaServerManager();
        _chatClient = new LlamaChatClient();

        // Crear navegación con todos los servicios compartidos
        var fileDialogService = new FileDialogService();
        string catalogPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Config", "model_catalog.json");
        string modelsDir = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Models");
        var profileService = new ModelProfileService(catalogPath, modelsDir);

        // Perfiles de uso: si profiles.json no existe se generan los dos de fábrica
        var usageProfileService = new UsageProfileService();
        usageProfileService.Load();

        _navigationVm = new NavigationViewModel(_serverManager, _chatClient, _logger, _configManager, fileDialogService, profileService, usageProfileService);

        // Título dinámico con versión desde AssemblyInfo. NavigationViewModel le agrega el perfil.
        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        string versionText = version != null ? $"v{version.Major}.{version.Minor}.{version.Build}" : "v1.0.0";
        _navigationVm.BaseWindowTitle = $"PR-AI Assistant {versionText} — Patagonia Robot";

        DataContext = _navigationVm;

        // Forzar título oscuro en Windows 10/11. Acá antes de mostrarse, para que el marco
        // no aparezca blanco ni un instante; las demás ventanas lo toman de App.
        Helpers.DarkTitleBar.Apply(this);
    }

    /// <summary>
    /// Limpieza de todos los subprocesos e I/O al cerrar la ventana.
    /// </summary>
    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        _serverManager?.Dispose();
        _chatClient?.Dispose();
        _logger?.Dispose();
    }
}
