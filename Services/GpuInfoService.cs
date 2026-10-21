using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace PRImager.AIAssistant.Services;

/// <summary>
/// VRAM detectada de la GPU. Todos los valores en MB.
/// </summary>
public sealed class GpuVramInfo
{
    public bool Available { get; init; }
    public string Name { get; init; } = "";
    public int TotalMb { get; init; }
    public int FreeMb { get; init; }

    /// <summary>Motivo por el que no se pudo detectar, si corresponde.</summary>
    public string? UnavailableReason { get; init; }

    public string TotalText => Available
        ? $"{TotalMb / 1024.0:F1} GB" : "desconocida";

    public static GpuVramInfo Unavailable(string reason) =>
        new() { Available = false, UnavailableReason = reason };
}

/// <summary>
/// Consulta la VRAM de la GPU con nvidia-smi.
///
/// Nota: la detección de backend que ya tenía la app sale del stderr de llama-server
/// y solo da el nombre del dispositivo, no la memoria. Para el semáforo hace falta
/// la capacidad real, así que se consulta nvidia-smi aparte.
///
/// No es código de red: se compila también en la configuración Forense.
/// </summary>
public class GpuInfoService
{
    private GpuVramInfo? _cached;

    /// <summary>
    /// Consulta la VRAM. El resultado se cachea: la capacidad total no cambia
    /// durante la sesión y lanzar el proceso tiene costo.
    /// </summary>
    public async Task<GpuVramInfo> GetVramAsync(bool forceRefresh = false, CancellationToken ct = default)
    {
        if (_cached != null && !forceRefresh) return _cached;
        _cached = await QueryNvidiaSmiAsync(ct);
        return _cached;
    }

    private static async Task<GpuVramInfo> QueryNvidiaSmiAsync(CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "nvidia-smi",
                Arguments = "--query-gpu=name,memory.total,memory.free --format=csv,noheader,nounits",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null)
                return GpuVramInfo.Unavailable(Loc.T("vram.smi.notRun"));

            string output = await process.StandardOutput.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);

            if (process.ExitCode != 0)
                return GpuVramInfo.Unavailable(Loc.F("vram.smi.exitCode", process.ExitCode));

            return Parse(output);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // nvidia-smi no está instalado: GPU no NVIDIA, o sin drivers. La placa se nombra
            // igual, por DXGI (E5); lo que no se puede saber es cuánta memoria tiene libre,
            // y el semáforo queda en «desconocido», que no impide generar.
            var placa = HardwareInfoService.ReadGpus()
                .Where(g => g.IsDiscrete).OrderByDescending(g => g.DedicatedVramMb).FirstOrDefault();
            return GpuVramInfo.Unavailable(placa != null
                ? Loc.F("vram.smi.notNvidia", placa.Name, placa.DedicatedVramMb / 1024.0)
                : Loc.T("vram.smi.missing"));
        }
        catch (OperationCanceledException)
        {
            return GpuVramInfo.Unavailable(Loc.T("vram.smi.cancelled"));
        }
        catch (Exception ex)
        {
            return GpuVramInfo.Unavailable(Loc.F("vram.smi.error", ex.Message));
        }
    }

    /// <summary>
    /// Parsea la primera línea del CSV. Con varias GPU se usa la primera,
    /// que es la que llama.cpp toma como Device 0.
    /// </summary>
    internal static GpuVramInfo Parse(string csvOutput)
    {
        if (string.IsNullOrWhiteSpace(csvOutput))
            return GpuVramInfo.Unavailable(Loc.T("vram.smi.noData"));

        string firstLine = csvOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)[0]
            .Trim();

        var parts = firstLine.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length < 3)
            return GpuVramInfo.Unavailable(Loc.T("vram.smi.badFormat"));

        if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int total) ||
            !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int free))
        {
            return GpuVramInfo.Unavailable(Loc.T("vram.smi.badValues"));
        }

        return new GpuVramInfo
        {
            Available = true,
            Name = parts[0],
            TotalMb = total,
            FreeMb = free
        };
    }
}
