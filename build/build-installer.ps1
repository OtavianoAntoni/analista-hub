# Gera o instalador do Analista Hub (dist\setup\AnalistaHub-Setup-<versao>.exe).
#
# Sem administrador e sem Visual Studio: usa o NSIS 3 portátil (baixado uma vez para
# %LOCALAPPDATA%\AnalistaHub\buildtools) e o .nsi em build\installer.
#
# Uso:
#   powershell -ExecutionPolicy Bypass -File build\build-installer.ps1 -Version 2.2.1
#   # teste local: aponta o canal para um servidor/HTTP e congela o pacote no build
#   powershell -ExecutionPolicy Bypass -File build\build-installer.ps1 -Version 2.2.1 `
#       -ChannelUrl http://127.0.0.1:8899/latest.json -AssetUrl http://127.0.0.1:8899/app.zip `
#       -Sha256 <hash> -Size <bytes>
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$Out = 'dist\setup',
    # Manifesto do canal consultado em tempo de instalação (padrão: release mais recente do GitHub)
    [string]$ChannelUrl = '',
    # Manifesto do canal de fallback (padrão: espelho no GitLab) — UPD-002
    [string]$ChannelFallbackUrl = '',
    # Quando informados, o instalador não consulta o canal: usa estes valores (CI e testes)
    [string]$AssetUrl = '',
    [string]$Sha256 = '',
    [long]$Size = 0,
    # Pula o download do NSIS (usa o que já estiver em buildtools)
    [switch]$NoDownload
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$version = $Version.Trim().TrimStart('v', 'V')
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "versão inválida: '$Version' (use 2.2.1)" }

$outDir = if ([System.IO.Path]::IsPathRooted($Out)) { $Out } else { Join-Path $root $Out }
$srcDir = Join-Path $root 'build\installer'
$nsi = Join-Path $srcDir 'AnalistaHub.nsi'
if (-not (Test-Path $nsi)) { throw "script do instalador não encontrado: $nsi" }

# ---------------------------------------------------------------- NSIS portátil
$tools = Join-Path $env:LOCALAPPDATA 'AnalistaHub\buildtools'
$nsisDir = Join-Path $tools 'nsis'
$makensis = Get-ChildItem $nsisDir -Filter makensis.exe -Recurse -ErrorAction SilentlyContinue |
    Select-Object -First 1

# NSIS já instalado na máquina (choco no CI, instalador manual, PATH): usa antes de baixar o portátil.
if (-not $makensis)
{
    $cmd = Get-Command makensis.exe -ErrorAction SilentlyContinue
    if ($cmd) { $makensis = Get-Item $cmd.Source }
}
if (-not $makensis)
{
    foreach ($p in @("$env:ProgramFiles\NSIS\makensis.exe", "${env:ProgramFiles(x86)}\NSIS\makensis.exe"))
    {
        if (Test-Path $p) { $makensis = Get-Item $p; break }
    }
}

if (-not $makensis -and -not $NoDownload)
{
    Write-Host "== baixando NSIS 3.10 portátil para $nsisDir"
    New-Item -ItemType Directory -Force -Path $tools | Out-Null
    $zip = Join-Path $tools 'nsis.zip'
    $url = 'https://sourceforge.net/projects/nsis/files/NSIS%203/3.10/nsis-3.10.zip/download'
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    Invoke-WebRequest -Uri $url -OutFile $zip -UseBasicParsing
    if (-not (Test-Path $zip)) { throw "download do NSIS falhou: $url" }
    if (Test-Path $nsisDir) { Remove-Item $nsisDir -Recurse -Force }
    Expand-Archive -Path $zip -DestinationPath $nsisDir -Force
    Remove-Item $zip -Force
    $makensis = Get-ChildItem $nsisDir -Filter makensis.exe -Recurse | Select-Object -First 1
}
if (-not $makensis) { throw "makensis.exe não encontrado em $nsisDir (rode sem -NoDownload)" }
Write-Host "== makensis: $($makensis.FullName)"

# ---------------------------------------------------------------- ícone e compilação
$ico = Join-Path $root 'src\AnalistaHub\Assets\app.ico'
if (-not (Test-Path $ico)) { throw "ícone não encontrado: $ico" }
Copy-Item $ico (Join-Path $srcDir 'app.ico') -Force

$defines = @(
    "/DVERSION=$version",
    "/DASSET_URL=$AssetUrl",
    "/DASSET_SHA256=$Sha256",
    "/DASSET_SIZE=$Size"
)
if ($ChannelUrl) { $defines += "/DCHANNEL=$ChannelUrl" }
if ($ChannelFallbackUrl) { $defines += "/DCHANNEL_FALLBACK=$ChannelFallbackUrl" }

Write-Host "== compilando o instalador $version"
Push-Location $srcDir
try
{
    & $makensis.FullName -INPUTCHARSET UTF8 -V2 @defines 'AnalistaHub.nsi'
    if ($LASTEXITCODE -ne 0) { throw "makensis falhou (exit $LASTEXITCODE)" }
}
finally { Pop-Location }

$built = Join-Path $srcDir "AnalistaHub-Setup-$version.exe"
if (-not (Test-Path $built)) { throw "o instalador não foi gerado em $built" }

# ---------------------------------------------------------------- entrega
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$final = Join-Path $outDir "AnalistaHub-Setup-$version.exe"
Move-Item $built $final -Force

$info = (Get-Item $final).VersionInfo
if (-not $info.ProductVersion.StartsWith($version))
{
    throw "ProductVersion do instalador ($($info.ProductVersion)) não confere com $version"
}
$hash = (Get-FileHash $final -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -Path "$final.sha256" -Value "$hash  AnalistaHub-Setup-$version.exe" -Encoding ASCII

$mb = [math]::Round((Get-Item $final).Length / 1MB, 1)
Write-Host "== pronto: $final"
Write-Host "   produto: $($info.ProductName) · versão: $($info.ProductVersion) · $mb MB"
Write-Host "   sha256:  $hash"
Write-Host "   canal:   $(if ($ChannelUrl) { $ChannelUrl } else { 'release mais recente do GitHub' })"
Write-Host "   pacote:  $(if ($AssetUrl) { $AssetUrl } else { 'lido do latest.json na instalação' })"
