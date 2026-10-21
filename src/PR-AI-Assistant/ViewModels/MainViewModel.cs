using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using PRImager.AIAssistant.Controls;
using PRImager.AIAssistant.Helpers;
using PRImager.AIAssistant.Localization;
using PRImager.AIAssistant.Services;
using PRImager.AIAssistant.Services.Enums;
using PRImager.AIAssistant.Services.Models;

namespace PRImager.AIAssistant.ViewModels;

/// <summary>
/// ViewModel principal para enrutar los eventos de la UX y exponer el estado global.
/// </summary>
public class MainViewModel : ObservableObject
{
    private readonly ChatOrchestrator _orchestrator;
    private readonly IFileDialogService _fileDialogService;
    private readonly ConfigManager _configManager;
    private readonly ModelProfileService _profileService;
    private readonly UsageProfileService _usageProfileService;
    private readonly ContextIngestionService _ingestionService;
    private readonly ContextWindowManager _contextWindowManager;
    private AppSettings _settings;
    private System.Windows.Threading.DispatcherTimer? _thinkingTimer;

    // Coalescencia del streaming: los tokens se acumulan y se vuelcan a la burbuja
    // cada _streamFlushInterval en lugar de actualizar la UI por cada uno.
    private readonly System.Text.StringBuilder _pendingTokens = new();
    private System.Windows.Threading.DispatcherTimer? _streamFlushTimer;
    private static readonly TimeSpan _streamFlushInterval = TimeSpan.FromMilliseconds(100);
    private DateTime _thinkingStartTime;

    private ServerStatus _serverStatus = ServerStatus.Stopped;
    private string _userInput = string.Empty;
    private bool _isProcessing;
    private string _selectedModel = string.Empty;
    private string _gpuInfo = "";
    private int _loadedArtifacts;
    private int _estimatedTokens;
    private bool _isNearContextLimit;
    private string _contextAvailableText = string.Empty;
    private OperationMode _currentMode = OperationMode.Investigation;
    private InvestigationSession? _currentSession;
    private bool _isSidebarOpen;
    private string _startupError = string.Empty;
    private UsageProfile? _selectedUsageProfile;
    private EffectiveProfile? _effectiveProfile;
    private bool _isRevertingProfileSelection;

    public ObservableCollection<ChatMessageViewModel> Messages { get; }
    public ObservableCollection<string> AvailableModels { get; }
    public ObservableCollection<LoadedArtifactViewModel> LoadedArtifactsList { get; }

    /// <summary>
    /// Adjuntos del próximo mensaje en Modo General. Se vacía al enviar: son del turno,
    /// no de la sesión, que es justamente lo que los distingue del contexto forense.
    /// </summary>
    public ObservableCollection<LoadedArtifactViewModel> PendingAttachments { get; }

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
                // que no viven en Models/. Para los del catálogo es el nombre a secas.
                _settings.SelectedModel = value.Model.SelectionKey;
                _settings.MmprojFileName = value.Model.MmprojFileName;
                if (AutoExecutionProfile.IsAuto(_selectedExecutionProfile?.Profile))
                    _autoRecommendation = AutoExecutionProfile.Apply(_settings, value.Model);
                _configManager.Save(_settings);

                OnPropertyChanged(nameof(SelectedModelHardwareTag));
                OnPropertyChanged(nameof(SelectedModelCapabilitiesText));
                OnPropertyChanged(nameof(SelectedModelContextInfo));
                OnPropertyChanged(nameof(IsSelectedModelAvailable));
                OnPropertyChanged(nameof(ActiveModelDisplayName));
                ((AsyncRelayCommand)StartServerCommand).RaiseCanExecuteChanged();
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

    /// <summary>Tag de hardware del modelo seleccionado.</summary>
    public string SelectedModelHardwareTag => _selectedModelItem?.Model.HardwareTag ?? "";

    /// <summary>Texto de capacidades del modelo seleccionado (ej: "🔍 Vision · 🔊 Audio").</summary>
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

    /// <summary>Texto con info de contexto/GPU del perfil de ejecución activo.</summary>
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

    /// <summary>Si el modelo seleccionado tiene el .gguf disponible en disco.</summary>
    public bool IsSelectedModelAvailable => _selectedModelItem?.IsAvailable ?? false;

    /// <summary>
    /// Nombre del modelo activo para los avisos de contenido generado por IA y para
    /// la declaración del reporte exportado. Cae al nombre del .gguf configurado
    /// cuando el modelo no está en el catálogo (por ejemplo, uno descargado a mano):
    /// declarar el archivo real es preferible a declarar uno equivocado.
    /// </summary>
    public string ActiveModelDisplayName
    {
        get
        {
            if (_selectedModelItem != null)
                return _selectedModelItem.Model.DisplayName;

            return string.IsNullOrWhiteSpace(_settings.SelectedModel)
                ? Loc.T("message.localModel")
                : Path.GetFileNameWithoutExtension(_settings.SelectedModel);
        }
    }

    public ServerStatus ServerStatus
    {
        get => _serverStatus;
        private set
        {
            if (SetProperty(ref _serverStatus, value))
            {
                OnPropertyChanged(nameof(ServerStatusText));
                OnPropertyChanged(nameof(IsServerReady));
                ((AsyncRelayCommand)SendMessageCommand).RaiseCanExecuteChanged();
                // Actualizar habilitación de botones Iniciar/Detener según estado
                ((AsyncRelayCommand)StartServerCommand).RaiseCanExecuteChanged();
                ((AsyncRelayCommand)StopServerCommand).RaiseCanExecuteChanged();
            }
        }
    }

    public string ServerStatusText => ServerStatus switch
    {
        ServerStatus.Stopped => Loc.T("server.stopped"),
        ServerStatus.Starting => Loc.T("server.starting"),
        ServerStatus.LoadingModel => Loc.T("server.loadingModel"),
        ServerStatus.Ready => Loc.T("server.ready"),
        ServerStatus.Error => Loc.T("server.error"),
        _ => Loc.T("server.unknown")
    };

    public bool IsServerReady => ServerStatus == ServerStatus.Ready;

