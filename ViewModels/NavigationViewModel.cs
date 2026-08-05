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

    // Sesión de investigación activa (null si no hay)
    private InvestigationSession? _currentSession;

    // Modo actual
    public OperationMode? CurrentMode { get; private set; }

    public NavigationViewModel(
        LlamaServerManager serverManager,
        LlamaChatClient chatClient,
        ForensicLogger logger,
        ConfigManager configManager,
        IFileDialogService fileDialogService,
        ModelProfileService profileService)
    {
        _serverManager = serverManager;
        _chatClient = chatClient;
        _logger = logger;
        _configManager = configManager;
        _fileDialogService = fileDialogService;
        _profileService = profileService;

        // Iniciar en pantalla de selección
        NavigateToModeSelection();
    }

    /// <summary>
    /// Navega a la pantalla de selección de modo.
    /// </summary>
    public void NavigateToModeSelection()
    {
        CurrentMode = null;
        _currentSession = null;

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
        orchestrator.SystemPrompt = SystemPrompts.GetInvestigationPrompt(session);

        var vm = new MainViewModel(orchestrator, _fileDialogService, _configManager, _profileService);
        vm.CurrentMode = OperationMode.Investigation;
        vm.CurrentSession = session;
        vm.GoBackCommand = new RelayCommand(() => NavigateToModeSelection());

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
        orchestrator.SystemPrompt = SystemPrompts.GetConsultationPrompt();

        _logger.LogCustom("SESSION_START", "mode=CONSULTATION");

        var vm = new MainViewModel(orchestrator, _fileDialogService, _configManager, _profileService);
        vm.CurrentMode = OperationMode.Consultation;
        vm.GoBackCommand = new RelayCommand(() => NavigateToModeSelection());

        var view = new Views.ChatView { DataContext = vm };
        CurrentView = view;
    }
}
