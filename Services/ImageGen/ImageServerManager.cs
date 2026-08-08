using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using PRImager.AIAssistant.Helpers;
using PRImager.AIAssistant.Services.Enums;

namespace PRImager.AIAssistant.Services.ImageGen;

/// <summary>
/// Gestiona sd-server.exe como proceso hijo. Replica el patrón de LlamaServerManager
/// —health check, JobObject, detección de puerto ocupado— con tres diferencias que
/// impuso el reconocimiento de E4.0:
///
/// 1. El progreso de muestreo NO viene por la API: la respuesta del job solo tiene
///    estados. Se parsea de stdout, que es la única fuente que hay.
/// 2. La API no cancela un job que ya está generando: devuelve 409 y el job termina
///    igual. Cancelar de verdad es matar el proceso y relanzarlo (~1,1 s medidos).
/// 3. El servidor ocioso ocupa 0 MiB de VRAM porque carga los pesos por job y los
///    libera al terminar. Puede quedar levantado sin estorbar al modelo de lenguaje.
/// </summary>
public sealed class ImageServerManager : IDisposable
{
    /// <summary>
    /// Progreso de muestreo tal como lo escribe el motor:
    ///   |======>          | 3/8 - 1.84s/it
    ///
    /// El "s/it" del final no es decorativo: el motor usa una barra de aspecto casi
    /// idéntico para la CARGA de tensores, pero terminada en "GB/s"
    /// (|####| 108/386 - 2.34GB/s). Sin anclar la unidad, la barra de carga se
    /// informaría como progreso de generación y el avance saltaría de 386 a 8.
    /// </summary>
    private static readonly Regex ProgressPattern = new(
        @"\|\s*(\d+)/(\d+)\s*-\s*([\d.]+)s/it",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly HttpClient _httpClient;
    private readonly object _lock = new();

    private Process? _serverProcess;
    private CancellationTokenSource? _healthCheckCts;
    private ImageServerConfig? _currentConfig;
    private ServerStatus _currentStatus = ServerStatus.Stopped;
    private JobObject? _jobObject;
    private bool _disposed;
    private int _consecutiveFailures;

    // --- Eventos ---

    public event EventHandler<ServerStatusChangedEventArgs>? StatusChanged;

    /// <summary>Cada línea de stdout/stderr de sd-server.</summary>
    public event EventHandler<string>? LogReceived;

    /// <summary>Progreso de muestreo, extraído de stdout.</summary>
    public event EventHandler<ImageGenProgress>? ProgressReported;

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

    public ImageServerConfig? CurrentConfig => _currentConfig;

    public string BaseUrl => _currentConfig?.BaseUrl ?? "";

    public ImageServerManager()
    {
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
    }

    // --- Ciclo de vida ---

    /// <summary>
    /// Arranca sd-server. Devuelve un mensaje de error legible en lugar de lanzar
    /// cuando falta el motor o falta alguno de los tres archivos del modelo: son
    /// situaciones normales en una instalación nueva, no errores de programación.
    /// </summary>
    /// <returns>null si arrancó; el motivo si no.</returns>
    public async Task<string?> StartAsync(ImageServerConfig config, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!File.Exists(config.ServerExecutablePath))
        {
            return $"No se encontró el motor de generación de imágenes en:\n{config.ServerExecutablePath}\n\n" +
                   "Descargá stable-diffusion.cpp (build CUDA + runtime) y descomprimilo en esa carpeta.";
        }

        var falta = config.FirstMissingModelFile();
        if (falta is not null)
        {
            return $"Falta un archivo del modelo de imagen:\n{falta}\n\n" +
                   "Un modelo de imagen son tres archivos: difusión, text encoder y VAE.";
        }

        // Si ya hay algo escuchando, se reutiliza SOLO si tiene cargado el mismo modelo
        // de difusión. Reutilizar a ciegas haría que el usuario pida una imagen con un
        // modelo y la reciba de otro, sin ningún aviso.
        var yaLevantado = await GetCapabilitiesAsync(config.BaseUrl, ct);
        if (yaLevantado is not null)
        {
            var esperado = Path.GetFileNameWithoutExtension(config.DiffusionModelPath);
            var cargado = Path.GetFileNameWithoutExtension(yaLevantado.ModelName);

            if (string.Equals(esperado, cargado, StringComparison.OrdinalIgnoreCase))
            {
                OnLog($"Ya hay un sd-server en {config.Host}:{config.Port} con el mismo modelo. Se reutiliza.");
                _currentConfig = config;
                CurrentStatus = ServerStatus.Ready;
                StartHealthCheck();
                return null;
            }

            return $"El puerto {config.Port} ya está ocupado por otro sd-server que tiene cargado " +
                   $"'{cargado}' en lugar de '{esperado}'.\n\n" +
                   "Cerrá esa instancia o configurá otro puerto.";
        }

        await StopInternalAsync(ct);

        _currentConfig = config;
        _consecutiveFailures = 0;
        CurrentStatus = ServerStatus.Starting;

        var error = LaunchProcess(config);
        if (error is not null)
        {
            CurrentStatus = ServerStatus.Error;
            return error;
        }

        StartHealthCheck();
        return null;
    }

