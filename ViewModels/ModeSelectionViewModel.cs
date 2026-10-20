using System;
using System.Windows.Input;
using PRImager.AIAssistant.Helpers;
using PRImager.AIAssistant.Services.Models;

using System.Collections.ObjectModel;
using System.Linq;
using PRImager.AIAssistant.Services;

namespace PRImager.AIAssistant.ViewModels;

public class ModeSelectionViewModel : ObservableObject
{
    // Evento que notifica al contenedor (MainWindow) qué modo se seleccionó
    public event EventHandler<OperationMode>? ModeSelected;

    // Comandos
    public ICommand SelectInvestigationCommand { get; }
    public ICommand SelectConsultationCommand { get; }
    public ICommand HelpCommand { get; }
    public ICommand LicensesCommand { get; }

    /// <summary>Versión de la aplicación para mostrar en la pantalla de selección.</summary>
    public string VersionText
    {
        get
        {
            var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            return version != null ? $"v{version.Major}.{version.Minor}.{version.Build} — Patagonia Robot" : "v1.0.0 — Patagonia Robot";
        }
    }

    private readonly ConfigManager _configManager;
    private readonly ModelProfileService _profileService;
    private AppSettings _settings;

    // === Modelo y perfil de ejecución (catálogo v2) ===

    /// <summary>Modelos disponibles para el selector de la UI.</summary>
    public ObservableCollection<ModelDisplayItem> AvailableModelItems { get; } = new();

    /// <summary>Perfiles de ejecución disponibles para los botones toggle.</summary>
    public ObservableCollection<ExecutionProfileItem> AvailableExecutionProfiles { get; } = new();

    private ModelDisplayItem? _selectedModelItem;
    /// <summary>Modelo seleccionado actualmente en la UI.</summary>
    public ModelDisplayItem? SelectedModelItem
    {
        get => _selectedModelItem;
        set
        {
            if (SetProperty(ref _selectedModelItem, value) && value != null)
            {
                // SelectionKey trae la ruta completa para los modelos descargados,
                // que no viven en la carpeta Models/. Guardar solo el nombre de
                // archivo dejaría la selección irresoluble al lanzar el servidor.
                _settings.SelectedModel = value.Model.SelectionKey;
                _settings.MmprojFileName = value.Model.MmprojFileName;
                if (AutoExecutionProfile.IsAuto(_selectedExecutionProfile?.Profile))
                    _autoRecommendation = AutoExecutionProfile.Apply(_settings, value.Model);
                _configManager.Save(_settings);

                OnPropertyChanged(nameof(SelectedModelHardwareTag));
                OnPropertyChanged(nameof(SelectedModelCapabilitiesText));
                OnPropertyChanged(nameof(SelectedModelContextInfo));
            }
        }
    }

    private ExecutionProfileItem? _selectedExecutionProfile;
    /// <summary>Perfil de ejecución seleccionado actualmente.</summary>
    public ExecutionProfileItem? SelectedExecutionProfile
    {
        get => _selectedExecutionProfile;
        set
        {
            if (SetProperty(ref _selectedExecutionProfile, value) && value != null)
            {
                _settings.ExecutionProfileId = value.Profile.Id;
                if (AutoExecutionProfile.IsAuto(value.Profile))
                {
                    _autoRecommendation = _selectedModelItem != null
                        ? AutoExecutionProfile.Apply(_settings, _selectedModelItem.Model)
                        : null;
                }
                else
                {
                    _settings.GpuLayers = value.Profile.GpuLayers;
                    _settings.CtxSize = value.Profile.CtxSize;
                    _settings.KvCacheType = "f16";
                    if (value.Profile.Threads > 0)
                        _settings.Threads = value.Profile.Threads;
                }
                _configManager.Save(_settings);

                // Marcar el perfil activo y desmarcar los demás
                foreach (var ep in AvailableExecutionProfiles)
                    ep.IsActive = ep == value;

                OnPropertyChanged(nameof(SelectedModelContextInfo));
            }
        }
    }

    public string SelectedModelHardwareTag => _selectedModelItem?.Model.HardwareTag ?? "";

    public string SelectedModelCapabilitiesText
    {
        get
        {
            if (_selectedModelItem == null) return "";
            var caps = _selectedModelItem.Model.Capabilities;
            if (caps == null || caps.Count == 0) return "";
            var badges = caps.Select(c => c switch
            {
                "Vision" => "🔍 Vision",
                "Audio" => "🔊 Audio",
                _ => c
            });
            return string.Join(" · ", badges);
        }
    }

    private RunRecommendation? _autoRecommendation;

    public string SelectedModelContextInfo
    {
        get
        {
            if (_selectedExecutionProfile == null) return "";
            if (AutoExecutionProfile.IsAuto(_selectedExecutionProfile.Profile))
                return _selectedModelItem?.IsAvailable == true
                    ? AutoExecutionProfile.Describe(_autoRecommendation)
                    : AutoExecutionProfile.NoModelText;
            var p = _selectedExecutionProfile.Profile;
            return $"ctx: {p.CtxSize} · capas GPU: {p.GpuLayers}";
        }
    }

    public ICommand SelectExecutionProfileCommand { get; }

