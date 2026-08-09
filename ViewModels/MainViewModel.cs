using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using PRImager.AIAssistant.Helpers;
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
    private string _gpuInfo = "GPU: Detección Automática";
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
                _settings.GpuLayers = value.Profile.GpuLayers;
                _settings.CtxSize = value.Profile.CtxSize;
                if (value.Profile.Threads > 0)
                    _settings.Threads = value.Profile.Threads;
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
            var badges = caps.Select(c => c switch
            {
                "Vision" => "🔍 Vision",
                "Audio" => "🔊 Audio",
                _ => c
            });
            return string.Join(" · ", badges);
        }
    }

    /// <summary>Texto con info de contexto/GPU del perfil de ejecución activo.</summary>
    public string SelectedModelContextInfo
    {
        get
        {
            if (_selectedExecutionProfile == null) return "";
            var p = _selectedExecutionProfile.Profile;
            return $"ctx: {p.CtxSize} · capas GPU: {p.GpuLayers}";
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
                ? "modelo local"
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
        ServerStatus.Stopped => "Detenido",
        ServerStatus.Starting => "Iniciando...",
        ServerStatus.LoadingModel => "Cargando modelo...",
        ServerStatus.Ready => "Listo",
        ServerStatus.Error => "Error",
        _ => "Desconocido"
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
        new MaxTokensOption(2048, "2048 (rápido)"),
        new MaxTokensOption(4096, "4096 (normal)"),
        new MaxTokensOption(8192, "8192 (largo)"),
        new MaxTokensOption(16384, "16384 (máximo)")
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
                    $"Vas a cambiar el perfil de «{previous.Name}» a «{value.Name}».\n\n" +
                    "La conversación en curso se generó con el perfil anterior.\n\n" +
                    "Sí — cambiar y limpiar el historial\n" +
                    "No — cambiar y conservar el historial\n" +
                    "Cancelar — seguir con el perfil actual",
                    "Cambio de perfil",
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

    /// <summary>
    /// Generación de imágenes efectivamente disponible (perfil + modo + edición).
    ///
    /// No repite la comprobación de compilación: <c>EffectiveProfile.Resolve</c> ya la
    /// aplica, y la regla de precedencia vive en un único lugar por diseño (1.2 del
    /// maestro). <see cref="IsImageGenCompiledIn"/> se usa para otra cosa: esconder la
    /// sección entera de la barra lateral en lugar de mostrarla deshabilitada.
    /// </summary>
    public bool IsImageGenAvailable => _effectiveProfile?.ImageGenEnabled ?? false;

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
        ? "El Modo Investigación tiene precedencia: se usa el prompt institucional y se fuerza el registro de auditoría."
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
                return "Esta compilación no incluye búsqueda web.";
            if (IsProfilePromptOverridden)
                return "Búsqueda web deshabilitada: el Modo Investigación no permite acceso a red.";
            if (!IsWebSearchAvailable)
                return $"El perfil «{ProfileName}» no habilita la búsqueda web.";
            return IsWebSearchOn
                ? "Búsqueda web activa: la consulta se enriquecerá con resultados frescos."
                : "Activar búsqueda web para esta consulta.";
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
        new FreshnessOption(1, "Último día"),
        new FreshnessOption(2, "Última semana"),
        new FreshnessOption(3, "Último mes"),
        new FreshnessOption(0, "Sin filtro")
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
                return $"Instancia configurada. Las consultas salen hacia {_settings.SearxngUrl}.";

            return string.IsNullOrWhiteSpace(_settings.SearxngUrl)
                ? "Sin instancia. Indicá la dirección de un SearXNG, por ejemplo " +
                  "http://127.0.0.1:8080. Ver la ayuda para levantar uno propio."
                : "La dirección no es válida. Debe ser una URL http o https, " +
                  "por ejemplo http://127.0.0.1:8080.";
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
        ? "SANDBOX — Sin acceso a evidencia" : "";

    /// <summary>Muestra el badge de sandbox (solo Consulta).</summary>
    public bool ShowModeBadge => CurrentMode == OperationMode.Consultation;

    /// <summary>Indica si estamos en modo investigación (para visibilidad de controles).</summary>
    public bool IsInvestigationMode => CurrentMode == OperationMode.Investigation;

    /// <summary>Indica si hay una sesión de investigación activa.</summary>
    public bool HasSession => CurrentSession != null;

    /// <summary>Texto con nombre y legajo del perito para el panel izquierdo.</summary>
    public string SessionPeritoText => CurrentSession != null
        ? $"Perito: {CurrentSession.PeritoName} | {CurrentSession.PeritoBadge}" : "";

    /// <summary>Texto con número de causa para el panel izquierdo.</summary>
    public string SessionCaseText => CurrentSession != null
        ? $"Causa: {CurrentSession.CaseNumber}" : "";

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
        OpenImageGenerationCommand = new RelayCommand(ExecuteOpenImageGeneration, () => IsImageGenAvailable);
        OpenImagePackagesCommand = new RelayCommand(ExecuteOpenImagePackages, () => IsImageGenAvailable);

        _orchestrator.ServerStatusChanged += (s, status) =>
            System.Windows.Application.Current.Dispatcher.Invoke(() => ServerStatus = status);
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
                    $"ℹ️ Se recortaron {count} mensajes anteriores para mantener el contexto dentro del límite.")));

        // Actualizar habilitación de ExportSessionCommand cuando cambian los mensajes
        Messages.CollectionChanged += (_, _) =>
            ((RelayCommand)ExportSessionCommand).RaiseCanExecuteChanged();

        ExecuteRefreshModels();
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
            });

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
#endif

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
        window.Closed += (_, _) => _imageGenWindow = null;

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
                lastMsg.GenerationMetrics = $"Pensando... {elapsed:F1}s";
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
                    $"⚠️ Búsqueda web: {context.ErrorMessage} Se responde sin fuentes web."));

                if (context.Sources.Count > 0)
                    assistantViewModel.WebSources = context.Sources;

                _orchestrator.Logger.LogCustom("WEB_SEARCH_FAILED", $"motivo=\"{context.ErrorMessage}\"");
                return null;
            }

            assistantViewModel.WebSources = context.Sources;

            _orchestrator.Logger.LogCustom("WEB_SEARCH",
                $"proveedor={provider.Name} leidas={context.PagesRead}/{context.PagesAttempted} " +
                $"frescura={freshness}");

            if (context.PagesRead < context.PagesAttempted)
            {
                Messages.Insert(Messages.Count - 1, new ChatMessageViewModel("system",
                    $"ℹ️ Se leyeron {context.PagesRead} de {context.PagesAttempted} fuentes. " +
                    "Las que fallaron figuran en el panel de fuentes."));
            }

            return context.EnrichedQuery;
        }
        catch (Exception ex)
        {
            Messages.Insert(Messages.Count - 1, new ChatMessageViewModel("system",
                $"⚠️ Búsqueda web interrumpida: {ex.Message}. Se responde sin fuentes web."));
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
                lastMessage.AppendContent($"\n\n[Error: {error}]");
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
        ContextAvailableText = $"Disponible: {Math.Max(0, usage.AvailableTokens - usage.TotalInputTokens)} / {usage.AvailableTokens}";
    }

    private bool CanExecuteStartServer()
    {
        return (ServerStatus == ServerStatus.Stopped || ServerStatus == ServerStatus.Error)
            && _selectedModelItem != null && _selectedModelItem.IsAvailable;
    }

    // Solo se puede detener si el servidor está activo en alguna fase
    private bool CanExecuteStopServer()
    {
        return ServerStatus == ServerStatus.Ready
            || ServerStatus == ServerStatus.Starting
            || ServerStatus == ServerStatus.LoadingModel;
    }

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

    private async Task ExecuteStopServerAsync()
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

    private async Task ExecuteLoadFilesAsync()
    {
        bool forensic = FileUploadIsForensic;

        // El diálogo no puede decir "contexto forense" fuera de una investigación:
        // nombrar evidencia a un archivo cualquiera confunde sobre qué se está haciendo.
        var files = _fileDialogService.OpenTextFiles(forensic
            ? "Seleccione archivos de contexto forense"
            : "Seleccione archivos para adjuntar al mensaje");

        if (files == null || files.Length == 0) return;

        foreach (var file in files)
        {
            try
            {
                var validation = _ingestionService.ValidateFile(file);
                if (!validation.IsAccepted)
                {
                    System.Windows.MessageBox.Show(
                        $"Archivo rechazado: {Path.GetFileName(file)}\n{validation.RejectionReason}",
                        "Ingesta rechazada", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                    continue;
                }

                string header = $"--- ARCHIVO: {Path.GetFileName(file)} [{validation.ContentType}] ---\n";
                string content = header + (validation.ProcessedContent ?? await File.ReadAllTextAsync(file));

                if (validation.WasTruncated)
                    content += $"\n[TRUNCADO: {validation.OriginalLength} → {validation.ProcessedLength} chars]";

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
                System.Windows.MessageBox.Show($"Error al cargar archivo {Path.GetFileName(file)}:\n{ex.Message}");
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
        var files = _fileDialogService.OpenImages("Seleccione imágenes para el análisis");
        if (files != null && files.Length > 0)
        {
            var batchValidation = _ingestionService.ValidateImageBatch(files.ToList());
            if (!batchValidation.IsAccepted)
            {
                System.Windows.MessageBox.Show(
                    batchValidation.RejectionReason,
                    "Ingesta rechazada", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                return;
            }

            foreach (var file in files)
            {
                var validation = _ingestionService.ValidateFile(file);
                if (!validation.IsAccepted)
                {
                    System.Windows.MessageBox.Show(
                        $"Imagen rechazada: {Path.GetFileName(file)}\n{validation.RejectionReason}",
                        "Ingesta rechazada", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
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
                    "Ingesta rechazada", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
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
                        $"Archivo rechazado: {Path.GetFileName(file)}\n{validation.RejectionReason}",
                        "Ingesta rechazada", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                    continue;
                }

                string header = $"--- ARCHIVO: {Path.GetFileName(file)} [{validation.ContentType}] ---\n";
                string content = header + (validation.ProcessedContent ?? await File.ReadAllTextAsync(file));

                if (validation.WasTruncated)
                    content += $"\n[TRUNCADO: {validation.OriginalLength} → {validation.ProcessedLength} chars]";

                _orchestrator.LoadForensicContext(content);

                var artifact = LoadedArtifactViewModel.FromFile(file);
                artifact.RemoveCommand = new RelayCommand(() => RemoveArtifact(artifact));
                LoadedArtifactsList.Add(artifact);
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Error al cargar {Path.GetFileName(file)}:\n{ex.Message}");
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

        // Seleccionar perfil de ejecución "balanced" por defecto
        var defaultProfile = AvailableExecutionProfiles.FirstOrDefault(p => p.Profile.Id == "balanced")
            ?? AvailableExecutionProfiles.FirstOrDefault();
        if (defaultProfile != null)
            SelectedExecutionProfile = defaultProfile;
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
        string filter = "Texto plano (*.txt)|*.txt|HTML (*.html)|*.html|JSON (*.json)|*.json";

        string? savePath = _fileDialogService.SaveFile("Exportar sesión", filter, defaultName);
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
            Messages.Add(new ChatMessageViewModel("system", $"✅ Sesión exportada: {savePath}\nHash SHA-256: {result.FileHash}"));
            _orchestrator.Logger.LogCustom("SESSION_EXPORTED", $"path=\"{savePath}\" format={format} hash={result.FileHash}");
        }
        else
        {
            Messages.Add(new ChatMessageViewModel("system", $"❌ Error al exportar: {result.ErrorMessage}"));
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
    public string AvailabilityText => IsAvailable ? "" : "(modelo no encontrado)";
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
