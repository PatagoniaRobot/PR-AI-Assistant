using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using PRImager.AIAssistant.Helpers;
using PRImager.AIAssistant.Localization;
using PRImager.AIAssistant.Services;
using PRImager.AIAssistant.Services.HuggingFace;

namespace PRImager.AIAssistant.ViewModels.HuggingFace;

/// <summary>
/// Item de la tabla de cuantizaciones: el archivo más su semáforo de VRAM.
/// Un archivo que no es un modelo no lleva semáforo: lleva qué es.
/// </summary>
public class QuantizationItem : ObservableObject
{
    private const string NotModelColor = "#64748B";

    public HfFile File { get; }
    public VramEstimate Vram { get; }

    /// <summary>Qué es el archivo según su nombre. La cabecera se mira al pedir la descarga.</summary>
    public GgufVerdict Verdict { get; }

    public string FileName => File.FileName;
    public string Quantization => string.IsNullOrEmpty(File.Quantization) ? "—" : File.Quantization;
    public string SizeText => File.SizeText;

    /// <summary>
    /// Cómo correría en esta PC, calculado con la cabecera del modelo (E5). Null si no se
    /// pudo leer: entonces manda el semáforo por tamaño de <see cref="Vram"/>.
    /// </summary>
    public RunRecommendation? Run { get; }

    /// <summary>La cuantización que se recomienda para esta PC entre las del repositorio.</summary>
    public bool IsRecommended { get; }

    public string VramLabel => Verdict.Kind switch
    {
        // La estrella de la recomendada la dibuja la vista como ícono; acá solo el texto
        GgufKind.Model when Run != null => (IsRecommended ? Loc.T("catalog.recommended") + " · " : "") + RunLabel(Run),
        GgufKind.Model => Vram.Label,
        GgufKind.Projector => Loc.T("catalog.kind.projector"),
        GgufKind.SplitPart => Loc.T("catalog.kind.splitPart"),
        _ => Loc.T("catalog.kind.notModel")
    };

    public string VramColor => !Verdict.IsModel ? NotModelColor
        : Run == null ? Vram.Color
        : !Run.Fits ? "#EF4444"
        : Run.FullyOnGpu ? "#10B981"
        : Run.CpuOnly ? "#F97316"
        : "#F59E0B";

    public string VramExplanation => !Verdict.IsModel ? Verdict.Reason
        : Run == null ? Vram.Explanation
        : Run.Explanation +
          (Run.Fits ? " " + Loc.F("catalog.run.params", ModelRecommender.K(Run.ContextTokens), GpuText(Run), Run.Threads) : "") +
          (ModelRecommender.IsLowQuality(File.Quantization) ? " " + Loc.T("catalog.run.lowQuality") : "");

    /// <summary>True si no entra en la PC ni repartiendo: la descarga se confirma antes.</summary>
    public bool DoesNotFit => Run != null ? !Run.Fits : Vram.Fit == VramFit.Red;

    private static string RunLabel(RunRecommendation r) =>
        !r.Fits ? Loc.T("catalog.fit.no") : r.FullyOnGpu ? Loc.T("catalog.fit.full") : r.CpuOnly ? Loc.T("catalog.fit.cpu") : Loc.T("catalog.fit.split");

    private static string GpuText(RunRecommendation r) =>
        r.FullyOnGpu ? Loc.T("catalog.gpu.all") : r.CpuOnly ? Loc.T("catalog.gpu.none") : Loc.F("catalog.gpu.some", r.GpuLayers);

    /// <summary>
    /// Se puede bajar un modelo o un proyector, que la aplicación usa junto al modelo.
    /// Lo demás no carga y bajarlo es tirar la descarga.
    /// </summary>
    public bool CanDownload => Verdict.Kind is GgufKind.Model or GgufKind.Projector;

    private bool _isDownloading;
    public bool IsDownloading
    {
        get => _isDownloading;
        set => SetProperty(ref _isDownloading, value);
    }

    public QuantizationItem(HfFile file, VramEstimate vram, RunRecommendation? run = null, bool isRecommended = false)
    {
        File = file;
        Vram = vram;
        Verdict = GgufInspector.FromFileName(file.FileName);
        Run = Verdict.IsModel ? run : null;
        IsRecommended = isRecommended && Run != null;
    }