    public ModeSelectionViewModel(ConfigManager configManager, ModelProfileService profileService)
    {
        _configManager = configManager;
        _profileService = profileService;
        _settings = _configManager.Load();

        SelectInvestigationCommand = new RelayCommand(
            () => ModeSelected?.Invoke(this, OperationMode.Investigation));
        SelectConsultationCommand = new RelayCommand(
            () => ModeSelected?.Invoke(this, OperationMode.Consultation));

        SelectExecutionProfileCommand = new RelayCommand<ExecutionProfileItem>(ep => { if (ep != null) SelectedExecutionProfile = ep; });

        LoadProfiles();

        HelpCommand = new RelayCommand(() =>
        {
            string helpPath = System.IO.Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "Help", "Help_PRAIA.html");
            if (System.IO.File.Exists(helpPath))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = helpPath,
                    UseShellExecute = true
                });
            }
            else
            {
                System.Windows.MessageBox.Show(
                    "No se encontró el archivo de ayuda.\nRuta esperada: " + helpPath,
                    "Ayuda no disponible",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Warning);
            }
        });

        LicensesCommand = new RelayCommand(() =>
        {
            System.Windows.MessageBox.Show(
                "PR-AI Assistant utiliza los siguientes componentes de código abierto:\n\n" +
                "• llama.cpp / ggml — Licencia MIT\n" +
                "  https://github.com/ggml-org/llama.cpp\n" +
                "  Motor de inferencia local para modelos de lenguaje.\n\n" +
                "• Gemma 4 (variantes E2B y E4B) — Licencia Apache 2.0\n" +
                "  https://ai.google.dev/gemma\n" +
                "  Modelos de Google DeepMind, cuando se incluyen en la distribución.\n\n" +
                "• NVIDIA CUDA Runtime — NVIDIA CUDA Toolkit EULA\n" +
                "  Componentes redistribuibles para aceleración por GPU.\n\n" +
                "• LLVM OpenMP Runtime — Apache 2.0 con excepciones LLVM\n" +
                "  https://llvm.org\n\n" +
                "• .NET 8 — Licencia MIT\n" +
                "  https://dotnet.microsoft.com\n" +
                "  Framework de desarrollo de Microsoft.\n\n" +
#if WEB_SEARCH
                "• AngleSharp — Licencia MIT\n" +
                "  https://anglesharp.github.io\n" +
                "  Extracción de texto de páginas web.\n\n" +
#endif
                "Los avisos completos están en THIRD-PARTY-NOTICES.txt, junto al\n" +
                "ejecutable.\n\n" +
                "Si cargó un modelo propio, ese modelo no forma parte de esta\n" +
                "distribución y se rige por su propia licencia.\n\n" +
                "PR-AI Assistant © 2026 Claudio Andino — Patagonia Robot.\n" +
                "Distribuido bajo la Licencia Apache 2.0 (ver el archivo LICENSE).",
                "PR-AI Assistant — Licencias de Terceros",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
        });
    }

    private void LoadProfiles()
    {
        AvailableModelItems.Clear();
        AvailableExecutionProfiles.Clear();
        _profileService.LoadCatalog();

        string? downloadDirectory = ModelDirectoryResolver.ResolveDownloadDirectory(_settings);

        // Una configuración sin descargas no debe honrar un modelo de ruta absoluta
        // heredado de un settings.json escrito por el binario comercial: se descarta
        // acá y más abajo se cae al primer modelo del catálogo.
        if (downloadDirectory == null && System.IO.Path.IsPathRooted(_settings.SelectedModel))
        {
            _settings.SelectedModel = "";
            _settings.MmprojFileName = "";
            _configManager.Save(_settings);
        }

        // Los .gguf de la carpeta de descargas se ofrecen junto a los del catálogo:
        // esta pantalla es la que fija el modelo con el que arranca el Modo
        // Investigación, y sin ellos siempre caía en el primero del catálogo.
        var models = _profileService.GetAvailableModels(downloadDirectory);
        foreach (var (model, isAvailable) in models)
        {
            AvailableModelItems.Add(new ModelDisplayItem(model, isAvailable));
        }

        var execProfiles = _profileService.GetExecutionProfiles();
        foreach (var ep in execProfiles)
        {
            AvailableExecutionProfiles.Add(new ExecutionProfileItem(ep));
        }

        var savedMatch = AvailableModelItems.FirstOrDefault(m =>
            m.IsAvailable && string.Equals(m.Model.SelectionKey, _settings.SelectedModel,
                                           StringComparison.OrdinalIgnoreCase));

        if (savedMatch != null)
        {
            SelectedModelItem = savedMatch;
        }
        else
        {
            var firstAvailable = AvailableModelItems.FirstOrDefault(m => m.IsAvailable);
            if (firstAvailable != null)
                SelectedModelItem = firstAvailable;
        }

        // El perfil guardado; si no hay ninguno —o el guardado ya no existe—, el automático
        var defaultProfile = AvailableExecutionProfiles.FirstOrDefault(p =>
            string.Equals(p.Profile.Id, _settings.ExecutionProfileId, StringComparison.OrdinalIgnoreCase))
            ?? AvailableExecutionProfiles.FirstOrDefault(p => AutoExecutionProfile.IsAuto(p.Profile))
            ?? AvailableExecutionProfiles.FirstOrDefault();

        if (defaultProfile != null)
            SelectedExecutionProfile = defaultProfile;
    }
}
