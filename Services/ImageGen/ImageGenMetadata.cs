using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace PRImager.AIAssistant.Services.ImageGen;

/// <summary>
/// Todo lo que hace falta para volver a producir una imagen, escrito al lado del PNG.
///
/// La sección 3.3 de la especificación pedía prompt, semilla, pasos, CFG, sampler y
/// modelo. **Esa lista no alcanza.** Comparando SHA-256 con semilla fija, E4.0 midió que
/// tres flags de EJECUCIÓN cambian la imagen:
///
/// | comparación                          | ¿misma imagen? |
/// |--------------------------------------|----------------|
/// | baseline vs <c>--offload-to-cpu</c>  | sí, bit a bit  |
/// | baseline vs <c>--max-vram</c>        | sí, bit a bit  |
/// | sin fa vs <c>--diffusion-fa</c>      | **no**         |
/// | sin tiling vs <c>--vae-tiling</c>    | **no**         |
/// | GPU vs <c>--backend te=cpu</c>       | **no**         |
///
/// Es incómodo justamente porque son flags de rendimiento, que uno esperaría que no
/// afecten el resultado. Por eso además del desglose se guarda **la línea de comandos
/// entera**: si alguna vez se agrega una palanca nueva, queda registrada sin que haya
/// que acordarse de sumarla a este archivo.
///
/// La línea de comandos no alcanzó para una: **el build del motor**, que también cambia
/// los bytes y no aparece en ningún flag. Por eso <see cref="ImageGenEngineInfo.Build"/>.
/// </summary>
public sealed class ImageGenMetadata
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>Fecha de generación en ISO 8601. Invariante: un log con la coma decimal
    /// del locale fue el defecto 4.1 del maestro y no se repite.</summary>
    public string GeneratedAt { get; init; } =
        DateTime.Now.ToString("yyyy-MM-dd'T'HH:mm:ssK", CultureInfo.InvariantCulture);

    public string Prompt { get; init; } = "";
    public string NegativePrompt { get; init; } = "";

    public int Width { get; init; }
    public int Height { get; init; }
    public int Steps { get; init; }
    public double CfgScale { get; init; }

    /// <summary>
    /// Semilla concreta. **Nunca -1.**
    ///
    /// El motor acepta -1 para pedir una semilla aleatoria, pero la respuesta del job
    /// —<c>completed, created, error, id, kind, queue_position, result, started,
    /// status</c>— no devuelve cuál usó. Guardar -1 sería prometer una reproducibilidad
    /// que no existe, así que la aleatoria la sortea la aplicación y manda el número.
    /// </summary>
    public long Seed { get; init; }

    /// <summary>Sampler pedido. Vacío significa el que elige el motor para la familia.</summary>
    public string Sampler { get; init; } = "";
    public string Scheduler { get; init; } = "";

    public ImageGenModelInfo Model { get; init; } = new();
    public ImageGenEngineInfo Engine { get; init; } = new();

    /// <summary>Cuánto tardó, para poder comparar configuraciones.</summary>
    public double ElapsedSeconds { get; init; }

    /// <summary>
    /// Arma la metadata a partir de lo que efectivamente se pidió y de la configuración
    /// con la que está corriendo el motor.
    /// </summary>
    public static ImageGenMetadata For(
        ImageGenRequest request,
        ImageModelPackage package,
        ImageServerConfig config,
        double elapsedSeconds,
        string engineBuild = ImageEngineVersion.Unknown) => new()
        {
            Prompt = request.Prompt,
            NegativePrompt = request.NegativePrompt,
            Width = request.Width,
            Height = request.Height,
            Steps = request.Steps,
            CfgScale = request.CfgScale,
            Seed = request.Seed,
            Sampler = request.SampleMethod ?? "",
            Scheduler = request.Scheduler ?? "",
            ElapsedSeconds = Math.Round(elapsedSeconds, 2),
            Model = new ImageGenModelInfo
            {
                PackageId = package.Id,
                DisplayName = package.DisplayName,
                Family = package.Family,
                DiffusionModel = Path.GetFileName(package.DiffusionModelPath),
                TextEncoder = Path.GetFileName(package.TextEncoderPath),
                Vae = Path.GetFileName(package.VaePath)
            },
            Engine = new ImageGenEngineInfo
            {
                Executable = Path.GetFileName(config.ServerExecutablePath),
                Build = engineBuild,
                FlashAttention = config.FlashAttention,
                VaeTiling = config.VaeTiling,
                OffloadToCpu = config.OffloadToCpu,
                // ImageServerConfig no expone --backend, así que hoy siempre es el
                // predeterminado. Se escribe igual porque la ausencia del flag ES el
                // dato: con `--backend te=cpu` la imagen sale distinta.
                Backend = "predeterminado",
                CommandLine = config.ToCommandLineArgs()
            }
        };

    /// <summary>Ruta del archivo de metadata que corresponde a una imagen.</summary>
    public static string SidecarPathFor(string imagePath) =>
        Path.ChangeExtension(imagePath, ".json");

    /// <summary>
    /// Escribe la metadata al lado de la imagen. Devuelve la ruta escrita, o null si no
    /// se pudo: que falle el sidecar no debe perder la imagen, que es lo que el usuario
    /// pidió.
    /// </summary>
    public async Task<string?> WriteAsync(string imagePath, CancellationToken ct = default)
    {
        try
        {
            var destino = SidecarPathFor(imagePath);
            var json = JsonSerializer.Serialize(this, JsonOptions);
            await File.WriteAllTextAsync(destino, json, Encoding.UTF8, ct).ConfigureAwait(false);
            return destino;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Vuelve a leer una metadata escrita. La usa el banco de pruebas.</summary>
    public static ImageGenMetadata? Read(string sidecarPath)
    {
        try
        {
            return JsonSerializer.Deserialize<ImageGenMetadata>(
                File.ReadAllText(sidecarPath), JsonOptions);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Resumen de una línea para la galería.</summary>
    [JsonIgnore]
    public string ShortSummary =>
        $"{Width}×{Height} · {Steps} pasos · CFG {CfgScale.ToString("0.#", CultureInfo.InvariantCulture)} · semilla {Seed}";
}

public sealed class ImageGenModelInfo
{
    public string PackageId { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string Family { get; init; } = "";
    public string DiffusionModel { get; init; } = "";
    public string TextEncoder { get; init; } = "";
    public string Vae { get; init; } = "";
}

public sealed class ImageGenEngineInfo
{
    public string Executable { get; init; } = "";

    /// <summary>Commit del motor (pendiente 6.6). Cambia los bytes de la imagen aunque
    /// a la vista sea la misma. Los sidecars anteriores al 20 de octubre de 2026 no lo
    /// tienen: salieron todos de <c>bfbef5b</c>, el único build que usó la aplicación.</summary>
    public string Build { get; init; } = "";

    /// <summary>--diffusion-fa. Cambia la imagen.</summary>
    public bool FlashAttention { get; init; }

    /// <summary>--vae-tiling. Cambia la imagen.</summary>
    public bool VaeTiling { get; init; }

    /// <summary>--offload-to-cpu. NO cambia la imagen; se guarda para explicar el tiempo.</summary>
    public bool OffloadToCpu { get; init; }

    /// <summary>Asignación de backend. Cambia la imagen.</summary>
    public string Backend { get; init; } = "";

    /// <summary>La línea entera, para que ninguna palanca futura quede sin registrar.</summary>
    public string CommandLine { get; init; } = "";
}

/// <summary>
/// Dónde aterrizan las imágenes generadas.
///
/// Van a %APPDATA%, fuera de bin\, con el mismo criterio que la carpeta de modelos de
/// imagen de E4.3: la de descargas de E3 apunta adentro de bin\ y cualquier limpieza de
/// build se lleva lo descargado (pendiente 6.3 del maestro). No se repite el error.
///
/// Como la carpeta no es obvia para el usuario, la vista tiene un botón que la abre.
/// </summary>
public static class ImageOutputFolder
{
    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "PatagoniaRobot", "PR-AIA", "ImagenesGeneradas");

    /// <summary>
    /// Arma un nombre libre para una imagen nueva: fecha, hora y semilla. El prompt NO
    /// entra en el nombre —puede ser larguísimo y traer caracteres que Windows rechaza—;
    /// está en el sidecar, que es donde se lo busca.
    /// </summary>
    public static string BuildPath(string directory, long seed, DateTime? when = null)
    {
        var momento = when ?? DateTime.Now;
        var baseName = momento.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + $"-{seed}";

        var candidata = Path.Combine(directory, baseName + ".png");
        int n = 2;
        while (File.Exists(candidata))
            candidata = Path.Combine(directory, $"{baseName}-{n++}.png");

        return candidata;
    }
}
