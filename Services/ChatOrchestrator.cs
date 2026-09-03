using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PRImager.AIAssistant.Services.Enums;
using PRImager.AIAssistant.Services.Models;

namespace PRImager.AIAssistant.Services;

/// <summary>
/// Orquesta la interacción entre el ViewModel y los múltiples servicios existentes (ServerManager, ChatClient, Logger).
/// Es el bloque principal de lógica del chat forensic.
/// </summary>
public class ChatOrchestrator : IDisposable
{
    private readonly LlamaServerManager _serverManager;
    private readonly LlamaChatClient _chatClient;
    private readonly ForensicLogger _logger;
    public ForensicLogger Logger => _logger;
    private readonly ConfigManager _configManager;
    private readonly ContextWindowManager _contextWindowManager;

    private readonly List<ChatMessage> _sessionHistory;
    private AppSettings _currentSettings;

    private CancellationTokenSource? _currentCts;
    private bool _isProcessing;

    public ChatOrchestrator(
        LlamaServerManager serverManager,
        LlamaChatClient chatClient,
        ForensicLogger logger,
        ConfigManager configManager)
    {
        _serverManager = serverManager;
        _chatClient = chatClient;
        _logger = logger;
        _configManager = configManager;
        
        _sessionHistory = new List<ChatMessage>();
        _currentSettings = _configManager.Load();
        _contextWindowManager = new ContextWindowManager
        {
            MaxContextTokens = _currentSettings.CtxSize,
            ReservedResponseTokens = _currentSettings.MaxTokens
        };

        _serverManager.StatusChanged += (s, e) => ServerStatusChanged?.Invoke(this, e);
        _serverManager.DetectedBackendChanged += (s, backend) => DetectedBackendChanged?.Invoke(this, backend);
        _chatClient.TokenReceived += (s, e) => StreamingTokenReceived?.Invoke(this, e.Token);
    }

    public bool IsServerReady => _serverManager.IsReady;
    public ServerStatus CurrentServerStatus => _serverManager.CurrentStatus;
    
    public bool IsProcessing
    {
        get => _isProcessing;
        private set => _isProcessing = value;
    }

    public string CurrentModelName => _currentSettings.SelectedModel;
    public string DetectedBackend => _serverManager.DetectedBackend;

    public event EventHandler<string>? StreamingTokenReceived;
    public event EventHandler<string>? ResponseCompleted;
    public event EventHandler<string>? ErrorOccurred;
    /// <summary>
    /// Cambio de estado del servidor, con el motivo cuando el estado es Error.
    /// Antes solo viajaba el enum y la causa que informaba llama-server se perdía.
    /// </summary>
    public event EventHandler<ServerStatusChangedEventArgs>? ServerStatusChanged;
    public event EventHandler<string>? DetectedBackendChanged;
    /// <summary>Se dispara cuando se recortan mensajes del historial por límite de contexto.</summary>
    public event EventHandler<int>? HistoryTrimmed;

    public event EventHandler<string>? MetricsCalculated;

    /// <summary>
    /// Progreso del razonamiento, para que la pantalla no quede muda mientras el
    /// modelo piensa. No es contenido de la respuesta.
    /// </summary>
    public event EventHandler<string>? ReasoningProgress;

    private string _baseSystemPrompt = string.Empty;
    private readonly List<string> _additionalContext = new();

    /// <summary>
    /// Prompt de sistema base (institucional o del perfil de uso).
    /// Se puede reemplazar en caliente al cambiar de perfil sin perder el contexto
    /// forense ya cargado, que vive aparte en <see cref="_additionalContext"/>.
    /// </summary>
    public string BaseSystemPrompt
    {
        get => _baseSystemPrompt;
        set => _baseSystemPrompt = value ?? string.Empty;
    }

    /// <summary>
    /// Prompt efectivo enviado al modelo: base + artefactos cargados en la sesión.
    /// </summary>
    public string SystemPrompt =>
        _additionalContext.Count == 0
            ? _baseSystemPrompt
            : _baseSystemPrompt + string.Concat(_additionalContext);

    /// <summary>
    /// Temperatura impuesta por el perfil de uso activo. Null = usar la de AppSettings.
    /// Se aplica por request, así que un cambio de perfil rige desde la inferencia siguiente
    /// sin reiniciar llama-server.
    /// </summary>
    public double? TemperatureOverride { get; set; }

