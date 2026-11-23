#Requires -Version 5.1
<#
    compilar.ps1 — Compila y publica IPTV 101.

    Uso:
        .\compilar.ps1                # Publica el ejecutable final en .\publicacion
        .\compilar.ps1 -Icono         # Regenera el ícono (SVG + ICO + PNG) antes de compilar
        .\compilar.ps1 -Ejecutar      # Compila y abre la app al terminar
        .\compilar.ps1 -Configuracion Debug

    Resultado: .\publicacion\IPTV101.exe (junto con libmpv-2.dll).
    IPTV 101 se instala SIN canales: la primera vez hay que importar una lista propia
    (archivo .m3u/.m3u8 o URL) desde el menú "···" → "Importar o gestionar listas…".
    No requiere permisos de administrador.
#>
[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')] [string] $Configuracion = 'Release',
    [string] $RutaLibmpv,
    [switch] $Icono,
    [switch] $Ejecutar
)

$ErrorActionPreference = 'Stop'
$raiz     = $PSScriptRoot
$proyecto = Join-Path $raiz 'src\IPTV101\IPTV101.csproj'
$salida   = Join-Path $raiz 'publicacion'
$runtime  = Join-Path $raiz 'runtime\libmpv-2.dll'

$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

function Fallo($msg) { Write-Host "  ✗ $msg" -ForegroundColor Red; exit 1 }

Write-Host "`n  IPTV 101 — compilación ($Configuracion)" -ForegroundColor Cyan

# 1. Verificar el SDK de .NET 10.
$sdk = (& dotnet --list-sdks 2>$null) | Where-Object { $_ -match '^10\.' }
if (-not $sdk) {
    Fallo "Falta el SDK de .NET 10. Instálalo con:  winget install Microsoft.DotNet.SDK.10"
}

# 2. Verificar el motor de video.
if (-not (Test-Path $runtime)) {
    if ($RutaLibmpv -and (Test-Path $RutaLibmpv)) {
        Write-Host "  · Copiando libmpv-2.dll desde $RutaLibmpv..."
        Copy-Item $RutaLibmpv $runtime -Force
    } elseif ($env:LIBMPV_PATH -and (Test-Path $env:LIBMPV_PATH)) {
        Write-Host "  · Copiando libmpv-2.dll desde `$env:LIBMPV_PATH..."
        Copy-Item $env:LIBMPV_PATH $runtime -Force
    } else {
        Fallo "Falta runtime\libmpv-2.dll (binario de ~116 MB). Descárgalo desde los Releases del repositorio (https://github.com/navarretenoir/iptv101/releases) o indica su ubicación con: .\compilar.ps1 -RutaLibmpv <ruta>\libmpv-2.dll"
    }
}

# 3. Ícono (opcional).
if ($Icono) {
    Write-Host "  · Generando ícono…"
    & dotnet run --project (Join-Path $raiz 'tools\IconGen\IconGen.csproj') -c Release -- (Join-Path $raiz 'iconos')
    if ($LASTEXITCODE -ne 0) { Fallo "No se pudo generar el ícono." }
}
if (-not (Test-Path (Join-Path $raiz 'iconos\iptv101.ico'))) {
    Fallo "Falta iconos\iptv101.ico. Ejecuta:  .\compilar.ps1 -Icono"
}

# 4. Publicar (dependiente del runtime de .NET, ya instalado en el equipo).
if (Test-Path $salida) { Remove-Item $salida -Recurse -Force }
Write-Host "  · Publicando…"
& dotnet publish $proyecto -c $Configuracion -o $salida --nologo -v minimal `
    -p:DebugType=none -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) { Fallo "La compilación falló." }

$exe = Join-Path $salida 'IPTV101.exe'
if (-not (Test-Path $exe)) { Fallo "No se generó IPTV101.exe." }

# 5. Resumen.
Write-Host "`n  ✓ Listo: $exe" -ForegroundColor Green
Write-Host ("    Tamaño del paquete: {0:N1} MB" -f ((Get-ChildItem $salida -Recurse | Measure-Object Length -Sum).Sum / 1MB))
if (Test-Path (Join-Path $salida 'libmpv-2.dll')) { Write-Host "    Motor de video: incluido" }
Write-Host "    Listas precargadas: ninguna (se importan desde la app)"

if ($Ejecutar) {
    Write-Host "  · Abriendo IPTV 101…"
    Start-Process $exe
}