    public string UserInput
    {
        get => _userInput;
        set
        {
            if (SetProperty(ref _userInput, value))
            {
                ((AsyncRelayCommand)SendMessageCommand).RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsProcessing
    {
        get => _isProcessing;
        private set
        {
            if (SetProperty(ref _isProcessing, value))
            {
                ((AsyncRelayCommand)SendMessageCommand).RaiseCanExecuteChanged();
                ((RelayCommand)CancelCommand).RaiseCanExecuteChanged();
            }
        }
    }

    public string SelectedModel
    {
        get => _selectedModel;
        set
        {
            if (SetProperty(ref _selectedModel, value))
            {
                _settings.SelectedModel = value;
                _configManager.Save(_settings);
            }
        }
    }

    public bool EnableThinking
    {
        get => _settings.EnableThinking;
        set
        {
            if (_settings.EnableThinking != value)
            {
                _settings.EnableThinking = value;
                OnPropertyChanged();
                _configManager.Save(_settings);
            }
        }
    }

    /// <summary>
    /// Temperatura efectiva de la sesión. La fija el perfil al activarse; moverla acá
    /// la sobrescribe solo para la sesión en curso, sin alterar el perfil (los de fábrica
    /// son inmutables).
    /// </summary>
    public double Temperature
    {
        get => _orchestrator.TemperatureOverride ?? _settings.Temperature;
        set
        {
            if (Math.Abs(Temperature - value) > 0.01)
            {
                _orchestrator.TemperatureOverride = value;
                _settings.Temperature = (float)value;
                OnPropertyChanged();
                _configManager.Save(_settings);
            }
        }
    }

    /// <summary>Opciones predefinidas para MaxTokens en el ComboBox de la UI.</summary>
    public List<MaxTokensOption> MaxTokensOptions { get; } = new()
    {
        new MaxTokensOption(2048, Loc.F("maxTokens.fast", 2048)),
        new MaxTokensOption(4096, Loc.F("maxTokens.normal", 4096)),
        new MaxTokensOption(8192, Loc.F("maxTokens.long", 8192)),
        new MaxTokensOption(16384, Loc.F("maxTokens.max", 16384))
    };

    private MaxTokensOption? _selectedMaxTokens;

    /// <summary>Opción de MaxTokens seleccionada en la UI.</summary>
    public MaxTokensOption? SelectedMaxTokens
    {
        get => _selectedMaxTokens;
        set
        {
            if (SetProperty(ref _selectedMaxTokens, value) && value != null)
            {
                _settings.MaxTokens = value.Value;
                _configManager.Save(_settings);
                UpdateContextUsage();
            }
        }
    }

    public int LoadedArtifacts
    {
        get => _loadedArtifacts;
        private set => SetProperty(ref _loadedArtifacts, value);
    }

    public int EstimatedTokens
    {
        get => _estimatedTokens;
        private set => SetProperty(ref _estimatedTokens, value);
    }

    /// <summary>Indica si el contexto está cerca del límite (para cambio de color en UI).</summary>
    public bool IsNearContextLimit
    {
        get => _isNearContextLimit;
        private set => SetProperty(ref _isNearContextLimit, value);
    }

    /// <summary>Texto con tokens disponibles vs máximo.</summary>
    public string ContextAvailableText
    {
        get => _contextAvailableText;
        private set => SetProperty(ref _contextAvailableText, value);
    }

    public string GpuInfo
    {
        get => _gpuInfo;
        private set => SetProperty(ref _gpuInfo, value);
    }

    public bool IsSidebarOpen
    {
        get => _isSidebarOpen;
        set => SetProperty(ref _isSidebarOpen, value);
    }

    public string StartupError
    {
        get => _startupError;
        set => SetProperty(ref _startupError, value);
    }

    // === Propiedades de perfil de uso ===

    /// <summary>Perfiles de uso disponibles para el selector de la barra superior.</summary>
    public ObservableCollection<UsageProfile> AvailableUsageProfiles { get; } = new();

    /// <summary>
    /// Prompt institucional del modo de operación. Lo fija NavigationViewModel al crear la vista
    /// y tiene precedencia sobre el prompt del perfil en modo Investigación.
    /// </summary>
    public string InstitutionalSystemPrompt { get; set; } = string.Empty;

    /// <summary>Se dispara al aplicarse un perfil, para que la ventana actualice su título.</summary>
    public event EventHandler<UsageProfile>? UsageProfileApplied;

    /// <summary>
    /// Perfil de uso activo. Cambiarlo con conversación en curso pide confirmación
    /// y ofrece limpiar el historial.
    /// </summary>
    public UsageProfile? SelectedUsageProfile
    {
        get => _selectedUsageProfile;
        set
        {
            // Reversión programada tras un "Cancelar": solo reposicionar el ComboBox
            if (_isRevertingProfileSelection)
            {
                SetProperty(ref _selectedUsageProfile, value);
                return;
            }

            if (value == null || ReferenceEquals(value, _selectedUsageProfile)) return;

            var previous = _selectedUsageProfile;
            if (previous != null && Messages.Count > 0)
            {
                var choice = System.Windows.MessageBox.Show(
                    Loc.F("profile.change.message", previous.Name, value.Name),
                    Loc.T("profile.change.title"),
                    System.Windows.MessageBoxButton.YesNoCancel,
                    System.Windows.MessageBoxImage.Question);

                if (choice == System.Windows.MessageBoxResult.Cancel)
                {
                    RevertProfileSelection(previous);
                    return;
                }

                if (choice == System.Windows.MessageBoxResult.Yes)
                    ExecuteClearHistory();
            }

            SetProperty(ref _selectedUsageProfile, value);
            ApplyUsageProfile(value);
        }
    }

    /// <summary>
    /// Devuelve el ComboBox al perfil anterior. Se difiere porque la UI ya movió
    /// la selección cuando el setter se ejecuta.
    /// </summary>
    private void RevertProfileSelection(UsageProfile previous)
    {
        System.Windows.Application.Current.Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Background,
            new Action(() =>
            {
                _isRevertingProfileSelection = true;
                SelectedUsageProfile = previous;
                _isRevertingProfileSelection = false;
            }));
    }

    /// <summary>
    /// Aplica el perfil: resuelve la precedencia contra el modo, empuja prompt y temperatura
    /// al orquestador y persiste el perfil como activo.
    /// </summary>
    private void ApplyUsageProfile(UsageProfile profile)
    {
        _usageProfileService.SetActiveProfile(profile.Id);

        var effective = EffectiveProfile.Resolve(profile, CurrentMode);
        _effectiveProfile = effective;

        _orchestrator.BaseSystemPrompt = effective.ResolveSystemPrompt(InstitutionalSystemPrompt);
        _orchestrator.TemperatureOverride = effective.Temperature;

        // Cultura invariante: el log de auditoría debe ser parseable sin depender del locale
        _orchestrator.Logger.LogCustom("PROFILE_APPLIED", string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            "id={0} mode={1} temp={2:F2} web={3} descargas={4} imagenes={5} archivos={6} " +
            "log_obligatorio={7} prompt_pisado={8}",
            profile.Id, CurrentMode, effective.Temperature, effective.WebSearchEnabled,
            effective.ModelDownloadEnabled, effective.ImageGenEnabled, effective.FileUploadEnabled,
            effective.MandatoryLog, effective.IsSystemPromptOverridden));

        OnPropertyChanged(nameof(Temperature));
        OnPropertyChanged(nameof(AccentColor));
        OnPropertyChanged(nameof(ProfileName));
        OnPropertyChanged(nameof(IsWebSearchAvailable));
        OnPropertyChanged(nameof(IsModelDownloadAvailable));
        NotifyFirstRun();
        ((RelayCommand)UseGeneralProfileCommand).RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(IsImageGenAvailable));
        OnPropertyChanged(nameof(IsFileUploadAvailable));
        OnPropertyChanged(nameof(FileUploadIsForensic));
        OnPropertyChanged(nameof(ShowFileButtons));
        OnPropertyChanged(nameof(IsProfilePromptOverridden));
        OnPropertyChanged(nameof(ProfileOverrideNotice));

        // Si el perfil nuevo no habilita web, el toggle queda apagado por el getter
        OnPropertyChanged(nameof(IsWebSearchOn));
        OnPropertyChanged(nameof(WebSearchToggleTooltip));

        ((RelayCommand)OpenModelCatalogCommand).RaiseCanExecuteChanged();
        ((RelayCommand)OpenImageGenerationCommand).RaiseCanExecuteChanged();
        ((RelayCommand)OpenImagePackagesCommand).RaiseCanExecuteChanged();
        ((AsyncRelayCommand)StopImageEngineCommand).RaiseCanExecuteChanged();

        UsageProfileApplied?.Invoke(this, profile);
        UpdateContextUsage();
    }

    /// <summary>Color de acento del perfil activo, para la franja de identificación visual.</summary>
    public string AccentColor => _selectedUsageProfile?.AccentColor ?? "#64748B";

    /// <summary>Nombre del perfil activo.</summary>
    public string ProfileName => _selectedUsageProfile?.Name ?? "";

    /// <summary>Búsqueda web efectivamente disponible (perfil + modo). Consumido por E2.</summary>
    public bool IsWebSearchAvailable => _effectiveProfile?.WebSearchEnabled ?? false;

    /// <summary>Descarga de modelos efectivamente disponible (perfil + modo). Consumido por E3.</summary>
    public bool IsModelDownloadAvailable => _effectiveProfile?.ModelDownloadEnabled ?? false;

    // === Primer arranque: sin ningún modelo ===
    //
    // PR-AIA se publica sin modelos (DISTRIBUCION.md). Antes, quien lo abría por primera
    // vez veía «PR-AI Assistant listo» con el botón de enviar apagado y el de iniciar
    // también, sin una palabra de por qué. Ahora la pantalla dice qué falta y ofrece el
    // paso siguiente, según lo que esta edición y este perfil permitan.

    /// <summary>True si hay al menos un modelo en disco para elegir.</summary>
    public bool HasAvailableModel => AvailableModelItems.Any(m => m.IsAvailable);

    /// <summary>
    /// True si el catálogo está compilado pero el perfil activo no lo habilita: el caso en
    /// que la salida es cambiar de perfil, no bajar nada.
    /// </summary>
    public bool CanSwitchToGeneralForDownload =>
        IsModelCatalogCompiledIn && !IsModelDownloadAvailable && CurrentMode != OperationMode.Investigation;

    /// <summary>Qué hacer para tener un modelo, en palabras del usuario.</summary>
    public string FirstRunText
    {
        get
        {
            string carpeta = ResolveDownloadDirectory() ?? ResolveModelsDirectory();
            if (IsModelDownloadAvailable)
                return Loc.F("firstRun.withCatalog", carpeta);
            if (CanSwitchToGeneralForDownload)
                return Loc.F("firstRun.profileBlocks", _selectedUsageProfile?.Name, carpeta);
            return Loc.F("firstRun.copyOnly", carpeta);
        }
    }

    /// <summary>Pasa al perfil General, que es el que habilita el catálogo.</summary>
    public ICommand UseGeneralProfileCommand { get; private set; } = null!;

    /// <summary>Abre en el Explorador la carpeta donde van los modelos.</summary>
    public ICommand OpenModelsFolderCommand { get; private set; } = null!;

    private void NotifyFirstRun()
    {
        OnPropertyChanged(nameof(HasAvailableModel));
        OnPropertyChanged(nameof(CanSwitchToGeneralForDownload));
        OnPropertyChanged(nameof(FirstRunText));
    }

    private void ExecuteUseGeneralProfile()
    {
        var general = AvailableUsageProfiles.FirstOrDefault(p => p.Id == UsageProfileService.GeneralProfileId);
        if (general != null) SelectedUsageProfile = general;
    }

    private void ExecuteOpenModelsFolder()
    {
        string carpeta = ResolveDownloadDirectory() ?? ResolveModelsDirectory();
        try
        {
            Directory.CreateDirectory(carpeta);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = carpeta,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"No se pudo abrir {carpeta}: {ex.Message}");
        }
    }

    /// <summary>
    /// Generación de imágenes efectivamente disponible (perfil + modo + edición).
    ///
    /// No repite la comprobación de compilación: <c>EffectiveProfile.Resolve</c> ya la
    /// aplica, y la regla de precedencia vive en un único lugar por diseño (1.2 del
    /// maestro). <see cref="IsImageGenCompiledIn"/> se usa para otra cosa: esconder la
    /// sección entera de la barra lateral en lugar de mostrarla deshabilitada.
    ///
    /// Encima del perfil está el interruptor de la pantalla de inicio: apagado, no hay
    /// imágenes aunque el perfil las permita. El perfil puede quitar; el interruptor también.
    /// </summary>
    public bool IsImageGenAvailable => (_effectiveProfile?.ImageGenEnabled ?? false) && IsImageGenSwitchedOn;

    /// <summary>El interruptor de la pantalla de inicio (<c>ModeSelectionViewModel.IsImageGenOn</c>).</summary>
    private bool IsImageGenSwitchedOn
    {
        get
        {
#if IMAGE_GEN
            return ImageModelServiceCompartido.GenerationEnabled;
#else
            return false;
#endif
        }
    }

    /// <summary>
    /// Por qué la sección de imágenes está desactivada: lo apagó el usuario al entrar, o
    /// el perfil no lo permite. Son dos arreglos distintos y el aviso tiene que decir cuál.
    /// </summary>
    public string ImageGenDisabledText => IsImageGenCompiledIn && !IsImageGenSwitchedOn
        ? Loc.T("sidebar.images.switchedOff")
        : Loc.T("sidebar.images.disabled");

    /// <summary>
    /// Carga de archivos efectivamente disponible (perfil + modo).
    /// El Modo Investigación la fuerza a true; fuera de él la decide el perfil.
    /// </summary>
    public bool IsFileUploadAvailable => _effectiveProfile?.FileUploadEnabled ?? false;

    /// <summary>
    /// True cuando lo que se cargue entra como evidencia auditada al contexto forense.
    /// False en Modo General, donde el archivo es un adjunto del mensaje y nada más.
    /// </summary>
    public bool FileUploadIsForensic => _effectiveProfile?.FileUploadIsForensic ?? false;

    /// <summary>Hay adjuntos esperando: la tira sobre el input se muestra.</summary>
    public bool HasPendingAttachments => PendingAttachments.Count > 0;

    /// <summary>True si el modo institucional pisó el prompt del perfil.</summary>
    public bool IsProfilePromptOverridden => _effectiveProfile?.IsSystemPromptOverridden ?? false;

    /// <summary>Aviso para la UI cuando el perfil está parcialmente pisado por el modo.</summary>
    public string ProfileOverrideNotice => IsProfilePromptOverridden
        ? Loc.T("profile.overriddenByInvestigation")
        : "";

    // === Búsqueda web (E2) ===

    /// <summary>
    /// True si el subsistema de búsqueda web fue compilado en este binario.
    /// En la configuración Forense es false y el código de red directamente no existe.
    /// El XAML no puede evaluar #if, así que lo consulta por acá.
    /// </summary>
    public bool IsWebSearchCompiledIn =>
