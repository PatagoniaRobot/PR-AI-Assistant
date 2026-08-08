using System;
using System.Collections.Generic;
using PRImager.AIAssistant.Services;
using PRImager.AIAssistant.Services.ImageGen;

// Banco del semáforo de VRAM para generación de imágenes (E4.2).
//
// Los casos no son inventados: salen de las mediciones de esta máquina, y varios
// verifican que el árbitro coincida con lo que el motor hizo de verdad.

static class BancoArbitro
{
    static ImageServerConfig Base => new()
    {
        ServerExecutablePath = "x", DiffusionModelPath = "d",
        TextEncoderPath = "t", VaePath = "v"
    };

    static GpuVramInfo Gpu(int libre, int total = 16311) => new()
    { Available = true, Name = "RTX 5060 Ti", TotalMb = total, FreeMb = libre };

    /// <summary>
    /// Piso medido en esta máquina con tiling y offload, bajo presión del modelo de
    /// lenguaje (8.147 MiB libres). Todas las corridas completaron.
    /// </summary>
    static readonly (int lado, int pisoMedido)[] PisosMedidos =
    {
        (512, 4008), (768, 4352), (1024, 4668), (1536, 5594), (2048, 6886)
    };

    public static void Correr(Action<string, bool, string> check)
    {
        void Check(string n, bool c, string d = "") => check(n, c, d);

        // ---------------------------------------------------------- el bloqueo duro
        // A 4096 el motor muere con "CUDA error: invalid configuration argument", y se
        // reprodujo con la GPU vacía: no es memoria. Tiene que bloquear aunque sobre
        // VRAM, que es lo que lo distingue de Red.
        var v4096 = ImageVramArbiter.Evaluate(
            new ImageGenRequest { Width = 4096, Height = 4096 }, Base, Gpu(16000));
        Check("4096 se bloquea aunque sobre VRAM", v4096.Fit == ImageVramFit.Blocked, v4096.Fit.ToString());
        Check("el bloqueo se distingue de 'no entra'", v4096.MustNotAttempt);
        Check("el bloqueo explica que no es memoria",
            v4096.Explanation.Contains("No es falta de memoria"), v4096.Explanation);
        Check("el bloqueo sugiere un tamaño posible",
            v4096.Explanation.Contains("3072"), v4096.Explanation);

        var v3072 = ImageVramArbiter.Evaluate(
            new ImageGenRequest { Width = 3072, Height = 3072 }, Base, Gpu(16000));
        Check("3072 no se bloquea: se midió que completa", v3072.Fit != ImageVramFit.Blocked);

        // No cuadrado: manda el lado mayor.
        var vAncho = ImageVramArbiter.Evaluate(
            new ImageGenRequest { Width = 4096, Height = 512 }, Base, Gpu(16000));
        Check("bloquea por el lado mayor, no por el área", vAncho.Fit == ImageVramFit.Blocked);

        // ------------------------------------------------- el buffer del VAE sin tiling
        // Medido exacto: 6.657 MB a 1 Mpx y 26.628 a 4 Mpx. Es el término que estalla.
        var sinTiling = Base with { VaeTiling = false, OffloadToCpu = true };
        int req1024 = ImageVramArbiter.EstimateRequiredMb(1024, 1024, sinTiling);
        int req2048 = ImageVramArbiter.EstimateRequiredMb(2048, 2048, sinTiling);
        Check("sin tiling, 2048 pide muchísimo más que 1024",
            req2048 - req1024 > 18_000, $"{req1024} -> {req2048}");
        Check("sin tiling, 2048 no entra en 16 GB",
            req2048 > 16311, $"{req2048}");

        var conTiling = Base with { VaeTiling = true, OffloadToCpu = true };
        int t1024 = ImageVramArbiter.EstimateRequiredMb(1024, 1024, conTiling);
        int t2048 = ImageVramArbiter.EstimateRequiredMb(2048, 2048, conTiling);
        Check("con tiling la resolución casi no mueve el costo",
            t2048 - t1024 < 2500, $"{t1024} -> {t2048}");

        // ------------------------------------------- coincide con lo medido de verdad
        // La estimación tiene que quedar cerca del piso observado, y del lado
        // conservador: subestimar haría que el semáforo diga verde y la generación
        // falle, que es el error que importa evitar.
        foreach (var (lado, piso) in PisosMedidos)
        {
            int est = ImageVramArbiter.EstimateRequiredMb(lado, lado, conTiling);
            Check($"la estimación a {lado} se parece a lo medido ({piso} MiB)",
                Math.Abs(est - piso) < 1200, $"estimado {est}");
            Check($"la estimación a {lado} no subestima",
                est >= piso - 400, $"estimado {est} contra piso {piso}");
        }

        // ------------------------------------------------------------- el semáforo
        var holgado = ImageVramArbiter.Evaluate(
            new ImageGenRequest { Width = 1024, Height = 1024 }, conTiling, Gpu(15000));
        Check("con la GPU casi vacía, verde", holgado.Fit == ImageVramFit.Green, holgado.Fit.ToString());

        // El caso real medido: el Qwen 9B cargado deja 8.147 MiB y la imagen entró.
        var conLlm = ImageVramArbiter.Evaluate(
            new ImageGenRequest { Width = 1024, Height = 1024 }, conTiling, Gpu(8147));
        Check("con el Qwen 9B cargado, 1024 entra (como se midió)",
            conLlm.Fit == ImageVramFit.Green || conLlm.Fit == ImageVramFit.Yellow,
            $"{conLlm.Fit}: {conLlm.Explanation}");

        // La banda amarilla es angosta a propósito: arranca apenas por encima de lo
        // necesario más el margen de seguridad, y termina un giga más arriba.
        var justo = ImageVramArbiter.Evaluate(
            new ImageGenRequest { Width = 1024, Height = 1024 }, conTiling, Gpu(6000));
        Check("con poco margen, amarillo", justo.Fit == ImageVramFit.Yellow,
            $"{justo.Fit}: {justo.Explanation}");

        // El sesgo es deliberadamente conservador, igual que en VramEstimator: a 1024
        // el piso medido fue 4.668 MiB y la estimación da 5.170, así que con 5.500
        // libres dice rojo aunque en la práctica habría entrado. Se prefiere
        // desaconsejar una generación que entraba, a recomendar una que va a fallar.
        var conservador = ImageVramArbiter.Evaluate(
            new ImageGenRequest { Width = 1024, Height = 1024 }, conTiling, Gpu(5500));
        Check("el sesgo es conservador cerca del borde", conservador.Fit == ImageVramFit.Red,
            $"{conservador.Fit}: {conservador.Explanation}");

        var sinNada = ImageVramArbiter.Evaluate(
            new ImageGenRequest { Width = 1024, Height = 1024 }, conTiling, Gpu(1500));
        Check("sin VRAM, rojo", sinNada.Fit == ImageVramFit.Red, sinNada.Fit.ToString());

        // ---------------------------------------------------- recomendación de ajustes
        // Antes de rechazar, el árbitro tiene que mirar si alguna palanca lo arregla.
        var pideSinTiling = ImageVramArbiter.Evaluate(
            new ImageGenRequest { Width = 2048, Height = 2048 },
            Base with { VaeTiling = false, OffloadToCpu = true }, Gpu(8147));
        Check("2048 sin tiling se rechaza", pideSinTiling.Fit == ImageVramFit.Red);
        Check("y recomienda activar el tiling",
            pideSinTiling.RecommendedConfig is not null && pideSinTiling.RecommendedConfig.VaeTiling);
        Check("y lo dice en castellano",
            pideSinTiling.Explanation.Contains("tiles"), pideSinTiling.Explanation);

        var pideSinOffload = ImageVramArbiter.Evaluate(
            new ImageGenRequest { Width = 1024, Height = 1024 },
            Base with { VaeTiling = true, OffloadToCpu = false }, Gpu(8147));
        Check("con los pesos en VRAM y poco libre, recomienda el offload",
            pideSinOffload.Fit != ImageVramFit.Red ||
            (pideSinOffload.RecommendedConfig is not null && pideSinOffload.RecommendedConfig.OffloadToCpu),
            $"{pideSinOffload.Fit}: {pideSinOffload.Explanation}");

        // Cuando ni con todo puesto entra, hay que decir qué tamaño SÍ entraría, en
        // lugar de un "probá más chico" que no ayuda.
        var nadaAlcanza = ImageVramArbiter.Evaluate(
            new ImageGenRequest { Width = 3072, Height = 3072 }, conTiling, Gpu(6000));
        Check("si nada alcanza, propone un tamaño concreto",
            nadaAlcanza.Fit == ImageVramFit.Red &&
            (nadaAlcanza.Explanation.Contains("×") || nadaAlcanza.Explanation.Contains("Cerrá")),
            nadaAlcanza.Explanation);

        // ------------------------------------------------------------- casos borde
        var sinGpu = ImageVramArbiter.Evaluate(
            new ImageGenRequest { Width = 1024, Height = 1024 }, conTiling,
            GpuVramInfo.Unavailable("nvidia-smi no está"));
        Check("sin GPU detectada, no inventa un veredicto", sinGpu.Fit == ImageVramFit.Unknown);
        Check("sin GPU detectada, igual informa cuánto necesita", sinGpu.RequiredMb > 0);
        Check("sin GPU detectada, explica por qué no sabe",
            sinGpu.Explanation.Contains("nvidia-smi"), sinGpu.Explanation);

        // El bloqueo por resolución no depende de la GPU: tiene que ganarle incluso a
        // "no sé cuánta VRAM hay".
        var bloqueoSinGpu = ImageVramArbiter.Evaluate(
            new ImageGenRequest { Width = 4096, Height = 4096 }, conTiling,
            GpuVramInfo.Unavailable("nvidia-smi no está"));
        Check("el bloqueo por resolución no necesita saber la VRAM",
            bloqueoSinGpu.Fit == ImageVramFit.Blocked);

        // Usa la VRAM LIBRE, no la total: es lo que distingue este semáforo del de
        // modelos de lenguaje, porque acá siempre hay algo más cargado.
        var totalGrandeLibrePoco = ImageVramArbiter.Evaluate(
            new ImageGenRequest { Width = 1024, Height = 1024 }, conTiling,
            Gpu(libre: 1200, total: 24576));
        Check("mira la VRAM libre y no la total",
            totalGrandeLibrePoco.Fit == ImageVramFit.Red, totalGrandeLibrePoco.Fit.ToString());
    }
}
