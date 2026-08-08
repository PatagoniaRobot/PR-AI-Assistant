using System.IO;
using System.Text;

namespace PRImager.AIAssistant.Services.ImageGen;

/// <summary>
/// Configuración para iniciar sd-server.exe, el servidor HTTP de stable-diffusion.cpp.
///
/// A diferencia de un modelo de lenguaje, un modelo de imagen son TRES archivos:
/// difusión, text encoder y VAE. No hay un solo .gguf que alcance.
/// </summary>
public record ImageServerConfig
{
    /// <summary>Ruta a sd-server.exe.</summary>
    public required string ServerExecutablePath { get; init; }

    /// <summary>Modelo de difusión (--diffusion-model).</summary>
    public required string DiffusionModelPath { get; init; }

    /// <summary>Text encoder (--llm). Para Z-Image es un Qwen3-4B.</summary>
    public required string TextEncoderPath { get; init; }

    /// <summary>Autoencoder (--vae).</summary>
    public required string VaePath { get; init; }

    /// <summary>Puerto HTTP. 1234 es el que trae el motor de fábrica.</summary>
    public int Port { get; init; } = 1234;

    public string Host { get; init; } = "127.0.0.1";

    /// <summary>
    /// Flash attention. Medido en E4.0: NO ahorra VRAM, pero baja el tiempo un 25%.
    /// Conviene dejarla siempre puesta.
    /// </summary>
    public bool FlashAttention { get; init; } = true;

    /// <summary>
    /// Decodificación del VAE por tiles. Es LA palanca de memoria, no una optimización
    /// menor: el buffer de decodificación a 1024x1024 son 6.657 MB y con tiling baja a
    /// 416 MB. Sin esto el pico roza el total de una GPU de 16 GB.
    /// </summary>
    public bool VaeTiling { get; init; } = true;

    /// <summary>
    /// Pesos en RAM, subiéndolos a VRAM solo durante el cómputo. Baja el pico de
    /// ~9.300 a ~5.800 MiB por 1,7 s de costo, y produce una imagen bit a bit idéntica
    /// a la de GPU pura. Es lo que permite convivir con el modelo de lenguaje.
    /// </summary>
    public bool OffloadToCpu { get; init; } = true;

    /// <summary>Hilos de CPU. -1 deja que el motor use los núcleos físicos.</summary>
    public int Threads { get; init; } = -1;

    public string BaseUrl => $"http://{Host}:{Port}";

    /// <summary>
    /// Devuelve el primero de los tres archivos del paquete que falte, o null si están
    /// todos. Se informa como dato y no como excepción: que falte un modelo es una
    /// situación normal, no un error de programación.
    /// </summary>
    public string? FirstMissingModelFile()
    {
        if (!File.Exists(DiffusionModelPath)) return DiffusionModelPath;
        if (!File.Exists(TextEncoderPath)) return TextEncoderPath;
        if (!File.Exists(VaePath)) return VaePath;
        return null;
    }

    public string ToCommandLineArgs()
    {
        var sb = new StringBuilder();
        sb.Append($"--diffusion-model \"{DiffusionModelPath}\"");
        sb.Append($" --llm \"{TextEncoderPath}\"");
        sb.Append($" --vae \"{VaePath}\"");
        sb.Append($" --listen-ip {Host} --listen-port {Port}");

        if (FlashAttention) sb.Append(" --diffusion-fa");
        if (VaeTiling) sb.Append(" --vae-tiling");
        if (OffloadToCpu) sb.Append(" --offload-to-cpu");
        if (Threads > 0) sb.Append($" -t {Threads}");

        // -v es necesario: el progreso por paso solo aparece en la salida detallada, y
        // la API no lo expone por ningún otro lado. Ver ImageServerManager.ParseProgress.
        sb.Append(" -v");

        // NO se usa --eager-load a propósito. Sin él, sd-server carga los pesos por job
        // y los libera al terminar, así que ocioso ocupa 0 MiB de VRAM. Activarlo haría
        // que retenga la memoria y rompería la convivencia con el modelo de lenguaje.

        return sb.ToString();
    }
}
