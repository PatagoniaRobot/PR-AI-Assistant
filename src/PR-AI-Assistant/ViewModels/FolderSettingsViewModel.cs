using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using PRImager.AIAssistant.Helpers;
using PRImager.AIAssistant.Services;
using PRImager.AIAssistant.Services.Folders;

namespace PRImager.AIAssistant.ViewModels;

/// <summary>
/// Una carpeta dibujada en la ventana, con su estado de movimiento.
///
/// Es estado puro: los comandos viven en <see cref="FolderSettingsViewModel"/> y reciben la
/// fila como parámetro. Con una acción larga y cancelable por fila, tenerlos acá obligaría a
/// darle a cada fila una referencia al servicio.
/// </summary>
public sealed class AppFolderRowViewModel : ObservableObject
{
    public AppFolderKind Kind { get; init; }
    public string DisplayName { get; init; } = "";
    public string Description { get; init; } = "";
    public IReadOnlyList<string> Patterns { get; init; } = Array.Empty<string>();

    private string _path = "";
    public string Path { get => _path; set { _path = value; OnPropertyChanged(); } }

    private string _contentsText = "";
    /// <summary>«3 archivos · 6,53 GB», o que está vacía.</summary>
    public string ContentsText { get => _contentsText; set { _contentsText = value; OnPropertyChanged(); } }

    private bool _isInsideBuildOutput;
    public bool IsInsideBuildOutput
    {
        get => _isInsideBuildOutput;
        set { _isInsideBuildOutput = value; OnPropertyChanged(); OnPropertyChanged(nameof(WarningText)); }
    }

    /// <summary>
    /// El aviso que motiva media pantalla. Era el pendiente 6.3 del maestro: la carpeta de
    /// descargas apunta adentro de bin\ y cualquier limpieza de compilación se lleva los
    /// modelos bajados, que son gigabytes y una descarga de horas.
    /// </summary>
    public string WarningText => IsInsideBuildOutput
        ? Loc.T("folders.insideBuild")
        : "";

    // --- Movimiento pendiente ---

    private string _previousFolder = "";
    /// <summary>Carpeta anterior, mientras le quede algo para mover.</summary>
    public string PreviousFolder { get => _previousFolder; set { _previousFolder = value; OnPropertyChanged(); } }

    private string _pendingText = "";
    public string PendingText { get => _pendingText; set { _pendingText = value; OnPropertyChanged(); } }

    private bool _hasPendingMove;
    public bool HasPendingMove
    {
        get => _hasPendingMove;
        set { _hasPendingMove = value; OnPropertyChanged(); }
    }

    private bool _isMoving;
    public bool IsMoving
    {
        get => _isMoving;
        set { _isMoving = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsIdle)); }
    }

    public bool IsIdle => !IsMoving;

    private int _movePercent;
    public int MovePercent { get => _movePercent; set { _movePercent = value; OnPropertyChanged(); } }

    private string _moveStatus = "";
    public string MoveStatus { get => _moveStatus; set { _moveStatus = value; OnPropertyChanged(); } }

    private string _resultText = "";
    /// <summary>Lo que pasó en el último movimiento. Se muestra hasta el próximo cambio.</summary>
    public string ResultText { get => _resultText; set { _resultText = value; OnPropertyChanged(); } }

    public static string FormatSize(long bytes) => bytes >= 1024L * 1024 * 1024
        ? $"{bytes / (1024.0 * 1024 * 1024):F2} GB"
        : bytes >= 1024L * 1024
            ? $"{bytes / (1024.0 * 1024):F0} MB"
            : $"{bytes / 1024.0:F0} KB";
}

/// <summary>
/// Configuración de las tres carpetas de datos: modelos de lenguaje descargados, modelos de
/// imagen e imágenes generadas.
///
/// **Cambiar una carpeta solo redirige lo próximo.** No mueve un archivo, es instantáneo y no
/// puede fallar a la mitad. Si la carpeta anterior quedó con archivos, la fila lo dice y
/// ofrece moverlos como una acción aparte, con progreso y cancelación. Separar las dos cosas
/// es lo que hace que un movimiento cortado —son gigabytes entre discos— no deje la
/// configuración a medias: cuando el movimiento corre, la carpeta buena ya está guardada.
/// </summary>
public sealed class FolderSettingsViewModel : ObservableObject
{
    private readonly AppFolderService _folders;
    private readonly IFileDialogService _dialogs;
    private readonly Action<string, string>? _auditLog;

