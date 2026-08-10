using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PRImager.AIAssistant.Helpers;
using PRImager.AIAssistant.Services;
using PRImager.AIAssistant.Services.ImageGen;

namespace PRImager.AIAssistant.ViewModels.ImageGen;

/// <summary>Orientación de la salida. El lado corto y el largo salen de la tabla de tamaños.</summary>
public enum ImageOrientation
{
    Vertical,
    Square,
    Horizontal
}

/// <summary>
/// Una orientación con su etiqueta. Existe para que la lista de la interfaz salga en
/// español sin un converter: bindear el enum pelado dibujaría «Square».
/// </summary>
public sealed class ImageOrientationOption
{
    public ImageOrientationOption(ImageOrientation value, string label)
    {
        Value = value;
        Label = label;
    }

    public ImageOrientation Value { get; }
    public string Label { get; }

    public override string ToString() => Label;
}

/// <summary>
/// Un tamaño ofrecido por el selector.
///
/// Con <c>--vae-tiling</c> el tamaño casi no mueve la VRAM —512×512 pica en 9.082 MiB y
/// 1024×1024 en 9.298, 216 MiB de diferencia— pero sí mueve el tiempo: 6,3 s contra
/// 21,3 s. **Elegir el tamaño es una decisión de tiempo, no de memoria**, y la interfaz
/// lo dice así en lugar de dejar que el usuario suponga lo contrario.
/// </summary>
public sealed class ImageSizeOption
{
    public ImageSizeOption(int side, string label)
    {
        Side = side;
        Label = label;
    }

    /// <summary>Lado del cuadrado. Las otras orientaciones se derivan de acá.</summary>
    public int Side { get; }

    public string Label { get; }

    public override string ToString() => Label;
}

/// <summary>Una imagen ya generada, en la galería de la sesión.</summary>
public sealed class GeneratedImageViewModel
{
    public GeneratedImageViewModel(string imagePath, ImageGenMetadata metadata,
                                   ImageSource? thumbnail, ImageSource? preview)
    {
        ImagePath = imagePath;
        Metadata = metadata;
        Thumbnail = thumbnail;
        Preview = preview;
    }

    public string ImagePath { get; }
    public string SidecarPath => ImageGenMetadata.SidecarPathFor(ImagePath);
    public ImageGenMetadata Metadata { get; }

    /// <summary>Miniatura para la tira. Congelada, así que se puede decodificar fuera del hilo de UI.</summary>
    public ImageSource? Thumbnail { get; }

    /// <summary>La misma imagen en grande, para el panel principal.</summary>
    public ImageSource? Preview { get; }

    public string FileName => Path.GetFileName(ImagePath);

    public string Caption => Metadata.ShortSummary;

    public string Tooltip =>
        (string.IsNullOrWhiteSpace(Metadata.Prompt) ? "(sin prompt)" : Metadata.Prompt) +
        "\n\n" + Metadata.ShortSummary +
        $"\n{Metadata.ElapsedSeconds.ToString("0.#", CultureInfo.CurrentCulture)} s" +
        $"\n\n{FileName}";
}

/// <summary>
/// La vista de generación de imágenes (E4.4).
///
/// Cablea las cuatro piezas que dejaron las fases anteriores; no reimplementa ninguna:
///
/// | necesidad          | de dónde sale                                          |
/// |--------------------|--------------------------------------------------------|
/// | progreso           | <c>ImageServerManager.ProgressReported</c> (stdout)     |
/// | generar y guardar  | <c>ImageGenClient.GenerateAsync</c>                     |
/// | cancelar           | <c>ImageServerManager.CancelAndRestartAsync</c>         |
/// | ¿entra en la GPU?  | <c>ImageVramArbiter.Evaluate</c>                        |
/// | qué modelo usar    | <c>ImageModelService.SelectedPackage</c>                |
/// | pasos y CFG        | <c>ImageModelPackage.NewRequest</c>, NO /capabilities   |
///
/// **Se genera de a una imagen por vez** (decisión 5.7). Es lo que hacen Gemini y
/// ChatGPT, y disuelve el problema de que cancelar mate la cola: si nunca hay más de un
/// trabajo, matar el servidor no puede llevarse por delante nada que el usuario no haya
/// pedido cancelar. Por eso acá no hay ni lista de pendientes ni posiciones de cola.
/// </summary>
public sealed class ImageGenerationViewModel : ObservableObject, IDisposable
{
    private readonly ImageModelService _modelService;
    private readonly ImageServerManager _manager;
    private readonly ImageGenClient _client;
    private readonly GpuInfoService _gpuInfo;
    private readonly Func<string> _enginePathResolver;

