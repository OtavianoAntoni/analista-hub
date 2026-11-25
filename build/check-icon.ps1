# Gate do icone do executavel (PKG-001).
# Sem <ApplicationIcon> no csproj o Explorer mostra o icone generico, mesmo com a janela usando
# Assets/app.ico em runtime. Este script extrai o icone do PE e mede o que o Windows mostraria.
#
# Uso:  powershell -NoProfile -ExecutionPolicy Bypass -File build\check-icon.ps1 -Exe dist\AnalistaHub\AnalistaHub.exe
# Saida: JSON com cores/opacos e exit 1 quando o icone e o generico.

param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [string]$Icon = '',
    [int]$MinCores = 60
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

if (-not (Test-Path $Exe)) { Write-Host "icone: executavel nao encontrado: $Exe"; exit 2 }

function Medir-Icone([System.Drawing.Icon]$ico) {
    $bmp = $ico.ToBitmap()
    $cores = New-Object 'System.Collections.Generic.HashSet[int]'
    $opacos = 0
    for ($y = 0; $y -lt $bmp.Height; $y++) {
        for ($x = 0; $x -lt $bmp.Width; $x++) {
            $p = $bmp.GetPixel($x, $y)
            if ($p.A -gt 0) {
                $opacos++
                [void]$cores.Add($p.ToArgb())
            }
        }
    }
    return @{ Cores = $cores.Count; Opacos = $opacos; Largura = $bmp.Width; Altura = $bmp.Height }
}

$exeIco = [System.Drawing.Icon]::ExtractAssociatedIcon((Resolve-Path $Exe).Path)
$exeInfo = Medir-Icone $exeIco

$icoInfo = $null
if ($Icon -and (Test-Path $Icon)) {
    $icoPath = (Resolve-Path $Icon).Path
    $icoInfo = Medir-Icone (New-Object System.Drawing.Icon($icoPath, 32, 32))
}

[ordered]@{
    ok          = ($exeInfo.Cores -ge $MinCores)
    exe         = (Resolve-Path $Exe).Path
    cores       = $exeInfo.Cores
    opacos      = $exeInfo.Opacos
    tamanho     = "$($exeInfo.Largura)x$($exeInfo.Altura)"
    min_cores   = $MinCores
    icone_fonte = if ($icoInfo) { $icoInfo.Cores } else { $null }
    motivo      = if ($exeInfo.Cores -ge $MinCores) {
        "icone proprio embutido"
    } else {
        "icone generico do Windows ($($exeInfo.Cores) cores) - falta <ApplicationIcon> no csproj"
    }
} | ConvertTo-Json -Depth 4

if ($exeInfo.Cores -lt $MinCores) { exit 1 }
exit 0
