using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PRImager.AIAssistant.Services.HuggingFace;

namespace PRImager.AIAssistant.Services.ImageGen;

/// <summary>
/// Progreso de la descarga de un paquete completo. A diferencia del de un archivo
/// suelto, informa en qué componente va: bajar 7 GB sin decir cuál de los tres se está
/// bajando deja al usuario sin saber cuánto falta de verdad.
/// </summary>
public sealed class PackageDownloadProgress
{
    public int ComponentIndex { get; init; }
    public int ComponentCount { get; init; }
    public string ComponentRole { get; init; } = "";
    public string FileName { get; init; } = "";

    public long BytesDownloaded { get; init; }
    public long ComponentTotalBytes { get; init; }
    public double SpeedMbPerSecond { get; init; }

    /// <summary>Bytes ya completados de los componentes anteriores.</summary>
    public long BytesCompletedBefore { get; init; }

    /// <summary>Total esperado del paquete entero.</summary>
    public long PackageTotalBytes { get; init; }

    public double PackagePercent => PackageTotalBytes > 0
        ? (BytesCompletedBefore + BytesDownloaded) * 100.0 / PackageTotalBytes
        : 0;

    public string Text =>
        $"{ComponentRole} ({ComponentIndex + 1} de {ComponentCount}) — " +
        $"{BytesDownloaded / 1024.0 / 1024.0:N0} MB" +
        (ComponentTotalBytes > 0 ? $" de {ComponentTotalBytes / 1024.0 / 1024.0:N0} MB" : "") +
        (SpeedMbPerSecond > 0 ? $" a {SpeedMbPerSecond:N1} MB/s" : "");
}

/// <summary>Resultado de bajar un paquete entero.</summary>
public sealed class PackageDownloadResult
{
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
    public bool WasCancelled { get; init; }

    /// <summary>El paquete armado, si salió bien.</summary>
    public ImageModelPackage? Package { get; init; }

    /// <summary>Componentes que ya estaban en disco y no hizo falta bajar.</summary>
    public List<string> AlreadyPresent { get; init; } = new();

    public static PackageDownloadResult Ok(ImageModelPackage p, List<string> yaEstaban) =>
        new() { Success = true, Package = p, AlreadyPresent = yaEstaban };

    public static PackageDownloadResult Fail(string message) =>
        new() { Success = false, ErrorMessage = message };

    public static PackageDownloadResult Cancelled() =>
        new() { Success = false, WasCancelled = true, ErrorMessage = "Descarga cancelada." };
}

/// <summary>
/// Baja los tres archivos de un paquete de modelo de imagen.
///
/// Reutiliza <see cref="ModelDownloader"/> de E3 tal cual —streaming a .part, SHA-256,
/// progreso y cancelación—: no hacía falta escribir nada de eso otra vez. Lo que agrega
/// es la noción de paquete: tres descargas que valen como una sola, con progreso
/// acumulado y con la regla de que un paquete a medias no se guarda como utilizable.
///
/// Nota de acoplamiento: este archivo usa Services\HuggingFace, que se excluye de la
/// edición Forense con MODEL_DOWNLOAD. Services\ImageGen se excluye con IMAGE_GEN, y
/// hoy las dos condiciones son la misma —"no es Forense"—, así que no hay problema. Si
/// alguna vez se separan, esto deja de compilar y hay que decidir explícitamente.
/// </summary>
public sealed class ImagePackageDownloader : IDisposable
{
    private readonly ModelDownloader _downloader;
    private readonly ModelDownloadLog? _log;
    private bool _disposed;

    public event EventHandler<PackageDownloadProgress>? ProgressChanged;

    public ImagePackageDownloader(Func<string>? tokenProvider = null, string? logDirectory = null)
    {
        _downloader = new ModelDownloader(tokenProvider: tokenProvider);
        if (!string.IsNullOrWhiteSpace(logDirectory))
            _log = new ModelDownloadLog(logDirectory);
    }