    /// <summary>
    /// Carpeta de salida, resuelta **en cada uso** y no capturada al construir.
    ///
    /// Es un resolutor y no una cadena por la lección del defecto 4.19: la carpeta se puede
    /// cambiar desde la ventana de carpetas con esta abierta, y una copia tomada en el
    /// constructor guardaría las imágenes en la carpeta vieja mientras el botón de abrir la
    /// carpeta mostraría la nueva. Dos fuentes para la misma pregunta es exactamente lo que
    /// hizo que la vista avisara que faltaba el motor con el motor instalado.
    /// </summary>
    private readonly Func<string> _outputDirectoryResolver;

    private readonly bool _ownsManager;

    /// <summary>
    /// Contexto del hilo de UI, capturado al construir.
    ///
    /// No se usa <c>Application.Current.Dispatcher</c>, que es lo que hace
    /// <c>MainViewModel</c>, porque el progreso llega desde el hilo que lee stdout de
    /// sd-server y este ViewModel tiene que poder ejercerse desde un banco sin
    /// <c>Application</c>. Con null se invoca derecho, que es lo correcto ahí.
    /// </summary>
    private readonly SynchronizationContext? _uiContext;

    private GpuVramInfo _gpu = GpuVramInfo.Unavailable("Sin consultar.");
    private ImageServerConfig? _activeConfig;
    private CancellationTokenSource? _generationCts;
    private Task? _generationTask;
    private bool _shutdown;
    private bool _disposed;

    /// <summary>
    /// Registro de auditoría. Se le pasa el hecho, **nunca el contenido**: queda que se
    /// generó una imagen, con qué modelo, qué tamaño y qué semilla, pero no el prompt.
    ///
    /// Es el mismo criterio con el que 9.3 resolvió la carga de archivos en Modo General:
    /// auditar lo que el usuario escribe en el modo personal lo convertiría en otra cosa.
    /// El prompt no se pierde — está en el sidecar, al lado de la imagen, que es donde
    /// sirve para reproducirla.
    /// </summary>
    private readonly Action<string, string>? _auditLog;

    public ImageGenerationViewModel(
        ImageModelService modelService,
        ImageServerManager? manager = null,
        GpuInfoService? gpuInfo = null,
        Func<string>? enginePathResolver = null,
        string? outputDirectory = null,
        Action<string, string>? auditLog = null)
    {
        _modelService = modelService;
        _auditLog = auditLog;
        _ownsManager = manager is null;
        _manager = manager ?? new ImageServerManager();
        _client = new ImageGenClient(_manager);
        _gpuInfo = gpuInfo ?? new GpuInfoService();
        _enginePathResolver = enginePathResolver ?? ImageEngineLocator.ResolveExecutablePath;
        // Una carpeta explícita —la que pasan los bancos— manda; si no, se pregunta al
        // servicio cada vez, que es quien sabe si el usuario la cambió.
        _outputDirectoryResolver = outputDirectory is not null
            ? () => outputDirectory
            : modelService.ResolveOutputDirectory;
        _uiContext = SynchronizationContext.Current;

        // Se fijan acá y no en el inicializador del campo porque dependen de las listas,
        // que son propiedades de instancia: el orden de inicialización sería frágil.
        _selectedSize = SizeOptions[2];                                    // 1024
        _selectedOrientation = Orientations[1];                            // cuadrada

        _manager.ProgressReported += OnProgressReported;

        GenerateCommand = new AsyncRelayCommand(GenerateAsync, () => CanGenerate);
        CancelCommand = new RelayCommand(Cancel, () => IsGenerating);
        OpenFolderCommand = new RelayCommand(OpenOutputFolder);
        OpenImageCommand = new RelayCommand<GeneratedImageViewModel>(OpenImage);

        ApplyPackageDefaults();
    }

    // ------------------------------------------------------------------ el prompt

