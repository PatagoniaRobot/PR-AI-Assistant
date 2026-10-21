using System;
using System.Windows.Input;
using PRImager.AIAssistant.Helpers;
using PRImager.AIAssistant.Services;
using PRImager.AIAssistant.Services.Models;

namespace PRImager.AIAssistant.ViewModels;

/// <summary>
/// Controla la navegación entre las vistas de PRAI.
/// Es el DataContext de MainWindow.
/// </summary>
public class NavigationViewModel : ObservableObject
{
    // Vista activa (se bindea al ContentControl de MainWindow)
    private object? _currentView;
    public object? CurrentView
    {
        get => _currentView;
        set => SetProperty(ref _currentView, value);
    }

    // Servicios compartidos (se crean una sola vez en MainWindow)
    private readonly LlamaServerManager _serverManager;
    private readonly LlamaChatClient _chatClient;
    private readonly ForensicLogger _logger;
    private readonly ConfigManager _configManager;
    private readonly IFileDialogService _fileDialogService;
    private readonly ModelProfileService _profileService;
    private readonly UsageProfileService _usageProfileService;

    // Sesión de investigación activa (null si no hay)
    private InvestigationSession? _currentSession;

    // Modo actual
    public OperationMode? CurrentMode { get; private set; }

    private string _windowTitle = "PR-AI Assistant";

    /// <summary>
    /// Título de la ventana. Incluye el perfil activo entre corchetes: es parte del
    /// control de identificación visual exigido por E1.
    /// </summary>
    public string WindowTitle
    {
        get => _windowTitle;
        private set => SetProperty(ref _windowTitle, value);
    }

    private string _baseWindowTitle = "PR-AI Assistant";
    private UsageProfile? _titleProfile;

    /// <summary>Texto base del título, sin el perfil. Lo fija MainWindow con la versión.</summary>
    public string BaseWindowTitle
    {
        get => _baseWindowTitle;
        set
        {
            _baseWindowTitle = value;
            UpdateWindowTitle(_titleProfile);
        }
    }

    public NavigationViewModel(
        LlamaServerManager serverManager,
        LlamaChatClient chatClient,
        ForensicLogger logger,
        ConfigManager configManager,
        IFileDialogService fileDialogService,
        ModelProfileService profileService,
        UsageProfileService usageProfileService)
    {
        _serverManager = serverManager;
        _chatClient = chatClient;
        _logger = logger;
        _configManager = configManager;
        _fileDialogService = fileDialogService;
        _profileService = profileService;
        _usageProfileService = usageProfileService;

        // Iniciar en pantalla de selección
        NavigateToModeSelection();
    }

    /// <summary>Refleja el perfil activo en el título de la ventana.</summary>
    private void UpdateWindowTitle(UsageProfile? profile)
    {
        _titleProfile = profile;
        WindowTitle = profile == null
            ? _baseWindowTitle
            : $"{_baseWindowTitle} — [{profile.Name}]";
    }

    /// <summary>
    /// Navega a la pantalla de selección de modo.
    /// </summary>
    public void NavigateToModeSelection()
    {
        CurrentMode = null;
        _currentSession = null;
        UpdateWindowTitle(null);

#if IMAGE_GEN
        // Volver al inicio cierra la ventana de imágenes, y con ella su motor. Cada entrada
        // al chat crea su propio MainViewModel: una ventana que sobreviviera quedaría suelta,
        // sin que «Detener» ni «Apagar» la vieran, y «Generar imágenes» abriría una segunda
        // (visto el 21 de octubre de 2026).
        foreach (var w in System.Windows.Application.Current.Windows
                     .OfType<Views.ImageGen.ImageGenerationWindow>().ToList())
            w.Close();
#endif

        var vm = new ModeSelectionViewModel(_configManager, _profileService);
        vm.ModeSelected += OnModeSelected;

        var view = new Views.ModeSelectionView { DataContext = vm };
        CurrentView = view;
    }

    /// <summary>
    /// Maneja la selección de modo y redirige a la vista correspondiente.
    /// </summary>
    private void OnModeSelected(object? sender, OperationMode mode)
    {
        CurrentMode = mode;
        switch (mode)
        {
            case OperationMode.Investigation:
                NavigateToInvestigationForm();
                break;
            case OperationMode.Consultation:
                NavigateToConsultationChat();
                break;
        }
    }

    /// <summary>
    /// Muestra el formulario obligatorio de identificación del perito.
    /// </summary>
    private void NavigateToInvestigationForm()
    {
        var vm = new InvestigationFormViewModel(_logger);
        vm.NavigateBack += (_, _) => NavigateToModeSelection();
        vm.SessionCreated += OnSessionCreated;

        var view = new Views.InvestigationFormView { DataContext = vm };
        CurrentView = view;
    }

    /// <summary>
    /// Callback cuando el perito completa el formulario y acepta el disclaimer.
    /// </summary>
    private void OnSessionCreated(object? sender, InvestigationSession session)
    {
        _currentSession = session;
        NavigateToInvestigationChat(session);
    }

    /// <summary>
    /// Crea el chat en Modo Investigación con system prompt forense.
    /// </summary>
    private void NavigateToInvestigationChat(InvestigationSession session)
    {
        // Crear ChatOrchestrator con system prompt de investigación
        var orchestrator = new ChatOrchestrator(_serverManager, _chatClient, _logger, _configManager);
        string institutionalPrompt = SystemPrompts.GetInvestigationPrompt(session);
        orchestrator.BaseSystemPrompt = institutionalPrompt;

        var vm = new MainViewModel(orchestrator, _fileDialogService, _configManager, _profileService, _usageProfileService);
        vm.CurrentMode = OperationMode.Investigation;
        vm.CurrentSession = session;
        vm.InstitutionalSystemPrompt = institutionalPrompt;
        vm.GoBackCommand = new RelayCommand(() => NavigateToModeSelection());
        vm.UsageProfileApplied += (_, profile) => UpdateWindowTitle(profile);

        // El perfil se resuelve contra el modo, así que va después de fijarlo
        vm.InitializeUsageProfile();

        var view = new Views.ChatView { DataContext = vm };
        CurrentView = view;
    }

    /// <summary>
    /// Crea el chat en Modo Consulta (sandbox académico).
    /// </summary>
    private void NavigateToConsultationChat()
    {
        // Crear ChatOrchestrator con system prompt de consulta
        var orchestrator = new ChatOrchestrator(_serverManager, _chatClient, _logger, _configManager);
        string institutionalPrompt = SystemPrompts.GetConsultationPrompt();
        orchestrator.BaseSystemPrompt = institutionalPrompt;

        _logger.LogCustom("SESSION_START", "mode=CONSULTATION");

        var vm = new MainViewModel(orchestrator, _fileDialogService, _configManager, _profileService, _usageProfileService);
        vm.CurrentMode = OperationMode.Consultation;
        vm.InstitutionalSystemPrompt = institutionalPrompt;
        vm.GoBackCommand = new RelayCommand(() => NavigateToModeSelection());
        vm.UsageProfileApplied += (_, profile) => UpdateWindowTitle(profile);

        // En consulta el perfil manda: su prompt reemplaza al institucional
        vm.InitializeUsageProfile();

        var view = new Views.ChatView { DataContext = vm };
        CurrentView = view;
    }
}
