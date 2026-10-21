using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PRImager.AIAssistant.Helpers;
using PRImager.AIAssistant.Services;
using PRImager.AIAssistant.Services.ImageGen;

namespace PRImager.AIAssistant.ViewModels.ImageGen;

/// <summary>
/// Una fila del selector. Un paquete incompleto no se oculta ni se borra: se muestra
/// en gris diciendo qué archivo falta, con el mismo criterio que el selector de
/// modelos del chat.
/// </summary>
public sealed class ImagePackageRow : ObservableObject
{
    public ImagePackageRow(ImageModelPackage package, GpuVramInfo gpu)
    {
        Package = package;
        Gpu = gpu;
    }

    public ImageModelPackage Package { get; }
    private GpuVramInfo Gpu { get; }

    public string Id => Package.Id;
    public string DisplayName => Package.DisplayName;
    public string Family => Package.Family;
    public bool IsComplete => Package.IsComplete;
    public bool IsFactory => Package.IsFactory;

    /// <summary>Lo que se dibuja en la columna de estado.</summary>
    public string StatusText => Package.IsComplete
        ? Loc.T("imagegen.packages.ready")
        : Package.MissingText;

    /// <summary>Gris para los incompletos, para que se lean como no utilizables.</summary>
    public string StatusColor => Package.IsComplete ? "#10B981" : "#6B7280";
    public string NameColor => Package.IsComplete ? "#E0E0E0" : "#6B7280";

    /// <summary>
    /// Tamaño en disco. Se cambia de unidad por debajo del giga: un paquete chico
    /// informado como "0,00 GB" parece vacío, y eso es exactamente lo que se veía en
    /// la ventana con archivos de prueba.
    /// </summary>
    public string SizeText => Package.IsComplete ? FormatearTamano(Package.TotalSizeBytes) : "—";

    /// <summary>Una sola forma de escribir un tamaño, para que la grilla y el detalle
    /// no se contradigan: la primera versión mostraba «192 KB» arriba y «0,00 GB» abajo.</summary>
    public static string FormatearTamano(long bytes)
    {
        double b = bytes;
        if (b >= 1024.0 * 1024 * 1024) return $"{b / 1024 / 1024 / 1024:N2} GB";
        if (b >= 1024.0 * 1024) return $"{b / 1024 / 1024:N0} MB";
        return $"{b / 1024:N0} KB";
    }

    public string ParamsText => Loc.F("imagegen.packages.params", Package.DefaultSteps, Package.DefaultCfgScale);

    /// <summary>
    /// Semáforo del paquete a 1024×1024 con las palancas de producción. Se evalúa el
    /// paquete COMPLETO, no solo el archivo de difusión.
    ///
    /// Con --offload-to-cpu los pesos viven en RAM, así que el tamaño de los tres
    /// archivos casi no mueve la VRAM: mueve la RAM. Por eso la fila informa las dos
    /// cosas por separado en lugar de sumar todo en un número que no significaría nada.
    /// </summary>
    private ImageVramVerdict Verdict => ImageVramArbiter.Evaluate(
        new ImageGenRequest { Width = 1024, Height = 1024 },
        Package.ToServerConfig("sd-server.exe"),
        Gpu);

    public string VramLabel => Package.IsComplete ? Verdict.Label : "—";
    public string VramColor => Package.IsComplete ? Verdict.Color : "#6B7280";

    public string RamText => Package.IsComplete
        ? Loc.F("imagegen.packages.ram", FormatearTamano(Package.TotalSizeBytes))
        : "—";

    public string Tooltip => Package.IsComplete
        ? Verdict.Explanation + "\n\n" + Loc.T("imagegen.packages.ramNote")
        : Loc.T("imagegen.packages.missingFiles") + "\n" +
          string.Join("\n", Package.MissingComponents.Select(c => "  · " + ImageModelPackage.RoleText(c)));
}

/// <summary>
/// Selector de paquetes de modelo de imagen (E4.3).
///
/// Tres campos, uno por archivo, con «Examinar…» y validación de existencia, más la
/// descarga del paquete recomendado. Los paquetes conocidos se persisten, así que se
/// configuran una vez.
/// </summary>
public sealed class ImagePackageSelectorViewModel : ObservableObject
{
    private readonly ImageModelService _service;
    private readonly IFileDialogService _dialogs;
    private readonly GpuInfoService _gpuInfo;
    private readonly Func<ImagePackageDownloader> _downloaderFactory;

    private GpuVramInfo _gpu = GpuVramInfo.Unavailable(Loc.T("imagegen.gpuNotQueried"));
    private CancellationTokenSource? _downloadCts;

