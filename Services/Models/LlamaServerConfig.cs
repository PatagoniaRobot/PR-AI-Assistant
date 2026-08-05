using System.IO;

namespace PRImager.AIAssistant.Services.Models;

/// <summary>
/// Configuración para iniciar llama-server.exe.
/// </summary>
public record LlamaServerConfig
{
    /// <summary>Ruta al ejecutable llama-server.exe.</summary>
    public required string ServerExecutablePath { get; init; }

    /// <summary>Ruta al archivo de modelo .gguf.</summary>
    public required string ModelPath { get; init; }

    /// <summary>Ruta al archivo mmproj (projector multimodal). Puede ser null si no se usa visión.</summary>
    public string? MmprojPath { get; init; }

    /// <summary>Puerto HTTP del servidor.</summary>
    public int Port { get; init; } = 8787;

    /// <summary>Host de escucha.</summary>
    public string Host { get; init; } = "127.0.0.1";

    /// <summary>Tamaño del contexto en tokens.</summary>
    public int CtxSize { get; init; } = 4096;

    /// <summary>Capas del modelo a descargar en GPU. -1 = todas.</summary>
    public int GpuLayers { get; init; } = -1;

    /// <summary>Número de hilos de CPU para inferencia.</summary>
    public int Threads { get; init; } = Environment.ProcessorCount / 2;

    /// <summary>
    /// Construye la lista de argumentos de línea de comandos para llama-server.
    /// </summary>
    public string ToCommandLineArgs()
    {
        var args = $"-m \"{ModelPath}\" --host {Host} --port {Port} -c {CtxSize} -ngl {GpuLayers} -t {Threads}";

        if (!string.IsNullOrEmpty(MmprojPath) && File.Exists(MmprojPath))
            args += $" --mmproj \"{MmprojPath}\"";

        return args;
    }
}