    /// <summary>
    /// Contexto del hilo de UI, capturado al construir. El progreso del movimiento llega
    /// desde un hilo del pool, porque el copiado usa ConfigureAwait(false) de punta a punta
    /// (la lección de 4.10). Con null se invoca derecho, que es lo correcto en un banco.
    /// </summary>
    private readonly SynchronizationContext? _uiContext;

    private readonly Dictionary<AppFolderKind, CancellationTokenSource> _moveCts = new();

    public ObservableCollection<AppFolderRowViewModel> Folders { get; } = new();

    public ICommand ChangeFolderCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand MoveCommand { get; }
    public ICommand CancelMoveCommand { get; }
    public ICommand DismissPendingCommand { get; }
    public ICommand ResetToDefaultCommand { get; }

    private string _message = "";
    public string Message { get => _message; set { _message = value; OnPropertyChanged(); } }

    /// <summary>
    /// True si esta edición no configura ninguna carpeta. Es el caso de la edición
    /// institucional, que no descarga modelos ni genera imágenes: no tiene ninguna carpeta
    /// de datos del usuario que elegir.
    /// </summary>
    public bool IsEmpty => Folders.Count == 0;

    public FolderSettingsViewModel(
        AppFolderService folders,
        IFileDialogService dialogs,
        Action<string, string>? auditLog = null)
    {
        _folders = folders;
        _dialogs = dialogs;
        _auditLog = auditLog;
        _uiContext = SynchronizationContext.Current;

        ChangeFolderCommand = new RelayCommand<AppFolderRowViewModel>(ChangeFolder, r => r is not null && !r.IsMoving);
        OpenFolderCommand = new RelayCommand<AppFolderRowViewModel>(OpenFolder, r => r is not null);
        MoveCommand = new AsyncRelayCommand<AppFolderRowViewModel>(MoveAsync,
            r => r is not null && r.HasPendingMove && !r.IsMoving);
        CancelMoveCommand = new RelayCommand<AppFolderRowViewModel>(CancelMove, r => r is not null && r.IsMoving);
        DismissPendingCommand = new RelayCommand<AppFolderRowViewModel>(DismissPending, r => r is not null && !r.IsMoving);
        ResetToDefaultCommand = new RelayCommand<AppFolderRowViewModel>(ResetToDefault, r => r is not null && !r.IsMoving);

        Refresh();
    }

    /// <summary>
    /// Relee las tres carpetas y las mide. Recorre directorios, así que se llama
    /// explícitamente y no desde un getter que un binding pueda disparar solo.
    /// </summary>
    public void Refresh()
    {
        // El estado de movimiento pendiente vive en la fila y sobrevive al refresco: se
        // pierde solo cuando el movimiento termina o el usuario lo descarta.
        var pendientes = Folders.ToDictionary(f => f.Kind, f => (f.PreviousFolder, f.PendingText, f.HasPendingMove, f.ResultText));

        Folders.Clear();

        foreach (var carpeta in _folders.List())
        {
            var fila = new AppFolderRowViewModel
            {
                Kind = carpeta.Kind,
                DisplayName = carpeta.DisplayName,
                Description = carpeta.Description,
                Patterns = carpeta.Patterns,
                Path = carpeta.Path,
                IsInsideBuildOutput = carpeta.IsInsideBuildOutput,
                ContentsText = DescribeContents(carpeta.FileCount, carpeta.Bytes)
            };

            if (pendientes.TryGetValue(carpeta.Kind, out var p))
            {
                fila.PreviousFolder = p.PreviousFolder;
                fila.PendingText = p.PendingText;
                fila.HasPendingMove = p.HasPendingMove;
                fila.ResultText = p.ResultText;
            }

            Folders.Add(fila);
        }

        OnPropertyChanged(nameof(IsEmpty));
    }