    /// <summary>Espera a que el servidor responda, o se agote el timeout.</summary>
    public async Task<bool> WaitForReadyAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var cron = Stopwatch.StartNew();
        while (cron.Elapsed < timeout && !ct.IsCancellationRequested)
        {
            if (CurrentStatus == ServerStatus.Ready) return true;
            if (CurrentStatus == ServerStatus.Error || CurrentStatus == ServerStatus.Stopped) return false;

            // Se sondea directo en lugar de esperar al health check periódico: el
            // servidor está listo en menos de un segundo y el ciclo de salud corre
            // cada cinco.
            if (_currentConfig is not null &&
                await GetCapabilitiesAsync(_currentConfig.BaseUrl, ct) is not null)
            {
                CurrentStatus = ServerStatus.Ready;
                return true;
            }

            try { await Task.Delay(100, ct); }
            catch (OperationCanceledException) { break; }
        }
        return CurrentStatus == ServerStatus.Ready;
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await StopInternalAsync(ct);
        CurrentStatus = ServerStatus.Stopped;
    }

    /// <summary>
    /// Cancela una generación en curso. La API no puede hacerlo —un job en
    /// 'generating' devuelve 409 y termina igual—, así que se mata el proceso y se
    /// vuelve a levantar. Medido en E4.0: ~460 ms en salir, 0,17 s en devolver la
    /// VRAM y 0,65 s en volver a responder.
    ///
    /// IMPORTANTE: se espera la salida real del proceso antes de relanzar. Sin ese
    /// WaitForExit el relanzamiento encuentra el puerto todavía tomado y falla de
    /// forma intermitente, que es la peor manera de fallar.
    ///
    /// Advertencia de alcance: matar el servidor DESCARTA LA COLA ENTERA. Si había
    /// varios trabajos encolados, se pierden todos.
    /// </summary>
    /// <returns>null si quedó listo de nuevo; el motivo si no.</returns>
    public async Task<string?> CancelAndRestartAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var config = _currentConfig;
        if (config is null) return "No hay un servidor de imágenes configurado.";

        OnLog("Cancelando: se detiene sd-server y se vuelve a levantar.");
        await StopInternalAsync(ct);

        CurrentStatus = ServerStatus.Starting;
        var error = LaunchProcess(config);
        if (error is not null)
        {
            CurrentStatus = ServerStatus.Error;
            return error;
        }

        StartHealthCheck();
        return await WaitForReadyAsync(TimeSpan.FromSeconds(30), ct)
            ? null
            : "El motor de imágenes no volvió a responder después de cancelar.";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _healthCheckCts?.Cancel();
        _healthCheckCts?.Dispose();
        _healthCheckCts = null;

        KillProcess();

        // El JobObject es lo que garantiza que sd-server no sobreviva a un cierre
        // sucio de la aplicación: al soltarse el handle, Windows mata lo que quede.
        _jobObject?.Dispose();
        _jobObject = null;

        _httpClient.Dispose();
        GC.SuppressFinalize(this);
    }

    // --- Interno ---

    /// <returns>null si el proceso arrancó; el motivo si no.</returns>
    private string? LaunchProcess(ImageServerConfig config)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = config.ServerExecutablePath,
            Arguments = config.ToCommandLineArgs(),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // El motor deja las rutas de sus DLL relativas al ejecutable.
            WorkingDirectory = Path.GetDirectoryName(config.ServerExecutablePath) ?? ""
        };

        OnLog($"Iniciando: {startInfo.FileName} {startInfo.Arguments}");

        _serverProcess = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        _serverProcess.OutputDataReceived += (_, e) => HandleOutput(e.Data);
        _serverProcess.ErrorDataReceived += (_, e) => HandleOutput(e.Data);
        _serverProcess.Exited += (_, _) =>
        {
            if (!_disposed && CurrentStatus != ServerStatus.Stopped && CurrentStatus != ServerStatus.Starting)
            {
                OnLog("El proceso sd-server terminó inesperadamente.");
                CurrentStatus = ServerStatus.Error;
            }
        };

        try
        {
            _serverProcess.Start();

            _jobObject ??= new JobObject();
            _jobObject.AssignProcess(_serverProcess);

            _serverProcess.BeginOutputReadLine();
            _serverProcess.BeginErrorReadLine();
            CurrentStatus = ServerStatus.LoadingModel;
            OnLog($"sd-server iniciado con PID {_serverProcess.Id}.");
            return null;
        }
        catch (Exception ex)
        {
            OnLog($"Error al iniciar sd-server: {ex.Message}");
            return $"No se pudo iniciar el motor de imágenes: {ex.Message}";
        }
    }

    private void HandleOutput(string? line)
    {
        if (line is null) return;
        ParseProgress(line);
        OnLog(line);
    }

    /// <summary>
    /// Extrae el progreso de muestreo de una línea de stdout. Ver ProgressPattern para
    /// por qué la unidad "s/it" es parte del patrón y no un adorno.
    /// </summary>
    private void ParseProgress(string line)
    {
        var m = ProgressPattern.Match(line);
        if (!m.Success) return;

        if (!int.TryParse(m.Groups[1].Value, out var paso)) return;
        if (!int.TryParse(m.Groups[2].Value, out var total) || total <= 0) return;
        _ = double.TryParse(m.Groups[3].Value,
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var porIteracion);

        ProgressReported?.Invoke(this, new ImageGenProgress
        {
            Step = paso,
            TotalSteps = total,
            SecondsPerIteration = porIteracion
        });
    }

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
                try { await Task.Delay(TimeSpan.FromSeconds(5), token); }
                catch (OperationCanceledException) { break; }

                if (_currentConfig is null) continue;

                if (await GetCapabilitiesAsync(_currentConfig.BaseUrl, token) is not null)
                {
                    _consecutiveFailures = 0;
                    if (CurrentStatus != ServerStatus.Ready) CurrentStatus = ServerStatus.Ready;
                }
                else if (!token.IsCancellationRequested)
                {
                    _consecutiveFailures++;
                    OnLog($"Health check del motor de imágenes fallido ({_consecutiveFailures}/3).");
                    if (_consecutiveFailures >= 3) CurrentStatus = ServerStatus.Error;
                }
            }
        }, token);
    }

    /// <summary>
    /// Consulta /sdcpp/v1/capabilities. Sirve de health check y a la vez trae lo que
    /// la UI necesita para poblarse. Devuelve null si no hay nadie del otro lado.
    /// </summary>
    public async Task<ImageServerCapabilities?> GetCapabilitiesAsync(string baseUrl, CancellationToken ct = default)
    {
        try
        {
            using var resp = await _httpClient.GetAsync($"{baseUrl}/sdcpp/v1/capabilities", ct)
                                              .ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;

            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return ImageGenClient.ParseCapabilities(json);
        }
        catch
        {
            return null;
        }
    }

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

        try
        {
            _serverProcess.Kill(entireProcessTree: true);

            // Esperar la salida REAL no es opcional. Medido: el proceso tarda ~460 ms
            // en desaparecer cuando se lo mata en plena generación, y relanzar antes
            // deja el puerto ocupado.
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            await _serverProcess.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            OnLog("sd-server detenido.");
        }
        catch (Exception ex)
        {
            OnLog($"Error al detener sd-server: {ex.Message}");
            KillProcess();
        }

        _serverProcess?.Dispose();
        _serverProcess = null;
    }

    private void KillProcess()
    {
        try
        {
            if (_serverProcess is not null && !_serverProcess.HasExited)
                _serverProcess.Kill(entireProcessTree: true);
        }
        catch
        {
            // Ya murió, o no hay permisos. Nada que hacer.
        }
    }

    private void OnLog(string message) => LogReceived?.Invoke(this, message);
}