#if WEB_SEARCH
        true;
#else
        false;
#endif

    private bool _isWebSearchOn;

    /// <summary>
    /// Toggle 🌐: el usuario decide cuándo buscar. El modelo nunca lo decide.
    /// No se puede encender si el perfil efectivo no lo habilita.
    /// </summary>
    public bool IsWebSearchOn
    {
        get => _isWebSearchOn && IsWebSearchAvailable;
        set
        {
            bool effective = value && IsWebSearchAvailable;
            if (SetProperty(ref _isWebSearchOn, effective))
                OnPropertyChanged(nameof(WebSearchToggleTooltip));
        }
    }

    /// <summary>Explica por qué el toggle está como está.</summary>
    public string WebSearchToggleTooltip
    {
        get
        {
            if (!IsWebSearchCompiledIn)
                return Loc.T("web.toggle.notCompiled");
            if (IsProfilePromptOverridden)
                return Loc.T("web.toggle.investigation");
            if (!IsWebSearchAvailable)
                return Loc.F("web.toggle.profileBlocks", ProfileName);
            return IsWebSearchOn
                ? Loc.T("web.toggle.on")
                : Loc.T("web.toggle.off");
        }
    }

    private string _webSearchStatus = string.Empty;

    /// <summary>Texto de progreso: "Buscando…" → "Leyendo 3 páginas…".</summary>
    public string WebSearchStatus
    {
        get => _webSearchStatus;
        private set
        {
            if (SetProperty(ref _webSearchStatus, value))
                OnPropertyChanged(nameof(IsWebSearchRunning));
        }
    }

    /// <summary>True mientras el pipeline está trabajando.</summary>
    public bool IsWebSearchRunning => !string.IsNullOrEmpty(_webSearchStatus);

    /// <summary>Opciones de frescura para el ComboBox.</summary>
    public List<FreshnessOption> FreshnessOptions { get; } = new()
    {
        new FreshnessOption(1, Loc.T("web.freshness.day")),
        new FreshnessOption(2, Loc.T("web.freshness.week")),
        new FreshnessOption(3, Loc.T("web.freshness.month")),
        new FreshnessOption(0, Loc.T("web.freshness.any"))
    };

    private FreshnessOption? _selectedFreshness;

    /// <summary>Ventana de frescura seleccionada.</summary>
    public FreshnessOption? SelectedFreshness
    {
        get => _selectedFreshness;
        set
        {
            if (SetProperty(ref _selectedFreshness, value) && value != null)
            {
                _settings.WebSearchFreshness = value.Value;
                _configManager.Save(_settings);
            }
        }
    }

    /// <summary>
    /// Dirección de la instancia de SearXNG. Se persiste en la configuración de
    /// usuario. No es una credencial: se muestra en claro a propósito, para que el
    /// perito pueda verificar contra qué servidor sale la consulta.
    /// </summary>
    public string SearxngUrl
    {
        get => _settings.SearxngUrl;
        set
        {
            string incoming = (value ?? "").Trim();
            if (_settings.SearxngUrl != incoming)
            {
                _settings.SearxngUrl = incoming;
                _configManager.Save(_settings);
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsWebSearchConfigured));
                OnPropertyChanged(nameof(WebSearchConfigStatus));
            }
        }
    }

    /// <summary>
    /// True si hay una instancia de SearXNG con dirección válida. En la configuración
    /// Forense el validador no existe —Services/WebSearch no se compila— y la respuesta
    /// es siempre false: ese binario no busca en la web.
    /// </summary>
    public bool IsWebSearchConfigured =>
#if WEB_SEARCH
        Services.WebSearch.SearxngSearchProvider.TryBuildBaseUri(_settings.SearxngUrl, out _);
#else
        false;