    private string _prompt = "";
    public string Prompt
    {
        get => _prompt;
        set
        {
            if (SetProperty(ref _prompt, value))
            {
                OnPropertyChanged(nameof(CanGenerate));
                GenerateCommand.RaiseCanExecuteChanged();
            }
        }
    }

    private string _negativePrompt = "";
    public string NegativePrompt
    {
        get => _negativePrompt;
        set => SetProperty(ref _negativePrompt, value);
    }

    // ------------------------------------------------------------------ el tamaño

    /// <summary>
    /// Los tres presets de 3.1. **No hay campo libre**, y por eso el tope de 3.072 de
    /// <see cref="ImageVramArbiter.MaxSafeDimension"/> no puede alcanzarse desde acá: el
    /// más grande que ofrece la lista es 1.216 de lado. El límite sigue comprobándose en
    /// <see cref="ImageGenClient"/> igual, porque pasarse mata el proceso y una barrera
    /// que solo vive en la interfaz no alcanza para algo que no se puede deshacer.
    /// </summary>
    public IReadOnlyList<ImageSizeOption> SizeOptions { get; } = new[]
    {
        new ImageSizeOption(512, "512 — rápido"),
        new ImageSizeOption(768, "768 — intermedio"),
        new ImageSizeOption(1024, "1024 — detallado")
    };

    private ImageSizeOption _selectedSize = null!;   // lo fija el constructor
    public ImageSizeOption SelectedSize
    {
        get => _selectedSize;
        set
        {
            if (value is not null && SetProperty(ref _selectedSize, value)) OnSizeChanged();
        }
    }

    public IReadOnlyList<ImageOrientationOption> Orientations { get; } = new[]
    {
        new ImageOrientationOption(ImageOrientation.Vertical, "Vertical"),
        new ImageOrientationOption(ImageOrientation.Square, "Cuadrada"),
        new ImageOrientationOption(ImageOrientation.Horizontal, "Horizontal")
    };

    private ImageOrientationOption _selectedOrientation = null!;   // lo fija el constructor
    public ImageOrientationOption SelectedOrientation
    {
        get => _selectedOrientation;
        set
        {
            if (value is not null && SetProperty(ref _selectedOrientation, value)) OnSizeChanged();
        }
    }

    public ImageOrientation Orientation => SelectedOrientation.Value;

    /// <summary>
    /// Tabla de tamaños. Todos múltiplos de 64 —el motor trabaja en bloques de esa
    /// medida— y de megapíxeles parecidos dentro de cada preset, para que cambiar la
    /// orientación no cambie el tiempo.
    /// </summary>
    private static (int Ancho, int Alto) Dimensiones(int lado, ImageOrientation o)
    {
        var (corto, largo) = lado switch
        {
            512 => (448, 640),
            768 => (640, 896),
            _ => (832, 1216)
        };

        return o switch
        {
            ImageOrientation.Vertical => (corto, largo),
            ImageOrientation.Horizontal => (largo, corto),
            _ => (lado, lado)
        };
    }

    public int Width => Dimensiones(SelectedSize.Side, Orientation).Ancho;
    public int Height => Dimensiones(SelectedSize.Side, Orientation).Alto;

    public string SizeText => $"{Width} × {Height}";

    /// <summary>
    /// Lo que el usuario tiene que entender del selector de tamaño, medido en E4.0.
    /// </summary>
    public string SizeHint =>
        "Un tamaño mayor cuesta tiempo, no memoria: con el VAE en tiles, 1024×1024 " +
        "usa apenas 216 MB más que 512×512, pero tarda unas tres veces más.";

    private void OnSizeChanged()
    {
        OnPropertyChanged(nameof(Orientation));
        OnPropertyChanged(nameof(Width));
        OnPropertyChanged(nameof(Height));
        OnPropertyChanged(nameof(SizeText));
        RefreshVerdict();
    }

    // -------------------------------------------------- parámetros de muestreo

    private int _steps = 8;
    public int Steps
    {
        get => _steps;
        set => SetProperty(ref _steps, Math.Clamp(value, 1, 100));
    }

    private double _cfgScale = 1.0;
    public double CfgScale
    {
        get => _cfgScale;
        set => SetProperty(ref _cfgScale, Math.Clamp(value, 0.0, 30.0));
    }

