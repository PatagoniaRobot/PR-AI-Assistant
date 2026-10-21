using System;
using System.Windows.Input;
using PRImager.AIAssistant.Helpers;
using PRImager.AIAssistant.Localization;
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

    /// <summary>Cierra y vuelve a abrir la aplicación para aplicar el idioma elegido.</summary>
    public ICommand RestartCommand { get; } = new RelayCommand(App.Restart);

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

    // === Idioma ===

    /// <summary>Una opción del selector de idioma: el código que se guarda y lo que se lee.</summary>
    public sealed record LanguageOption(string Code, string Label)
    {
        // Lo que lee un lector de pantalla en cada opción: sin esto, el nombre del record
        public override string ToString() => Label;
    }

    /// <summary>
    /// «Automático» sigue a Windows. Español e English van en su propio idioma, no
    /// traducidos: quien no entiende el idioma en que está la pantalla tiene que poder
    /// encontrar el suyo.
    /// </summary>
    public List<LanguageOption> LanguageOptions { get; } = new()
    {
        new LanguageOption(Loc.Automatic, Loc.T("language.auto")),
        new LanguageOption(Loc.Spanish, "Español"),
        new LanguageOption(Loc.English, "English")
    };

    private LanguageOption? _selectedLanguage;

    public LanguageOption? SelectedLanguage
    {
        get => _selectedLanguage;
        set
        {
            var previous = _selectedLanguage;
            if (!SetProperty(ref _selectedLanguage, value) || value is null || previous is null) return;

            _settings.Language = value.Code;
            _configManager.Save(_settings);

            // Elegir «Automático» cuando Windows ya está en el idioma actual no cambia nada
            // visible: no hace falta volver a abrir. Si cambia, se avisa al lado del
            // selector con un botón, y no con un diálogo: un MessageBox abierto desde acá
            // dejaba el desplegable trabado detrás hasta contestarlo.
            IsRestartPending = Loc.Resolve(value.Code) != Loc.Language;
        }
    }

    private bool _isRestartPending;

    /// <summary>El idioma elegido no es el que está en pantalla: falta volver a abrir.</summary>
    public bool IsRestartPending
    {
        get => _isRestartPending;
        private set => SetProperty(ref _isRestartPending, value);
    }

    // === Interruptor de la generación de imágenes ===

    /// <summary>
    /// True si el binario trae la generación de imágenes. En la edición Forense el
    /// interruptor no se dibuja: no hay nada que encender.
    /// </summary>
    public bool IsImageGenCompiledIn =>
#if IMAGE_GEN
        true;
#else
        false;
#endif

#if IMAGE_GEN
    private readonly Services.ImageGen.ImageModelService _imageModels = new();
#endif

    /// <summary>
    /// Encender o apagar la generación de imágenes antes de entrar. Apagada, la sección del
    /// chat queda desactivada y el motor de imágenes no arranca nunca (pedido del usuario,
    /// 21 de octubre de 2026: «por ahí el usuario no quiera que se active porque no la
    /// necesita»). Se guarda con la configuración de imágenes, detrás de la barrera.
    /// </summary>
    public bool IsImageGenOn
    {
        get
        {
#if IMAGE_GEN
            return _imageModels.GenerationEnabled;
#else
            return false;
#endif
        }
        set
        {
#if IMAGE_GEN
            if (value == _imageModels.GenerationEnabled) return;
            _imageModels.GenerationEnabled = value;

            // Una ventana de imágenes que quedó abierta de antes de volver al inicio se
            // cierra, y con ella el motor: apagado tiene que querer decir apagado.
            if (!value)
            {
                foreach (var w in System.Windows.Application.Current.Windows
                             .OfType<Views.ImageGen.ImageGenerationWindow>().ToList())
                    w.Close();
            }

            OnPropertyChanged();
            OnPropertyChanged(nameof(ImageGenSwitchText));
#endif
        }
    }

    public string ImageGenSwitchText => IsImageGenOn ? Loc.T("mode.images.on") : Loc.T("mode.images.off");

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
            return CapabilityText.Describe(caps);
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
            return Loc.F("model.manualProfileInfo", p.CtxSize, p.GpuLayers);
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

        _selectedLanguage = LanguageOptions.FirstOrDefault(o => o.Code == _settings.Language)
                            ?? LanguageOptions[0];

        HelpCommand = new RelayCommand(() =>
        {
            if (!HelpLauncher.Open())
            {
                System.Windows.MessageBox.Show(
                    Loc.F("help.missing.message", HelpLauncher.HelpPath),
                    Loc.T("help.missing.title"),
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Warning);
            }
        });

        LicensesCommand = new RelayCommand(() =>
        {
            System.Windows.MessageBox.Show(
                Loc.T("licenses.intro") +
                Loc.T("licenses.components") +
#if WEB_SEARCH
                Loc.T("licenses.angleSharp") +
#endif
                Loc.T("licenses.outro"),
                Loc.T("licenses.title"),
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
