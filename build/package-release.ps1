# Empacota uma versão do Analista Hub para o canal de atualização.
#
# Gera em dist\release:
#   AnalistaHub-<versao>-win-x64.zip   pasta portátil comprimida (conteúdo na raiz do zip)
#   AnalistaHub-<versao>-win-x64.zip.sha256
#   latest.json                        manifesto que o app lê em releases/latest/download/latest.json
#   notas.md                           texto da release
#
# Uso:
#   powershell -ExecutionPolicy Bypass -File build\package-release.ps1 -Version 0.2.0
#   powershell -ExecutionPolicy Bypass -File build\package-release.ps1 -Version 0.2.0 -BaseUrl http://127.0.0.1:8899/
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$Out = 'dist\release',
    [string]$InstallDir = 'dist\AnalistaHub',
    [string]$AssetName = '',
    [string]$BaseUrl = '',
    [string]$Notes = '',
    [string]$Repo = 'OtavianoAntoni/analista-hub',
    # Pasta/canal de rede: copia zip, .sha256 e (por último) o latest.json para lá
    [string]$ChannelDir = '',
    [switch]$SkipScripts
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$version = $Version.Trim().TrimStart('v', 'V')
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "versão inválida: '$Version' (use 0.2.0)" }
if (-not $AssetName) { $AssetName = "AnalistaHub-$version-win-x64.zip" }
if (-not $BaseUrl) { $BaseUrl = "https://github.com/$Repo/releases/download/v$version/" }
if (-not $BaseUrl.EndsWith('/')) { $BaseUrl += '/' }

$outDir = if ([System.IO.Path]::IsPathRooted($Out)) { $Out } else { Join-Path $root $Out }
$installDir = if ([System.IO.Path]::IsPathRooted($InstallDir)) { $InstallDir } else { Join-Path $root $InstallDir }

Write-Host "== publicando versão $version"
$publishArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $root 'build\publish.ps1'),
    '-Version', $version, '-Out', $InstallDir)
if ($SkipScripts) { $publishArgs += '-SkipScripts' }
& powershell.exe @publishArgs
if ($LASTEXITCODE -ne 0) { throw "publish falhou (exit $LASTEXITCODE)" }

$exe = Join-Path $installDir 'AnalistaHub.exe'
if (-not (Test-Path $exe)) { throw "sem AnalistaHub.exe em $installDir" }
$builtVersion = (Get-Item $exe).VersionInfo.ProductVersion
Write-Host "== executável: $builtVersion"
if (-not $builtVersion.StartsWith($version)) { throw "versão do exe ($builtVersion) não confere com $version" }

New-Item -ItemType Directory -Force -Path $outDir | Out-Null
# Higiene: nada de cache de token (JWT), cache de OUI, log ou backup dentro do zip do release.
$toolsDir = Join-Path $installDir 'tools'
if (Test-Path $toolsDir)
{
    Get-ChildItem $toolsDir -File | Where-Object {
        $_.Name -match '(?i)token.*\.json$' -or $_.Name -match '(?i)_cache\.json$' -or
        $_.Extension -in @('.log', '.bak') -or $_.Name -match '(?i)\.bak'
    } | ForEach-Object {
        Remove-Item $_.FullName -Force
        Write-Host "== removido do pacote: tools\$($_.Name)"
    }
}

$zipPath = Join-Path $outDir $AssetName
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }

Write-Host "== comprimindo $installDir"
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory(
    $installDir, $zipPath, [System.IO.Compression.CompressionLevel]::Optimal, $false)

$hash = (Get-FileHash $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
$size = (Get-Item $zipPath).Length
Set-Content -Path "$zipPath.sha256" -Value "$hash  $AssetName" -Encoding ASCII
Write-Host "== zip: $([math]::Round($size/1MB,1)) MB · sha256 $hash"

if (-not $Notes) {
    $Notes = "Atualização do Analista Hub para a versão $version." + [Environment]::NewLine +
             "Instale por Configurações → Atualizações → Procurar por atualização agora."
}
# UTF-8 SEM BOM: Set-Content -Encoding UTF8 do PowerShell 5.1 grava BOM, e o manifesto é lido
# pelo app (e por qualquer outro consumidor) como JSON puro.
$utf8 = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText((Join-Path $outDir 'notas.md'), $Notes, $utf8)

$manifest = [ordered]@{
    schema      = 1
    version     = $version
    publishedAt = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
    notes       = $Notes
    assets      = [ordered]@{
        'win-x64' = [ordered]@{
            url    = "$BaseUrl$AssetName"
            sha256 = $hash
            size   = $size
        }
    }
}
$json = $manifest | ConvertTo-Json -Depth 6
[System.IO.File]::WriteAllText((Join-Path $outDir 'latest.json'), $json, $utf8)
Write-Host "== manifesto: $(Join-Path $outDir 'latest.json')"

# Canal de rede (share): zip e hash primeiro, manifesto por ÚLTIMO — ninguém deve ler um manifesto
# que aponta para um zip que ainda não chegou.
if ($ChannelDir) {
    if (-not (Test-Path $ChannelDir)) { New-Item -ItemType Directory -Force -Path $ChannelDir | Out-Null }
    Copy-Item $zipPath (Join-Path $ChannelDir $AssetName) -Force
    Copy-Item "$zipPath.sha256" (Join-Path $ChannelDir "$AssetName.sha256") -Force
    Copy-Item (Join-Path $outDir 'notas.md') (Join-Path $ChannelDir 'notas.md') -Force
    Copy-Item (Join-Path $outDir 'latest.json') (Join-Path $ChannelDir 'latest.json') -Force
    Write-Host "== canal atualizado: $ChannelDir"
}

Write-Host "== pronto: $outDir"
