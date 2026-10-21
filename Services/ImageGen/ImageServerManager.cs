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
    ///   |======&gt;          | 3/8 - 1.84s/it
    ///   |======&gt;          | 3/8 - 1.27it/s
    ///
    /// **El motor cambia de unidad según la velocidad.** El formato es
    /// <c>%s %i/%i - %s</c> y ese último <c>%s</c> vale <c>s/it</c> cuando una iteración
    /// pasa del segundo y <c>it/s</c> cuando no llega. Una primera versión de este patrón
    /// solo aceptaba <c>s/it</c> y por eso funcionaba a 1024×1024 —donde se midió en
    /// E4.0, a 1,85 s/it— y **perdía el progreso a 512×512**, que va por debajo del
    /// segundo: llegaba el primer paso, todavía lento, y ninguno más. Lo encontró el
    /// banco de E4.4 al generar en el tamaño chico, que E4.1 nunca había ejercido.
    ///
    /// La unidad tiene que seguir anclada, eso sí: el motor usa una barra de aspecto casi
    /// idéntico para la CARGA de tensores, terminada en <c>GB/s</c>
    /// (<c>|####| 108/386 - 2.34GB/s</c>). Sin la unidad, esa barra se informaría como
    /// progreso de generación y el avance saltaría de 386 a 8. Aceptar las dos unidades
    /// reales y ninguna más es lo que distingue un caso del otro.
    /// </summary>
    private static readonly Regex ProgressPattern = new(
        @"\|\s*(\d+)/(\d+)\s*-\s*([\d.]+)(s/it|it/s)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Interpreta una línea de stdout del motor como progreso de muestreo.
    ///
    /// Es público y estático para poder ejercerlo contra líneas reales sin levantar el
    /// motor: el caso que importa —que la unidad cambie con la velocidad— solo aparece
    /// generando en tamaños chicos, y una prueba que dependa de eso es lenta y frágil.
    /// </summary>
    /// <returns>false si la línea no es progreso de muestreo.</returns>
    public static bool TryParseProgress(string line, out ImageGenProgress progress)
    {
        progress = new ImageGenProgress();

        var m = ProgressPattern.Match(line);
        if (!m.Success) return false;

        if (!int.TryParse(m.Groups[1].Value, out var paso)) return false;
        if (!int.TryParse(m.Groups[2].Value, out var total) || total <= 0) return false;

        _ = double.TryParse(m.Groups[3].Value,
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var valor);

        // Se normaliza siempre a segundos por iteración, que es lo que necesita la
        // estimación de tiempo restante. Con "it/s" el número viene invertido.
        double porIteracion = m.Groups[4].Value == "it/s"
            ? (valor > 0 ? 1.0 / valor : 0)
            : valor;

        progress = new ImageGenProgress
        {
            Step = paso,
            TotalSteps = total,
            SecondsPerIteration = porIteracion
        };
        return true;
    }

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
            return Loc.F("imagegen.server.engineMissing", config.ServerExecutablePath);
        }

        var falta = config.FirstMissingModelFile();
        if (falta is not null)
        {
            return Loc.F("imagegen.server.fileMissing", falta);
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

            return Loc.F("imagegen.server.portBusy", config.Port, cargado, esperado);
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
        if (config is null) return Loc.T("imagegen.server.notConfigured");

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
            : Loc.T("imagegen.server.noAnswerAfterCancel");
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
            return Loc.F("imagegen.server.startFailed", ex.Message);
        }
    }

    private void HandleOutput(string? line)
    {
        if (line is null) return;
        if (line.Contains("out of memory", StringComparison.OrdinalIgnoreCase)) _jobOutOfMemory = true;
        ParseProgress(line);
        OnLog(line);
    }

    /// <summary>
    /// Pasos que se le pidieron al motor para el trabajo en curso. 0 = no hay trabajo.
    /// Es lo único que distingue el muestreo de las otras barras: ver BeginJob.
    /// </summary>
    private int _expectedSteps;

    /// <summary>El muestreo del trabajo actual ya llegó a su último paso.</summary>
    private bool _samplingFinished;

    /// <summary>
    /// Declara cuántos pasos va a tener el trabajo que se está por mandar.
    ///
    /// **Sin esto no se puede saber cuál de las barras de stdout es el muestreo.** El
    /// motor dibuja al menos tres barras con el mismo formato exacto
    /// (<c>%s %i/%i - %s</c>) y hay que quedarse con una sola:
    ///
    /// <code>
    ///   |####| 108/386 - 2.34GB/s    carga de tensores
    ///   |####| 3/8    - 1.84s/it     MUESTREO — la que importa
    ///   |####| 47/49  - 14.08it/s    decodificación del VAE en tiles (7x7)
    /// </code>
    ///
    /// La unidad no alcanza para separarlas: el muestreo usa <c>s/it</c> o <c>it/s</c>
    /// según la velocidad, y la del VAE usa <c>it/s</c> siempre. Una primera versión se
    /// apoyaba en la unidad y funcionaba por casualidad —solo aceptaba <c>s/it</c>, que a
    /// 1024×1024 es lo que imprime el muestreo—, a costa de perder el progreso entero en
    /// los tamaños chicos. Al aceptar las dos unidades apareció la barra del VAE, que
    /// hacía saltar el avance de 8 a 49.
    ///
    /// Comparar contra los pasos pedidos sí es seguro, porque el que los pidió es quien
    /// llama. Lo hace <see cref="ImageGenClient"/> por cada generación.
    /// </summary>
    public void BeginJob(int expectedSteps)
    {
        _expectedSteps = expectedSteps > 0 ? expectedSteps : 0;
        _samplingFinished = false;
        _jobOutOfMemory = false;
    }

    private volatile bool _jobOutOfMemory;

    /// <summary>
    /// True si el motor escribió «out of memory» desde el último <see cref="BeginJob"/>.
    /// Es lo único que dice POR QUÉ se cayó: la API solo deja de contestar (6.9 del
    /// maestro — `failed to allocate ... of pinned memory: out of memory` y enseguida
    /// un error de CUDA que aborta el proceso).
    /// </summary>
    public bool JobRanOutOfMemory => _jobOutOfMemory;

    /// <summary>
    /// Espera hasta <paramref name="timeout"/> a que el proceso termine y devuelve true si
    /// murió. Se usa después de un trabajo fallido: el poll se entera de que no hay nadie
    /// antes de que llegue el Exited, así que mirar el estado en el acto no alcanza.
    /// </summary>
    public async Task<bool> DiedDuringJobAsync(TimeSpan timeout)
    {
        var proceso = _serverProcess;
        if (proceso is null || _disposed) return false;
        try
        {
            using var cts = new CancellationTokenSource(timeout);
            await proceso.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception)
        {
            return false;   // sigue vivo, o ya no es nuestro
        }
    }

    /// <summary>Cierra el trabajo: deja de informar progreso hasta el próximo BeginJob.</summary>
    public void EndJob()
    {
        _expectedSteps = 0;
        _samplingFinished = false;
    }

    /// <summary>
    /// Extrae el progreso de muestreo de una línea de stdout y lo informa si de verdad
    /// es del muestreo. Ver BeginJob para por qué el filtro no puede salir de la línea.
    /// </summary>
    private void ParseProgress(string line)
    {
        if (!TryParseProgress(line, out var progreso)) return;

        // Sin trabajo declarado no se informa nada: es preferible quedarse sin barra que
        // mostrar el avance de otra cosa.
        if (_expectedSteps <= 0 || progreso.TotalSteps != _expectedSteps) return;

        // Y una vez que el muestreo terminó, cualquier barra posterior con el mismo total
        // es de otra etapa. Pasa si los tiles del VAE coinciden en número con los pasos:
        // la barra volvería a empezar y el avance iría para atrás.
        if (_samplingFinished) return;
        if (progreso.Step >= progreso.TotalSteps) _samplingFinished = true;

        ProgressReported?.Invoke(this, progreso);
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