    /// <summary>
    /// Descarga los componentes que falten de <paramref name="source"/> en
    /// <paramref name="destinationDirectory"/> y devuelve el paquete armado.
    ///
    /// Los componentes que ya están en disco se saltean en lugar de fallar: el
    /// DownloadAsync de E3 rechaza un archivo existente, y acá eso convertiría un
    /// reintento tras una cancelación en un callejón sin salida.
    /// </summary>
    public async Task<PackageDownloadResult> DownloadAsync(
        ImagePackageSource source,
        string destinationDirectory,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (source.Components.Count == 0)
            return PackageDownloadResult.Fail("El paquete no declara ningún archivo.");

        try
        {
            Directory.CreateDirectory(destinationDirectory);
        }
        catch (Exception ex)
        {
            return PackageDownloadResult.Fail($"No se pudo crear la carpeta de modelos: {ex.Message}");
        }

        var yaEstaban = new List<string>();
        long completadosAntes = 0;
        long totalPaquete = source.TotalExpectedBytes;

        for (int i = 0; i < source.Components.Count; i++)
        {
            var comp = source.Components[i];
            string destino = Path.Combine(destinationDirectory, comp.LocalFileName);

            if (File.Exists(destino))
            {
                yaEstaban.Add(comp.Role);
                completadosAntes += new FileInfo(destino).Length;
                continue;
            }

            // Se reenvía el progreso del archivo suelto como progreso del paquete.
            var indice = i;
            var antes = completadosAntes;
            void AlProgresar(object? _, DownloadProgress p) =>
                ProgressChanged?.Invoke(this, new PackageDownloadProgress
                {
                    ComponentIndex = indice,
                    ComponentCount = source.Components.Count,
                    ComponentRole = comp.Role,
                    FileName = comp.LocalFileName,
                    BytesDownloaded = p.BytesDownloaded,
                    ComponentTotalBytes = p.TotalBytes > 0 ? p.TotalBytes : comp.ExpectedBytes,
                    SpeedMbPerSecond = p.SpeedMbPerSecond,
                    BytesCompletedBefore = antes,
                    PackageTotalBytes = totalPaquete
                });

            _downloader.ProgressChanged += AlProgresar;
            HfResult<ModelDownloadRecord> r;
            try
            {
                r = await _downloader.DownloadAsync(comp.RepoId, comp.FileName, destinationDirectory, ct)
                                     .ConfigureAwait(false);
            }
            finally
            {
                _downloader.ProgressChanged -= AlProgresar;
            }

            if (ct.IsCancellationRequested) return PackageDownloadResult.Cancelled();

            if (!r.Success)
            {
                // Se nombra el componente: "error al bajar el VAE" es accionable,
                // "error al bajar" no.
                var detalle = r.IsGated
                    ? $"{r.ErrorMessage}\n\nEse repositorio exige autenticación. " +
                      "Cargá un token de HuggingFace en la configuración."
                    : r.ErrorMessage;
                return PackageDownloadResult.Fail($"No se pudo descargar el {comp.Role}: {detalle}");
            }

            if (r.Value is not null)
            {
                _log?.Record(r.Value);
                completadosAntes += r.Value.SizeBytes;
            }
        }

        var paquete = new ImageModelPackage
        {
            Id = source.Id,
            DisplayName = source.DisplayName,
            Family = source.Family,
            DefaultSteps = source.DefaultSteps,
            DefaultCfgScale = source.DefaultCfgScale,
            IsFactory = source.Id == ImageModelService.RecommendedPackageId,
            DiffusionModelPath = RutaDe(source, destinationDirectory, "modelo de difusión"),
            TextEncoderPath = RutaDe(source, destinationDirectory, "text encoder"),
            VaePath = RutaDe(source, destinationDirectory, "VAE")
        };

        // Se comprueba contra el disco antes de declararlo utilizable: si algo quedó a
        // medias, el paquete tiene que salir incompleto y no como listo para usar.
        if (!paquete.IsComplete)
            return PackageDownloadResult.Fail(
                $"La descarga terminó pero el paquete quedó incompleto: {paquete.MissingText}.");

        return PackageDownloadResult.Ok(paquete, yaEstaban);
    }

    /// <summary>
    /// Ubica el archivo de un componente POR SU ROL, no por su posición en la lista.
    ///
    /// La primera versión mapeaba por índice —0 difusión, 1 text encoder, 2 VAE— y
    /// funcionaba de casualidad, por el orden en que están declarados. Reordenar el
    /// descriptor habría armado el paquete cruzado **en silencio**, porque los tres
    /// archivos existen igual y IsComplete habría dado true.
    /// </summary>
    private static string RutaDe(ImagePackageSource source, string carpeta, string rol)
    {
        foreach (var c in source.Components)
        {
            if (string.Equals(c.Role, rol, StringComparison.OrdinalIgnoreCase))
                return Path.Combine(carpeta, c.LocalFileName);
        }
        return "";   // el descriptor no declara ese componente
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _downloader.Dispose();
    }
}