    /// <summary>Las filas del DataGrid se anuncian con esto en accesibilidad.</summary>
    public override string ToString() => FileName;
}

/// <summary>
/// ViewModel del catálogo de modelos de HuggingFace: búsqueda, evaluación
/// contra la VRAM real y descarga.
/// </summary>
public class ModelCatalogViewModel : ObservableObject, IDisposable
{
    private readonly HuggingFaceClient _client;
    private readonly ModelDownloader _downloader;
    private readonly GpuInfoService _gpuInfo;
    private readonly HardwareInfoService _hardware;
    private HardwareInfo? _hw;
    private readonly ModelDownloadLog _downloadLog;
    private readonly DownloadedModelStore? _store;
    private readonly string _destinationDirectory;
    private readonly int _contextTokens;
    private readonly Action<string>? _onModelDownloaded;
    private readonly Action<ModelDeletionRecord>? _onModelDeleted;
    private readonly Func<DownloadedModelInfo, bool> _confirmDeletion;

    private CancellationTokenSource? _downloadCts;
    private GpuVramInfo? _vram;

    public ObservableCollection<HfRepo> Repos { get; } = new();
    public ObservableCollection<QuantizationItem> Quantizations { get; } = new();

    /// <summary>Modelos ya descargados, para poder eliminarlos y recuperar disco.</summary>
    public ObservableCollection<DownloadedModelInfo> DownloadedModels { get; } = new();

    private string _query = "";
    public string Query
    {
        get => _query;
        set
        {
            if (SetProperty(ref _query, value))
                ((AsyncRelayCommand)SearchCommand).RaiseCanExecuteChanged();
        }
    }

    private HfRepo? _selectedRepo;
    public HfRepo? SelectedRepo
    {
        get => _selectedRepo;
        set
        {
            if (SetProperty(ref _selectedRepo, value) && value != null)
                _ = LoadQuantizationsAsync(value);
        }
    }

    private QuantizationItem? _selectedQuantization;
    public QuantizationItem? SelectedQuantization
    {
        get => _selectedQuantization;
        set
        {
            if (SetProperty(ref _selectedQuantization, value))
            {
                OnPropertyChanged(nameof(SelectedVramExplanation));
                ((AsyncRelayCommand)DownloadCommand).RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>Explicación del semáforo del archivo seleccionado.</summary>
    public string SelectedVramExplanation => _selectedQuantization?.VramExplanation ?? "";

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                ((AsyncRelayCommand)SearchCommand).RaiseCanExecuteChanged();
                ((AsyncRelayCommand)DownloadCommand).RaiseCanExecuteChanged();
            }
        }
    }

    private string _status = "";
    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    private bool _isError;
    /// <summary>True si Status describe un fallo (para colorearlo en la UI).</summary>
    public bool IsError
    {
        get => _isError;
        private set => SetProperty(ref _isError, value);
    }

    private string _gpuText = Loc.T("catalog.detectingGpu");
    public string GpuText
    {
        get => _gpuText;
        private set => SetProperty(ref _gpuText, value);
    }

    // --- Estado de descarga ---

    private bool _isDownloading;
    public bool IsDownloading
    {
        get => _isDownloading;
        private set
        {
            if (SetProperty(ref _isDownloading, value))
            {
                OnPropertyChanged(nameof(CanEditSearch));
                ((RelayCommand)CancelDownloadCommand).RaiseCanExecuteChanged();
                ((AsyncRelayCommand)DownloadCommand).RaiseCanExecuteChanged();
                // Mientras baja un modelo no se borra ninguno: el disco está en uso
                ((RelayCommand)DeleteDownloadedCommand).RaiseCanExecuteChanged();
                ((RelayCommand)CleanPartialsCommand).RaiseCanExecuteChanged();
            }
        }
    }

    public bool CanEditSearch => !IsDownloading;

    private double _downloadPercent;
    public double DownloadPercent
    {
        get => _downloadPercent;
        private set => SetProperty(ref _downloadPercent, value);
    }

    private string _downloadText = "";
    public string DownloadText
    {
        get => _downloadText;
        private set => SetProperty(ref _downloadText, value);
    }

