using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace PRImager.AIAssistant.Services.Folders;

/// <summary>Avance del movimiento. Se informa por archivo y cada 100 ms dentro de uno grande.</summary>
public sealed class FolderMigrationProgress
{
    public string CurrentFile { get; init; } = "";
    public int FilesDone { get; init; }
    public int FilesTotal { get; init; }
    public long BytesDone { get; init; }
    public long BytesTotal { get; init; }

    /// <summary>Texto ya armado para la interfaz, para que el ViewModel no lo rearme.</summary>
    public string Text { get; init; } = "";

    public int Percent => BytesTotal <= 0 ? 0 : (int)Math.Clamp(BytesDone * 100 / BytesTotal, 0, 100);
}

/// <summary>
/// Resultado de un movimiento. Los fallos son datos, no excepciones, con el mismo
/// criterio que el resto del proyecto.
/// </summary>
public sealed class FolderMigrationResult
{
    public bool Success { get; init; }
    public bool WasCancelled { get; init; }
    public string ErrorMessage { get; init; } = "";

    public List<string> Moved { get; } = new();

    /// <summary>Ya existía un archivo con ese nombre en el destino. No se pisa nunca.</summary>
    public List<string> Skipped { get; } = new();

    /// <summary>Nombre del archivo y el motivo por el que no se pudo mover.</summary>
    public List<string> Failed { get; } = new();

    public long BytesMoved { get; set; }

    /// <summary>
    /// True si origen y destino estaban en el mismo volumen. Importa para leer el
    /// resultado: en el mismo volumen mover es una operación de metadatos y los bytes
    /// no se copian ni se verifican, porque no hay nada que se pueda corromper.
    /// </summary>
    public bool WasSameVolume { get; init; }

    public bool NothingToDo => Moved.Count == 0 && Skipped.Count == 0 && Failed.Count == 0;
}

/// <summary>
/// Mueve los archivos de una carpeta de datos a otra.
///
/// **No se llama al cambiar la carpeta.** Cambiar la carpeta solo redirige lo próximo,
/// que es instantáneo y no puede fallar; mover lo que ya está es una acción aparte que
/// el usuario pide explícitamente. Separarlas es lo que hace que un movimiento cortado a
/// la mitad no deje la configuración a medias: cuando este código corre, la carpeta nueva
/// ya es la buena.
///
/// Dos caminos, y la diferencia no es de rendimiento sino de riesgo:
///
/// - **Mismo volumen**: <c>File.Move</c>, que reescribe una entrada de directorio. No
///   copia bytes, no puede corromper nada y es instantáneo aun con 7 GB.
/// - **Entre volúmenes**: hay que copiar de verdad, y ahí sí se puede perder un archivo.
///   Se copia a un <c>.part</c>, se verifica por SHA-256 contra el origen, se promueve al
///   nombre final y **recién entonces** se borra el origen. Es la disciplina del .part que
///   E3 ya usa para las descargas, aplicada al único paso irreversible que hay acá.
/// </summary>
public sealed class FolderMigrator
{
    /// <summary>Sufijo del archivo a medio copiar. El mismo que usa ModelDownloader.</summary>
    public const string PartialExtension = ".part";

    private const int BufferSize = 1024 * 1024;
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(100);

    public event EventHandler<FolderMigrationProgress>? ProgressChanged;

