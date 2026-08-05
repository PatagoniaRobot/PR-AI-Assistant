using System.Diagnostics;
using System.IO;
using System.Net.Http;
using PRImager.AIAssistant.Helpers;
using PRImager.AIAssistant.Services.Enums;
using PRImager.AIAssistant.Services.Models;

namespace PRImager.AIAssistant.Services;

/// <summary>
/// Gestiona el ciclo de vida de llama-server.exe como proceso hijo.
/// Incluye health check periódico, reinicio automático y cierre limpio.
/// </summary>
public class LlamaServerManager : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly object _lock = new();

    private Process? _serverProcess;
    private CancellationTokenSource? _healthCheckCts;
    private LlamaServerConfig? _currentConfig;
    private ServerStatus _currentStatus = ServerStatus.Stopped;
    private bool _disposed;
    private int _consecutiveFailures;
    private bool _modelLoadedOnce;
    private JobObject? _jobObject;
    private string _detectedBackend = "Detectando...";

    // --- Eventos ---

    /// <summary>Notifica cambios de estado del servidor.</summary>
    public event EventHandler<ServerStatusChangedEventArgs>? StatusChanged;

    /// <summary>Cada línea de stdout/stderr del proceso llama-server.</summary>
    public event EventHandler<string>? LogReceived;

    /// <summary>Notifica que se ha detectado el backend (GPU/CPU).</summary>
    public event EventHandler<string>? DetectedBackendChanged;

    // --- Propiedades ---

    public ServerStatus CurrentStatus
    {
        get { lock (_lock) return _currentStatus; }
        private set
        {
            ServerStatus old;
            lock (_lock)
            {
                old = _currentStatus;
                if (old == value) return;
                _currentStatus = value;
            }
            StatusChanged?.Invoke(this, new ServerStatusChangedEventArgs(old, value));
        }
    }

    public bool IsReady => CurrentStatus == ServerStatus.Ready;

    public string DetectedBackend
    {
        get => _detectedBackend;
        private set
        {
            if (_detectedBackend != value)
            {
                _detectedBackend = value;
                DetectedBackendChanged?.Invoke(this, value);
            }
        }
    }

    // --- Constructor ---

    public LlamaServerManager()
    {
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
    }

    // --- Métodos públicos ---

    /// <summary>
    /// Inicia llama-server.exe con la configuración proporcionada.
    /// Valida existencia del ejecutable y del modelo antes de iniciar.
    /// </summary>
    public async Task StartAsync(LlamaServerConfig config, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Validar que el ejecutable existe
        Debug.WriteLine($"LlamaServerManager: Buscando ejecutable en: {config.ServerExecutablePath}");
        if (!File.Exists(config.ServerExecutablePath))
        {
            Debug.WriteLine("LlamaServerManager: Ejecutable encontrado: NO");
            throw new InvalidOperationException(
                $"No se encontró llama-server.exe en: {config.ServerExecutablePath}");
        }
        Debug.WriteLine("LlamaServerManager: Ejecutable encontrado: SÍ");

        // Validar que el modelo existe
        Debug.WriteLine($"LlamaServerManager: Buscando modelo en: {config.ModelPath}");
        if (!File.Exists(config.ModelPath))
        {
            Debug.WriteLine("LlamaServerManager: Modelo encontrado: NO");
            throw new InvalidOperationException(
                $"No se encontró el archivo de modelo .gguf en: {config.ModelPath}");
        }
        Debug.WriteLine("LlamaServerManager: Modelo encontrado: SÍ");

        // Verificar si ya hay un proceso escuchando en el puerto
        if (await IsPortInUseAsync(config.Host, config.Port, ct))
        {
            OnLog($"Ya hay un servicio escuchando en {config.Host}:{config.Port}. Se usará la instancia existente.");
            _currentConfig = config;
            _modelLoadedOnce = true;
            CurrentStatus = ServerStatus.Ready;
            StartHealthCheck();
            return;
        }

        // Detener proceso anterior si existe
        await StopInternalAsync(ct);

        _currentConfig = config;
        _consecutiveFailures = 0;
        _modelLoadedOnce = false;
        DetectedBackend = "Detectando...";
        CurrentStatus = ServerStatus.Starting;

        LaunchProcess(config);
        StartHealthCheck();
    }

    /// <summary>
    /// Detiene el servidor limpiamente. Espera 5 segundos antes de forzar el kill.
    /// </summary>
    public async Task StopAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await StopInternalAsync(ct);
        CurrentStatus = ServerStatus.Stopped;
    }

    /// <summary>
    /// Espera hasta que el servidor esté en estado Ready o se agote el timeout.
    /// </summary>
    public async Task<bool> WaitForReadyAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try
        {
            while (!linkedCts.Token.IsCancellationRequested)
            {
                if (CurrentStatus == ServerStatus.Ready)
                    return true;

                if (CurrentStatus == ServerStatus.Error || CurrentStatus == ServerStatus.Stopped)
                    return false;

                await Task.Delay(250, linkedCts.Token);
            }
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            // Timeout alcanzado
        }

        return CurrentStatus == ServerStatus.Ready;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _healthCheckCts?.Cancel();
        _healthCheckCts?.Dispose();
        _healthCheckCts = null;

        KillProcess();

        _jobObject?.Dispose();
        _jobObject = null;

        _httpClient.Dispose();
        GC.SuppressFinalize(this);
    }

    // --- Métodos privados ---

    /// <summary>
    /// Lanza el proceso llama-server con la configuración indicada.
    /// </summary>
    private void LaunchProcess(LlamaServerConfig config)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = config.ServerExecutablePath,
            Arguments = config.ToCommandLineArgs(),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        OnLog($"Iniciando: {startInfo.FileName} {startInfo.Arguments}");

        _serverProcess = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

        _serverProcess.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                ParseBackendInfo(e.Data);
                OnLog(e.Data);
            }
        };

        _serverProcess.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                ParseBackendInfo(e.Data);
                OnLog($"[stderr] {e.Data}");
            }
        };

        _serverProcess.Exited += (_, _) =>
        {
            OnLog("El proceso llama-server terminó inesperadamente.");
            if (!_disposed && CurrentStatus != ServerStatus.Stopped)
                CurrentStatus = ServerStatus.Error;
        };

        try
        {
            Debug.WriteLine($"LlamaServerManager: Iniciando proceso con args: {startInfo.Arguments}");
            _serverProcess.Start();

            // Gestionar proceso huérfano
            _jobObject = new JobObject();
            _jobObject.AssignProcess(_serverProcess);

            _serverProcess.BeginOutputReadLine();
            _serverProcess.BeginErrorReadLine();
            CurrentStatus = ServerStatus.LoadingModel;
            Debug.WriteLine($"LlamaServerManager: Proceso iniciado PID: {_serverProcess.Id}");
            OnLog($"Proceso iniciado con PID {_serverProcess.Id}.");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"LlamaServerManager: ERROR: {ex.Message}");
            OnLog($"Error al iniciar el proceso: {ex.Message}");
            CurrentStatus = ServerStatus.Error;
        }
    }

    /// <summary>
    /// Inicia el bucle de health check en background.
    /// </summary>
    private void StartHealthCheck()
    {
        _healthCheckCts?.Cancel();
        _healthCheckCts?.Dispose();
        _healthCheckCts = new CancellationTokenSource();

        var token = _healthCheckCts.Token;

        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                await PerformHealthCheckAsync(token);
            }
        }, token);
    }

    /// <summary>
    /// Ejecuta un health check contra el endpoint /health del servidor.
    /// Si falla 3 veces consecutivas, intenta reiniciar.
    /// </summary>
    private async Task PerformHealthCheckAsync(CancellationToken ct)
    {
        if (_currentConfig is null) return;

        var url = $"http://{_currentConfig.Host}:{_currentConfig.Port}/health";

        try
        {
            var response = await _httpClient.GetAsync(url, ct);

            if (response.IsSuccessStatusCode)
            {
                _consecutiveFailures = 0;

                // Primer health check exitoso indica que el modelo terminó de cargar
                if (!_modelLoadedOnce)
                {
                    _modelLoadedOnce = true;
                    OnLog("Modelo cargado correctamente. Servidor listo.");
                }

                if (CurrentStatus != ServerStatus.Ready)
                    CurrentStatus = ServerStatus.Ready;
            }
            else
            {
                OnHealthCheckFailed($"HTTP {(int)response.StatusCode}");
            }
        }
        catch (OperationCanceledException)
        {
            // Cancelación normal, no hacer nada
        }
        catch (Exception ex)
        {
            OnHealthCheckFailed(ex.Message);
        }
    }

    /// <summary>
    /// Registra un fallo de health check. Tras 3 fallos consecutivos, reinicia el servidor.
    /// </summary>
    private void OnHealthCheckFailed(string reason)
    {
        _consecutiveFailures++;
        OnLog($"Health check fallido ({_consecutiveFailures}/3): {reason}");

        if (_consecutiveFailures >= 3)
        {
            OnLog("3 fallos consecutivos. Reiniciando servidor...");
            CurrentStatus = ServerStatus.Error;

            // Reiniciar en background para no bloquear el health check loop
            var config = _currentConfig;
            if (config is not null)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        KillProcess();
                        await Task.Delay(1000);
                        _consecutiveFailures = 0;
                        _modelLoadedOnce = false;
                        CurrentStatus = ServerStatus.Starting;
                        LaunchProcess(config);
                    }
                    catch (Exception ex)
                    {
                        OnLog($"Error durante reinicio automático: {ex.Message}");
                    }
                });
            }
        }
    }

    /// <summary>
    /// Detiene el health check y mata el proceso si está corriendo.
    /// </summary>
    private async Task StopInternalAsync(CancellationToken ct)
    {
        _healthCheckCts?.Cancel();
        _healthCheckCts?.Dispose();
        _healthCheckCts = null;

        if (_serverProcess is null || _serverProcess.HasExited)
        {
            _serverProcess?.Dispose();
            _serverProcess = null;
            return;
        }

        OnLog("Deteniendo servidor...");

        // Intentar cierre limpio con timeout de 5 segundos
        try
        {
            _serverProcess.Kill();

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            await _serverProcess.WaitForExitAsync(linked.Token);
            OnLog("Servidor detenido limpiamente.");
        }
        catch (OperationCanceledException)
        {
            // Timeout: forzar kill
            OnLog("Timeout esperando cierre. Forzando kill...");
            KillProcess();
        }
        catch (Exception ex)
        {
            OnLog($"Error al detener servidor: {ex.Message}");
            KillProcess();
        }

        _serverProcess?.Dispose();
        _serverProcess = null;
    }

    /// <summary>
    /// Fuerza la terminación del proceso si sigue corriendo.
    /// </summary>
    private void KillProcess()
    {
        try
        {
            if (_serverProcess is not null && !_serverProcess.HasExited)
            {
                _serverProcess.Kill(entireProcessTree: true);
                OnLog("Proceso terminado forzosamente.");
            }
        }
        catch (Exception ex)
        {
            OnLog($"Error al forzar terminación: {ex.Message}");
        }
    }

    /// <summary>
    /// Verifica si el puerto ya está en uso intentando un GET al endpoint /health.
    /// </summary>
    private async Task<bool> IsPortInUseAsync(string host, int port, CancellationToken ct)
    {
        try
        {
            var response = await _httpClient.GetAsync($"http://{host}:{port}/health", ct);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Emite una línea de log a través del evento LogReceived.
    /// </summary>
    private void OnLog(string message)
    {
        LogReceived?.Invoke(this, message);
    }

    /// <summary>
    /// Parsea la salida de llama-server para detectar el backend utilizado.
    /// </summary>
    private void ParseBackendInfo(string line)
    {
        // Detección de CUDA
        if (line.Contains("ggml_cuda_init: found") && line.Contains("CUDA devices"))
        {
            // Se marca como GPU genérica hasta encontrar el nombre del dispositivo
            if (DetectedBackend == "Detectando..." || DetectedBackend.StartsWith("CPU"))
                DetectedBackend = "GPU: Detectando...";
        }
        else if (line.Contains("Device 0:") && (DetectedBackend == "GPU: Detectando..." || DetectedBackend == "Detectando..."))
        {
            // Extraer nombre del dispositivo: "Device 0: NVIDIA GeForce RTX 3060, compute capability 8.6"
            int index = line.IndexOf("Device 0:");
            string info = line.Substring(index + 9).Split(',')[0].Trim();
            DetectedBackend = $"GPU: {info}";
        }
        // Detección de CPU como fallback
        else if (line.Contains("load_backend: loaded CPU backend"))
        {
            if (DetectedBackend == "Detectando...")
            {
                DetectedBackend = "CPU: Backend Estándar";
            }
        }
    }
}
