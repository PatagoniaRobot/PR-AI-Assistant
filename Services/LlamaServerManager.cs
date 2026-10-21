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
    // El dispositivo detectado se muestra en el pie de la barra lateral. Los textos de
    // «detectando» son además los centinelas que ParseBackendInfo compara: se leen de
    // un solo lugar para que la comparación y el valor sean siempre el mismo texto.
    private static string Detecting => Loc.T("backend.detecting");
    private static string GpuDetecting => Loc.T("backend.gpuDetecting");

    private string _detectedBackend = Detecting;

    /// <summary>
    /// Última línea de llama-server que explica por qué no pudo cargar el modelo.
    /// El motor imprime el motivo exacto —«missing tensor», «unknown model architecture»,
    /// falta de memoria— y sin esto se perdía: la vista quedaba en «Iniciando modelo...»
    /// para siempre sin decir nunca qué había pasado.
    /// </summary>
    private string? _lastLoadError;

    /// <summary>Reinicios ya consumidos por el servidor que sí llegó a estar listo alguna vez.</summary>
    private int _restartsDone;

    /// <summary>Cerrojo para que haya un solo reinicio en vuelo. 0 = libre, 1 = tomado.</summary>
    private int _restartInFlight;

    /// <summary>
    /// El usuario pidió detener. Se levanta ANTES de matar el proceso: el evento Exited
    /// llega mientras el estado todavía dice Ready, y sin esto se leía como una caída y
    /// el reinicio automático lanzaba otro llama-server —que además quedaba sin dueño:
    /// el siguiente arranque lo adoptaba por el puerto y «Detener» ya no lo alcanzaba—.
    /// </summary>
    private volatile bool _stopRequested;

    /// <summary>
    /// Tope de reinicios automáticos. Reiniciar sin límite convierte un modelo que no
    /// carga en un bucle perpetuo que relee el .gguf cada medio minuto y mantiene la
    /// carpeta de modelos tomada.
    /// </summary>
    private const int MaxRestarts = 3;

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
        private set => SetStatus(value, null);
    }

    /// <summary>
    /// Cambia el estado adjuntando, cuando lo hay, el motivo que dio el motor.
    /// <see cref="ServerStatusChangedEventArgs.ErrorMessage"/> existía desde el principio
    /// y nunca se llenaba: el estado llegaba a la vista sin la razón.
    /// </summary>
    private void SetStatus(ServerStatus value, string? errorMessage)
    {
        ServerStatus old;
        lock (_lock)
        {
            old = _currentStatus;
            if (old == value && errorMessage is null) return;
            _currentStatus = value;
        }
        StatusChanged?.Invoke(this, new ServerStatusChangedEventArgs(old, value, errorMessage));
    }

    /// <summary>
    /// Motivo del último fallo de carga, tal como lo informó llama-server.
    /// </summary>
    public string? LastLoadError => _lastLoadError;

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
                Loc.F("llama.exeMissing", config.ServerExecutablePath));
        }
        Debug.WriteLine("LlamaServerManager: Ejecutable encontrado: SÍ");

        // Validar que el modelo existe
        Debug.WriteLine($"LlamaServerManager: Buscando modelo en: {config.ModelPath}");
        if (!File.Exists(config.ModelPath))
        {
            Debug.WriteLine("LlamaServerManager: Modelo encontrado: NO");
            throw new InvalidOperationException(
                Loc.F("llama.modelMissing", config.ModelPath));
        }
        Debug.WriteLine("LlamaServerManager: Modelo encontrado: SÍ");

        // Verificar si ya hay un proceso escuchando en el puerto
        if (await IsPortInUseAsync(config.Host, config.Port, ct))
        {
            OnLog($"Ya hay un servicio escuchando en {config.Host}:{config.Port}. Se usará la instancia existente.");
            _currentConfig = config;
            _stopRequested = false;
            _modelLoadedOnce = true;
            CurrentStatus = ServerStatus.Ready;
            StartHealthCheck();
            return;
        }

        // Detener proceso anterior si existe
        await StopInternalAsync(ct);

        _currentConfig = config;
        _stopRequested = false;
        _consecutiveFailures = 0;
        _modelLoadedOnce = false;
        _restartsDone = 0;
        _restartInFlight = 0;
        _lastLoadError = null;
        DetectedBackend = Detecting;
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
        _stopRequested = true;
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

        StopHealthCheck();

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

        // El Process anterior se soltaba sin Dispose en cada relanzamiento: con el bucle
        // de reinicio eso acumulaba un handle por vuelta.
        _serverProcess?.Dispose();
        _serverProcess = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

        _serverProcess.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                ParseBackendInfo(e.Data);
                CaptureLoadError(e.Data);
                OnLog(e.Data);
            }
        };

        _serverProcess.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                ParseBackendInfo(e.Data);
                CaptureLoadError(e.Data);
                OnLog($"[stderr] {e.Data}");
            }
        };

        _serverProcess.Exited += (_, _) => OnProcessExited();

        try
        {
            Debug.WriteLine($"LlamaServerManager: Iniciando proceso con args: {startInfo.Arguments}");
            _serverProcess.Start();

            // Gestionar proceso huérfano. El job anterior se cierra recién acá: cerrarlo
            // antes mataría al proceso que todavía estamos por reemplazar.
            var previousJob = _jobObject;
            _jobObject = new JobObject();
            _jobObject.AssignProcess(_serverProcess);
            previousJob?.Dispose();

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
                    _restartsDone = 0;
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
    /// Guarda el motivo por el que llama-server no pudo cargar el modelo.
    /// El motor lo imprime y después se va; si no se retiene acá, cuando el proceso
    /// termina ya no queda nada que mostrarle al usuario.
    /// </summary>
    private void CaptureLoadError(string line)
    {
        if (_modelLoadedOnce) return;

        if (line.Contains("error loading model:", StringComparison.OrdinalIgnoreCase)
            || line.Contains("failed to load model", StringComparison.OrdinalIgnoreCase)
            || line.Contains("unknown model architecture", StringComparison.OrdinalIgnoreCase)
            || line.Contains("error while handling argument", StringComparison.OrdinalIgnoreCase))
        {
            // Se queda con la primera, que es la causa; las siguientes son su arrastre
            // («failed to load model» viene después de «error loading model:»).
            _lastLoadError ??= line.Trim();
        }
    }

    /// <summary>
    /// El proceso terminó por su cuenta. Es la señal de fallo confiable, no el health
    /// check: llama-server se va solo cuando no puede cargar el modelo.
    /// </summary>
    /// <remarks>
    /// La distinción que importa es si el modelo llegó a cargar alguna vez. Si nunca
    /// cargó, el modelo no sirve y relanzarlo repite el mismo fallo para siempre —era
    /// el bucle que dejaba la vista en «Iniciando modelo...» releyendo el .gguf cada
    /// medio minuto—. Si sí cargó y después se cayó, reiniciar es lo correcto.
    /// </remarks>
    private void OnProcessExited()
    {
        if (_disposed || _stopRequested) return;
        if (CurrentStatus == ServerStatus.Stopped) return;

        if (!_modelLoadedOnce)
        {
            StopHealthCheck();
            string reason = _lastLoadError
                ?? Loc.T("llama.exitedBeforeLoad");
            OnLog($"El modelo no se pudo cargar: {reason}");
            SetStatus(ServerStatus.Error, reason);
            return;
        }

        OnLog("El proceso llama-server terminó inesperadamente.");
        RestartOrGiveUp(Loc.T("llama.restart.exited"));
    }

    /// <summary>
    /// Reinicia el servidor que ya había funcionado, hasta <see cref="MaxRestarts"/> veces.
    /// </summary>
    private void RestartOrGiveUp(string reason)
    {
        var config = _currentConfig;
        if (config is null || _stopRequested) return;

        // Un solo reinicio en vuelo. La caída de un servidor que estaba listo llega por
        // dos caminos a la vez —el evento Exited y los health checks que empiezan a
        // fallar— y sin este cerrojo cada uno lanzaría su propio llama-server.
        if (Interlocked.CompareExchange(ref _restartInFlight, 1, 0) != 0) return;

        // Se corta acá, no dentro de la tarea: mientras el reinicio corre no debe seguir
        // habiendo chequeos que lo vuelvan a disparar.
        StopHealthCheck();

        if (_restartsDone >= MaxRestarts)
        {
            string message = Loc.F("llama.restart.gaveUp", MaxRestarts, reason);
            OnLog(message);
            SetStatus(ServerStatus.Error, message);
            Interlocked.Exchange(ref _restartInFlight, 0);
            return;
        }

        _restartsDone++;
        OnLog($"Reiniciando servidor (intento {_restartsDone} de {MaxRestarts}): {reason}");
        CurrentStatus = ServerStatus.Error;

        _ = Task.Run(async () =>
        {
            try
            {
                KillProcess();

                // Esperar la salida real antes de relanzar: si el proceso viejo todavía
                // tiene el puerto, el nuevo no puede escuchar y se va también.
                await WaitForProcessExitAsync(TimeSpan.FromSeconds(10));

                if (_disposed || _stopRequested || CurrentStatus == ServerStatus.Stopped) return;

                _consecutiveFailures = 0;
                _modelLoadedOnce = false;
                _lastLoadError = null;
                CurrentStatus = ServerStatus.Starting;
                LaunchProcess(config);
                StartHealthCheck();
            }
            catch (Exception ex)
            {
                OnLog($"Error durante reinicio automático: {ex.Message}");
                SetStatus(ServerStatus.Error, Loc.F("llama.restart.error", ex.Message));
            }
            finally
            {
                Interlocked.Exchange(ref _restartInFlight, 0);
            }
        });
    }

    /// <summary>Espera a que el proceso actual termine de verdad, con tope.</summary>
    private async Task WaitForProcessExitAsync(TimeSpan timeout)
    {
        var process = _serverProcess;
        if (process is null) return;

        try
        {
            using var cts = new CancellationTokenSource(timeout);
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            OnLog("El proceso anterior no terminó dentro del plazo.");
        }
        catch (InvalidOperationException)
        {
            // El proceso nunca llegó a arrancar; no hay nada que esperar.
        }
    }

    /// <summary>Detiene el bucle de health check sin tocar el proceso.</summary>
    private void StopHealthCheck()
    {
        _healthCheckCts?.Cancel();
        _healthCheckCts?.Dispose();
        _healthCheckCts = null;
    }

    /// <summary>
    /// Registra un fallo de health check. Tras 3 fallos consecutivos, reinicia el servidor.
    /// </summary>
    private void OnHealthCheckFailed(string reason)
    {
        // Mientras el modelo carga, llama-server responde 503 y rechaza conexiones: es
        // progreso normal, no un fallo. Contarlo como fallo mataba a los quince segundos
        // un arranque perfectamente sano —tres chequeos de cinco— y volvía a lanzar otro
        // que moría en el mismo punto. Un modelo grande en un disco lento no cargaba nunca.
        // Acá el fallo real lo señala la salida del proceso, en OnProcessExited.
        if (!_modelLoadedOnce)
        {
            OnLog($"Todavía cargando el modelo ({reason}).");
            return;
        }

        _consecutiveFailures++;
        OnLog($"Health check fallido ({_consecutiveFailures}/3): {reason}");

        if (_consecutiveFailures >= 3)
            RestartOrGiveUp(Loc.F("llama.restart.healthChecks", reason));
    }

    /// <summary>
    /// Detiene el health check y mata el proceso si está corriendo.
    /// </summary>
    private async Task StopInternalAsync(CancellationToken ct)
    {
        StopHealthCheck();

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
            if (DetectedBackend == Detecting || DetectedBackend.StartsWith("CPU"))
                DetectedBackend = GpuDetecting;
        }
        else if (line.Contains("Device 0:") && (DetectedBackend == GpuDetecting || DetectedBackend == Detecting))
        {
            // Extraer nombre del dispositivo: "Device 0: NVIDIA GeForce RTX 3060, compute capability 8.6"
            int index = line.IndexOf("Device 0:");
            string info = line.Substring(index + 9).Split(',')[0].Trim();
            DetectedBackend = $"GPU: {info}";
        }
        // Detección de CPU como fallback
        else if (line.Contains("load_backend: loaded CPU backend"))
        {
            if (DetectedBackend == Detecting)
            {
                DetectedBackend = Loc.T("backend.cpu");
            }
        }
    }
}
