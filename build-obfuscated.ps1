<#
  build-obfuscated.ps1 — build distribuibile OFFUSCATA di Nexus Streamer.

  Due modalita':
    (default)      publish self-contained a CARTELLA in .\publish  + installer Inno Setup
    -SingleFile    UN SOLO .exe autoestraente in .\publish_single (FFmpeg incluso)

  Esempi:
    .\build-obfuscated.ps1                 # cartella + installer
    .\build-obfuscated.ps1 -SingleFile     # un exe unico
    .\build-obfuscated.ps1 -NoInstaller    # cartella, senza installer
#>
[CmdletBinding()]
param(
    [string]$Config = "Release",
    [string]$Rid = "win-x64",
    [switch]$SingleFile,   # produce un unico .exe autoestraente
    [switch]$NoInstaller   # (solo modalita' cartella) salta l'installer Inno Setup
)
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
Set-Location $root

$proj    = Join-Path $root "src\StreamingDoc.App\StreamingDoc.App.csproj"
$dllName = "Nexus Streamer.dll"
$binDir  = Join-Path $root "src\StreamingDoc.App\bin\$Config\net9.0-windows10.0.19041.0\$Rid"

if ($SingleFile) {
    $out = Join-Path $root "publish_single"

    Write-Host "==> [1/3] Build self-contained ($Config / $Rid)..." -ForegroundColor Cyan
    dotnet build $proj -c $Config -r $Rid --self-contained true
    if ($LASTEXITCODE -ne 0) { throw "build fallito" }

    Write-Host "==> [2/3] Offusco il dll in bin (prima del bundling)..." -ForegroundColor Cyan
    dotnet obfuscar.console (Join-Path $root "obfuscar-single.xml")
    if ($LASTEXITCODE -ne 0) { throw "obfuscar fallito" }
    $obf = Join-Path $binDir "_obf\$dllName"
    if (-not (Test-Path $obf)) { throw "dll offuscata non trovata: $obf" }
    Copy-Item $obf (Join-Path $binDir $dllName) -Force
    Remove-Item (Join-Path $binDir "_obf") -Recurse -Force

    Write-Host "==> [3/3] Bundle single-file (--no-build, tutto autoestraente)..." -ForegroundColor Cyan
    if (Test-Path $out) { Remove-Item $out -Recurse -Force }
    dotnet publish $proj -c $Config -r $Rid --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:IncludeAllContentForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true `
        --no-build -o $out
    if ($LASTEXITCODE -ne 0) { throw "publish single-file fallito" }
    Remove-Item (Join-Path $out "*.pdb") -Force -ErrorAction SilentlyContinue

    $exe = Join-Path $out $($dllName -replace '\.dll$','.exe')
    Write-Host "OK. Exe unico offuscato: $exe" -ForegroundColor Green
    Write-Host ("Dimensione: {0:N0} MB" -f ((Get-Item $exe).Length/1MB))
    return
}

# ---- Modalita' CARTELLA (default) ----
$publish = Join-Path $root "publish"
$obfOut  = Join-Path $publish "_obf"

Write-Host "==> [1/3] Publish self-contained ($Config / $Rid)..." -ForegroundColor Cyan
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
dotnet publish $proj -c $Config -r $Rid --self-contained true -o $publish
if ($LASTEXITCODE -ne 0) { throw "publish fallito" }

Write-Host "==> [2/3] Offuscazione (Obfuscar)..." -ForegroundColor Cyan
dotnet obfuscar.console (Join-Path $root "obfuscar.xml")
if ($LASTEXITCODE -ne 0) { throw "obfuscar fallito" }
$obfDll = Join-Path $obfOut $dllName
if (-not (Test-Path $obfDll)) { throw "dll offuscata non trovata: $obfDll" }

Write-Host "==> [3/3] Sostituisco la dll offuscata nel folder di publish..." -ForegroundColor Cyan
Copy-Item $obfDll (Join-Path $publish $dllName) -Force
Remove-Item $obfOut -Recurse -Force

Write-Host "OK. Build offuscata pronta in: $publish" -ForegroundColor Green
Write-Host "Eseguibile: $(Join-Path $publish 'Nexus Streamer.exe')"

if (-not $NoInstaller) {
    $iscc = @(
        "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
        "C:\Program Files\Inno Setup 6\ISCC.exe"
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1
    if ($iscc) {
        Write-Host "==> [4/4] Compilo installer (Inno Setup)..." -ForegroundColor Cyan
        & $iscc (Join-Path $root "installer\NexusStreamer.iss")
        if ($LASTEXITCODE -ne 0) { throw "ISCC fallito" }
        Write-Host "Installer: $(Join-Path $root 'installer\NexusStreamer-Setup.exe')" -ForegroundColor Green
    } else {
        Write-Host "ISCC non trovato: salto l'installer (build portable in .\publish comunque pronta)." -ForegroundColor Yellow
    }
}