    private static string DescribeContents(int archivos, long bytes) => archivos switch
    {
        0 => Loc.T("folders.empty"),
        _ => Loc.P("folders.contents", archivos, AppFolderRowViewModel.FormatSize(bytes))
    };

    // --- Cambiar la carpeta ---

    private void ChangeFolder(AppFolderRowViewModel? fila)
    {
        if (fila is null) return;

        var elegida = _dialogs.SelectFolder(Loc.F("folders.pickFor", fila.DisplayName), fila.Path);
        if (string.IsNullOrWhiteSpace(elegida)) return;

        Apply(fila, elegida);
    }

    private void ResetToDefault(AppFolderRowViewModel? fila)
    {
        if (fila is null) return;

        var porDefecto = _folders.List().FirstOrDefault(f => f.Kind == fila.Kind)?.DefaultPath;
        if (string.IsNullOrWhiteSpace(porDefecto)) return;

        Apply(fila, porDefecto);
    }

    /// <summary>
    /// Guarda la carpeta nueva y, si la anterior quedó con archivos, deja la fila lista para
    /// ofrecer el movimiento. Lo que NO hace es mover nada.
    /// </summary>
    private void Apply(AppFolderRowViewModel fila, string nueva)
    {
        string anterior;
        try
        {
            anterior = _folders.Set(fila.Kind, nueva);
        }
        catch (Exception ex)
        {
            Message = Loc.F("folders.changeFailed", ex.Message);
            return;
        }

        var (archivos, bytes) = FolderMigrator.Survey(anterior, fila.Patterns);

        Refresh();
        var actualizada = Folders.FirstOrDefault(f => f.Kind == fila.Kind);
        if (actualizada is null) return;

        actualizada.ResultText = "";

        if (archivos > 0 && !PathsEqual(anterior, actualizada.Path))
        {
            actualizada.PreviousFolder = anterior;
            actualizada.PendingText = Loc.P("folders.pending", archivos, AppFolderRowViewModel.FormatSize(bytes));
            actualizada.HasPendingMove = true;
        }
        else
        {
            actualizada.HasPendingMove = false;
            actualizada.PreviousFolder = "";
        }

        Message = Loc.F("folders.saved", actualizada.DisplayName);
        _auditLog?.Invoke("FOLDER_CHANGED", $"kind={fila.Kind} to=\"{actualizada.Path}\"");

        ((RelayCommand<AppFolderRowViewModel>)DismissPendingCommand).RaiseCanExecuteChanged();
        ((AsyncRelayCommand<AppFolderRowViewModel>)MoveCommand).RaiseCanExecuteChanged();
    }

    private void DismissPending(AppFolderRowViewModel? fila)
    {
        if (fila is null) return;
        fila.HasPendingMove = false;
        fila.PreviousFolder = "";
        fila.PendingText = "";
    }

    // --- Mover ---

    private async Task MoveAsync(AppFolderRowViewModel? fila)
    {
        if (fila is null || !fila.HasPendingMove || fila.IsMoving) return;

        string origen = fila.PreviousFolder;
        string destino = fila.Path;

        var cts = new CancellationTokenSource();
        _moveCts[fila.Kind] = cts;

        fila.IsMoving = true;
        fila.MovePercent = 0;
        fila.MoveStatus = "Preparando…";
        fila.ResultText = "";
        RaiseMoveCommands();

        var migrador = new FolderMigrator();
        migrador.ProgressChanged += (_, p) => OnUi(() =>
        {
            fila.MovePercent = p.Percent;
            fila.MoveStatus = p.Text;
        });

        FolderMigrationResult resultado;
        try
        {
            resultado = await migrador
                .MoveAsync(origen, destino, fila.Patterns, cts.Token)
                .ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            fila.IsMoving = false;
            fila.ResultText = Loc.F("folders.moveUnexpected", ex.Message);
            RaiseMoveCommands();
            return;
        }
        finally
        {
            _moveCts.Remove(fila.Kind);
            cts.Dispose();
        }

        // Los paquetes de imagen guardan una ruta absoluta por archivo. Se reapuntan acá,
        // después de mover y no al cambiar la carpeta, y archivo por archivo según dónde
        // esté cada uno: así un movimiento cancelado a la mitad deja el paquete igual de
        // utilizable, con unos componentes de un lado y otros del otro.
        int reapuntados = _folders.RepointAfterMove(fila.Kind, origen, destino);

        fila.IsMoving = false;
        fila.MovePercent = 0;
        fila.MoveStatus = "";
        fila.ResultText = Describe(resultado, origen, reapuntados);

        var (quedan, _) = FolderMigrator.Survey(origen, fila.Patterns);
        Refresh();

        var actualizada = Folders.FirstOrDefault(f => f.Kind == fila.Kind);
        if (actualizada is not null)
        {
            actualizada.ResultText = fila.ResultText;
            actualizada.PreviousFolder = origen;
            actualizada.PendingText = fila.PendingText;
            // Mientras quede algo atrás el botón sigue disponible: es lo que permite
            // retomar un movimiento cancelado sin volver a elegir la carpeta.
            actualizada.HasPendingMove = quedan > 0;
        }

        _auditLog?.Invoke("FOLDER_MOVED",
            $"kind={fila.Kind} moved={resultado.Moved.Count} skipped={resultado.Skipped.Count} " +
            $"failed={resultado.Failed.Count} bytes={resultado.BytesMoved} cancelled={resultado.WasCancelled}");

        RaiseMoveCommands();
    }

