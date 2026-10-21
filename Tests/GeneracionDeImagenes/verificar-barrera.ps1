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
    [string]$Raiz = "$PSScriptRoot\..\..\src\PR-AI-Assistant"
)

$forense = Join-Path $Raiz "bin\Forense\net10.0-windows\PR-AI-Assistant.dll"
$debug   = Join-Path $Raiz "bin\Debug\net10.0-windows\PR-AI-Assistant.dll"

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
    # pendiente 6.6 — el build del motor en el sidecar
    @{ n = "ImageEngineVersion";            futuro = $false },
    @{ n = "ImagenesGeneradas";             futuro = $false },
    # E4.5 — integración
    @{ n = "ImageGenerationWindow";         futuro = $false },
    @{ n = "IMAGE_GEN_OPENED";              futuro = $false },
    @{ n = "IMAGE_MODEL_SELECTED";          futuro = $false },
    @{ n = "IMAGE_ENGINE_CRASHED";          futuro = $false },

    # Excepción declarada. Los dos comandos son propiedades de MainViewModel, que se
    # compila entero en las dos ediciones, así que sus NOMBRES llegan a los metadatos del
    # binario Forense. El cuerpo de lo que ejecutan no: está dentro de #if IMAGE_GEN, así
    # que ahí son métodos vacíos y CanExecute devuelve false siempre.
    #
    # Se aceptan con el mismo criterio con el que 1.3 del maestro admite ResolveDownload-
    # Directory y el DTO WebSource: son andamiaje local inerte, no la capacidad. Sacarlos
    # exigiría quitar el binding del XAML, que no admite #if, y eso rompería con errores
    # de binding el banco de CargaDeArchivos, que sí corre en las dos ediciones.
    #
    # Se listan igual —en vez de omitirlos— para que la excepción esté a la vista y se
    # revise, y para que un cambio de nombre no la haga desaparecer en silencio.
    @{ n = "OpenImageGenerationCommand";    inerte = $true;
       motivo = "propiedad de MainViewModel; el cuerpo va tras #if y CanExecute es false" },
    @{ n = "OpenImagePackagesCommand";      inerte = $true;
       motivo = "idem" }
)

# ImageGenEnabled NO va en esta lista, y la omisión es deliberada.
#
# Vive en UsageProfile, que SÍ se compila en Forense: es configuración, no código de
# imágenes. Buscarla acá daría una falla legítima y no probaría nada.
#
# Lo que hay que garantizar es otra cosa —que en esa edición la capacidad no pueda quedar
# en true aunque el profiles.json lo pida— y eso no se verifica contando bytes. Lo prueba
# el banco cargando el binario Forense en un proceso aparte y ejerciendo
# EffectiveProfile.Resolve sobre él. Ver Integracion.cs.

# ImageGenEnabled NO va en esta lista, y la omisión es deliberada.
#
# Vive en UsageProfile, que SÍ se compila en Forense: es configuración, no código de
# imágenes, con el mismo criterio con el que 1.3 admite el DTO WebSource. Buscarla acá
# daría una falla legítima y no probaría nada.
#
# Lo que hay que garantizar es otra cosa —que en esa edición la capacidad no pueda
# quedar en true aunque el profiles.json lo pida— y eso no se puede verificar contando
# bytes. Lo prueba el banco, cargando el binario Forense en un proceso aparte y
# ejerciendo EffectiveProfile.Resolve sobre él. Ver Integracion.cs.

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
    elseif ($s.inerte) {
        # Excepción declarada: se espera que esté en las dos ediciones. Si DESAPARECIERA
        # de Forense habría que sacarlo de las excepciones, y si desapareciera de Debug
        # es que le cambiaron el nombre y esta entrada dejó de vigilar algo.
        if ($enForense -and $enDebug) {
            $veredicto = "aceptado inerte: " + $s.motivo; $color = "Yellow"
        }
        elseif (-not $enDebug) {
            $veredicto = "FALLA: no esta en Debug, la excepcion ya no vigila nada"
            $color = "Red"; $fallas++
        }
        else {
            $veredicto = "REVISAR: ya no esta en Forense, sacarlo de las excepciones"
            $color = "Red"; $fallas++
        }
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
