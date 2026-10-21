# Arma los dos paquetes de la edición estándar a partir de Publish\ (lo que deja publish.bat):
#
#   PR-AI-Assistant-<versión>.zip          para cualquier PC: solo el motor Vulkan (~0,25 GB)
#   PR-AI-Assistant-<versión>-NVIDIA.zip   con el motor CUDA además (~0,9 GB)
#
# Cada .zip lleva al lado su .sha256, con el mismo formato que las publicaciones de ForZip:
#   certutil -hashfile PR-AI-Assistant-1.0.0.zip SHA256
#
# La aplicación elige sola el motor según la placa y, si falta uno, usa el otro: por eso el
# paquete liviano anda también en NVIDIA. Ver docs\DISTRIBUCION.md.

param(
    [string]$Origen = "Publish",
    [string]$Destino = "Paquetes"
)
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem

$raiz = $PSScriptRoot
$Origen = Join-Path $raiz $Origen
$Destino = Join-Path $raiz $Destino
$csproj = Join-Path $raiz "src\PR-AI-Assistant\PR-AI-Assistant.csproj"
$version = ([xml](Get-Content $csproj -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1

# Lo que tiene que estar, y lo que no puede estar, antes de empaquetar nada
$faltan = @("PR-AI-Assistant.exe", "LICENSE", "NOTICE", "THIRD-PARTY-NOTICES.txt", "Config\settings.json",
            "Help\Help_PRAIA.html", "Tools\llama-server.exe", "Tools\vulkan\llama-server.exe") |
          Where-Object { -not (Test-Path (Join-Path $Origen $_)) }
if ($faltan) { throw "Falta en ${Origen}: $($faltan -join ', '). Correr publish.bat primero." }
$prohibidos = Get-ChildItem $Origen -Recurse -File -Include "ggml-rpc.dll", "rpc-server.exe", "*.gguf"
if ($prohibidos) { throw "No se distribuye: $($prohibidos.FullName -join ', ')" }

New-Item -ItemType Directory -Force $Destino | Out-Null
$trabajo = Join-Path ([System.IO.Path]::GetTempPath()) ("praia-paquetes-" + [guid]::NewGuid().ToString("N"))

function New-Paquete([string]$nombre, [bool]$conCuda) {
    $carpeta = Join-Path $trabajo "PR-AI-Assistant"
    if (Test-Path $carpeta) { Remove-Item $carpeta -Recurse -Force }
    Copy-Item $Origen $carpeta -Recurse
    Get-ChildItem $carpeta -Recurse -File -Filter "*.pdb" | Remove-Item -Force
    if (-not $conCuda) {
        # El motor CUDA son los archivos sueltos de Tools\; Tools\vulkan\ queda
        Get-ChildItem (Join-Path $carpeta "Tools") -File | Remove-Item -Force
    }
    # Models\ sale vacía: la aplicación se publica sin modelos
    New-Item -ItemType Directory -Force (Join-Path $carpeta "Models") | Out-Null

    $zip = Join-Path $Destino $nombre
    if (Test-Path $zip) { Remove-Item $zip -Force }
    # A mano y no con ZipFile.CreateFromDirectory: el de Windows PowerShell escribe las rutas
    # con «\», que el formato zip no admite; 7-Zip y unzip arman mal las carpetas
    $archivo = [System.IO.Compression.ZipFile]::Open($zip, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        $base = Split-Path $carpeta -Parent
        foreach ($f in Get-ChildItem $carpeta -Recurse -File) {
            $entrada = $f.FullName.Substring($base.Length + 1).Replace('\', '/')
            [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archivo, $f.FullName, $entrada,
                [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
        $archivo.CreateEntry("PR-AI-Assistant/Models/") | Out-Null
    }
    finally { $archivo.Dispose() }
    $hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    # Sin BOM y con LF, como lo lee sha256sum -c
    [System.IO.File]::WriteAllText("$zip.sha256", "$hash  $nombre`n")
    "{0}  {1:N0} MB  {2}" -f $nombre, ((Get-Item $zip).Length / 1MB), $hash
}

try {
    New-Item -ItemType Directory -Force $trabajo | Out-Null
    New-Paquete "PR-AI-Assistant-$version.zip" $false
    New-Paquete "PR-AI-Assistant-$version-NVIDIA.zip" $true
}
finally {
    if (Test-Path $trabajo) { Remove-Item $trabajo -Recurse -Force }
}