    private void CancelMove(AppFolderRowViewModel? fila)
    {
        if (fila is null) return;
        if (_moveCts.TryGetValue(fila.Kind, out var cts))
        {
            fila.MoveStatus = "Cancelando…";
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
        }
    }

    /// <summary>
    /// Arma el texto del resultado. Dice qué pasó con cada grupo por separado porque los
    /// tres significan cosas distintas: lo movido está del otro lado, lo salteado ya estaba
    /// y lo fallido **sigue en la carpeta anterior**, que es lo que el usuario necesita
    /// saber para no darlo por perdido.
    /// </summary>
    private static string Describe(FolderMigrationResult r, string origen, int paquetesReapuntados)
    {
        if (!r.Success) return r.ErrorMessage;

        if (r.NothingToDo)
            return Loc.T("folders.nothingToMove");

        var partes = new List<string>();

        if (r.Moved.Count > 0)
            partes.Add(Loc.P("folders.result.moved", r.Moved.Count, AppFolderRowViewModel.FormatSize(r.BytesMoved)));

        if (r.Skipped.Count > 0)
            partes.Add(Loc.P("folders.result.skipped", r.Skipped.Count));

        if (r.Failed.Count > 0)
            partes.Add(Loc.P("folders.result.failed", r.Failed.Count, origen, string.Join("; ", r.Failed)));

        if (paquetesReapuntados > 0)
            partes.Add(Loc.P("folders.result.repointed", paquetesReapuntados));

        var texto = string.Join(". ", partes) + ".";

        if (r.WasCancelled)
            texto = Loc.F("folders.result.cancelled", texto);

        return texto;
    }

    private void OpenFolder(AppFolderRowViewModel? fila)
    {
        if (fila is null) return;

        try
        {
            Directory.CreateDirectory(fila.Path);
            Process.Start(new ProcessStartInfo { FileName = fila.Path, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Message = Loc.F("folders.openFailed", ex.Message);
        }
    }

    private void RaiseMoveCommands()
    {
        ((AsyncRelayCommand<AppFolderRowViewModel>)MoveCommand).RaiseCanExecuteChanged();
        ((RelayCommand<AppFolderRowViewModel>)CancelMoveCommand).RaiseCanExecuteChanged();
        ((RelayCommand<AppFolderRowViewModel>)ChangeFolderCommand).RaiseCanExecuteChanged();
        ((RelayCommand<AppFolderRowViewModel>)DismissPendingCommand).RaiseCanExecuteChanged();
        ((RelayCommand<AppFolderRowViewModel>)ResetToDefaultCommand).RaiseCanExecuteChanged();
    }

    private void OnUi(Action accion)
    {
        if (_uiContext is null) accion();
        else _uiContext.Post(_ => accion(), null);
    }

    private static bool PathsEqual(string a, string b)
    {
        try
        {
            return string.Equals(
                System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(a)),
                System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(b)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
    }
}
