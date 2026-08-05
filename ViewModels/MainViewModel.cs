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
    private readonly ContextIngestionService _ingestionService;
    private readonly ContextWindowManager _contextWindowManager;
    private AppSettings _settings;
    private System.Windows.Threading.DispatcherTimer? _thinkingTimer;
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

    public ObservableCollection<ChatMessageViewModel> Messages { get; }
    public ObservableCollection<string> AvailableModels { get; }
    public ObservableCollection<LoadedArtifactViewModel> LoadedArtifactsList { get; }

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
                _settings.SelectedModel = value.Model.ModelFileName;
                _settings.MmprojFileName = value.Model.MmprojFileName;
                _configManager.Save(_settings);

                OnPropertyChanged(nameof(SelectedModelHardwareTag));
                OnPropertyChanged(nameof(SelectedModelCapabilitiesText));
                OnPropertyChanged(nameof(SelectedModelContextInfo));
                OnPropertyChanged(nameof(IsSelectedModelAvailable));
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

    public double Temperature
    {
        get => _settings.Temperature;
        set
        {
            if (Math.Abs(_settings.Temperature - value) > 0.01)
            {
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

    /// <summary>Muestra el panel derecho de contexto forense (solo Investigación).</summary>
    public bool ShowContextPanel => CurrentMode == OperationMode.Investigation;

    /// <summary>Muestra los botones de carga de archivos e imágenes (solo Investigación).</summary>
    public bool ShowFileButtons => CurrentMode == OperationMode.Investigation;

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

    public ICommand ToggleSidebarCommand { get; }

    public MainViewModel(ChatOrchestrator orchestrator, IFileDialogService fileDialogService, ConfigManager configManager, ModelProfileService profileService)
    {
        _orchestrator = orchestrator;
        _fileDialogService = fileDialogService;
        _configManager = configManager;
        _profileService = profileService;
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

        // Seleccionar opción de MaxTokens según configuración guardada
        _selectedMaxTokens = MaxTokensOptions.FirstOrDefault(o => o.Value == _settings.MaxTokens)
            ?? MaxTokensOptions[2]; // Default: 8192

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

        _orchestrator.ServerStatusChanged += (s, status) =>
            System.Windows.Application.Current.Dispatcher.Invoke(() => ServerStatus = status);
        _orchestrator.StreamingTokenReceived += OnStreamingTokenReceived;
        _orchestrator.ResponseCompleted += OnResponseCompleted;
        _orchestrator.MetricsCalculated += OnMetricsCalculated;
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

        var userViewModel = new ChatMessageViewModel("user", message, imagesToSend.Any() ? imagesToSend : null);
        Messages.Add(userViewModel);

        var assistantViewModel = new ChatMessageViewModel("assistant", "") { IsStreaming = true };
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

        try
        {
            await _orchestrator.SendMessageAsync(message, imagesToSend.Any() ? imagesToSend : null);
        }
        catch
        {
            // Logging occurs at the orchestrator layer, handled.
        }
    }

    // Despachar actualizaciones de streaming al hilo de UI desde hilos de background
    private void OnStreamingTokenReceived(object? sender, string token)
    {
        System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var lastMessage = Messages.LastOrDefault();
            if (lastMessage != null && lastMessage.IsAssistant && lastMessage.IsStreaming)
            {
                lastMessage.Content += token;
            }
        });
    }

    private void OnResponseCompleted(object? sender, string finalResponse)
    {
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            var lastMessage = Messages.LastOrDefault();
            if (lastMessage != null && lastMessage.IsAssistant && lastMessage.IsStreaming)
            {
                lastMessage.IsStreaming = false;
                lastMessage.Content = finalResponse;
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

    private void OnErrorOccurred(object? sender, string error)
    {
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            var lastMessage = Messages.LastOrDefault();
            if (lastMessage != null && lastMessage.IsAssistant && lastMessage.IsStreaming)
            {
                lastMessage.IsStreaming = false;
                lastMessage.Content += $"\n\n[Error: {error}]";
            }
            IsProcessing = false;
        });
    }

    private bool CanExecuteCancel() => IsProcessing;

    private void ExecuteCancel()
    {
        _orchestrator.CancelCurrentRequest();
        IsProcessing = false;
    }

    private void ExecuteClearHistory()
    {
        _orchestrator.ClearHistory();
        Messages.Clear();
        LoadedArtifactsList.Clear();
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
        var files = _fileDialogService.OpenTextFiles("Seleccione archivos de contexto forense");
        if (files != null && files.Length > 0)
        {
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

                    _orchestrator.LoadForensicContext(content);

                    var artifact = LoadedArtifactViewModel.FromFile(file);
                    artifact.RemoveCommand = new RelayCommand(() => RemoveArtifact(artifact));
                    LoadedArtifactsList.Add(artifact);
                }
                catch (Exception ex)
                {
                    System.Windows.MessageBox.Show($"Error al cargar archivo {Path.GetFileName(file)}:\n{ex.Message}");
                }
            }
            LoadedArtifacts = LoadedArtifactsList.Count + _pendingImages.Count;
            UpdateContextUsage();
        }
    }

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
                var artifact = LoadedArtifactViewModel.FromFile(file);
                artifact.RemoveCommand = new RelayCommand(() =>
                {
                    _pendingImages.Remove(file);
                    RemoveArtifact(artifact);
                });
                LoadedArtifactsList.Add(artifact);
            }
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

        // Cargar modelos
        var models = _profileService.GetAvailableModels();
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
            m.IsAvailable && m.Model.ModelFileName == _settings.SelectedModel);

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
