using System.Collections.Generic;
using System.IO;
using System.Text.Json.Serialization;

namespace PRImager.AIAssistant.Services.ImageGen;

/// <summary>
/// Un modelo de imagen son TRES archivos, no uno: difusión, text encoder y VAE.
/// Eso rompe el modelo mental de <c>ModelInfo</c>, que asume un .gguf más un mmproj
/// opcional, y es la razón por la que no se reutiliza <c>ModelProfileService</c>: ese
/// servicio ya carga tres ejes de configuración encima (ExecutionProfile, ModelProfile
/// y UsageProfile) y un cuarto concepto con reglas distintas lo volvería ilegible.
///
/// El paquete también lleva los valores de generación que le corresponden. No son un
/// detalle: <c>/sdcpp/v1/capabilities</c> informa los defaults del MOTOR —20 pasos y
/// CFG 7.0— y con esos un modelo Turbo produce una imagen mala. Los buenos vienen de
/// acá y pisan a los del endpoint.
/// </summary>
public sealed record ImageModelPackage
{
    /// <summary>Identificador estable. Es lo que se persiste como selección.</summary>
    public string Id { get; init; } = "";

    public string DisplayName { get; init; } = "";

    /// <summary>Modelo de difusión (--diffusion-model).</summary>
    public string DiffusionModelPath { get; init; } = "";

    /// <summary>Text encoder (--llm).</summary>
    public string TextEncoderPath { get; init; } = "";

    /// <summary>Autoencoder (--vae).</summary>
    public string VaePath { get; init; } = "";

    /// <summary>CFG que corresponde a la familia. 1.0 en los Turbo destilados.</summary>
    public double DefaultCfgScale { get; init; } = 1.0;

    /// <summary>Pasos de muestreo. 8 en Z-Image Turbo.</summary>
    public int DefaultSteps { get; init; } = 8;

    /// <summary>Familia del modelo: z-image, flux, sdxl, sd15…</summary>
    public string Family { get; init; } = "z-image";

    /// <summary>
    /// True para el paquete que la aplicación conoce de fábrica. Se regenera si el
    /// archivo de configuración no existe o está corrupto, igual que los perfiles de
    /// uso de E1. Un paquete creado por el usuario no puede declararse de fábrica.
    /// </summary>
    public bool IsFactory { get; init; }

    // --- Estado en disco. No se persiste: se calcula al leer. ---

    /// <summary>Los tres componentes, en el orden en que los pide el motor.</summary>
    [JsonIgnore]
    public IEnumerable<(string Rol, string Ruta)> Components
    {
        get
        {
            yield return ("modelo de difusión", DiffusionModelPath);
            yield return ("text encoder", TextEncoderPath);
            yield return ("VAE", VaePath);
        }
    }

    /// <summary>Componentes que faltan en disco, por su rol.</summary>
    [JsonIgnore]
    public List<string> MissingComponents
    {
        get
        {
            var faltan = new List<string>();
            foreach (var (rol, ruta) in Components)
            {
                if (string.IsNullOrWhiteSpace(ruta) || !File.Exists(ruta))
                    faltan.Add(rol);
            }
            return faltan;
        }
    }

    [JsonIgnore]
    public bool IsComplete => MissingComponents.Count == 0;

    /// <summary>
    /// Suma de los tres archivos presentes, en bytes. Sirve para informar cuánto ocupa
    /// el paquete en disco.
    ///
    /// **No sirve como proxy de VRAM**, y conviene no usarlo para eso: el text encoder
    /// ocupa 3.555 MB cargado contra 2.497 de archivo, un 42% más. Para la VRAM está
    /// <see cref="ImageVramArbiter"/>, calibrado contra mediciones.
    /// </summary>
    [JsonIgnore]
    public long TotalSizeBytes
    {
        get
        {
            long total = 0;
            foreach (var (_, ruta) in Components)
            {
                if (!string.IsNullOrWhiteSpace(ruta) && File.Exists(ruta))
                    total += new FileInfo(ruta).Length;
            }
            return total;
        }
    }

    /// <summary>Texto para el selector cuando el paquete no está completo.</summary>
    [JsonIgnore]
    public string MissingText
    {
        get
        {
            var faltan = MissingComponents;
            if (faltan.Count == 0) return "";
            return faltan.Count == 1
                ? $"falta el {faltan[0]}"
                : "faltan " + string.Join(", ", faltan);
        }
    }

    /// <summary>Convierte el paquete en la configuración de arranque del servidor.</summary>
    public ImageServerConfig ToServerConfig(string serverExecutablePath, int port = 1234) => new()
    {
        ServerExecutablePath = serverExecutablePath,
        DiffusionModelPath = DiffusionModelPath,
        TextEncoderPath = TextEncoderPath,
        VaePath = VaePath,
        Port = port
    };

    /// <summary>Los parámetros de generación que corresponden a este paquete.</summary>
    public ImageGenRequest NewRequest(string prompt) => new()
    {
        Prompt = prompt,
        Steps = DefaultSteps,
        CfgScale = DefaultCfgScale
    };
}
