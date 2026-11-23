# Publica o Analista Hub como pasta portátil (self-contained, sem administrador).
# Uso:  powershell -ExecutionPolicy Bypass -File build\publish.ps1
param(
    [string]$Configuration = 'Release',
    [string]$Out = 'dist\AnalistaHub'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\AnalistaHub\AnalistaHub.csproj'
$outDir = Join-Path $root $Out

$dotnet = Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' }

Write-Host "== dotnet: $dotnet"
& $dotnet --version

Write-Host "== publicando em $outDir"
& $dotnet publish $project -c $Configuration -r win-x64 --self-contained true -o $outDir
if ($LASTEXITCODE -ne 0) { throw "publish falhou (exit $LASTEXITCODE)" }

# ATENÇÃO: o publish do WinUI 3 não copia os recursos XAML compilados (.xbf) nem o .pri do app.
# Sem eles o executável abre e morre com "XamlParseException: XAML parsing failed".
# Copiamos os artefatos gerados pelo build normal.
$bin = Join-Path $root "src\AnalistaHub\bin\$Configuration\net8.0-windows10.0.19041.0\win-x64"
if (-not (Test-Path $bin)) { throw "build não encontrado em $bin" }

$artifacts = Get-ChildItem $bin -Recurse -File | Where-Object {
    $_.Extension -in @('.xbf') -or $_.Name -eq 'AnalistaHub.pri'
}
foreach ($file in $artifacts)
{
    $relative = $file.FullName.Substring($bin.Length).TrimStart('\')
    $destino = Join-Path $outDir $relative
    New-Item -ItemType Directory -Force -Path (Split-Path $destino) | Out-Null
    Copy-Item $file.FullName $destino -Force
}
Write-Host "== recursos XAML copiados: $($artifacts.Count)"

# Enxuga as pastas de idioma: o app é pt-BR, mantém só pt-BR, en-US e pt-PT.
# (nomes de cultura variam: pt-BR, az-Latn-AZ, zh-Hans-CN — o filtro cobre todos)
$keep = @('pt-BR', 'en-US', 'pt-PT')
Get-ChildItem $outDir -Directory |
    Where-Object { $_.Name -match '^[a-z]{2,3}(-[A-Za-z]{2,4}){0,2}$' -and $keep -notcontains $_.Name } |
    ForEach-Object { Remove-Item $_.FullName -Recurse -Force -ErrorAction SilentlyContinue }

# símbolos de depuração não vão para a máquina do usuário
Get-ChildItem $outDir -Filter *.pdb -File | Remove-Item -Force -ErrorAction SilentlyContinue

$totalMb = [math]::Round(((Get-ChildItem $outDir -Recurse -File | Measure-Object Length -Sum).Sum / 1MB), 1)
$files = (Get-ChildItem $outDir -Recurse -File | Measure-Object).Count

# lançadores de duplo-clique (copiados do repositório, CRLF preservado)
$launcherDir = Join-Path $root 'launcher'
if (Test-Path $launcherDir)
{
    Get-ChildItem $launcherDir -File | ForEach-Object {
        Copy-Item $_.FullName (Join-Path $outDir $_.Name) -Force
        Write-Host "== lançador: $($_.Name)"
    }
}

Write-Host "== pronto: $files arquivos, $totalMb MB em $outDir"