#endif

    /// <summary>Estado de configuración de la búsqueda web, para el panel lateral.</summary>
    public string WebSearchConfigStatus
    {
        get
        {
            if (IsWebSearchConfigured)
                return Loc.F("web.config.ok", _settings.SearxngUrl);

            return string.IsNullOrWhiteSpace(_settings.SearxngUrl)
                ? Loc.T("web.config.missing")
                : Loc.T("web.config.invalid");
        }
    }

    // === Propiedades de modo de operación ===

    /// <summary>Modo de operación actual (Investigation o Consultation).</summary>
    public OperationMode CurrentMode
    {
        get => _currentMode;
        set
        {
            if (SetProperty(ref _currentMode, value))
            {
                OnPropertyChanged(nameof(ShowContextPanel));
                OnPropertyChanged(nameof(ShowFileButtons));
                OnPropertyChanged(nameof(ShowModeBadge));
                OnPropertyChanged(nameof(ModeBadgeText));
                OnPropertyChanged(nameof(IsInvestigationMode));
                ((RelayCommand)ExportSessionCommand).RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>Sesión de investigación activa (null en modo consulta).</summary>
    public InvestigationSession? CurrentSession
    {
        get => _currentSession;
        set
        {
            if (SetProperty(ref _currentSession, value))
            {
                OnPropertyChanged(nameof(HasSession));
                OnPropertyChanged(nameof(SessionPeritoText));
                OnPropertyChanged(nameof(SessionCaseText));
            }
        }
    }

    /// <summary>
    /// Muestra el panel derecho de contexto forense. Sigue atado al modo, no al perfil:
    /// ese panel significa evidencia, y en Modo General lo que se carga no lo es. Los
    /// adjuntos de General se ven en la tira sobre el input.
    /// </summary>
    public bool ShowContextPanel => CurrentMode == OperationMode.Investigation;

    /// <summary>
    /// Muestra los botones de carga de archivos e imágenes. Depende del perfil efectivo
    /// y no del modo: no todo lo que se carga es evidencia, y el usuario debe poder
    /// adjuntar un archivo en Modo General.
    /// </summary>
    public bool ShowFileButtons => IsFileUploadAvailable;

    /// <summary>Texto del badge de sandbox para modo consulta.</summary>
    public string ModeBadgeText => CurrentMode == OperationMode.Consultation
        ? Loc.T("chat.badge.sandbox") : "";

    /// <summary>Muestra el badge de sandbox (solo Consulta).</summary>
    public bool ShowModeBadge => CurrentMode == OperationMode.Consultation;

    /// <summary>Indica si estamos en modo investigación (para visibilidad de controles).</summary>
    public bool IsInvestigationMode => CurrentMode == OperationMode.Investigation;

    /// <summary>Indica si hay una sesión de investigación activa.</summary>
    public bool HasSession => CurrentSession != null;

    /// <summary>Texto con nombre y legajo del perito para el panel izquierdo.</summary>
    public string SessionPeritoText => CurrentSession != null
        ? Loc.F("session.peritoFormat", CurrentSession.PeritoName, CurrentSession.PeritoBadge) : "";

    /// <summary>Texto con número de causa para el panel izquierdo.</summary>
    public string SessionCaseText => CurrentSession != null
        ? Loc.F("session.caseFormat", CurrentSession.CaseNumber) : "";

    // Comandos
    public ICommand SendMessageCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand ClearHistoryCommand { get; }
    public ICommand StartServerCommand { get; }
    public ICommand StopServerCommand { get; }
    public ICommand LoadFilesCommand { get; }
    public ICommand LoadImagesCommand { get; }
    public ICommand RefreshModelsCommand { get; }

    /// <summary>Exportar sesión de investigación (placeholder hasta P18).</summary>
    public ICommand ExportSessionCommand { get; }

    /// <summary>Comando para seleccionar un perfil de ejecución desde los botones toggle.</summary>
    public ICommand SelectExecutionProfileCommand { get; }

    /// <summary>Comando para volver a la pantalla de selección de modo. Asignado por NavigationViewModel.</summary>
    public ICommand? GoBackCommand { get; set; }

    /// <summary>Abre la ventana de gestión de perfiles de uso.</summary>
    public ICommand ManageProfilesCommand { get; }

    /// <summary>Abre el catálogo de modelos de HuggingFace.</summary>
    public ICommand OpenModelCatalogCommand { get; }

    /// <summary>Abre la ventana de generación de imágenes (E4).</summary>
    public ICommand OpenImageGenerationCommand { get; }

    /// <summary>Abre el selector de paquetes de modelo de imagen (E4.3).</summary>
    public ICommand OpenImagePackagesCommand { get; }

    /// <summary>Apaga solo el motor de imágenes, dejando el del chat como está.</summary>
    public ICommand StopImageEngineCommand { get; }

    /// <summary>Abre la configuración de carpetas de datos.</summary>
    public ICommand OpenFolderSettingsCommand { get; }

    public ICommand ToggleSidebarCommand { get; }

    public MainViewModel(ChatOrchestrator orchestrator, IFileDialogService fileDialogService, ConfigManager configManager, ModelProfileService profileService, UsageProfileService usageProfileService)
    {
        _orchestrator = orchestrator;
        _fileDialogService = fileDialogService;
        _configManager = configManager;
        _profileService = profileService;
        _usageProfileService = usageProfileService;
        _ingestionService = new ContextIngestionService();
        _settings = _configManager.Load();

        // El pie de la barra lateral decía «GPU: Detección Automática» hasta arrancar el
        // servidor. La PC se conoce desde antes (E5); al arrancar, el motor lo reemplaza
        // por el dispositivo que efectivamente usa.
        _gpuInfo = Loc.F("hardware.yourPc", AutoExecutionProfile.Hardware(AutoExecutionProfile.ServerDirectory(_settings)).Summary);

        // Inicializar gestor de ventana de contexto con valores de configuración
        _contextWindowManager = new ContextWindowManager
        {
            MaxContextTokens = _settings.CtxSize,
            ReservedResponseTokens = _settings.MaxTokens
        };

        Messages = new ObservableCollection<ChatMessageViewModel>();
        AvailableModels = new ObservableCollection<string>();
        LoadedArtifactsList = new ObservableCollection<LoadedArtifactViewModel>();
        PendingAttachments = new ObservableCollection<LoadedArtifactViewModel>();
        PendingAttachments.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasPendingAttachments));

        // Seleccionar opción de MaxTokens según configuración guardada
        _selectedMaxTokens = MaxTokensOptions.FirstOrDefault(o => o.Value == _settings.MaxTokens)
            ?? MaxTokensOptions[2]; // Default: 8192

        // Frescura de búsqueda web según configuración guardada
        _selectedFreshness = FreshnessOptions.FirstOrDefault(f => f.Value == _settings.WebSearchFreshness)
            ?? FreshnessOptions[1]; // Default: última semana

        SendMessageCommand = new AsyncRelayCommand(ExecuteSendMessageAsync, CanExecuteSendMessage);
        CancelCommand = new RelayCommand(ExecuteCancel, CanExecuteCancel);
        ClearHistoryCommand = new RelayCommand(ExecuteClearHistory);
        StartServerCommand = new AsyncRelayCommand(ExecuteStartServerAsync, CanExecuteStartServer);
        StopServerCommand = new AsyncRelayCommand(ExecuteStopServerAsync, CanExecuteStopServer);
        LoadFilesCommand = new AsyncRelayCommand(ExecuteLoadFilesAsync);
        LoadImagesCommand = new AsyncRelayCommand(ExecuteLoadImagesAsync);
        RefreshModelsCommand = new RelayCommand(ExecuteRefreshModels);
        ExportSessionCommand = new RelayCommand(ExecuteExportSession, CanExecuteExportSession);
        SelectExecutionProfileCommand = new RelayCommand<ExecutionProfileItem>(ep => { if (ep != null) SelectedExecutionProfile = ep; });
        ToggleSidebarCommand = new RelayCommand(() => IsSidebarOpen = !IsSidebarOpen);
        ManageProfilesCommand = new RelayCommand(ExecuteManageProfiles);
        OpenModelCatalogCommand = new RelayCommand(ExecuteOpenModelCatalog, () => IsModelDownloadAvailable);
        UseGeneralProfileCommand = new RelayCommand(ExecuteUseGeneralProfile, () => CanSwitchToGeneralForDownload);
        OpenModelsFolderCommand = new RelayCommand(ExecuteOpenModelsFolder);
        OpenImageGenerationCommand = new RelayCommand(ExecuteOpenImageGeneration, () => IsImageGenAvailable);
        OpenImagePackagesCommand = new RelayCommand(ExecuteOpenImagePackages, () => IsImageGenAvailable);
        StopImageEngineCommand = new AsyncRelayCommand(ExecuteStopImageEngineAsync, () => IsImageEngineRunning);
        OpenFolderSettingsCommand = new RelayCommand(ExecuteOpenFolderSettings, () => AreFoldersConfigurable);

        _orchestrator.ServerStatusChanged += (s, e) =>
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                // El motivo se fija antes que el estado: la vista de error se muestra
                // por ServerStatus y tiene que encontrar el texto ya puesto.
                if (!string.IsNullOrWhiteSpace(e.ErrorMessage))
                    StartupError = e.ErrorMessage!;
                else if (e.NewStatus == ServerStatus.Ready)
                    StartupError = string.Empty;

                ServerStatus = e.NewStatus;
            });
        _orchestrator.StreamingTokenReceived += OnStreamingTokenReceived;
        _orchestrator.ResponseCompleted += OnResponseCompleted;
        _orchestrator.MetricsCalculated += OnMetricsCalculated;
        _orchestrator.ReasoningProgress += OnReasoningProgress;
        _orchestrator.ErrorOccurred += OnErrorOccurred;
        _orchestrator.DetectedBackendChanged += (s, backend) =>
            System.Windows.Application.Current.Dispatcher.Invoke(() => GpuInfo = backend);
        _orchestrator.HistoryTrimmed += (s, count) =>
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
                Messages.Add(new ChatMessageViewModel("system",
                    Loc.F("message.historyTrimmed", count), icon: IconKind.Info)));

        // Actualizar habilitación de ExportSessionCommand cuando cambian los mensajes
        Messages.CollectionChanged += (_, _) =>
            ((RelayCommand)ExportSessionCommand).RaiseCanExecuteChanged();

        ExecuteRefreshModels();
        RefreshImageModelChoices();
        LoadProfiles();

        // Inicializar el ServerStatus con el estado actual del orquestador
        ServerStatus = _orchestrator.CurrentServerStatus;

        // Auto-iniciar el servidor si está detenido y hay un modelo válido seleccionado
        if (ServerStatus == ServerStatus.Stopped && _selectedModelItem != null && _selectedModelItem.IsAvailable)
        {
            _ = ExecuteStartServerAsync();
        }
    }

    /// <summary>
    /// Completa la inicialización que depende del modo de operación.
    /// NavigationViewModel debe invocarlo después de fijar <see cref="CurrentMode"/> e
    /// <see cref="InstitutionalSystemPrompt"/>: la precedencia se resuelve contra ambos.
    /// </summary>
    public void InitializeUsageProfile()
    {
        AvailableUsageProfiles.Clear();
        foreach (var p in _usageProfileService.Profiles)
            AvailableUsageProfiles.Add(p);

        var active = _usageProfileService.ActiveProfile;

        // Asignación directa al campo: en el arranque no corresponde pedir confirmación
        _selectedUsageProfile = AvailableUsageProfiles.FirstOrDefault(p => p.Id == active.Id)
            ?? AvailableUsageProfiles.FirstOrDefault();
        OnPropertyChanged(nameof(SelectedUsageProfile));

        if (_selectedUsageProfile != null)
            ApplyUsageProfile(_selectedUsageProfile);
    }

    /// <summary>
    /// Abre la gestión de perfiles y recarga la lista al cerrarse, conservando el activo
    /// si sigue existiendo.
    /// </summary>
    private void ExecuteManageProfiles()
    {
        var vm = new ProfileManagerViewModel(_usageProfileService);
        var window = new Views.ProfileManagerWindow
        {
            DataContext = vm,
            Owner = System.Windows.Application.Current.MainWindow
        };
        vm.CloseRequested += (_, _) => window.Close();
        window.ShowDialog();

        string? activeId = _selectedUsageProfile?.Id;

        AvailableUsageProfiles.Clear();
        foreach (var p in _usageProfileService.Profiles)
            AvailableUsageProfiles.Add(p);

        var restored = AvailableUsageProfiles.FirstOrDefault(p => p.Id == activeId);
        if (restored != null)
        {
            // Mismo perfil: refrescar la referencia sin disparar el diálogo de cambio
            _selectedUsageProfile = restored;
            OnPropertyChanged(nameof(SelectedUsageProfile));
            ApplyUsageProfile(restored);
        }
        else
        {
            // El perfil activo fue eliminado: el servicio ya movió el foco a otro
            _selectedUsageProfile = null;
            SelectedUsageProfile = AvailableUsageProfiles
                .FirstOrDefault(p => p.Id == _usageProfileService.ActiveProfile.Id);
        }
    }

    /// <summary>
    /// True si el catálogo de modelos fue compilado en este binario.
    /// El XAML no puede evaluar #if, así que lo consulta por acá.
    /// </summary>
    public bool IsModelCatalogCompiledIn =>
#if MODEL_DOWNLOAD
        true;
#else
        false;
#endif

    /// <summary>
    /// Abre el catálogo de modelos. Al terminar una descarga, o al eliminar un
    /// modelo, refresca el listado local para que el selector no quede describiendo
    /// un disco que ya cambió.
    /// </summary>
    private void ExecuteOpenModelCatalog()
    {
#if MODEL_DOWNLOAD
        string modelsDir = ResolveDownloadDirectory() ?? ResolveModelsDirectory();

        string logsDir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "PRImager", "AIAssistant", "Logs");

        // Los archivos que vienen con el producto no se ofrecen para eliminar. Se
        // incluyen los mmproj: si se borra el proyector, el modelo pierde la visión
        // sin dar ningún error, que es el defecto 4.8 otra vez.
        var protegidos = _profileService.GetAvailableModels(null)
            .SelectMany(m => new[] { m.Model.ModelFileName, m.Model.MmprojFileName })
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .ToList();

        var store = new Services.HuggingFace.DownloadedModelStore(
            downloadDirectory: ResolveDownloadDirectory(),
            catalogFileNames: protegidos,
            activeSelectionKey: () => _settings.SelectedModel);

        var vm = new HuggingFace.ModelCatalogViewModel(
            destinationDirectory: modelsDir,
            contextTokens: _settings.CtxSize,
            logDirectory: logsDir,
            tokenProvider: () => _settings.HuggingFaceToken,
            onModelDownloaded: path =>
            {
                _orchestrator.Logger.LogCustom("MODEL_DOWNLOADED", $"path=\"{path}\"");
                ExecuteRefreshModels();
            },
            store: store,
            onModelDeleted: record =>
            {
                _orchestrator.Logger.LogCustom("MODEL_DELETED",
                    $"file=\"{record.FileName}\" bytes={record.SizeBytes}");
                ExecuteRefreshModels();
            },
            // El motor de chat que se va a usar decide si la placa sirve: CUDA no usa una AMD
            hardware: new Services.HardwareInfoService(AutoExecutionProfile.ServerDirectory(_settings)));

        var window = new Views.HuggingFace.ModelCatalogWindow
        {
            DataContext = vm,
            Owner = System.Windows.Application.Current.MainWindow
        };
        vm.CloseRequested += (_, _) => window.Close();
        window.ShowDialog();
        vm.Dispose();
