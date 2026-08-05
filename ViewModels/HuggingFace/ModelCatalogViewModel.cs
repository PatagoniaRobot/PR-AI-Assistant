using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using PRImager.AIAssistant.Helpers;
using PRImager.AIAssistant.Services;
using PRImager.AIAssistant.Services.HuggingFace;

namespace PRImager.AIAssistant.ViewModels.HuggingFace;

/// <summary>
/// Item de la tabla de cuantizaciones: el archivo más su semáforo de VRAM.
/// </summary>
public class QuantizationItem : ObservableObject
{
    public HfFile File { get; }
    public VramEstimate Vram { get; }

    public string FileName => File.FileName;
    public string Quantization => string.IsNullOrEmpty(File.Quantization) ? "—" : File.Quantization;
    public string SizeText => File.SizeText;
    public string VramLabel => Vram.Label;
    public string VramColor => Vram.Color;
    public string VramExplanation => Vram.Explanation;

    private bool _isDownloading;
    public bool IsDownloading
    {
        get => _isDownloading;
        set => SetProperty(ref _isDownloading, value);
    }

    public QuantizationItem(HfFile file, VramEstimate vram)
    {
        File = file;
        Vram = vram;
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
    private readonly ModelDownloadLog _downloadLog;
    private readonly string _destinationDirectory;
    private readonly int _contextTokens;
    private readonly Action<string>? _onModelDownloaded;

    private CancellationTokenSource? _downloadCts;
    private GpuVramInfo? _vram;

    public ObservableCollection<HfRepo> Repos { get; } = new();
    public ObservableCollection<QuantizationItem> Quantizations { get; } = new();

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

    private string _gpuText = "Detectando GPU…";
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

    public ICommand SearchCommand { get; }
    public ICommand DownloadCommand { get; }
    public ICommand CancelDownloadCommand { get; }
    public ICommand CloseCommand { get; }

    public event EventHandler? CloseRequested;

    public ModelCatalogViewModel(
        string destinationDirectory,
        int contextTokens,
        string logDirectory,
        Func<string>? tokenProvider = null,
        Action<string>? onModelDownloaded = null,
        HuggingFaceClient? client = null,
        ModelDownloader? downloader = null,
        GpuInfoService? gpuInfo = null)
    {
        _destinationDirectory = destinationDirectory;
        _contextTokens = contextTokens;
        _downloadLog = new ModelDownloadLog(logDirectory);
        _onModelDownloaded = onModelDownloaded;

        _client = client ?? new HuggingFaceClient(tokenProvider: tokenProvider);
        _downloader = downloader ?? new ModelDownloader(tokenProvider: tokenProvider);
        _gpuInfo = gpuInfo ?? new GpuInfoService();

        _downloader.ProgressChanged += OnDownloadProgress;

        SearchCommand = new AsyncRelayCommand(ExecuteSearchAsync,
            () => !IsBusy && !IsDownloading && !string.IsNullOrWhiteSpace(Query));
        DownloadCommand = new AsyncRelayCommand(ExecuteDownloadAsync,
            () => !IsDownloading && _selectedQuantization != null);
        CancelDownloadCommand = new RelayCommand(ExecuteCancelDownload, () => IsDownloading);
        CloseCommand = new RelayCommand(() => CloseRequested?.Invoke(this, EventArgs.Empty));

        _ = InitializeGpuAsync();
    }

    private async Task InitializeGpuAsync()
    {
        _vram = await _gpuInfo.GetVramAsync();
        GpuText = _vram.Available
            ? $"{_vram.Name} · {_vram.TotalMb / 1024.0:F0} GB VRAM · contexto {_contextTokens} tokens"
            : _vram.UnavailableReason ?? "VRAM no detectada";
    }

    private async Task ExecuteSearchAsync()
    {
        IsBusy = true;
        IsError = false;
        Status = "Buscando en HuggingFace…";
        Repos.Clear();
        Quantizations.Clear();

        var result = await _client.SearchAsync(Query.Trim());

        if (!result.Success)
        {
            IsError = true;
            Status = result.ErrorMessage ?? "Fallo la búsqueda.";
            IsBusy = false;
            return;
        }

        foreach (var r in result.Value!)
            Repos.Add(r);

        Status = Repos.Count == 0
            ? $"Sin resultados GGUF para «{Query.Trim()}»."
            : $"{Repos.Count} repositorios encontrados. Seleccioná uno para ver sus cuantizaciones.";
        IsBusy = false;
    }

    private async Task LoadQuantizationsAsync(HfRepo repo)
    {
        IsBusy = true;
        IsError = false;
        Status = $"Cargando archivos de {repo.Id}…";
        Quantizations.Clear();
        SelectedQuantization = null;

        var detail = await _client.GetRepoDetailAsync(repo.Id);

        if (!detail.Success)
        {
            IsError = true;
            // Un repo gated se informa tal cual: no se esconde el fallo
            Status = detail.ErrorMessage ?? "No se pudo cargar el repositorio.";
            IsBusy = false;
            return;
        }

        _vram ??= await _gpuInfo.GetVramAsync();

        foreach (var f in detail.Value!.GgufFiles)
        {
            var estimate = VramEstimator.Estimate(f.SizeBytes, _contextTokens, _vram);
            Quantizations.Add(new QuantizationItem(f, estimate));
        }

        Status = Quantizations.Count == 0
            ? "El repositorio no expone archivos .gguf."
            : $"{Quantizations.Count} cuantizaciones. El semáforo compara contra la VRAM detectada.";

        if (detail.Value.Repo.IsGated)
        {
            IsError = true;
            Status = "Atención: este repositorio es de acceso restringido. " + HuggingFaceClient.GatedMessage();
        }

        IsBusy = false;
    }

    private async Task ExecuteDownloadAsync()
    {
        if (_selectedQuantization == null || _selectedRepo == null) return;

        var item = _selectedQuantization;

        if (item.Vram.Fit == VramFit.Red)
        {
            var confirm = System.Windows.MessageBox.Show(
                $"{item.VramExplanation}\n\n¿Descargar igual?",
                "El modelo no entra en la GPU",
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
        DownloadText = "Iniciando descarga…";
        Status = $"Descargando {item.FileName}…";

        var result = await _downloader.DownloadAsync(
            _selectedRepo.Id, item.FileName, _destinationDirectory, _downloadCts.Token);

        item.IsDownloading = false;
        IsDownloading = false;
        _downloadCts.Dispose();
        _downloadCts = null;

        if (!result.Success)
        {
            IsError = true;
            Status = result.ErrorMessage ?? "La descarga falló.";
            DownloadText = "";
            DownloadPercent = 0;
            return;
        }

        var record = result.Value!;
        _downloadLog.Record(record);

        LastHash = record.Sha256;
        DownloadPercent = 100;
        DownloadText = "";
        Status = $"✅ {record.FileName} descargado en {_destinationDirectory}. " +
                 "Ya aparece en el selector de modelos.";

        // Refrescar el catálogo local para que el modelo quede usable sin pasos manuales
        _onModelDownloaded?.Invoke(record.DestinationPath);
    }

    private void ExecuteCancelDownload()
    {
        _downloadCts?.Cancel();
        Status = "Cancelando…";
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
