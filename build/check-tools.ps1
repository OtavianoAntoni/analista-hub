# Gate de catálogo: falha se alguma ferramenta quebrar as regras de docs/REGRAS-DE-FERRAMENTA.md
# Uso: powershell -ExecutionPolicy Bypass -File build\check-tools.ps1 [-Exe <caminho do AnalistaHub.exe>]
param(
    [string]$Exe = ''
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

if (-not $Exe) {
    $candidates = @(
        (Join-Path $root 'dist\AnalistaHub\AnalistaHub.exe'),
        (Join-Path $root 'src\AnalistaHub\bin\Release\net8.0-windows10.0.19041.0\win-x64\AnalistaHub.exe'),
        (Join-Path $root 'src\AnalistaHub\bin\Debug\net8.0-windows10.0.19041.0\win-x64\AnalistaHub.exe')
    )
    $Exe = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
}

if (-not $Exe -or -not (Test-Path $Exe)) {
    throw 'AnalistaHub.exe não encontrado. Compile antes de rodar o gate.'
}

Write-Host "== gate de catálogo: $Exe"

# O executável é um WinExe: redirecionamos a saída para um arquivo em vez de depender do console
# (com pipe/console anexado ele pode bloquear o processo chamador).
$tmp = Join-Path $env:TEMP ('hub-check-' + [guid]::NewGuid().ToString('N') + '.json')

$proc = Start-Process -FilePath $Exe -ArgumentList '--check-tools' -NoNewWindow -Wait -PassThru `
    -RedirectStandardOutput $tmp -RedirectStandardError ($tmp + '.err')

$output = ''
if (Test-Path $tmp) { $output = Get-Content $tmp -Raw }
if (-not $output) {
    # reserva: o próprio app grava o último resultado em cli-last.json
    $fallback = Join-Path $env:LOCALAPPDATA 'AnalistaHub\cli-last.json'
    if (Test-Path $fallback) { $output = Get-Content $fallback -Raw }
}

Remove-Item $tmp, ($tmp + '.err') -Force -ErrorAction SilentlyContinue

if ($output) { Write-Host $output } else { Write-Host '(sem saída do executável)' }

if ($proc.ExitCode -ne 0) {
    throw ('Catálogo inválido (exit ' + $proc.ExitCode + '): corrija os problemas acima antes de publicar (ver docs/REGRAS-DE-FERRAMENTA.md).')
}

Write-Host '== catálogo ok'
