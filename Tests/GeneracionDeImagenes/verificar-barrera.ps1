# Verifica la barrera de compilación de E4 sobre el binario Forense.
#
# Busca los símbolos como BYTES, en cualquier alineación y en las dos codificaciones
# que usa un ensamblado .NET (UTF-8 para metadatos, UTF-16 para literales). Decodificar
# el archivo entero como texto desde el offset 0 da falsos negativos cuando una cadena
# empieza en offset impar: ese error ya se cometió una vez en este proyecto.
#
# El control positivo sobre el binario Debug no es opcional. Sin él, un patrón mal
# escrito "no encuentra nada" en Forense y parece una barrera sana.

param(
    [string]$Raiz = "$PSScriptRoot\..\.."
)

$forense = Join-Path $Raiz "bin\Forense\net8.0-windows\PR-AI-Assistant.dll"
$debug   = Join-Path $Raiz "bin\Debug\net8.0-windows\PR-AI-Assistant.dll"

foreach ($f in @($forense, $debug)) {
    if (-not (Test-Path $f)) {
        Write-Host "No está $f. Compilá las dos ediciones primero:" -ForegroundColor Red
        Write-Host "  dotnet build -c Debug" -ForegroundColor Red
        Write-Host "  dotnet build -c Forense" -ForegroundColor Red
        exit 1
    }
}

# Símbolos de 2.4 de la especificación. Los dos de E4.3 se marcan como todavía
# inexistentes para que su ausencia no se lea como barrera verificada.
$simbolos = @(
    @{ n = "ImageServerManager";   futuro = $false },
    @{ n = "ImageGenClient";       futuro = $false },
    @{ n = "ImageServerConfig";    futuro = $false },
    @{ n = "ImageGenRequest";      futuro = $false },
    @{ n = "sd-server";            futuro = $false },
    @{ n = "/sdcpp/v1/img_gen";    futuro = $false },
    @{ n = "--diffusion-model";    futuro = $false },
    @{ n = "--offload-to-cpu";     futuro = $false },
    # IMAGE_GEN no va en esta lista: es una constante de preprocesador y no llega a
    # ningún binario, así que buscarla daría "ausente en Forense" siempre, incluso con
    # la barrera desmontada. El control positivo lo detectó al escribir este script.
    @{ n = "ImageModelService";    futuro = $false },
    @{ n = "ImageModelPackage";    futuro = $false },
    # E4.3
    @{ n = "ImagePackageDownloader";        futuro = $false },
    @{ n = "ImagePackageSelectorViewModel"; futuro = $false },
    @{ n = "ImageVramArbiter";              futuro = $false },
    @{ n = "image_models.json";             futuro = $false },
    @{ n = "Comfy-Org/z_image_turbo";       futuro = $false },
    # E4.4 — la vista de generación
    @{ n = "ImageGenerationViewModel";      futuro = $false },
    @{ n = "ImageGenerationView";           futuro = $false },
    @{ n = "ImageGenMetadata";              futuro = $false },
    @{ n = "ImageEngineLocator";            futuro = $false },
    @{ n = "ImageOutputFolder";             futuro = $false },
    @{ n = "ImagenesGeneradas";             futuro = $false }
)

function Buscar([byte[]]$heno, [byte[]]$aguja) {
    $limite = $heno.Length - $aguja.Length
    for ($i = 0; $i -le $limite; $i++) {
        $j = 0
        while ($j -lt $aguja.Length -and $heno[$i + $j] -eq $aguja[$j]) { $j++ }
        if ($j -eq $aguja.Length) { return $true }
    }
    return $false
}

function Contiene([byte[]]$bytes, [string]$texto) {
    if (Buscar $bytes ([Text.Encoding]::UTF8.GetBytes($texto))) { return $true }
    if (Buscar $bytes ([Text.Encoding]::Unicode.GetBytes($texto))) { return $true }
    return $false
}

$bytesForense = [IO.File]::ReadAllBytes($forense)
$bytesDebug   = [IO.File]::ReadAllBytes($debug)

"Forense: {0:N0} bytes" -f $bytesForense.Length
"Debug  : {0:N0} bytes" -f $bytesDebug.Length
""
"{0,-24} {1,-10} {2,-10} {3}" -f "simbolo", "Forense", "Debug", "veredicto"
"-" * 72

$fallas = 0
foreach ($s in $simbolos) {
    $enForense = Contiene $bytesForense $s.n
    $enDebug   = Contiene $bytesDebug   $s.n

    if ($s.futuro) {
        # Todavía no existe en ninguna edición: su ausencia no prueba nada.
        $veredicto = if (-not $enForense -and -not $enDebug) { "aun no existe (E4.3)" }
                     else { "REVISAR: ya existe, sacar el marcador de futuro" }
        $color = "DarkGray"
    }
    elseif ($enForense) {
        $veredicto = "FALLA: esta en Forense"; $color = "Red"; $fallas++
    }
    elseif (-not $enDebug) {
        # Control positivo: si tampoco está en Debug, el patrón no prueba nada.
        $veredicto = "FALLA: tampoco en Debug, el control no sirve"; $color = "Red"; $fallas++
    }
    else {
        $veredicto = "ok: ausente en Forense, presente en Debug"; $color = "Green"
    }

    Write-Host ("{0,-24} {1,-10} {2,-10} {3}" -f $s.n,
        $(if ($enForense) { "SI" } else { "no" }),
        $(if ($enDebug)   { "SI" } else { "no" }),
        $veredicto) -ForegroundColor $color
}

""
if ($fallas -eq 0) { "Barrera verificada: 0 fallas."; exit 0 }
else { "BARRERA ROTA: $fallas fallas."; exit 1 }