    /// <summary>
    /// Samplers y schedulers salen de <c>/capabilities</c>: de ahí vienen **las listas y
    /// los límites**. Los VALORES de generación —pasos y CFG— vienen del paquete y pisan
    /// a los del endpoint, que son los del motor (20 pasos, CFG 7.0) y en un Turbo dan
    /// una imagen mala.
    ///
    /// La primera entrada, vacía, deja elegir al motor lo que corresponda a la familia.
    /// </summary>
    public ObservableCollection<string> Samplers { get; } = new() { "" };
    public ObservableCollection<string> Schedulers { get; } = new() { "" };

    private string _selectedSampler = "";
    public string SelectedSampler
    {
        get => _selectedSampler;
        set => SetProperty(ref _selectedSampler, value ?? "");
    }

    private string _selectedScheduler = "";
    public string SelectedScheduler
    {
        get => _selectedScheduler;
        set => SetProperty(ref _selectedScheduler, value ?? "");
    }

    // ------------------------------------------------------------------ la semilla

    private bool _useRandomSeed = true;
    public bool UseRandomSeed
    {
        get => _useRandomSeed;
        set
        {
            if (SetProperty(ref _useRandomSeed, value)) OnPropertyChanged(nameof(CanEditSeed));
        }
    }

    /// <summary>
    /// El campo de semilla solo se edita cuando no es aleatoria. Se expone como
    /// propiedad en lugar de negar el bool con un converter porque los converters de
    /// este proyecto devuelven <c>Visibility</c>, no <c>bool</c>.
    /// </summary>
    public bool CanEditSeed => !UseRandomSeed;

    private long _seed = 42;
    public long Seed
    {
        get => _seed;
        set => SetProperty(ref _seed, value);
    }

    /// <summary>
    /// Sortea la semilla del lado de la aplicación en lugar de mandar -1.
    ///
    /// El motor acepta -1 y elige él, pero **no informa cuál usó**: la respuesta del job
    /// no trae el campo. Guardar -1 en la metadata sería prometer una reproducibilidad
    /// que no existe, así que el número lo pone la aplicación y viaja concreto.
    /// </summary>
    private long ResolverSemilla() => UseRandomSeed
        ? Random.Shared.NextInt64(0, int.MaxValue)
        : Math.Max(0, Seed);

    // ------------------------------------------------------------------ el paquete

    public ImageModelPackage? Package => _modelService.SelectedPackage;

    public bool HasPackage => Package is not null;

    public string PackageText
    {
        get
        {
            var p = Package;
            if (p is null)
                return "No hay ningún paquete de modelo seleccionado. Elegí uno en «Modelos de imagen».";
            return p.IsComplete
                ? $"{p.DisplayName} — {p.DefaultSteps} pasos, CFG {p.DefaultCfgScale:0.#}"
                : $"{p.DisplayName} — incompleto: {p.MissingText}";
        }
    }

    /// <summary>Identificador del paquete del que salieron los pasos y el CFG actuales.</summary>
    private string? _defaultsFromPackageId;

    /// <summary>
    /// Toma del paquete los valores de generación. Se llama al construir y cada vez que
    /// se recarga la selección, para que cambiar de paquete no deje los pasos del
    /// anterior puestos en silencio.
    /// </summary>
    public void ApplyPackageDefaults()
    {
        var p = Package;
        if (p is not null)
        {
            Steps = p.DefaultSteps;
            CfgScale = p.DefaultCfgScale;
        }
        _defaultsFromPackageId = p?.Id;

        OnPropertyChanged(nameof(Package));
        OnPropertyChanged(nameof(HasPackage));
        OnPropertyChanged(nameof(PackageText));
        OnPropertyChanged(nameof(CanGenerate));
        GenerateCommand.RaiseCanExecuteChanged();
    }

    // --------------------------------------------------------------- estado y GPU

