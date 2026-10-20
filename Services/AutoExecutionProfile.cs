using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using PRImager.AIAssistant.Services.Models;

namespace PRImager.AIAssistant.Services;

/// <summary>
/// El perfil de ejecución «Automático» (etapa E5): contexto, capas en la placa e hilos
/// calculados para el modelo elegido y la PC donde corre, en lugar de los valores fijos de
/// Rendimiento, Equilibrado y Máx. Calidad.
///
/// Es el perfil por defecto porque los fijos suponen una PC: «Equilibrado» manda todas las
/// capas a la placa con 32k de contexto, que en una placa de 6 GB con un modelo de 9B no
/// arranca. Los fijos siguen estando para quien quiera elegir a mano.
///
/// Lee la cabecera completa del modelo —hasta la tabla de tensores, ~16 MB en el peor caso
/// medido— y la placa por DXGI, sin nvidia-smi: las dos cosas son locales y rápidas, así
/// que se hace en el momento de elegir y no queda nada pendiente al arrancar el servidor.
/// </summary>
public static class AutoExecutionProfile
{
    public const string Id = "auto";

    public static ExecutionProfile Create() => new()
    {
        Id = Id,
        DisplayName = "Automático",
        GpuLayers = RunRecommendation.AllLayers,
        CtxSize = ModelRecommender.MinComfortContext,
        Threads = 0
    };

    public static bool IsAuto(ExecutionProfile? p) =>
        p != null && string.Equals(p.Id, Id, StringComparison.OrdinalIgnoreCase);

    // La PC no cambia durante la sesión; el archivo puede cambiar, y por eso la clave lleva
    // tamaño y fecha.
    private static readonly ConcurrentDictionary<string, HardwareInfo> HardwareCache = new();
    private static readonly ConcurrentDictionary<string, RunRecommendation?> Cache = new();

    /// <summary>La PC, sin nvidia-smi: DXGI, RAM y núcleos alcanzan para recomendar.</summary>
    public static HardwareInfo Hardware(string serverDirectory) =>
        HardwareCache.GetOrAdd(serverDirectory, dir => HardwareInfoService.ReadBasic(dir));

    /// <summary>
    /// La recomendación para un modelo en disco, o null si su cabecera no se puede leer.
    /// El proyector de visión, si lo hay, se suma a lo que va a la placa.
    /// </summary>
    public static RunRecommendation? Recommend(string modelPath, string? mmprojPath, HardwareInfo hw)
    {
        try
        {
            if (!File.Exists(modelPath)) return null;
            var info = new FileInfo(modelPath);
            long extra = mmprojPath != null && File.Exists(mmprojPath) ? new FileInfo(mmprojPath).Length : 0;
            string key = $"{modelPath}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|{extra}|{hw.UsableVramMb}|{hw.TotalRamMb}";

            return Cache.GetOrAdd(key, _ =>
            {
                var header = GgufInspector.TryReadFileFull(modelPath);
                return header == null ? null
                    : ModelRecommender.RecommendRun(header, info.Length, hw, extraGpuBytes: extra);
            });
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Calcula y escribe en la configuración lo que el servidor va a usar. Devuelve la
    /// recomendación, o null si no se pudo leer el modelo: en ese caso quedan valores
    /// prudentes —8k de contexto, todo a la placa— y el error de carga dirá el resto.
    /// </summary>
    public static RunRecommendation? Apply(AppSettings settings, ModelInfo model)
    {
        var hw = Hardware(ServerDirectory(settings));
        string modelPath = ModelPath(settings, model);
        var rec = Recommend(modelPath, MmprojPath(settings, model, modelPath), hw);

        settings.CtxSize = rec?.ContextTokens ?? ModelRecommender.MinComfortContext;
        settings.GpuLayers = rec?.GpuLayers ?? RunRecommendation.AllLayers;
        settings.Threads = rec?.Threads ?? ModelRecommender.Threads(hw);
        return rec;
    }

    /// <summary>El texto bajo los botones de perfil.</summary>
    public static string Describe(RunRecommendation? rec) => rec == null
        ? "automático: no se pudo leer el modelo, se usan 8k y todo a la placa"
        : $"automático: {rec.Summary}";

    // --- rutas, con el mismo criterio que ChatOrchestrator al lanzar el servidor ---

    public static string ServerDirectory(AppSettings settings)
    {
        string exe = Path.IsPathRooted(settings.LlamaServerExecutable)
            ? settings.LlamaServerExecutable
            : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, settings.LlamaServerExecutable);
        return Path.GetDirectoryName(exe) ?? AppDomain.CurrentDomain.BaseDirectory;
    }

    public static string ModelPath(AppSettings settings, ModelInfo model) =>
        Path.Combine(model.IsDiscovered ? model.DirectoryOverride : ModelDirectoryResolver.ResolveModelsDirectory(settings),
                     model.ModelFileName);

    private static string? MmprojPath(AppSettings settings, ModelInfo model, string modelPath)
    {
        string dir = Path.GetDirectoryName(modelPath) ?? "";
        if (!string.IsNullOrEmpty(model.MmprojFileName)) return Path.Combine(dir, model.MmprojFileName);
        try
        {
            return Directory.Exists(dir) ? Directory.GetFiles(dir, "*mmproj*").FirstOrDefault() : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