    public ImagePackageSelectorViewModel(
        ImageModelService service,
        IFileDialogService dialogs,
        GpuInfoService? gpuInfo = null,
        Func<ImagePackageDownloader>? downloaderFactory = null)
    {
        _service = service;
        _dialogs = dialogs;
        _gpuInfo = gpuInfo ?? new GpuInfoService();
        _downloaderFactory = downloaderFactory ?? (() => new ImagePackageDownloader());

        BrowseEngineCommand = new RelayCommand(BrowseEngine);
        BrowseDiffusionCommand = new RelayCommand(() => Browse("modelo de difusión"));
        BrowseTextEncoderCommand = new RelayCommand(() => Browse("text encoder"));
        BrowseVaeCommand = new RelayCommand(() => Browse("VAE"));
        UseSelectedCommand = new RelayCommand(UseSelected, () => SelectedRow?.IsComplete == true);
        DownloadRecommendedCommand = new AsyncRelayCommand(DownloadRecommendedAsync, () => !IsDownloading);
        CancelDownloadCommand = new RelayCommand(CancelDownload, () => IsDownloading);

        Refresh();
    }

    // --- Colecciones y selección ---

    public ObservableCollection<ImagePackageRow> Packages { get; } = new();

    private ImagePackageRow? _selectedRow;
    public ImagePackageRow? SelectedRow
    {
        get => _selectedRow;
        set
        {
            if (SetProperty(ref _selectedRow, value))
            {
                OnPropertyChanged(nameof(DiffusionPath));
                OnPropertyChanged(nameof(TextEncoderPath));
                OnPropertyChanged(nameof(VaePath));
                OnPropertyChanged(nameof(SelectedDetail));
                UseSelectedCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>Identificador del paquete efectivamente en uso.</summary>
    public string? ActivePackageId => _service.SelectedPackageId;

    public string ActivePackageText
    {
        get
        {
            var p = _service.SelectedPackage;
            return p is null
                ? Loc.T("imagegen.packages.noneSelected")
                : Loc.F("imagegen.packages.inUse", p.DisplayName);
        }
    }

    // --- Las tres rutas del paquete seleccionado ---

    public string DiffusionPath => SelectedRow?.Package.DiffusionModelPath ?? "";
    public string TextEncoderPath => SelectedRow?.Package.TextEncoderPath ?? "";
    public string VaePath => SelectedRow?.Package.VaePath ?? "";

    public string SelectedDetail
    {
        get
        {
            if (SelectedRow is null) return "";
            var p = SelectedRow.Package;
            return p.IsComplete
                ? Loc.F("imagegen.packages.detail", p.DisplayName, ImagePackageRow.FormatearTamano(p.TotalSizeBytes), p.DefaultSteps, p.DefaultCfgScale)
                : Loc.F("imagegen.package.incomplete", p.DisplayName, p.MissingText);
        }
    }

    // --- El motor ---

    /// <summary>
    /// Ruta efectiva de sd-server.exe. Se muestra siempre, esté configurada a mano o
    /// descubierta sola, para que el usuario vea contra qué binario va a generar — el
    /// mismo criterio con el que E2 muestra la URL de SearXNG en un TextBox y no en un
    /// PasswordBox.
    /// </summary>
    public string EnginePath => _service.ResolveEnginePath();

    public string EngineStatusText => File.Exists(EnginePath)
        ? Loc.T("imagegen.engine.found")
        : Loc.T("imagegen.engine.notFound");

    public string EngineStatusColor => File.Exists(EnginePath) ? "#10B981" : "#F59E0B";

    private void BrowseEngine()
    {
        var elegido = _dialogs.OpenFiles(Loc.T("imagegen.engine.pickTitle"), Loc.T("imagegen.engine.pickFilter"));

        if (elegido is not { Length: > 0 }) return;

        _service.EngineExecutablePath = elegido[0];
        OnPropertyChanged(nameof(EnginePath));
        OnPropertyChanged(nameof(EngineStatusText));
        OnPropertyChanged(nameof(EngineStatusColor));
    }

    // --- Estado de la GPU ---

    public string GpuText => _gpu.Available
        ? Loc.F("imagegen.gpu", _gpu.Name, _gpu.FreeMb / 1024.0, _gpu.TotalMb / 1024.0)
        : _gpu.UnavailableReason ?? Loc.T("imagegen.vramUnknown");

    // --- Descarga ---

    private bool _isDownloading;
    public bool IsDownloading
    {
        get => _isDownloading;
        private set
        {
            if (SetProperty(ref _isDownloading, value))
            {
                DownloadRecommendedCommand.RaiseCanExecuteChanged();
                CancelDownloadCommand.RaiseCanExecuteChanged();
            }
        }
    }

    private string _downloadStatus = "";
    public string DownloadStatus
    {
        get => _downloadStatus;
        private set => SetProperty(ref _downloadStatus, value);
    }

    private double _downloadPercent;
    public double DownloadPercent
    {
        get => _downloadPercent;
        private set => SetProperty(ref _downloadPercent, value);
    }

    /// <summary>Tamaño del paquete recomendado, para que se vea antes de bajarlo.</summary>
    public string RecommendedSizeText =>
        $"{ImageModelService.RecommendedSource.TotalExpectedBytes / 1024.0 / 1024.0 / 1024.0:N1} GB";

    private string _message = "";
    public string Message
    {
        get => _message;
        private set => SetProperty(ref _message, value);
    }

    // --- Comandos ---

    public RelayCommand BrowseEngineCommand { get; }
    public RelayCommand BrowseDiffusionCommand { get; }
    public RelayCommand BrowseTextEncoderCommand { get; }
    public RelayCommand BrowseVaeCommand { get; }
    public RelayCommand UseSelectedCommand { get; }
    public AsyncRelayCommand DownloadRecommendedCommand { get; }
    public RelayCommand CancelDownloadCommand { get; }

    // --- Operaciones ---

    /// <summary>
    /// Consulta la GPU y arma las filas. Se llama al cargar la ventana, no desde el
    /// constructor: `nvidia-smi` lanza un proceso, y hacerlo de forma síncrona en el
    /// constructor bloquearía el hilo de UI. Es la lección del defecto 4.10 —lo que
    /// tarda poco en una máquina tarda mucho en otra— y sale barato respetarla.
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        _gpu = await _gpuInfo.GetVramAsync(ct: ct).ConfigureAwait(true);
        OnPropertyChanged(nameof(GpuText));
        Refresh();
    }

    public void Refresh()
    {
        var idSeleccionado = SelectedRow?.Id ?? _service.SelectedPackageId;

        Packages.Clear();
        foreach (var p in _service.Packages)
            Packages.Add(new ImagePackageRow(p, _gpu));

        SelectedRow = Packages.FirstOrDefault(r => r.Id == idSeleccionado) ?? Packages.FirstOrDefault();

        OnPropertyChanged(nameof(ActivePackageText));
        OnPropertyChanged(nameof(ActivePackageId));
    }

    /// <summary>
    /// Cambia una de las tres rutas del paquete seleccionado. Se persiste enseguida:
    /// configurar el paquete es de una vez, y perderlo al cerrar la ventana sería
    /// exactamente lo que 3.2 quiere evitar.
    /// </summary>
    public void SetComponentPath(string role, string path)
    {
        if (SelectedRow is null || string.IsNullOrWhiteSpace(path)) return;

        var p = SelectedRow.Package;
        var actualizado = role switch
        {
            "modelo de difusión" => p with { DiffusionModelPath = path },
            "text encoder" => p with { TextEncoderPath = path },
            "VAE" => p with { VaePath = path },
            _ => p
        };

        _service.Save(actualizado);
        Refresh();
    }

    private void Browse(string role)
    {
        var filtro = role == "VAE"
            ? Loc.T("imagegen.packages.vaeFilter")
            : Loc.T("imagegen.packages.ggufFilter");

        var elegido = _dialogs.OpenFiles(Loc.F("imagegen.packages.pickRole", ImageModelPackage.RoleText(role)), filtro);
        if (elegido is { Length: > 0 })
            SetComponentPath(role, elegido[0]);
    }

    private void UseSelected()
    {
        if (SelectedRow is null) return;

        if (!_service.Select(SelectedRow.Id))
        {
            Message = Loc.F("imagegen.packages.cantUseIncomplete", SelectedRow.Package.MissingText);
            return;
        }

        Message = Loc.F("imagegen.packages.nowInUse", SelectedRow.DisplayName);
        OnPropertyChanged(nameof(ActivePackageText));
        OnPropertyChanged(nameof(ActivePackageId));
    }

    private async Task DownloadRecommendedAsync()
    {
        IsDownloading = true;
        Message = "";
        DownloadPercent = 0;
        DownloadStatus = Loc.T("imagegen.packages.preparing");

        _downloadCts = new CancellationTokenSource();
        using var descargador = _downloaderFactory();

        descargador.ProgressChanged += (_, p) =>
        {
            DownloadPercent = p.PackagePercent;
            DownloadStatus = p.Text;
        };

        try
        {
            // La carpeta efectiva, no la de fábrica: si el usuario la cambió desde la
            // ventana de carpetas, el paquete recomendado tiene que bajar ahí.
            var carpeta = _service.ResolveModelsDirectory();
            var r = await descargador.DownloadAsync(
                ImageModelService.RecommendedSource, carpeta, _downloadCts.Token);

            if (r.WasCancelled)
            {
                Message = Loc.T("imagegen.packages.cancelled");
            }
            else if (!r.Success)
            {
                Message = r.ErrorMessage ?? Loc.T("imagegen.packages.downloadFailed");
            }
            else
            {
                _service.Save(r.Package!);
                _service.Select(r.Package!.Id);
                Message = r.AlreadyPresent.Count > 0
                    ? Loc.F("imagegen.packages.readyPartial", string.Join(", ", r.AlreadyPresent))
                    : Loc.T("imagegen.packages.downloaded");
                Refresh();
            }
        }
        catch (Exception ex)
        {
            Message = Loc.F("imagegen.packages.unexpected", ex.Message);
        }
        finally
        {
            IsDownloading = false;
            DownloadStatus = "";
            DownloadPercent = 0;
            _downloadCts?.Dispose();
            _downloadCts = null;
            Refresh();
        }
    }

    private void CancelDownload()
    {
        _downloadCts?.Cancel();
        DownloadStatus = "Cancelando…";
    }
}