    private bool _isGenerating;
    public bool IsGenerating
    {
        get => _isGenerating;
        private set
        {
            if (SetProperty(ref _isGenerating, value))
            {
                OnPropertyChanged(nameof(CanGenerate));
                GenerateCommand.RaiseCanExecuteChanged();
                CancelCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>
    /// Se puede generar si hay prompt, hay paquete completo y no hay otra en curso.
    /// Deshabilitar el botón mientras genera **es** la decisión 5.7: no se encola,
    /// porque no hay a dónde encolar.
    /// </summary>
    public bool CanGenerate =>
        !IsGenerating &&
        !string.IsNullOrWhiteSpace(Prompt) &&
        Package is { IsComplete: true };

    private double _progressPercent;
    public double ProgressPercent
    {
        get => _progressPercent;
        private set => SetProperty(ref _progressPercent, value);
    }

    private string _progressText = "";
    public string ProgressText
    {
        get => _progressText;
        private set => SetProperty(ref _progressText, value);
    }

    private string _statusMessage = "";
    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public string GpuText => _gpu.Available
        ? $"{_gpu.Name} — {_gpu.FreeMb / 1024.0:N1} GB libres de {_gpu.TotalMb / 1024.0:N1} GB"
        : _gpu.UnavailableReason ?? "VRAM desconocida";

    private ImageVramVerdict? _verdict;

    public string VramLabel => _verdict?.Label ?? "—";
    public string VramColor => _verdict?.Color ?? "#64748B";
    public string VramExplanation => _verdict?.Explanation ?? "";

    /// <summary>
    /// Reevalúa el semáforo con el tamaño y la configuración actuales. Es puro cálculo
    /// contra la última lectura de VRAM: no lanza nvidia-smi, así que puede llamarse
    /// desde el setter de cualquier propiedad sin costo.
    /// </summary>
    private void RefreshVerdict()
    {
        var p = Package;
        if (p is null) { _verdict = null; }
        else
        {
            var config = _activeConfig ?? p.ToServerConfig(_enginePathResolver());
            _verdict = ImageVramArbiter.Evaluate(
                new ImageGenRequest { Width = Width, Height = Height }, config, _gpu);
        }

        OnPropertyChanged(nameof(VramLabel));
        OnPropertyChanged(nameof(VramColor));
        OnPropertyChanged(nameof(VramExplanation));
    }

    // ------------------------------------------------------------------ la galería

    /// <summary>
    /// Lo ya generado en esta sesión. Tiene sentido aunque no haya cola: acumula
    /// resultados, que es otra cosa que una lista de pendientes.
    /// </summary>
    public ObservableCollection<GeneratedImageViewModel> Gallery { get; } = new();

    /// <summary>
    /// La tira de la galería se esconde mientras no haya nada. Sin esto queda una franja
    /// vacía con su encabezado ocupando lugar desde el arranque, que es lo que mostró la
    /// captura de la primera versión de esta vista.
    /// </summary>
    public bool HasImages => Gallery.Count > 0;

    private GeneratedImageViewModel? _currentImage;
    public GeneratedImageViewModel? CurrentImage
    {
        get => _currentImage;
        set
        {
            if (SetProperty(ref _currentImage, value))
                OnPropertyChanged(nameof(HasCurrentImage));
        }
    }

    public bool HasCurrentImage => CurrentImage is not null;

    // ------------------------------------------------------------------ comandos

    public AsyncRelayCommand GenerateCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand OpenFolderCommand { get; }
    public RelayCommand<GeneratedImageViewModel> OpenImageCommand { get; }

    // ------------------------------------------------------------------ ciclo de vida

    /// <summary>
    /// Consulta la GPU y prepara el estado inicial. Va acá y no en el constructor:
    /// nvidia-smi lanza un proceso, y hacerlo sincrónicamente en el hilo de UI es
    /// exactamente lo que dejó la ventana transparente en el defecto 4.10.
    ///
    /// **No arranca el motor.** Levantarlo cuesta 0,65 s y ocioso no consume VRAM, así
    /// que se hace en la primera generación: quien abre la vista para mirar la galería
    /// no tiene por qué pagar un proceso.
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        _gpu = await _gpuInfo.GetVramAsync(ct: ct).ConfigureAwait(true);
        OnPropertyChanged(nameof(GpuText));

        ApplyPackageDefaults();
        RefreshVerdict();

        if (!HasPackage)
        {
            StatusMessage = "Elegí un paquete de modelo de imagen para poder generar.";
        }
        else
        {
            // Se pregunta por la ruta que se va a USAR, no por la que descubriría el
            // localizador. No es lo mismo desde que la ruta del motor se puede configurar:
            // con una configurada a mano, `ImageEngineLocator.IsInstalled` sigue mirando
            // SD_RAIZ y Tools\sd\bin\, así que la vista avisaba «el motor no está
            // instalado» con el motor perfectamente instalado en otro lado. Lo encontró
            // abrir la aplicación de verdad: la generación andaba y el cartel mentía.
            var motor = _enginePathResolver();
            if (!File.Exists(motor))
            {
                StatusMessage = $"No se encontró el motor de imágenes en:\n{motor}\n\n" +
                                "Elegilo desde «Modelos de imagen» o descomprimí ahí " +
                                "stable-diffusion.cpp.";
            }
        }
    }

    /// <summary>
    /// Cierra la vista: cancela lo que esté en curso y baja el motor.
    ///
    /// El orden importa. Cancelar dispara <c>CancelAndRestartAsync</c>, que **vuelve a
    /// levantar** sd-server; si se detuviera el servidor sin esperar a que la generación
    /// termine de cancelarse, el relanzamiento ganaría la carrera y quedaría un proceso
    /// vivo después de cerrar. Por eso se espera la tarea antes de parar.
    ///
    /// Se deja el motor abajo en lugar de dejarlo ocioso: no consume VRAM, pero sí un
    /// proceso y su RAM, y volver a levantarlo cuesta 0,65 s.
    /// </summary>
    public async Task ShutdownAsync()
    {
        if (_shutdown) return;
        _shutdown = true;

        _generationCts?.Cancel();

        var enCurso = _generationTask;
        if (enCurso is not null)
        {
            try { await enCurso.ConfigureAwait(false); }
            catch (Exception) { /* ya se informó en el propio flujo */ }
        }

        try { await _manager.StopAsync().ConfigureAwait(false); }
        catch (Exception) { /* cerrar no debe fallar ruidosamente */ }
    }

    // ------------------------------------------------------------------ generación

    private async Task GenerateAsync()
    {
        if (!CanGenerate) return;

        var paquete = Package;
        if (paquete is null) return;

        // Si el paquete cambió desde afuera —el selector es otra ventana y no avisa—, los
        // pasos y el CFG que quedaron son los del paquete anterior. Usar el modelo nuevo
        // con los valores del viejo sale en la imagen y no da ningún error: un Turbo con
        // los 30 pasos de un SDXL, o al revés. Se rehacen antes de armar la solicitud.
        if (paquete.Id != _defaultsFromPackageId) ApplyPackageDefaults();

        var tarea = GenerarInternoAsync(paquete);
        _generationTask = tarea;
        try { await tarea.ConfigureAwait(true); }
        finally { _generationTask = null; }
    }

    private async Task GenerarInternoAsync(ImageModelPackage paquete)
    {
        IsGenerating = true;
        ProgressPercent = 0;
        ProgressText = "";
        StatusMessage = "";

        _generationCts?.Dispose();
        _generationCts = new CancellationTokenSource();
        var ct = _generationCts.Token;

        var cron = Stopwatch.StartNew();

        try
        {
            var config = paquete.ToServerConfig(_enginePathResolver());

            if (!await AsegurarMotorAsync(config, ct).ConfigureAwait(true))
                return;

            var semilla = ResolverSemilla();
            var request = paquete.NewRequest(Prompt);
            request.NegativePrompt = NegativePrompt;
            request.Width = Width;
            request.Height = Height;
            request.Steps = Steps;
            request.CfgScale = CfgScale;
            request.Seed = semilla;
            request.SampleMethod = string.IsNullOrWhiteSpace(SelectedSampler) ? null : SelectedSampler;
            request.Scheduler = string.IsNullOrWhiteSpace(SelectedScheduler) ? null : SelectedScheduler;

            // El semáforo se consulta acá aunque los presets no lleguen al tope: si el
            // modelo de lenguaje se comió la VRAM, conviene decirlo ANTES de arrancar
            // veinte segundos de trabajo que van a fallar.
            var veredicto = ImageVramArbiter.Evaluate(request, config, _gpu);
            if (veredicto.MustNotAttempt)
            {
                StatusMessage = veredicto.Explanation;
                return;
            }

            var carpetaSalida = _outputDirectoryResolver();
            Directory.CreateDirectory(carpetaSalida);
            var destino = ImageOutputFolder.BuildPath(carpetaSalida, semilla);

            StatusMessage = $"Generando {Width}×{Height} con semilla {semilla}…";

            var resultado = await _client.GenerateAsync(request, destino, ct).ConfigureAwait(true);
            cron.Stop();

            if (resultado.WasCancelled)
            {
                StatusMessage = "Generación cancelada.";
                return;
            }

            if (!resultado.Success || resultado.ImagePath is null)
            {
                StatusMessage = resultado.ErrorMessage ?? "No se pudo generar la imagen.";
                return;
            }

            var metadata = ImageGenMetadata.For(request, paquete, config, cron.Elapsed.TotalSeconds);
            var sidecar = await metadata.WriteAsync(resultado.ImagePath, CancellationToken.None)
                                        .ConfigureAwait(true);

            await AgregarALaGaleriaAsync(resultado.ImagePath, metadata).ConfigureAwait(true);

            // Cultura invariante, como el resto del log: tiene que poder parsearse sin
            // depender del locale. Fue el defecto 4.1 y no se repite.
            _auditLog?.Invoke("IMAGE_GENERATED", string.Format(
                CultureInfo.InvariantCulture,
                "file=\"{0}\" size={1}x{2} steps={3} cfg={4:F2} seed={5} modelo=\"{6}\" segundos={7:F1}",
                Path.GetFileName(resultado.ImagePath), request.Width, request.Height,
                request.Steps, request.CfgScale, semilla, paquete.Id, cron.Elapsed.TotalSeconds));

            StatusMessage = sidecar is null
                ? $"Imagen lista en {cron.Elapsed.TotalSeconds:N1} s. " +
                  "No se pudo escribir la metadata al lado del archivo."
                : $"Imagen lista en {cron.Elapsed.TotalSeconds:N1} s.";

            // La VRAM libre cambió: el motor cargó y liberó los pesos. Se vuelve a leer
            // para que el semáforo hable del estado real y no del de hace un minuto.
            _gpu = await _gpuInfo.GetVramAsync(forceRefresh: true, ct: CancellationToken.None)
                                 .ConfigureAwait(true);
            OnPropertyChanged(nameof(GpuText));
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Generación cancelada.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error inesperado al generar: {ex.Message}";
        }
        finally
        {
            IsGenerating = false;
            ProgressPercent = 0;
            ProgressText = "";
            RefreshVerdict();
        }
    }

    /// <summary>
    /// Deja el motor listo con el paquete pedido.
    ///
    /// Si ya está corriendo con OTRO modelo de difusión hay que pararlo primero: el
    /// manager reutiliza una instancia solo cuando el modelo coincide, y sin este paso
    /// el arranque devolvería «el puerto está ocupado» en vez de cambiar de modelo.
    /// </summary>
    private async Task<bool> AsegurarMotorAsync(ImageServerConfig config, CancellationToken ct)
    {
        bool cambióElModelo = _activeConfig is not null &&
            !string.Equals(_activeConfig.DiffusionModelPath, config.DiffusionModelPath,
                           StringComparison.OrdinalIgnoreCase);

        if (cambióElModelo)
        {
            StatusMessage = "Cambiando el modelo de imagen…";
            await _manager.StopAsync(ct).ConfigureAwait(true);
        }

        if (_manager.IsReady && !cambióElModelo)
        {
            _activeConfig = config;
            return true;
        }

        StatusMessage = "Iniciando el motor de imágenes…";

        var error = await _manager.StartAsync(config, ct).ConfigureAwait(true);
        if (error is not null)
        {
            StatusMessage = error;
            return false;
        }

        if (!await _manager.WaitForReadyAsync(TimeSpan.FromSeconds(60), ct).ConfigureAwait(true))
        {
            StatusMessage = "El motor de imágenes no llegó a responder. Revisá que sd-server " +
                            "pueda arrancar con este paquete.";
            return false;
        }

        _activeConfig = config;
        await CargarCapacidadesAsync(ct).ConfigureAwait(true);
        return true;
    }

    /// <summary>
    /// Puebla samplers y schedulers desde el motor. De <c>/capabilities</c> salen las
    /// LISTAS; los valores de generación ya vinieron del paquete y no se tocan acá.
    /// </summary>
    private async Task CargarCapacidadesAsync(CancellationToken ct)
    {
        var caps = await _manager.GetCapabilitiesAsync(_manager.BaseUrl, ct).ConfigureAwait(true);
        if (caps is null) return;

        // Se conserva lo que el usuario haya elegido: vaciar la colección desengancha el
        // SelectedItem del ComboBox y volvería a «el que elija el motor» sin avisar.
        var samplerElegido = SelectedSampler;
        var schedulerElegido = SelectedScheduler;

        Rellenar(Samplers, caps.Samplers);
        Rellenar(Schedulers, caps.Schedulers);

        SelectedSampler = Samplers.Contains(samplerElegido) ? samplerElegido : "";
        SelectedScheduler = Schedulers.Contains(schedulerElegido) ? schedulerElegido : "";

        static void Rellenar(ObservableCollection<string> destino, List<string> origen)
        {
            destino.Clear();
            destino.Add("");                       // el que elija el motor
            foreach (var v in origen)
                if (!string.IsNullOrWhiteSpace(v)) destino.Add(v);
        }
    }

    private void Cancel()
    {
        if (!IsGenerating) return;
        StatusMessage = "Cancelando: se detiene el motor y se vuelve a levantar…";
        _generationCts?.Cancel();
    }

    // ------------------------------------------------------------------ progreso

    /// <summary>
    /// El progreso llega desde el hilo que lee stdout de sd-server, no desde la API:
    /// la respuesta del job no tiene ningún campo de progreso. Hay que cruzar al hilo
    /// de UI antes de tocar nada bindeado.
    /// </summary>
    private void OnProgressReported(object? sender, ImageGenProgress p)
    {
        void Aplicar()
        {
            if (!IsGenerating) return;

            ProgressPercent = Math.Clamp(p.Fraction * 100, 0, 100);

            var restante = p.EstimatedSecondsRemaining;
            ProgressText = restante > 0
                ? $"Paso {p.Step} de {p.TotalSteps} — faltan ~{restante:N0} s"
                : $"Paso {p.Step} de {p.TotalSteps}";
        }

        if (_uiContext is null) Aplicar();
        else _uiContext.Post(_ => Aplicar(), null);
    }

    // ------------------------------------------------------------------ galería

    private async Task AgregarALaGaleriaAsync(string imagePath, ImageGenMetadata metadata)
    {
        // La decodificación va FUERA del hilo de UI y el resultado se congela, que es lo
        // que lo hace pasable entre hilos. Un PNG de 1024×1024 tarda poco acá y mucho en
        // otra máquina; la lección de 4.10 es no averiguarlo en producción.
        var (miniatura, vistaPrevia) = await Task.Run(() =>
            (CargarImagen(imagePath, 220), CargarImagen(imagePath, 900))).ConfigureAwait(true);

        var item = new GeneratedImageViewModel(imagePath, metadata, miniatura, vistaPrevia);
        Gallery.Insert(0, item);
        OnPropertyChanged(nameof(HasImages));
        CurrentImage = item;
    }

    /// <summary>
    /// Carga un PNG a un ancho dado. <c>OnLoad</c> no es opcional: sin él la imagen
    /// queda mapeada al archivo y el PNG no se puede borrar ni mover mientras la
    /// aplicación viva.
    /// </summary>
    private static ImageSource? CargarImagen(string path, int anchoDecodificado)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bmp.DecodePixelWidth = anchoDecodificado;
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void OpenOutputFolder()
    {
        try
        {
            var carpeta = _outputDirectoryResolver();
            Directory.CreateDirectory(carpeta);
            Process.Start(new ProcessStartInfo
            {
                FileName = carpeta,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            StatusMessage = $"No se pudo abrir la carpeta: {ex.Message}";
        }
    }

    private void OpenImage(GeneratedImageViewModel? item)
    {
        if (item is null) return;

        CurrentImage = item;

        try
        {
            if (!File.Exists(item.ImagePath))
            {
                StatusMessage = "El archivo ya no está en disco.";
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = item.ImagePath,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            StatusMessage = $"No se pudo abrir la imagen: {ex.Message}";
        }
    }

    /// <summary>
    /// Carpeta donde aterrizan las imágenes. La usa el banco de pruebas.
    /// Se resuelve al leerla, así que refleja un cambio hecho con la ventana abierta.
    /// </summary>
    public string OutputDirectory => _outputDirectoryResolver();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _manager.ProgressReported -= OnProgressReported;
        _generationCts?.Cancel();
        _generationCts?.Dispose();
        _client.Dispose();
        if (_ownsManager) _manager.Dispose();
    }
}