    /// <summary>
    /// Mueve de <paramref name="from"/> a <paramref name="to"/> los archivos que
    /// coincidan con alguno de los patrones.
    ///
    /// Un archivo que ya exista en el destino **se saltea**, nunca se pisa: quien mueve
    /// modelos no espera perder el que ya tenía del otro lado. Se informa aparte para que
    /// la interfaz pueda decirlo.
    /// </summary>
    public async Task<FolderMigrationResult> MoveAsync(
        string from,
        string to,
        IReadOnlyList<string> patterns,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to))
            return Fail(Loc.T("migrate.missingPath"));

        string origen, destino;
        try
        {
            origen = Path.GetFullPath(from);
            destino = Path.GetFullPath(to);
        }
        catch (Exception ex)
        {
            return Fail(Loc.F("migrate.invalidPath", ex.Message));
        }

        if (PathsEqual(origen, destino))
            return new FolderMigrationResult { Success = true, WasSameVolume = true };

        // Mover una carpeta adentro de sí misma copiaría archivos sobre los que todavía
        // se está iterando. Se corta antes de tocar nada.
        if (IsInside(destino, origen))
            return Fail(Loc.T("migrate.nested"));

        if (!Directory.Exists(origen))
            return new FolderMigrationResult { Success = true };

        List<string> archivos;
        try
        {
            archivos = patterns
                .SelectMany(p => Directory.GetFiles(origen, p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            return Fail(Loc.F("migrate.readFailed", ex.Message));
        }

        if (archivos.Count == 0)
            return new FolderMigrationResult { Success = true };

        try
        {
            Directory.CreateDirectory(destino);
        }
        catch (Exception ex)
        {
            return Fail(Loc.F("migrate.createFailed", ex.Message));
        }

        bool mismoVolumen = SameVolume(origen, destino);
        var resultado = new FolderMigrationResult { Success = true, WasSameVolume = mismoVolumen };

        long totalBytes = 0;
        foreach (var a in archivos)
        {
            try { totalBytes += new FileInfo(a).Length; }
            catch (Exception) { /* si no se puede medir, no cuenta para el total */ }
        }

        long bytesHechos = 0;
        int hechos = 0;

        foreach (var archivo in archivos)
        {
            if (ct.IsCancellationRequested)
                return Cancelled(resultado);

            string nombre = Path.GetFileName(archivo);
            string final = Path.Combine(destino, nombre);

            long tamano;
            try { tamano = new FileInfo(archivo).Length; }
            catch (Exception) { tamano = 0; }

            if (File.Exists(final))
            {
                resultado.Skipped.Add(nombre);
                hechos++;
                bytesHechos += tamano;
                Report(nombre, hechos, archivos.Count, bytesHechos, totalBytes, Loc.T("migrate.step.alreadyThere"));
                continue;
            }

            Report(nombre, hechos, archivos.Count, bytesHechos, totalBytes,
                mismoVolumen ? Loc.T("migrate.step.moving") : Loc.T("migrate.step.copying"));

            string? error = mismoVolumen
                ? MoveSameVolume(archivo, final)
                : await CopyVerifyAndDeleteAsync(archivo, final, nombre, hechos, archivos.Count,
                    bytesHechos, totalBytes, ct).ConfigureAwait(false);

            if (ct.IsCancellationRequested && error is not null)
                return Cancelled(resultado);

            if (error is null)
            {
                resultado.Moved.Add(nombre);
                resultado.BytesMoved += tamano;
            }
            else
            {
                resultado.Failed.Add($"{nombre}: {error}");
            }

            hechos++;
            bytesHechos += tamano;
            Report(nombre, hechos, archivos.Count, bytesHechos, totalBytes,
                error is null ? Loc.T("migrate.step.done") : Loc.T("migrate.step.failed"));
        }

        return resultado;
    }

    /// <summary>
    /// Cuenta qué hay para mover, sin mover nada. Es lo que le permite a la interfaz
    /// decir «quedaron 3 archivos (6,5 GB) en la carpeta anterior» antes de ofrecer el botón.
    /// </summary>
    public static (int Files, long Bytes) Survey(string folder, IReadOnlyList<string> patterns)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return (0, 0);

        try
        {
            var archivos = patterns
                .SelectMany(p => Directory.GetFiles(folder, p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            long bytes = 0;
            foreach (var a in archivos)
            {
                try { bytes += new FileInfo(a).Length; }
                catch (Exception) { }
            }
            return (archivos.Count, bytes);
        }
        catch (Exception)
        {
            return (0, 0);
        }
    }

    // --- Los dos caminos ---

    private static string? MoveSameVolume(string origen, string destino)
    {
        try
        {
            File.Move(origen, destino);
            return null;
        }
        catch (Exception ex)
        {
            // El caso real es tener el modelo cargado en llama-server o en sd-server.
            return ex.Message;
        }
    }

    /// <summary>
    /// Copia entre volúmenes con verificación, y borra el origen al final.
    ///
    /// El orden importa y es el único que no puede perder un archivo: mientras el destino
    /// no esté completo, verificado y promovido a su nombre final, el origen sigue
    /// intacto. Si algo falla en el medio, lo que se descarta es la copia.
    /// </summary>
    private async Task<string?> CopyVerifyAndDeleteAsync(
        string origen, string destinoFinal, string nombre,
        int hechos, int total, long bytesPrevios, long bytesTotales,
        CancellationToken ct)
    {
        string parcial = destinoFinal + PartialExtension;
        byte[] hashOrigen;

        try
        {
            hashOrigen = await CopyAsync(origen, parcial, nombre, hechos, total,
                bytesPrevios, bytesTotales, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryDelete(parcial);
            return "cancelado";
        }
        catch (Exception ex)
        {
            TryDelete(parcial);
            return ex.Message;
        }

        Report(nombre, hechos, total, bytesPrevios, bytesTotales, "verificando");

        byte[] hashDestino;
        try
        {
            hashDestino = await HashAsync(parcial, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryDelete(parcial);
            return "cancelado";
        }
        catch (Exception ex)
        {
            TryDelete(parcial);
            return Loc.F("migrate.verifyFailed", ex.Message);
        }

        if (!hashOrigen.AsSpan().SequenceEqual(hashDestino))
        {
            TryDelete(parcial);
            return Loc.T("migrate.hashMismatch");
        }

        try
        {
            File.Move(parcial, destinoFinal);
        }
        catch (Exception ex)
        {
            TryDelete(parcial);
            return Loc.F("migrate.promoteFailed", ex.Message);
        }

        try
        {
            File.Delete(origen);
        }
        catch (Exception ex)
        {
            // El archivo YA está bien en el destino. No es una falla del movimiento:
            // quedó duplicado, que es molesto pero no pierde nada. Se dice así.
            return Loc.F("migrate.deleteOriginalFailed", ex.Message);
        }

        return null;
    }

    /// <summary>Copia devolviendo el SHA-256 de lo que leyó del origen.</summary>
    private async Task<byte[]> CopyAsync(
        string origen, string parcial, string nombre,
        int hechos, int total, long bytesPrevios, long bytesTotales,
        CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[BufferSize];
        var ultimoAviso = DateTime.UtcNow;

        await using (var entrada = new FileStream(origen, FileMode.Open, FileAccess.Read,
                         FileShare.Read, BufferSize, FileOptions.SequentialScan | FileOptions.Asynchronous))
        await using (var salida = new FileStream(parcial, FileMode.Create, FileAccess.Write,
                         FileShare.None, BufferSize, FileOptions.Asynchronous))
        {
            long copiados = 0;
            int leidos;
            while ((leidos = await entrada.ReadAsync(buffer.AsMemory(0, BufferSize), ct).ConfigureAwait(false)) > 0)
            {
                await salida.WriteAsync(buffer.AsMemory(0, leidos), ct).ConfigureAwait(false);
                hash.AppendData(buffer, 0, leidos);
                copiados += leidos;

                if (DateTime.UtcNow - ultimoAviso >= ProgressInterval)
                {
                    ultimoAviso = DateTime.UtcNow;
                    Report(nombre, hechos, total, bytesPrevios + copiados, bytesTotales, "copiando");
                }
            }

            await salida.FlushAsync(ct).ConfigureAwait(false);
        }

        return hash.GetHashAndReset();
    }

    private static async Task<byte[]> HashAsync(string path, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[BufferSize];

        await using var entrada = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, BufferSize, FileOptions.SequentialScan | FileOptions.Asynchronous);

        int leidos;
        while ((leidos = await entrada.ReadAsync(buffer.AsMemory(0, BufferSize), ct).ConfigureAwait(false)) > 0)
            hash.AppendData(buffer, 0, leidos);

        return hash.GetHashAndReset();
    }

    // --- Auxiliares ---

    private void Report(string archivo, int hechos, int total, long bytes, long bytesTotales, string que)
    {
        ProgressChanged?.Invoke(this, new FolderMigrationProgress
        {
            CurrentFile = archivo,
            FilesDone = hechos,
            FilesTotal = total,
            BytesDone = bytes,
            BytesTotal = bytesTotales,
            Text = Loc.F("migrate.progress", archivo, que, Math.Min(hechos + 1, total), total)
        });
    }

    private static FolderMigrationResult Fail(string mensaje) =>
        new() { Success = false, ErrorMessage = mensaje };

    /// <summary>
    /// Una cancelación no es un fallo y lo movido hasta acá queda movido: la carpeta
    /// nueva ya es la configurada, así que esos archivos están donde corresponde. Lo que
    /// quedó atrás se puede mover en otro momento con el mismo botón.
    /// </summary>
    private static FolderMigrationResult Cancelled(FolderMigrationResult parcial)
    {
        var r = new FolderMigrationResult
        {
            Success = true,
            WasCancelled = true,
            WasSameVolume = parcial.WasSameVolume,
            BytesMoved = parcial.BytesMoved
        };
        r.Moved.AddRange(parcial.Moved);
        r.Skipped.AddRange(parcial.Skipped);
        r.Failed.AddRange(parcial.Failed);
        return r;
    }

    private static bool SameVolume(string a, string b)
    {
        try
        {
            return string.Equals(Path.GetPathRoot(a), Path.GetPathRoot(b), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            // Ante la duda se toma el camino cuidadoso, no el rápido.
            return false;
        }
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(Trim(a), Trim(b), StringComparison.OrdinalIgnoreCase);

    private static bool IsInside(string candidato, string carpeta)
    {
        string c = Trim(carpeta) + Path.DirectorySeparatorChar;
        return Trim(candidato).StartsWith(c, StringComparison.OrdinalIgnoreCase);
    }

    private static string Trim(string p) =>
        p.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception) { }
    }
}