    private string _lastHash = "";
    /// <summary>SHA-256 de la última descarga completada.</summary>
    public string LastHash
    {
        get => _lastHash;
        private set
        {
            if (SetProperty(ref _lastHash, value))
                OnPropertyChanged(nameof(HasLastHash));
        }
    }

    public bool HasLastHash => !string.IsNullOrEmpty(_lastHash);

    // --- Modelos descargados ---

    private DownloadedModelInfo? _selectedDownloaded;
    public DownloadedModelInfo? SelectedDownloaded
    {
        get => _selectedDownloaded;
        set
        {
            if (SetProperty(ref _selectedDownloaded, value))
            {
                OnPropertyChanged(nameof(SelectedDownloadedHint));
                ((RelayCommand)DeleteDownloadedCommand).RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>Explica por qué un modelo no se puede eliminar, cuando corresponde.</summary>
    public string SelectedDownloadedHint => _selectedDownloaded?.BlockedReason ?? "";

    private string _downloadedSummary = "";
    /// <summary>Cuántos modelos descargados hay y cuánto disco ocupan.</summary>
    public string DownloadedSummary
    {
        get => _downloadedSummary;
        private set => SetProperty(ref _downloadedSummary, value);
    }

    public ICommand SearchCommand { get; }
    public ICommand DownloadCommand { get; }
    public ICommand CancelDownloadCommand { get; }
    public ICommand DeleteDownloadedCommand { get; }
    public ICommand CleanPartialsCommand { get; }
    public ICommand CloseCommand { get; }

    public event EventHandler? CloseRequested;

    /// <param name="store">
    /// Gestión de los modelos ya descargados. Null deja la sección vacía, que es lo
    /// que corresponde cuando no hay carpeta de descargas que administrar.
    /// </param>
    /// <param name="onModelDeleted">Refresca el selector de modelos tras una baja.</param>
    /// <param name="confirmDeletion">
    /// Confirmación del usuario. Se inyecta para poder ejercer el borrado sin abrir
    /// un MessageBox, que en una corrida sin interacción bloquearía para siempre.
    /// </param>
    public ModelCatalogViewModel(
        string destinationDirectory,
        int contextTokens,
        string logDirectory,
        Func<string>? tokenProvider = null,
        Action<string>? onModelDownloaded = null,
        HuggingFaceClient? client = null,
        ModelDownloader? downloader = null,
        GpuInfoService? gpuInfo = null,
        DownloadedModelStore? store = null,
        Action<ModelDeletionRecord>? onModelDeleted = null,
        Func<DownloadedModelInfo, bool>? confirmDeletion = null,
        HardwareInfoService? hardware = null)
    {
        _destinationDirectory = destinationDirectory;
        _contextTokens = contextTokens;
        _downloadLog = new ModelDownloadLog(logDirectory);
        _onModelDownloaded = onModelDownloaded;
        _store = store;
        _onModelDeleted = onModelDeleted;
        _confirmDeletion = confirmDeletion ?? ConfirmDeletionWithDialog;

        _client = client ?? new HuggingFaceClient(tokenProvider: tokenProvider);
        _downloader = downloader ?? new ModelDownloader(tokenProvider: tokenProvider);
        _gpuInfo = gpuInfo ?? new GpuInfoService();
        // Sin carpeta del motor de chat, la de la instalación: Tools\ junto al ejecutable
        _hardware = hardware ?? new HardwareInfoService(
            System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Tools"), _gpuInfo);

        _downloader.ProgressChanged += OnDownloadProgress;

        SearchCommand = new AsyncRelayCommand(ExecuteSearchAsync,
            () => !IsBusy && !IsDownloading && !string.IsNullOrWhiteSpace(Query));
        DownloadCommand = new AsyncRelayCommand(ExecuteDownloadAsync,
            () => !IsBusy && !IsDownloading && _selectedQuantization is { CanDownload: true });
        CancelDownloadCommand = new RelayCommand(ExecuteCancelDownload, () => IsDownloading);
        DeleteDownloadedCommand = new RelayCommand(ExecuteDeleteDownloaded,
            () => !IsDownloading && _selectedDownloaded != null && _selectedDownloaded.CanDelete);
        CleanPartialsCommand = new RelayCommand(ExecuteCleanPartials, () => !IsDownloading && _store != null);
        CloseCommand = new RelayCommand(() => CloseRequested?.Invoke(this, EventArgs.Empty));

        RefreshDownloaded();
        _ = InitializeGpuAsync();
    }

    /// <summary>
    /// Relee la carpeta de descargas. Se llama al abrir la ventana y después de cada
    /// alta o baja, para que la lista no quede describiendo un disco que ya cambió.
    /// </summary>
    public void RefreshDownloaded()
    {
        DownloadedModels.Clear();
        SelectedDownloaded = null;

        if (_store == null)
        {
            DownloadedSummary = "";
            return;
        }

        var models = _store.List();
        foreach (var m in models)
            DownloadedModels.Add(m);

        if (models.Count == 0)
        {
            DownloadedSummary = Loc.T("catalog.downloaded.none");
            return;
        }

        string total = DownloadedModelInfo.FormatSize(models.Sum(m => m.SizeBytes));
        DownloadedSummary = Loc.P("catalog.downloaded.summary", models.Count, total, _store.DirectoryText);
    }

    /// <summary>
    /// La PC, de cualquier marca (E5). Antes la línea salía de nvidia-smi y en una PC sin
    /// NVIDIA decía «VRAM no detectada» y nada más.
    /// </summary>
    private async Task InitializeGpuAsync()
    {
        _hw = await _hardware.GetAsync();
        _vram = await _gpuInfo.GetVramAsync();
        GpuText = Loc.F("hardware.yourPc", _hw.Summary);
    }

    private async Task ExecuteSearchAsync()
    {
        IsBusy = true;
        IsError = false;
        Status = Loc.T("catalog.searching");
        Repos.Clear();
        Quantizations.Clear();

        var result = await _client.SearchAsync(Query.Trim());

        if (!result.Success)
        {
            IsError = true;
            Status = result.ErrorMessage ?? Loc.T("catalog.searchFailed");
            IsBusy = false;
            return;
        }

        foreach (var r in result.Value!)
            Repos.Add(r);

        Status = Repos.Count == 0
            ? Loc.F("catalog.noResults", Query.Trim())
            : Loc.P("catalog.reposFound", Repos.Count);
        IsBusy = false;
    }

    private async Task LoadQuantizationsAsync(HfRepo repo)
    {
        IsBusy = true;
        IsError = false;
        Status = Loc.F("catalog.loadingRepo", repo.Id);
        Quantizations.Clear();
        SelectedQuantization = null;

        var detail = await _client.GetRepoDetailAsync(repo.Id);

        if (!detail.Success)
        {
            IsError = true;
            // Un repo gated se informa tal cual: no se esconde el fallo
            Status = detail.ErrorMessage ?? Loc.T("catalog.repoFailed");
            IsBusy = false;
            return;
        }

        _vram ??= await _gpuInfo.GetVramAsync();
        _hw ??= await _hardware.GetAsync();

        var files = detail.Value!.GgufFiles;
        var (shape, probedBytes) = await ProbeShapeAsync(repo.Id, files);

        // Con la cabecera de una de las cuantizaciones se calculan todas: la arquitectura
        // es la misma, cambia el tamaño de los pesos
        var runs = new RunRecommendation?[files.Count];
        int recommended = -1;
        if (shape != null)
        {
            var models = files.Select((f, i) => (f, i))
                              .Where(x => GgufInspector.FromFileName(x.f.FileName).IsModel && x.f.SizeBytes > 0)
                              .ToList();
            foreach (var (f, i) in models)
                runs[i] = ModelRecommender.RecommendRun(shape, f.SizeBytes, _hw, probedBytes);

            int pick = ModelRecommender.PickQuantization(
                models.Select(x => (x.f.Quantization, x.f.SizeBytes)).ToList(), shape, probedBytes, _hw);
            if (pick >= 0) recommended = models[pick].i;
        }

        for (int i = 0; i < files.Count; i++)
        {
            var estimate = VramEstimator.Estimate(files[i].SizeBytes, _contextTokens, _vram);
            Quantizations.Add(new QuantizationItem(files[i], estimate, runs[i], i == recommended));
        }

        int notModels = Quantizations.Count(q => !q.Verdict.IsModel);
        string counts = notModels == 0
            ? Cuantizaciones(Quantizations.Count) + "."
            : Cuantizaciones(Quantizations.Count - notModels) + " " + Loc.P("catalog.notModelFiles", notModels);
        var best = recommended >= 0 ? Quantizations[recommended] : null;
        // La recomendada queda elegida: su explicación a la vista y el botón listo
        if (best != null) SelectedQuantization = best;
        Status = Quantizations.Count == 0
            ? Loc.T("catalog.noGguf")
            : best != null
                ? Loc.F("catalog.recommendedForPc", best.Quantization, best.Run!.Summary, counts)
                : shape != null
                    ? Loc.F("catalog.noneFits", counts)
                    : Loc.F("catalog.noHeader", counts);

        if (detail.Value.Repo.IsGated)
        {
            IsError = true;
            Status = Loc.T("catalog.gatedPrefix") + " " + HuggingFaceClient.GatedMessage();
        }

        IsBusy = false;
    }

    private static string Cuantizaciones(int n) => Loc.P("catalog.quantizations", n);

    /// <summary>
    /// Lee la cabecera de una de las cuantizaciones —la más chica, da igual cuál: la
    /// arquitectura es la misma— para calcular la memoria de todas. Lee hasta la tabla de
    /// tensores, que dice qué parte de los pesos queda en la RAM: sin ella, en Gemma 4 se
    /// suponía ~1,8 GB de más en la placa. Las demás cuantizaciones se escalan por tamaño.
    /// </summary>
    private async Task<(GgufHeader? shape, long probedBytes)> ProbeShapeAsync(string repoId, System.Collections.Generic.IList<HfFile> files)
    {
        // La Q4_K_M, o la del medio: el escalado por tamaño es más fiel cerca de la que se
        // mide, y las recomendaciones caen casi siempre por ahí. Pesa lo mismo leer
        // cualquiera: lo que se baja es la metadata, no los pesos.
        var models = files.Where(f => f.SizeBytes > 0 && GgufInspector.FromFileName(f.FileName).IsModel)
                          .OrderBy(f => f.SizeBytes)
                          .ToList();
        var probe = models.FirstOrDefault(f => f.Quantization.EndsWith("Q4_K_M", StringComparison.OrdinalIgnoreCase))
                    ?? (models.Count > 0 ? models[models.Count / 2] : null);
        if (probe == null) return (null, 0);

        Status = Loc.F("catalog.probing", repoId);
        var r = await _client.ProbeGgufAsync(repoId, probe.FileName, fileLength: probe.SizeBytes);
        if (!r.Success || r.Value!.BlockCount is null || !GgufInspector.FromHeader(r.Value).IsModel)
            return (null, 0);
        return (r.Value, probe.SizeBytes);
    }

    private async Task ExecuteDownloadAsync()
    {
        if (_selectedQuantization == null || _selectedRepo == null) return;

        var item = _selectedQuantization;
        if (!item.CanDownload) return;

        // El nombre no alcanza: nada obliga a que una cabeza de borrador diga «draft».
        // La cabecera son 64 KB y dice qué es el archivo antes de bajar varios GB.
        // Si no se puede leer, se sigue: ante la duda no se bloquea, y si el archivo no
        // carga, el error de carga ya dice por qué (4.21).
        if (item.Verdict.IsModel)
        {
            IsBusy = true;
            IsError = false;
            Status = Loc.F("catalog.verifying", item.FileName);
            var probe = await _client.ProbeGgufAsync(_selectedRepo.Id, item.FileName);
            IsBusy = false;

            if (probe.Success)
            {
                var verdict = GgufInspector.FromHeader(probe.Value!);
                if (!verdict.IsModel)
                {
                    IsError = true;
                    Status = Loc.F("catalog.notDownloading", item.FileName, verdict.Reason);
                    return;
                }
            }
            Status = "";
        }

        if (item.Verdict.IsModel && item.DoesNotFit)
        {
            var confirm = System.Windows.MessageBox.Show(
                Loc.F("catalog.confirmNoFit.message", item.VramExplanation),
                Loc.T("catalog.confirmNoFit.title"),
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Warning);

            if (confirm != System.Windows.MessageBoxResult.Yes) return;
        }

        _downloadCts = new CancellationTokenSource();
        IsDownloading = true;
        item.IsDownloading = true;
        IsError = false;
        LastHash = "";
        DownloadPercent = 0;
        DownloadText = Loc.T("catalog.download.starting");
        Status = Loc.F("catalog.download.running", item.FileName);

        var result = await _downloader.DownloadAsync(
            _selectedRepo.Id, item.FileName, _destinationDirectory, _downloadCts.Token);

        item.IsDownloading = false;
        IsDownloading = false;
        _downloadCts.Dispose();
        _downloadCts = null;

        if (!result.Success)
        {
            IsError = true;
            Status = result.ErrorMessage ?? Loc.T("catalog.download.failed");
            DownloadText = "";
            DownloadPercent = 0;
            return;
        }

        var record = result.Value!;
        _downloadLog.Record(record);

        LastHash = record.Sha256;
        DownloadPercent = 100;
        DownloadText = "";
        Status = Loc.F("catalog.download.done", record.FileName, _destinationDirectory);

        // El modelo recién bajado tiene que aparecer en la lista de descargados,
        // que es desde donde se lo puede volver a eliminar
        RefreshDownloaded();

        // Refrescar el catálogo local para que el modelo quede usable sin pasos manuales
        _onModelDownloaded?.Invoke(record.DestinationPath);
    }

    private void ExecuteCancelDownload()
    {
        _downloadCts?.Cancel();
        Status = Loc.T("catalog.cancelling");
    }

    /// <summary>
    /// Confirmación por defecto. El tamaño va en el texto porque el motivo habitual
    /// para borrar un modelo es recuperar disco, y sin el dato la decisión es a ciegas.
    /// </summary>
    private static bool ConfirmDeletionWithDialog(DownloadedModelInfo model)
    {
        var answer = System.Windows.MessageBox.Show(
            Loc.F("catalog.delete.message", model.FileName, model.SizeText, System.IO.Path.GetDirectoryName(model.FullPath)),
            Loc.T("catalog.delete.title"),
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning,
            // El botón por defecto es No: este diálogo borra gigabytes
            System.Windows.MessageBoxResult.No);

        return answer == System.Windows.MessageBoxResult.Yes;
    }

    private void ExecuteDeleteDownloaded()
    {
        if (_store == null || _selectedDownloaded == null) return;

        var model = _selectedDownloaded;
        if (!_confirmDeletion(model)) return;

        var result = _store.Delete(model);

        if (!result.Success)
        {
            IsError = true;
            Status = result.ErrorMessage ?? Loc.T("catalog.delete.failed");
            RefreshDownloaded();
            return;
        }

        var record = result.Value!;
        _downloadLog.RecordDeletion(record);

        string extra = record.AlsoDeleted.Count > 0
            ? " " + Loc.F("catalog.delete.alsoDeleted", string.Join(", ", record.AlsoDeleted))
            : "";

        IsError = false;
        Status = Loc.F("catalog.delete.done", record.FileName, DownloadedModelInfo.FormatSize(record.SizeBytes)) + extra;

        RefreshDownloaded();
        _onModelDeleted?.Invoke(record);
    }

    private void ExecuteCleanPartials()
    {
        if (_store == null) return;

        var (deleted, freed) = _store.CleanPartials();

        IsError = false;
        Status = deleted.Count == 0
            ? Loc.T("catalog.partials.none")
            : Loc.P("catalog.partials.deleted", deleted.Count, DownloadedModelInfo.FormatSize(freed));

        RefreshDownloaded();
    }

    private void OnDownloadProgress(object? sender, DownloadProgress p)
    {
        System.Windows.Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            DownloadPercent = p.Percent;
            DownloadText = p.Text;
        });
    }

    public void Dispose()
    {
        _downloader.ProgressChanged -= OnDownloadProgress;
        _downloadCts?.Cancel();
        _downloadCts?.Dispose();
        _client.Dispose();
        _downloader.Dispose();
    }
}