#endif
    }

    /// <summary>
    /// True si la generación de imágenes fue compilada en este binario.
    /// El XAML no puede evaluar #if, así que lo consulta por acá — mismo criterio que
    /// <see cref="IsModelCatalogCompiledIn"/>.
    /// </summary>
    public bool IsImageGenCompiledIn =>
#if IMAGE_GEN
        true;
#else
        false;
#endif

#if IMAGE_GEN
    /// <summary>
    /// Ventana de generación abierta, si hay una. Se guarda para no abrir dos: cada una
    /// levantaría su propio sd-server y pelearían por el mismo puerto.
    /// </summary>
    private Views.ImageGen.ImageGenerationWindow? _imageGenWindow;
    private ImageGen.ImageGenerationViewModel? _imageGenViewModel;
#endif

    /// <summary>True si la ventana de imágenes tiene sd-server arriba o arrancando.</summary>
    private bool IsImageEngineRunning
    {
        get
        {
#if IMAGE_GEN
            return _imageGenViewModel?.IsEngineRunning == true;
#else
            return false;
#endif
        }
    }

    /// <summary>
    /// Abre la ventana de generación de imágenes, **no modal**.
    ///
    /// No modal no es un detalle de comodidad: E4.0 midió que los dos motores conviven en
    /// esta GPU y que el chat responde en 0,3 s mientras se genera una imagen. Una ventana
    /// modal desperdiciaría eso — se podría hacer una cosa o la otra, nunca las dos.
    ///
    /// Si ya hay una abierta se la trae al frente en lugar de abrir otra: dos ventanas
    /// levantarían dos `sd-server` en el mismo puerto y la segunda fallaría con un mensaje
    /// que hablaría de puertos, no del problema real.
    /// </summary>
    private void ExecuteOpenImageGeneration()
    {
#if IMAGE_GEN
        if (_imageGenWindow is not null)
        {
            if (_imageGenWindow.WindowState == System.Windows.WindowState.Minimized)
                _imageGenWindow.WindowState = System.Windows.WindowState.Normal;
            _imageGenWindow.Activate();
            return;
        }

        var servicio = ImageModelServiceCompartido;
        var vm = new ImageGen.ImageGenerationViewModel(
            servicio,
            enginePathResolver: servicio.ResolveEnginePath,
            auditLog: (evento, detalle) => _orchestrator.Logger.LogCustom(evento, detalle));

        var window = new Views.ImageGen.ImageGenerationWindow(vm)
        {
            Owner = System.Windows.Application.Current.MainWindow
        };
        _imageGenWindow = window;
        _imageGenViewModel = vm;

        // «Detener» tiene que habilitarse cuando arranca el motor de imágenes aunque el chat
        // esté parado. El aviso llega desde el hilo del manager.
        EventHandler alCambiarMotor = (_, _) => System.Windows.Application.Current?.Dispatcher.BeginInvoke(
            NotifyImageEngineState);
        vm.EngineRunningChanged += alCambiarMotor;
        window.Closed += (_, _) =>
        {
            vm.EngineRunningChanged -= alCambiarMotor;
            _imageGenWindow = null;
            _imageGenViewModel = null;
            NotifyImageEngineState();
        };

        _orchestrator.Logger.LogCustom("IMAGE_GEN_OPENED", $"perfil={_selectedUsageProfile?.Id}");
        window.Show();
#endif
    }

    /// <summary>
    /// Abre el selector de paquetes de modelo de imagen. Es modal: configurar qué modelo
    /// se usa mientras se está generando con él no tendría sentido.
    /// </summary>
    private void ExecuteOpenImagePackages()
    {
#if IMAGE_GEN
        var vm = new ImageGen.ImagePackageSelectorViewModel(ImageModelServiceCompartido, _fileDialogService);
        var window = new Views.ImageGen.ImagePackageSelectorWindow(vm)
        {
            Owner = System.Windows.Application.Current.MainWindow
        };
        window.ShowDialog();

        // La ventana de generación lee el paquete al generar, así que un cambio acá se
        // toma solo. Los pasos y el CFG se rehacen del lado del ViewModel cuando detecta
        // que el paquete cambió, que es lo que evita generar con los valores del anterior.
        // Lo que sí hay que rehacer es la lista de la barra lateral: pudo bajarse uno.
        RefreshImageModelChoices();
#endif
    }

    // ------------------------------------------------ modelo de imagen en la barra lateral

    /// <summary>
    /// Los modelos de imagen listos para usar, para elegirlos desde la barra lateral como
    /// se elige el modelo de lenguaje. Hasta el 21 de octubre de 2026 había que abrir la
    /// ventana de modelos de imagen para cambiarlo. En la edición Forense queda vacía.
    /// </summary>
    public ObservableCollection<ImageModelChoice> ImageModelChoices { get; } = new();

    public bool HasImageModels => ImageModelChoices.Count > 0;

    private ImageModelChoice? _selectedImageModel;
    public ImageModelChoice? SelectedImageModel
    {
        get => _selectedImageModel;
        set
        {
            if (value is null || value == _selectedImageModel) return;
            _selectedImageModel = value;
            OnPropertyChanged();
#if IMAGE_GEN
            if (ImageModelServiceCompartido.SelectedPackageId != value.Id)
            {
                ImageModelServiceCompartido.Select(value.Id);
                _orchestrator.Logger.LogCustom("IMAGE_MODEL_SELECTED", $"modelo={value.Id}");
            }
            // La ventana de generación, si está abierta, muestra el modelo y sus pasos:
            // que no se quede con los del anterior hasta la próxima imagen.
            _imageGenViewModel?.ApplyPackageDefaults();
#endif
        }
    }

    private void RefreshImageModelChoices()
    {
#if IMAGE_GEN
        var servicio = ImageModelServiceCompartido;
        ImageModelChoices.Clear();
        foreach (var p in servicio.CompletePackages)
            ImageModelChoices.Add(new ImageModelChoice(p.Id, p.DisplayName));

        // Si no hay ninguno elegido y hay uno solo bajado, ese es: no tiene sentido
        // hacer elegir entre una opción.
        var elegido = ImageModelChoices.FirstOrDefault(c => c.Id == servicio.SelectedPackageId);
        if (elegido is null && ImageModelChoices.Count == 1)
            elegido = ImageModelChoices[0];

        _selectedImageModel = null;
        if (elegido is not null) SelectedImageModel = elegido;
        else OnPropertyChanged(nameof(SelectedImageModel));
        OnPropertyChanged(nameof(HasImageModels));
#endif
    }

    /// <summary>Encendido o apagado, para la línea de estado de la sección de imágenes.</summary>
    public string ImageEngineStatusText => IsImageEngineRunning
        ? Loc.T("sidebar.images.engineOn")
        : Loc.T("sidebar.images.engineOff");

    public string ImageEngineStatusColor => IsImageEngineRunning ? "#10B981" : "#64748B";

    private void NotifyImageEngineState()
    {
        ((AsyncRelayCommand)StopServerCommand).RaiseCanExecuteChanged();
        ((AsyncRelayCommand)StopImageEngineCommand).RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(ImageEngineStatusText));
        OnPropertyChanged(nameof(ImageEngineStatusColor));
    }

    /// <summary>
    /// Apaga solo el motor de imágenes, para devolverle a la placa lo que ocupa cuando
    /// solo se quiere conversar. Lo pidió el usuario el 21 de octubre de 2026: «Detener»
    /// apaga los dos. La próxima imagen lo vuelve a encender sola.
    /// </summary>
    private async Task ExecuteStopImageEngineAsync()
    {
#if IMAGE_GEN
        if (_imageGenViewModel is { IsEngineRunning: true } imagenes)
            await imagenes.StopEngineAsync();
        NotifyImageEngineState();
#else
        await Task.CompletedTask;
#endif
    }

#if IMAGE_GEN
    private Services.ImageGen.ImageModelService? _imageModelService;

    /// <summary>
    /// Un solo <c>ImageModelService</c> para las dos ventanas.
    ///
    /// Con instancias separadas, elegir un paquete en el selector no se vería desde la
    /// generación hasta reabrirla: cada una tendría su copia en memoria del mismo archivo.
    /// </summary>
    private Services.ImageGen.ImageModelService ImageModelServiceCompartido =>
        _imageModelService ??= new Services.ImageGen.ImageModelService();
