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

        _serverManager.StatusChanged += (s, e) => ServerStatusChanged?.Invoke(this, e.NewStatus);
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
    public event EventHandler<ServerStatus>? ServerStatusChanged;
    public event EventHandler<string>? DetectedBackendChanged;
    /// <summary>Se dispara cuando se recortan mensajes del historial por límite de contexto.</summary>
    public event EventHandler<int>? HistoryTrimmed;

    public event EventHandler<string>? MetricsCalculated;

    public string SystemPrompt { get; set; } = string.Empty;

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
        if (string.IsNullOrEmpty(SystemPrompt))
        {
            string promptFile = Path.Combine(promptsPath, "system_forense.txt");
            if (File.Exists(promptFile))
            {
                SystemPrompt = await File.ReadAllTextAsync(promptFile, ct);
            }
        }

        string modelFile = Path.Combine(modelsPath, _currentSettings.SelectedModel);
        if (string.IsNullOrEmpty(_currentSettings.SelectedModel))
            throw new InvalidOperationException("No se ha seleccionado ningún modelo .gguf.");

        // Detectar proyector multimodal (mmproj) para soporte de visión
        string? mmprojPath = null;
        if (!string.IsNullOrEmpty(_currentSettings.MmprojFileName))
        {
            mmprojPath = Path.Combine(modelsPath, _currentSettings.MmprojFileName);
        }
        else if (Directory.Exists(modelsPath))
        {
            // Auto-detectar si no está especificado
            mmprojPath = Directory.GetFiles(modelsPath, "*mmproj*").FirstOrDefault();
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
    public async Task<string> SendMessageAsync(string userMessage, List<string>? imagePaths = null, CancellationToken ct = default)
    {
        if (IsProcessing) throw new InvalidOperationException("Ya existe una operación de inferencia en curso.");
        if (!IsServerReady) throw new InvalidOperationException("El servidor no está disponible.");

        IsProcessing = true;
        _currentCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        
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

            var newMsg = new ChatMessage
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
                Temperature = _currentSettings.Temperature,
                MaxTokens = _currentSettings.MaxTokens,
                Stream = true,
                EnableThinking = _currentSettings.EnableThinking
            };

            var responseBuilder = new System.Text.StringBuilder(1024);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            
            DateTime generationStart = DateTime.Now;
            DateTime? firstTokenAt = null;
            int tokenCount = 0;

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
                _logger.LogCustom("EMPTY_RESPONSE", $"len={responseContent.Length} probable_cause=context_overflow");

                string warningMessage = "⚠️ El modelo no pudo generar respuesta. Posibles causas:\n"
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
        _logger.LogCustom("HISTORY_CLEARED", "Historial de sesión borrado");
    }

    /// <summary>
    /// Inyecta artefactos adicionales en el system prompt.
    /// </summary>
    public void LoadForensicContext(string contextJson)
    {
        // Se concatena al prompt del sistema base
        SystemPrompt += $"\n\n--- CONTEXTO FORENSE ADICIONAL ---\n{contextJson}";
        
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