    /// <summary>
    /// Configura el sistema, carga el prompt base e inicia el servidor llama-server.
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        _currentSettings = _configManager.Load();
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;

        string promptsPath = ResolvePath(baseDir, _currentSettings.PromptsPath);
        string modelsPath = ResolvePath(baseDir, _currentSettings.ModelPath);
        string serverExePath = ResolvePath(baseDir, _currentSettings.LlamaServerExecutable);

        // Cargar prompt base desde archivo solo si no se asignó uno programáticamente
        if (string.IsNullOrEmpty(_baseSystemPrompt))
        {
            string promptFile = Path.Combine(promptsPath, "system_forense.txt");
            if (File.Exists(promptFile))
            {
                BaseSystemPrompt = await File.ReadAllTextAsync(promptFile, ct);
            }
        }

        if (string.IsNullOrEmpty(_currentSettings.SelectedModel))
            throw new InvalidOperationException("No se ha seleccionado ningún modelo .gguf.");

        // Los modelos descargados se guardan fuera de Models/ y se persisten con ruta
        // completa: si ya viene absoluta, se respeta.
        string modelFile = ResolvePath(modelsPath, _currentSettings.SelectedModel);

        // Detectar proyector multimodal (mmproj) para soporte de visión.
        // Se busca junto al modelo, no en Models/: de lo contrario un modelo descargado
        // sin visión se llevaría el proyector de Gemma y el servidor arrancaría mal.
        string modelDirectory = Path.GetDirectoryName(modelFile) ?? modelsPath;

        string? mmprojPath = null;
        if (!string.IsNullOrEmpty(_currentSettings.MmprojFileName))
        {
            mmprojPath = Path.Combine(modelDirectory, _currentSettings.MmprojFileName);
        }
        else if (Directory.Exists(modelDirectory))
        {
            // Auto-detectar si no está especificado
            mmprojPath = Directory.GetFiles(modelDirectory, "*mmproj*").FirstOrDefault();
        }

        var config = new LlamaServerConfig
        {
            ServerExecutablePath = serverExePath,
            ModelPath = modelFile,
            MmprojPath = mmprojPath,
            Host = _currentSettings.Host,
            Port = _currentSettings.Port,
            CtxSize = _currentSettings.CtxSize,
            Threads = _currentSettings.Threads,
            GpuLayers = _currentSettings.GpuLayers
        };