#endif

    private Services.Folders.AppFolderService? _folderService;

    /// <summary>
    /// Las tres carpetas de datos del usuario, en un solo servicio.
    ///
    /// Se le conecta **la misma** instancia de <c>ImageModelService</c> que usan las dos
    /// ventanas de imágenes: con una copia aparte, cambiar la carpeta acá no se vería del
    /// otro lado hasta reabrir, que es el mismo motivo por el que esa instancia se comparte.
    /// </summary>
    private Services.Folders.AppFolderService FolderServiceCompartido
    {
        get
        {
            if (_folderService is null)
            {
                _folderService = new Services.Folders.AppFolderService(
                    _settings, () => _configManager.Save(_settings));
#if IMAGE_GEN
                _folderService.UseImageModelService(ImageModelServiceCompartido);
#endif
            }
            return _folderService;
        }
    }

    /// <summary>
    /// True si esta edición administra alguna carpeta de datos del usuario. En la edición
    /// institucional es false —no descarga modelos ni genera imágenes— y la entrada de la
    /// barra lateral no se muestra, en lugar de abrir una ventana vacía.
    /// </summary>
    public bool AreFoldersConfigurable => FolderServiceCompartido.List().Count > 0;

    /// <summary>
    /// Abre la ventana de carpetas. Es modal, como el selector de paquetes: mover
    /// gigabytes mientras se descarga o se genera con esa misma carpeta sería pedirle al
    /// usuario que se pise a sí mismo.
    /// </summary>
    private void ExecuteOpenFolderSettings()
    {
        var vm = new FolderSettingsViewModel(
            FolderServiceCompartido,
            _fileDialogService,
            auditLog: (evento, detalle) => _orchestrator.Logger.LogCustom(evento, detalle));

        var window = new Views.FolderSettingsWindow(vm)
        {
            Owner = System.Windows.Application.Current.MainWindow
        };
        window.ShowDialog();

        // La carpeta de descargas pudo cambiar, y el selector de modelos la escanea.
        ExecuteRefreshModels();
    }

    /// <summary>Carpeta de modelos efectiva, resuelta contra el directorio de la app.</summary>
    private string ResolveModelsDirectory() =>
        ModelDirectoryResolver.ResolveModelsDirectory(_settings);

    /// <summary>
    /// Carpeta donde aterrizan las descargas, que el selector escanea además del catálogo.
    /// La comparte con la pantalla de selección de modo: ver <see cref="ModelDirectoryResolver"/>.
    /// </summary>
    private string? ResolveDownloadDirectory() =>
        ModelDirectoryResolver.ResolveDownloadDirectory(_settings);

    private bool CanExecuteSendMessage()
    {
        return !string.IsNullOrWhiteSpace(UserInput) && IsServerReady && !IsProcessing;
    }

    private List<string> _pendingImages = new();

    private async Task ExecuteSendMessageAsync()
    {
        string message = UserInput;
        UserInput = string.Empty;
        IsProcessing = true;

        var imagesToSend = _pendingImages.ToList();
        _pendingImages.Clear();
        // Ajuste mocoso para coherencia visual en panel derecho de UI:
        if (LoadedArtifacts >= imagesToSend.Count)
            LoadedArtifacts -= imagesToSend.Count;

        // Los adjuntos de Modo General son del turno: se consumen acá y la tira queda
        // vacía, a diferencia del contexto forense, que persiste en toda la sesión.
        string attachmentsBlock = BuildAttachmentsBlock();
        PendingAttachments.Clear();

        var userViewModel = new ChatMessageViewModel("user", message, imagesToSend.Any() ? imagesToSend : null);
        Messages.Add(userViewModel);

        var assistantViewModel = new ChatMessageViewModel(
            "assistant", "", null, ActiveModelDisplayName) { IsStreaming = true };
        Messages.Add(assistantViewModel);

        _thinkingStartTime = DateTime.Now;
        _thinkingTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _thinkingTimer.Tick += (s, e) =>
        {
            var lastMsg = Messages.LastOrDefault();
            if (lastMsg != null && lastMsg.IsAssistant && lastMsg.IsWaitingForFirstToken)
            {
                var elapsed = (DateTime.Now - _thinkingStartTime).TotalSeconds;
                lastMsg.GenerationMetrics = Loc.F("message.thinkingElapsed", elapsed);
            }
            else
            {
                _thinkingTimer?.Stop();
            }
        };
        _thinkingTimer.Start();

        // El timer se arma acá, sobre el hilo de UI: OnStreamingTokenReceived corre en
        // un hilo de background y no puede crear ni arrancar un DispatcherTimer.
        StopStreamFlush();
        _streamFlushTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = _streamFlushInterval
        };
        _streamFlushTimer.Tick += (s, e) => FlushPendingTokens();
        _streamFlushTimer.Start();

        // Enriquecimiento web: solo si el usuario lo pidió con el toggle.
        // El modelo no decide cuándo buscar.
        string promptToSend = message;
        string? historyContent = null;

        if (IsWebSearchOn)
        {
            // La búsqueda se hace con el mensaje limpio: enriquecerlo antes mandaría
            // el archivo entero como consulta al buscador.
            var enriched = await RunWebSearchAsync(message, assistantViewModel);
            if (enriched != null)
            {
                promptToSend = enriched;
                historyContent = message;
            }
        }

        // Los adjuntos se anteponen al prompt de este turno y no entran al historial,
        // por el mismo motivo que las fuentes web (ver desvío «Historial sin la
        // consulta enriquecida»): de otro modo cada turno arrastraría el archivo
        // completo y el contexto se consumiría solo.
        if (attachmentsBlock.Length > 0)
        {
            promptToSend = attachmentsBlock + promptToSend;
            historyContent ??= message;
        }

        try
        {
            await _orchestrator.SendMessageAsync(
                promptToSend, imagesToSend.Any() ? imagesToSend : null, historyContent);
        }
        catch
        {
            // Logging occurs at the orchestrator layer, handled.
        }
    }

    /// <summary>
    /// Ejecuta el pipeline de búsqueda y adjunta las fuentes al mensaje del asistente.
    /// Devuelve la consulta enriquecida, o null si no se pudo enriquecer (en cuyo caso
    /// la consulta original sigue su curso normal).
    /// </summary>
    private async Task<string?> RunWebSearchAsync(string message, ChatMessageViewModel assistantViewModel)
    {
#if WEB_SEARCH
        var provider = new Services.WebSearch.SearxngSearchProvider(() => _settings.SearxngUrl);
        using var pipeline = new Services.WebSearch.WebSearchPipeline(provider);

        pipeline.ProgressChanged += (_, status) =>
            System.Windows.Application.Current.Dispatcher.InvokeAsync(() => WebSearchStatus = status);

        try
        {
            var freshness = (Services.WebSearch.SearchFreshness)(_selectedFreshness?.Value ?? 2);
            var context = await pipeline.BuildContextAsync(message, freshness);

            if (!context.Success)
            {
                // Sin fuentes utilizables se avisa y se sigue sin enriquecer:
                // es preferible una respuesta del modelo que un error opaco.
                Messages.Insert(Messages.Count - 1, new ChatMessageViewModel("system",
                    Loc.F("web.message.failed", context.ErrorMessage), icon: IconKind.Warning));

                if (context.Sources.Count > 0)
                    assistantViewModel.WebSources = context.Sources;

                _orchestrator.Logger.LogCustom("WEB_SEARCH_FAILED", $"motivo=\"{context.ErrorMessage}\"");
                return null;
            }

            assistantViewModel.WebSources = context.Sources;

            _orchestrator.Logger.LogCustom("WEB_SEARCH",
                $"proveedor={provider.Name} leidas={context.PagesRead}/{context.PagesAttempted} " +
                $"frescura={freshness}");

            if (context.TimeRangeDropped)
            {
                Messages.Insert(Messages.Count - 1, new ChatMessageViewModel("system",
                    Loc.T("web.message.widened"), icon: IconKind.Info));
            }

            if (context.PagesRead < context.PagesAttempted)
            {
                Messages.Insert(Messages.Count - 1, new ChatMessageViewModel("system",
                    Loc.F("web.message.partial", context.PagesRead, context.PagesAttempted), icon: IconKind.Info));
            }

            return context.EnrichedQuery;
        }
        catch (Exception ex)
        {
            Messages.Insert(Messages.Count - 1, new ChatMessageViewModel("system",
                Loc.F("web.message.interrupted", ex.Message), icon: IconKind.Warning));
            _orchestrator.Logger.LogCustom("WEB_SEARCH_ERROR", ex.Message);
            return null;
        }
        finally
        {
            WebSearchStatus = string.Empty;
        }
#else
        await Task.CompletedTask;
        return null;
#endif
    }

    // Los tokens llegan de a uno desde un hilo de background. Solo se acumulan acá:
    // el volcado a la UI lo hace _streamFlushTimer sobre el hilo de interfaz.
    private void OnStreamingTokenReceived(object? sender, string token)
    {
        lock (_pendingTokens)
        {
            _pendingTokens.Append(token);
        }
    }

    /// <summary>
    /// Vuelca a la burbuja los tokens acumulados. Corre en el hilo de UI.
    /// </summary>
    private void FlushPendingTokens()
    {
        string chunk;
        lock (_pendingTokens)
        {
            if (_pendingTokens.Length == 0) return;
            chunk = _pendingTokens.ToString();
            _pendingTokens.Clear();
        }

        var lastMessage = Messages.LastOrDefault();
        if (lastMessage != null && lastMessage.IsAssistant && lastMessage.IsStreaming)
        {
            lastMessage.AppendContent(chunk);
        }
    }

    /// <summary>
    /// Corta el volcado y descarta lo pendiente. Se llama siempre desde el hilo de UI,
    /// así que ningún tick puede colarse después y pisar el texto final.
    /// </summary>
    private void StopStreamFlush()
    {
        _streamFlushTimer?.Stop();
        _streamFlushTimer = null;

        lock (_pendingTokens)
        {
            _pendingTokens.Clear();
        }
    }

    private void OnResponseCompleted(object? sender, string finalResponse)
    {
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            // finalResponse trae la respuesta completa, así que lo pendiente se descarta.
            StopStreamFlush();

            var lastMessage = Messages.LastOrDefault();
            if (lastMessage != null && lastMessage.IsAssistant && lastMessage.IsStreaming)
            {
                // El contenido primero: invertido, el paso a markdown parsearía el texto
                // parcial y volvería a parsear el definitivo un instante después.
                lastMessage.Content = finalResponse;
                lastMessage.IsStreaming = false;
            }
            IsProcessing = false;
            UpdateContextUsage();
        });
    }

    private void OnMetricsCalculated(object? sender, string metrics)
    {
        System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var lastMessage = Messages.LastOrDefault();
            if (lastMessage != null && lastMessage.IsAssistant)
            {
                lastMessage.GenerationMetrics = metrics;
            }
        });
    }

    /// <summary>
    /// Muestra el avance del razonamiento. Corta el contador genérico "Pensando…":
    /// ese timer se apaga solo cuando llega el primer token de contenido, que con un
    /// modelo de razonamiento puede tardar miles de tokens en aparecer.
    /// </summary>
    private void OnReasoningProgress(object? sender, string progress)
    {
        System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            _thinkingTimer?.Stop();

            var lastMessage = Messages.LastOrDefault();
            if (lastMessage != null && lastMessage.IsAssistant)
            {
                lastMessage.GenerationMetrics = progress;
            }
        });
    }

    private void OnErrorOccurred(object? sender, string error)
    {
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            // Lo generado antes del error se conserva: primero se vuelca, después se corta.
            FlushPendingTokens();
            StopStreamFlush();

            var lastMessage = Messages.LastOrDefault();
            if (lastMessage != null && lastMessage.IsAssistant && lastMessage.IsStreaming)
            {
                lastMessage.AppendContent(Loc.F("message.errorSuffix", error));
                lastMessage.IsStreaming = false;
            }
            IsProcessing = false;
        });
    }

    private bool CanExecuteCancel() => IsProcessing;

    private void ExecuteCancel()
    {
        _orchestrator.CancelCurrentRequest();

        // La cancelación no dispara ResponseCompleted: el cierre corre por cuenta de acá.
        FlushPendingTokens();
        StopStreamFlush();

        var lastMessage = Messages.LastOrDefault();
        if (lastMessage != null && lastMessage.IsAssistant && lastMessage.IsStreaming)
        {
            lastMessage.IsStreaming = false;
        }

        IsProcessing = false;
    }

    private void ExecuteClearHistory()
    {
        _orchestrator.ClearHistory();
        StopStreamFlush();
        Messages.Clear();
        LoadedArtifactsList.Clear();
        // Los adjuntos pendientes también se descartan: "nueva sesión" no puede dejar
        // un archivo colgado del turno anterior, que es la versión suave de 4.1.
        PendingAttachments.Clear();
        _pendingImages.Clear();
        LoadedArtifacts = 0;
        EstimatedTokens = 0;
        IsNearContextLimit = false;
        ContextAvailableText = string.Empty;
    }

    /// <summary>
    /// Actualiza las estadísticas de uso de la ventana de contexto tras cada mensaje.
    /// </summary>
    private void UpdateContextUsage()
    {
        _contextWindowManager.ReservedResponseTokens = _settings.MaxTokens;
        _contextWindowManager.MaxContextTokens = _settings.CtxSize;

        var history = _orchestrator.GetSessionHistory();
        var usage = _contextWindowManager.GetUsage(
            _orchestrator.SystemPrompt, null, history);

        EstimatedTokens = usage.TotalInputTokens;
        IsNearContextLimit = usage.IsNearLimit;
        ContextAvailableText = Loc.F("context.availableFormat", Math.Max(0, usage.AvailableTokens - usage.TotalInputTokens), usage.AvailableTokens);
    }

    private bool CanExecuteStartServer()
    {
        return (ServerStatus == ServerStatus.Stopped || ServerStatus == ServerStatus.Error)
            && _selectedModelItem != null && _selectedModelItem.IsAvailable;
    }

    // Se puede detener si alguno de los dos motores está activo en alguna fase: el del
    // chat o el de imágenes.
    private bool CanExecuteStopServer()
    {
        return IsChatServerActive || IsImageEngineRunning;
    }

    private bool IsChatServerActive =>
        ServerStatus == ServerStatus.Ready
        || ServerStatus == ServerStatus.Starting
        || ServerStatus == ServerStatus.LoadingModel;

    private async Task ExecuteStartServerAsync()
    {
        System.Diagnostics.Debug.WriteLine("=== INICIAR SERVIDOR CLICKEADO ===");
        if (_selectedModelItem == null || !_selectedModelItem.IsAvailable)
        {
            System.Diagnostics.Debug.WriteLine("=== ERROR: No hay modelo disponible ===");
            return;
        }
        _configManager.Save(_settings);

        try
        {
            await _orchestrator.InitializeAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"=== ERROR AL INICIAR: {ex.Message} ===");
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                StartupError = ex.Message;
                // Si preferimos mantenerlo limpio en el inicio, solo marcamos StartupError.
                // Si el usuario intentó iniciarlo manualmente, podemos añadir un mensaje al log si quisiéramos, 
                // pero el estado ServerStatus.Error activará la vista de error.
            });
        }
    }

    /// <summary>
    /// Detiene los dos motores. Hasta el 20 de octubre de 2026 paraba solo llama-server y
    /// sd-server seguía vivo mientras la ventana de imágenes estuviera abierta.
    /// </summary>
    private async Task ExecuteStopServerAsync()
    {
        if (IsChatServerActive)
        {
            try
            {
                await _orchestrator.StopServerAsync();
            }
            catch
            {
                // Ignorar errores cerrando
            }
        }

#if IMAGE_GEN
        if (_imageGenViewModel is { IsEngineRunning: true } imagenes)
            await imagenes.StopEngineAsync();
#endif
    }

    /// <summary>
    /// Encabezado con que viaja al modelo el texto de un archivo. Va en el idioma de la
    /// aplicación: es parte de lo que el modelo lee, y mezclar idiomas en la consulta
    /// empuja a que conteste en el que no es.
    /// </summary>
    private static string FileContentHeader(string file, string contentType)
        => Loc.F("files.contentHeader", Path.GetFileName(file), contentType) + "\n";

    private static string TruncationNote(long original, long processed)
        => "\n" + Loc.F("files.truncated", original, processed);

    private async Task ExecuteLoadFilesAsync()
    {
        bool forensic = FileUploadIsForensic;

        // El diálogo no puede decir "contexto forense" fuera de una investigación:
        // nombrar evidencia a un archivo cualquiera confunde sobre qué se está haciendo.
        var files = _fileDialogService.OpenTextFiles(forensic
            ? Loc.T("files.pick.forensic")
            : Loc.T("files.pick.attachment"));

        if (files == null || files.Length == 0) return;

        foreach (var file in files)
        {
            try
            {
                var validation = _ingestionService.ValidateFile(file);
                if (!validation.IsAccepted)
                {
                    System.Windows.MessageBox.Show(
                        Loc.F("files.rejected.file", Path.GetFileName(file), validation.RejectionReason),
                        Loc.T("files.rejected.title"), System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                    continue;
                }

                string content = FileContentHeader(file, validation.ContentType) + (validation.ProcessedContent ?? await File.ReadAllTextAsync(file));

                if (validation.WasTruncated)
                    content += TruncationNote(validation.OriginalLength, validation.ProcessedLength);

                if (forensic)
                {
                    _orchestrator.LoadForensicContext(content);

                    var artifact = LoadedArtifactViewModel.FromFile(file);
                    artifact.RemoveCommand = new RelayCommand(() => RemoveArtifact(artifact));
                    LoadedArtifactsList.Add(artifact);
                }
                else
                {
                    AddPendingAttachment(LoadedArtifactViewModel.FromFile(file, content));
                }
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(Loc.F("files.loadError", Path.GetFileName(file), ex.Message));
            }
        }

        if (forensic)
        {
            LoadedArtifacts = LoadedArtifactsList.Count + _pendingImages.Count;
            UpdateContextUsage();
        }
    }

    /// <summary>
    /// Suma un adjunto al próximo mensaje y deja constancia del hecho.
    ///
    /// Se registra qué entró —nombre, tamaño, tipo— y **nunca el contenido**: en Modo
    /// General el archivo no es evidencia y auditar lo que el usuario lee convertiría
    /// una herramienta personal en otra cosa. Pero sin ningún rastro no habría manera
    /// de reconstruir qué vio el modelo, y eso sí hace falta.
    /// </summary>
    private void AddPendingAttachment(LoadedArtifactViewModel artifact)
    {
        artifact.RemoveCommand = new RelayCommand(() => RemovePendingAttachment(artifact));
        PendingAttachments.Add(artifact);

        _orchestrator.Logger.LogCustom("FILE_ATTACHED", string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            "file=\"{0}\" bytes={1} type={2} forense=false",
            artifact.FileName, artifact.FileSize, artifact.IsImage ? "imagen" : "texto"));
    }

    private void RemovePendingAttachment(LoadedArtifactViewModel artifact)
    {
        PendingAttachments.Remove(artifact);
        if (artifact.IsImage) _pendingImages.Remove(artifact.FilePath);
    }

    /// <summary>
    /// Bloque de texto de los adjuntos del turno. Ver <see cref="AttachmentPrompt"/>.
    /// </summary>
    private string BuildAttachmentsBlock() =>
        AttachmentPrompt.Build(PendingAttachments
            .Where(a => !a.IsImage)
            .Select(a => a.AttachedContent));

    private async Task ExecuteLoadImagesAsync()
    {
        var files = _fileDialogService.OpenImages(Loc.T("files.pick.images"));
        if (files != null && files.Length > 0)
        {
            var batchValidation = _ingestionService.ValidateImageBatch(files.ToList());
            if (!batchValidation.IsAccepted)
            {
                System.Windows.MessageBox.Show(
                    batchValidation.RejectionReason,
                    Loc.T("files.rejected.title"), System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                return;
            }

            foreach (var file in files)
            {
                var validation = _ingestionService.ValidateFile(file);
                if (!validation.IsAccepted)
                {
                    System.Windows.MessageBox.Show(
                        Loc.F("files.rejected.image", Path.GetFileName(file), validation.RejectionReason),
                        Loc.T("files.rejected.title"), System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                    continue;
                }

                _pendingImages.Add(file);

                if (FileUploadIsForensic)
                {
                    var artifact = LoadedArtifactViewModel.FromFile(file, isImage: true);
                    artifact.RemoveCommand = new RelayCommand(() =>
                    {
                        _pendingImages.Remove(file);
                        RemoveArtifact(artifact);
                    });
                    LoadedArtifactsList.Add(artifact);
                }
                else
                {
                    AddPendingAttachment(LoadedArtifactViewModel.FromFile(file, isImage: true));
                }
            }

            if (FileUploadIsForensic)
                LoadedArtifacts = LoadedArtifactsList.Count + _pendingImages.Count;
        }
        await Task.CompletedTask;
    }

    /// <summary>
    /// Elimina un artefacto de la lista visual y actualiza el contador.
    /// </summary>
    private void RemoveArtifact(LoadedArtifactViewModel artifact)
    {
        LoadedArtifactsList.Remove(artifact);
        LoadedArtifacts = LoadedArtifactsList.Count + _pendingImages.Count;
    }

    /// <summary>
    /// Procesa archivos recibidos por drag and drop, clasificándolos en texto e imágenes.
    /// </summary>
    public async Task HandleDroppedFilesAsync(string[] files)
    {
        var imageExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp" };

        var imageFiles = files.Where(f => imageExtensions.Contains(Path.GetExtension(f))).ToList();
        var textFiles = files.Where(f => !imageExtensions.Contains(Path.GetExtension(f))).ToList();

        // Procesar imágenes con validación
        if (imageFiles.Count > 0)
        {
            var batchValidation = _ingestionService.ValidateImageBatch(imageFiles);
            if (!batchValidation.IsAccepted)
            {
                System.Windows.MessageBox.Show(
                    batchValidation.RejectionReason,
                    Loc.T("files.rejected.title"), System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            }
            else
            {
                foreach (var file in imageFiles)
                {
                    var validation = _ingestionService.ValidateFile(file);
                    if (!validation.IsAccepted) continue;

                    _pendingImages.Add(file);
                    var artifact = LoadedArtifactViewModel.FromFile(file);
                    artifact.RemoveCommand = new RelayCommand(() =>
                    {
                        _pendingImages.Remove(file);
                        RemoveArtifact(artifact);
                    });
                    LoadedArtifactsList.Add(artifact);
                }
            }
        }

        // Procesar archivos de texto con validación
        foreach (var file in textFiles)
        {
            try
            {
                var validation = _ingestionService.ValidateFile(file);
                if (!validation.IsAccepted)
                {
                    System.Windows.MessageBox.Show(
                        Loc.F("files.rejected.file", Path.GetFileName(file), validation.RejectionReason),
                        Loc.T("files.rejected.title"), System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                    continue;
                }

                string content = FileContentHeader(file, validation.ContentType) + (validation.ProcessedContent ?? await File.ReadAllTextAsync(file));

                if (validation.WasTruncated)
                    content += TruncationNote(validation.OriginalLength, validation.ProcessedLength);

                _orchestrator.LoadForensicContext(content);

                var artifact = LoadedArtifactViewModel.FromFile(file);
                artifact.RemoveCommand = new RelayCommand(() => RemoveArtifact(artifact));
                LoadedArtifactsList.Add(artifact);
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(Loc.F("files.loadError", Path.GetFileName(file), ex.Message));
            }
        }

        LoadedArtifacts = LoadedArtifactsList.Count + _pendingImages.Count;
        UpdateContextUsage();
    }

    /// <summary>
    /// Carga modelos y perfiles de ejecución del catálogo v2.
    /// </summary>
    private void LoadProfiles()
    {
        AvailableModelItems.Clear();
        AvailableExecutionProfiles.Clear();
        _profileService.LoadCatalog();

        string? downloadDirectory = ResolveDownloadDirectory();

        // Una configuración sin descargas no debe honrar un modelo de ruta absoluta
        // heredado de un settings.json escrito por el binario comercial: se descarta
        // acá y más abajo se cae al primer modelo del catálogo.
        if (downloadDirectory == null && Path.IsPathRooted(_settings.SelectedModel))
        {
            _settings.SelectedModel = "";
            _settings.MmprojFileName = "";
            _configManager.Save(_settings);
        }

        // Cargar modelos del catálogo más los .gguf de la carpeta de descargas
        var models = _profileService.GetAvailableModels(downloadDirectory);
        foreach (var (model, isAvailable) in models)
        {
            AvailableModelItems.Add(new ModelDisplayItem(model, isAvailable));
        }

        // Cargar perfiles de ejecución
        var execProfiles = _profileService.GetExecutionProfiles();
        foreach (var ep in execProfiles)
        {
            AvailableExecutionProfiles.Add(new ExecutionProfileItem(ep));
        }

        // Seleccionar modelo guardado o el primer disponible
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

        // El perfil guardado; si no hay ninguno —o el guardado ya no existe—, el automático.
        // Antes caía siempre en «balanced», que manda todo a la placa con 32k: en una
        // placa chica, el modelo no arrancaba.
        var defaultProfile = AvailableExecutionProfiles.FirstOrDefault(p =>
                string.Equals(p.Profile.Id, _settings.ExecutionProfileId, StringComparison.OrdinalIgnoreCase))
            ?? AvailableExecutionProfiles.FirstOrDefault(p => AutoExecutionProfile.IsAuto(p.Profile))
            ?? AvailableExecutionProfiles.FirstOrDefault();
        if (defaultProfile != null)
            SelectedExecutionProfile = defaultProfile;

        NotifyFirstRun();
    }

    private void ExecuteRefreshModels()
    {
        AvailableModels.Clear();
        string modelsPath = Path.IsPathRooted(_settings.ModelPath)
            ? _settings.ModelPath
            : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, _settings.ModelPath);
        if (Directory.Exists(modelsPath))
        {
            var files = Directory.GetFiles(modelsPath, "*.gguf");
            foreach (var f in files)
            {
                AvailableModels.Add(Path.GetFileName(f));
            }
        }
        if (_profileService != null)
            LoadProfiles();
    }

    private bool CanExecuteExportSession()
    {
        return IsInvestigationMode && Messages.Any(m => m.IsAssistant);
    }

    private void ExecuteExportSession()
    {
        if (CurrentSession == null) return;

        string defaultName = $"PRAI_Sesion_{CurrentSession.CaseNumber}_{DateTime.Now:yyyyMMdd_HHmmss}";
        string filter = Loc.T("export.filter");

        string? savePath = _fileDialogService.SaveFile(Loc.T("export.title"), filter, defaultName);
        if (string.IsNullOrEmpty(savePath)) return;

        string ext = System.IO.Path.GetExtension(savePath).ToLowerInvariant();
        ExportFormat format = ext switch
        {
            ".html" => ExportFormat.Html,
            ".json" => ExportFormat.Json,
            _ => ExportFormat.Txt
        };

        var exportMessages = Messages.Select(m => new ExportMessage
        {
            Role = m.Role,
            Content = m.Content,
            Timestamp = m.Timestamp,
            HasImages = m.ImagePaths != null && m.ImagePaths.Any()
        }).ToList();

        var exportArtifacts = LoadedArtifactsList.Select(a => new ExportArtifact
        {
            FileName = a.FileName,
            FileType = a.FileType,
            FileSize = a.FileSize,
            Notes = null
        }).ToList();

        var data = new SessionExportData
        {
            Session = CurrentSession,
            Messages = exportMessages,
            Artifacts = exportArtifacts,
            ExportTimeUtc = DateTime.UtcNow,
            ModelName = ActiveModelDisplayName,
            TotalTokensEstimated = EstimatedTokens,
            TotalInferenceMs = 0
        };

        var exporter = new SessionExporter();
        var result = exporter.Export(data, savePath, format);

        if (result.Success)
        {
            Messages.Add(new ChatMessageViewModel("system", Loc.F("export.done", savePath, result.FileHash), icon: IconKind.Success));
            _orchestrator.Logger.LogCustom("SESSION_EXPORTED", $"path=\"{savePath}\" format={format} hash={result.FileHash}");
        }
        else
        {
            Messages.Add(new ChatMessageViewModel("system", Loc.F("export.failed", result.ErrorMessage), icon: IconKind.Error));
        }

        ((RelayCommand)ExportSessionCommand).RaiseCanExecuteChanged();
    }
}

/// <summary>
/// Opción predefinida de MaxTokens para el ComboBox de la UI.
/// </summary>
public record MaxTokensOption(int Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// Opción de ventana temporal para la búsqueda web.
/// Value coincide con SearchFreshness: 0=sin filtro, 1=día, 2=semana, 3=mes.
/// </summary>
public record FreshnessOption(int Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// Item de presentación para el selector de modelos en la UI.
/// </summary>
public class ModelDisplayItem
{
    public ModelInfo Model { get; }
    public bool IsAvailable { get; }

    /// <summary>Texto principal: nombre del modelo.</summary>
    public string DisplayName => Model.DisplayName;
    /// <summary>Texto de no disponible.</summary>
    public string AvailabilityText => IsAvailable ? "" : Loc.T("model.notFound");
    /// <summary>Opacidad para modelos no disponibles.</summary>
    public double ItemOpacity => IsAvailable ? 1.0 : 0.4;

    public ModelDisplayItem(ModelInfo model, bool isAvailable)
    {
        Model = model;
        IsAvailable = isAvailable;
    }

    public override string ToString() => DisplayName;
}

/// <summary>
/// Item de presentación para los botones toggle de perfil de ejecución.
/// </summary>
public class ExecutionProfileItem : PRImager.AIAssistant.Helpers.ObservableObject
{
    public ExecutionProfile Profile { get; }

    public string DisplayName => Profile.DisplayName;

    private bool _isActive;
    /// <summary>Indica si este perfil es el activo actualmente.</summary>
    public bool IsActive
    {
        get => _isActive;
        set => SetProperty(ref _isActive, value);
    }

    public ExecutionProfileItem(ExecutionProfile profile)
    {
        Profile = profile;
    }

    public override string ToString() => DisplayName;
}

/// <summary>Un modelo de imagen listo para usar, como opción del desplegable de la barra lateral.</summary>
public sealed record ImageModelChoice(string Id, string DisplayName)
{
    public override string ToString() => DisplayName;
}
