using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace PRImager.AIAssistant.Services.ImageGen;

/// <summary>
/// Qué build del motor está instalado, para el sidecar (pendiente 6.6 del maestro).
///
/// El build es una palanca más que cambia la imagen. Medido el 3 de septiembre: mismo
/// prompt, semilla, modelo y flags en <c>bfbef5b</c> y en <c>6b3edaa</c> dan la misma
/// imagen a la vista y distintos bytes. Sin el build en el sidecar, actualizar el motor
/// rompe en silencio la reproducibilidad de todo lo generado antes.
///
/// <c>sd-server --version</c> imprime <c>stable-diffusion.cpp version unknown, commit
/// bfbef5b</c> y sale enseguida, sin cargar nada ni tocar la GPU. El commit es lo único
/// confiable de esa línea: «version» viene «unknown» en los builds publicados.
/// </summary>
public static class ImageEngineVersion
{
    /// <summary>Lo que se escribe cuando no se pudo averiguar. Nunca vacío: un campo
    /// vacío se lee como «no se registraba», y acá sí se intentó.</summary>
    public const string Unknown = "desconocido";

    private static readonly Regex CommitPattern =
        new(@"commit\s+([0-9a-fA-F]{7,40})", RegexOptions.CultureInvariant);

    // Por ruta y fecha del ejecutable: reemplazar el motor en la misma carpeta, que es
    // justamente como se actualiza, no puede devolver el build viejo.
    private static readonly ConcurrentDictionary<string, string> Cache = new();

    /// <summary>Extrae el commit de la salida de <c>--version</c>, o null.</summary>
    public static string? ParseCommit(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;
        var m = CommitPattern.Match(output);
        return m.Success ? m.Groups[1].Value.ToLowerInvariant() : null;
    }

    /// <summary>
    /// El commit del motor en esa ruta, o <see cref="Unknown"/>. No lanza: que no se pueda
    /// leer la versión no debe impedir guardar la imagen.
    /// </summary>
    public static async Task<string> ReadAsync(string executablePath, CancellationToken ct = default)
    {
        try
        {
            if (!File.Exists(executablePath)) return Unknown;

            var clave = executablePath + "|" + File.GetLastWriteTimeUtc(executablePath).Ticks;
            if (Cache.TryGetValue(clave, out var cacheado)) return cacheado;

            var psi = new ProcessStartInfo
            {
                FileName = executablePath,
                Arguments = "--version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null) return Unknown;

            using var limite = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limite.CancelAfter(TimeSpan.FromSeconds(10));

            var salida = process.StandardOutput.ReadToEndAsync(limite.Token);
            var errores = process.StandardError.ReadToEndAsync(limite.Token);
            try
            {
                await process.WaitForExitAsync(limite.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return Unknown;
            }

            var commit = ParseCommit(await salida.ConfigureAwait(false))
                      ?? ParseCommit(await errores.ConfigureAwait(false));
            if (commit is null) return Unknown;

            Cache[clave] = commit;
            return commit;
        }
        catch (Exception)
        {
            return Unknown;
        }
    }
}