        try
        {
            await _serverManager.StartAsync(config, ct);
            _logger.LogModelLoaded(modelFile);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex.Message);
            ErrorOccurred?.Invoke(this, ex.Message);
            throw;
        }
    }

    /// <summary>
    /// Envía un mensaje a la IA y sincroniza todo en el archivo de auditoría Forense.
    /// Retorna la respuesta generada.
    /// </summary>
    /// <param name="historyContent">
    /// Texto a guardar en el historial en lugar de <paramref name="userMessage"/>.
    /// Lo usa la búsqueda web: el modelo recibe la consulta enriquecida con las fuentes,
    /// pero el historial conserva la pregunta original para no arrastrar miles de tokens
    /// de páginas web a los turnos siguientes.
    /// </param>
    public async Task<string> SendMessageAsync(string userMessage, List<string>? imagePaths = null, string? historyContent = null, CancellationToken ct = default)
    {
        if (IsProcessing) throw new InvalidOperationException("Ya existe una operación de inferencia en curso.");
        if (!IsServerReady) throw new InvalidOperationException("El servidor no está disponible.");

        IsProcessing = true;
        _currentCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // Declarado fuera del try para poder restaurarlo en finally
        ChatMessage? newMsg = null;

        try
        {
            _logger.LogUserQuery(userMessage);

            var images = new List<ChatImage>();
            if (imagePaths != null)
            {
                foreach (var path in imagePaths)
                {
                    if (File.Exists(path))
                    {
                        var bytes = await File.ReadAllBytesAsync(path, _currentCts.Token);
                        string base64 = Convert.ToBase64String(bytes);
                        string ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
                        string mime = ext switch
                        {
                            "png" => "image/png",
                            "jpg" or "jpeg" => "image/jpeg",
                            "gif" => "image/gif",
                            "webp" => "image/webp",
                            _ => "image/jpeg" // Fallback seguro
                        };
                        images.Add(new ChatImage { Base64Data = base64, MediaType = mime });
                    }
                }
            }

            newMsg = new ChatMessage
            {
                Role = "user",
                Content = userMessage,
                Images = images.Any() ? images : null
            };

            _sessionHistory.Add(newMsg);

            // Actualizar gestor de contexto con la configuración actual
            _contextWindowManager.MaxContextTokens = _currentSettings.CtxSize;
            _contextWindowManager.ReservedResponseTokens = _currentSettings.MaxTokens;

            // Recortar historial si excede la ventana de contexto
            var trimmedHistory = _contextWindowManager.TrimHistory(
                SystemPrompt, null, _sessionHistory);

            int trimmedCount = _sessionHistory.Count - trimmedHistory.Count;
            if (trimmedCount > 0)
            {
                _logger.LogCustom("HISTORY_TRIMMED", $"removed={trimmedCount} remaining={trimmedHistory.Count}");
                HistoryTrimmed?.Invoke(this, trimmedCount);
            }

            var request = new ChatRequest
            {
                SystemPrompt = SystemPrompt,
                Messages = trimmedHistory,
                Temperature = (float)(TemperatureOverride ?? _currentSettings.Temperature),
                MaxTokens = _currentSettings.MaxTokens,
                Stream = true,
                EnableThinking = _currentSettings.EnableThinking
            };

            var responseBuilder = new System.Text.StringBuilder(1024);
            var watch = System.Diagnostics.Stopwatch.StartNew();

            DateTime generationStart = DateTime.Now;
            DateTime? firstTokenAt = null;
            int tokenCount = 0;
            int reasoningTokens = 0;

            // Con razonamiento activo el modelo puede pensar miles de tokens antes de
            // emitir la primera palabra de la respuesta. Se informa el avance para que
            // la espera se distinga de un cuelgue.
            void OnReasoning(object? _, TokenReceivedEventArgs e)
            {
                reasoningTokens++;
                if (reasoningTokens % 10 == 0)
                {
                    var elapsed = (DateTime.Now - generationStart).TotalSeconds;
                    ReasoningProgress?.Invoke(this,
                        $"Razonando… {reasoningTokens} tokens · {elapsed:F0}s");
                }
            }

            _chatClient.ReasoningTokenReceived += OnReasoning;
            try
            {
            await foreach (var token in _chatClient.SendMessageStreamAsync(request, _currentCts.Token))
            {
                if (firstTokenAt == null) firstTokenAt = DateTime.Now;
                tokenCount++;
                responseBuilder.Append(token);

                if (tokenCount % 5 == 0)
                {
                    DateTime now = DateTime.Now;
                    var totalTime = now - generationStart;
                    var genTime = (now - firstTokenAt.Value).TotalSeconds;
                    var tps = genTime > 0 ? tokenCount / genTime : 0;
                    string metricsStr = $"{tokenCount} tokens · {totalTime.TotalSeconds:F1}s · {tps:F1} tok/s";
                    MetricsCalculated?.Invoke(this, metricsStr);
                }
            }
            }
            finally
            {
                _chatClient.ReasoningTokenReceived -= OnReasoning;
            }

            watch.Stop();
            DateTime generationEnd = DateTime.Now;
            string responseContent = responseBuilder.ToString();

            if (firstTokenAt != null)
            {
                var totalTime = generationEnd - generationStart;
                var ttft = firstTokenAt.Value - generationStart;
                var genTime = (generationEnd - firstTokenAt.Value).TotalSeconds;
                var tps = genTime > 0 ? tokenCount / genTime : 0;
                
                string metricsStr = $"{tokenCount} tokens · {totalTime.TotalSeconds:F1}s · {tps:F1} tok/s";
                MetricsCalculated?.Invoke(this, metricsStr);
            }

            // Detectar respuesta vacía o extremadamente corta
            if (string.IsNullOrWhiteSpace(responseContent) || responseContent.Trim().Length < 5)
            {
                // Un modelo de razonamiento que consumió todo el presupuesto pensando
                // devuelve contenido vacío. Culpar al contexto mandaba a limpiar el
                // historial, que no arregla nada.
                bool exhaustedThinking = reasoningTokens > 0;

                _logger.LogCustom("EMPTY_RESPONSE",
                    $"len={responseContent.Length} tokens_razonamiento={reasoningTokens} " +
                    $"probable_cause={(exhaustedThinking ? "reasoning_budget_exhausted" : "context_overflow")}");

                string warningMessage = exhaustedThinking
                    ? $"⚠️ El modelo gastó los {reasoningTokens} tokens disponibles razonando y no "
                      + "llegó a responder.\n"
                      + "• Desactivá «Thinking Mode» en el panel lateral (☰ → PARÁMETROS), o\n"
                      + "• subí «Máx. respuesta» para darle presupuesto suficiente."
                    : "⚠️ El modelo no pudo generar respuesta. Posibles causas:\n"
                      + "• El contexto excedió el límite. Intente con 'Nueva Sesión'.\n"
                      + "• La consulta fue demasiado compleja. Intente reformularla.";

                // No agregar respuesta vacía al historial
                ResponseCompleted?.Invoke(this, warningMessage);
                return string.Empty;
            }

            // El disclaimer se muestra en el XAML vía ChatMessageViewModel.DisclaimerText,
            // no se concatena al contenido para mantenerlo limpio para exportación.
            _sessionHistory.Add(new ChatMessage { Role = "assistant", Content = responseContent });
            _logger.LogAIResponse(responseContent, watch.ElapsedMilliseconds);

            ResponseCompleted?.Invoke(this, responseContent);
            return responseContent;
        }
        catch (OperationCanceledException)
        {
            _logger.LogCustom("INFERENCE_CANCELLED", "Inferencia cancelada por el operario/perito.");
            return string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex.Message);
            ErrorOccurred?.Invoke(this, ex.Message);
            throw;
        }
        finally
        {
            // El request ya se envió con el texto enriquecido: el historial se queda
            // con la pregunta original. En finally para que valga también si hubo
            // error o cancelación. ChatMessage es inmutable, así que se reemplaza.
            if (historyContent != null && newMsg != null)
            {
                int idx = _sessionHistory.IndexOf(newMsg);
                if (idx >= 0)
                {
                    _sessionHistory[idx] = new ChatMessage
                    {
                        Role = newMsg.Role,
                        Content = historyContent,
                        Images = newMsg.Images
                    };
                }
            }

            _currentCts?.Dispose();
            _currentCts = null;
            IsProcessing = false;
        }
    }

    /// <summary>
    /// Cancela una petición asíncrona en curso.
    /// </summary>
    public void CancelCurrentRequest()
    {
        _currentCts?.Cancel();
    }

    /// <summary>
    /// Limpia el historial actual de inferencia.
    /// </summary>
    public void ClearHistory()
    {
        _sessionHistory.Clear();
        // El contexto forense se descarta junto con el historial: la UI ya vacía la lista
        // de artefactos, y dejarlo en el prompt filtraría evidencia a la sesión siguiente.
        _additionalContext.Clear();
        _logger.LogCustom("HISTORY_CLEARED", "Historial de sesión y contexto forense borrados");
    }

    /// <summary>
    /// Inyecta artefactos adicionales en el system prompt.
    /// </summary>
    public void LoadForensicContext(string contextJson)
    {
        // Se acumula aparte del prompt base para sobrevivir a un cambio de perfil
        _additionalContext.Add($"\n\n--- CONTEXTO FORENSE ADICIONAL ---\n{contextJson}");

        int estimatedLength = contextJson.Length / 4;
        _logger.LogContextLoaded(1, estimatedLength);
    }

    /// <summary>
    /// Obtiene una copia del historial actual.
    /// </summary>
    public List<ChatMessage> GetSessionHistory() => _sessionHistory.ToList();

    /// <summary>
    /// Operación adicional para detener explícitamente el servidor desde Orchestrator.
    /// </summary>
    public async Task StopServerAsync()
    {
        await _serverManager.StopAsync();
    }

    public void Dispose()
    {
        _currentCts?.Dispose();
        GC.SuppressFinalize(this);
    }

    private static string ResolvePath(string baseDir, string path)
    {
        return Path.IsPathRooted(path) ? path : Path.Combine(baseDir, path);
    }
}
