<#
  mac_vendor.ps1 - Resolve o fabricante (OUI) de um MAC via api.macvendors.com

  Modos (-Mode):
    popup      -> mostra popup com MAC + fabricante (copia o fabricante pro clipboard)
    clipboard  -> coloca o fabricante no clipboard (nao imprime nada)
    type       -> imprime o fabricante no stdout (o Espanso digita no campo)

  Entrada: -Mac "00:1A:2B:3C:4D:5E"  (aceita qualquer separador; so precisa dos
  6 primeiros digitos hex). Sem -Mac / vazio -> le o MAC do clipboard.

  Cache: mac_vendor_cache.json ao lado do script (poupa o rate limit da API).
#>
[CmdletBinding()]
param(
    [string]$Mac = '',
    [ValidateSet('popup', 'clipboard', 'type', 'hub')][string]$Mode = 'popup',
    [switch]$NoCache,
    # usado internamente pelo processo filho do popup
    [string]$PopupB64 = ''
)

$ErrorActionPreference = 'Stop'
try { [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12 } catch {}

# ---------------------------------------------------------------- popup child
if ($PopupB64) {
    Add-Type -AssemblyName System.Windows.Forms
    Add-Type -AssemblyName System.Drawing
    try {
        Add-Type -Namespace MacVendor -Name Dpi -MemberDefinition `
            '[DllImport("user32.dll")] public static extern bool SetProcessDPIAware();' -ErrorAction Stop
        [void][MacVendor.Dpi]::SetProcessDPIAware()
    } catch {}
    $text = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($PopupB64))

    $form = New-Object System.Windows.Forms.Form
    $form.FormBorderStyle = 'None'
    $form.StartPosition   = 'Manual'
    $form.TopMost         = $true
    $form.AutoSize        = $true
    $form.AutoSizeMode    = 'GrowAndShrink'
    $form.BackColor       = [Drawing.Color]::FromArgb(28, 28, 40)
    $form.Padding         = New-Object System.Windows.Forms.Padding(20, 16, 20, 16)

    $lbl = New-Object System.Windows.Forms.Label
    $lbl.AutoSize    = $true
    $lbl.MaximumSize = New-Object Drawing.Size(560, 0)
    $lbl.ForeColor   = [Drawing.Color]::White
    $lbl.Font        = New-Object Drawing.Font('Segoe UI', 11)
    $lbl.Text        = $text
    $form.Controls.Add($lbl)

    # posiciona perto do cursor, dentro da area util do monitor onde ele esta
    $form.Add_Shown({
        $pos  = [System.Windows.Forms.Cursor]::Position
        $area = [System.Windows.Forms.Screen]::FromPoint($pos).WorkingArea
        $x = [Math]::Min($pos.X + 18, $area.Right  - $form.Width  - 8)
        $y = [Math]::Min($pos.Y + 18, $area.Bottom - $form.Height - 8)
        $x = [Math]::Max($x, $area.Left); $y = [Math]::Max($y, $area.Top)
        $form.Location = New-Object Drawing.Point($x, $y)
    })

    $form.Add_Click({ $form.Close() })
    $lbl.Add_Click({ $form.Close() })

    $timer = New-Object System.Windows.Forms.Timer
    $timer.Interval = 12000
    $timer.Add_Tick({ $timer.Stop(); $form.Close() })
    $timer.Start()

    [void]$form.ShowDialog()
    exit 0
}

# ------------------------------------------------------------------ utilidades
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$CacheFile = Join-Path $ScriptDir 'mac_vendor_cache.json'
$ApiBase   = 'https://api.macvendors.com/'

function Get-TextFromClipboard {
    try {
        $t = Get-Clipboard -Raw -ErrorAction Stop
        if ($null -eq $t) { return '' }
        return ([string]$t).Trim()
    } catch { return '' }
}

function Get-HexOnly([string]$s) {
    if (-not $s) { return '' }
    return ($s -replace '[^0-9A-Fa-f]', '').ToUpperInvariant()
}

function Read-Cache {
    $h = @{}
    if (Test-Path -LiteralPath $CacheFile) {
        try {
            $obj = Get-Content -LiteralPath $CacheFile -Raw | ConvertFrom-Json
            foreach ($p in $obj.PSObject.Properties) { $h[$p.Name] = [string]$p.Value }
        } catch { $h = @{} }
    }
    return $h
}

function Write-Cache($table) {
    try {
        $o = [ordered]@{}
        foreach ($k in ($table.Keys | Sort-Object)) { $o[$k] = $table[$k] }
        ($o | ConvertTo-Json -Compress) | Set-Content -LiteralPath $CacheFile -Encoding UTF8
    } catch {}
}

function Write-Log([string]$msg) {
    try {
        $log = Join-Path $env:TEMP 'mac_vendor.log'
        ("{0} {1}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $msg) | Add-Content -LiteralPath $log -Encoding UTF8
    } catch {}
}

function Show-Popup([string]$text) {
    $b64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($text))
    $ps  = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $argList = @('-NoProfile', '-STA', '-WindowStyle', 'Hidden', '-ExecutionPolicy', 'Bypass',
                 '-File', $PSCommandPath, '-PopupB64', $b64)
    try {
        Start-Process -FilePath $ps -ArgumentList $argList -WindowStyle Hidden -PassThru | Out-Null
    } catch {
        Write-Log ("popup FALHOU: " + $_.Exception.Message)
    }
}

# --------------------------------------------------------------------- entrada
$raw = $Mac
if (-not $raw -or -not $raw.Trim() -or $raw.Trim() -eq '-') { $raw = Get-TextFromClipboard }
$hex = Get-HexOnly $raw
$rawShow = if ($raw) { $raw.Trim() } else { '(vazio)' }

function Finish([string]$status, [string]$text, [string]$detail) {
    switch ($Mode) {
        'hub' {
            # contrato do Analista Hub: JSON no stdout; sem popup, sem clipboard e sem escrever o
            # erro como se fosse valor. status ok -> valor; error -> falha, com o motivo em detail.
            $payload = [ordered]@{
                status = if ($status -eq 'OK') { 'ok' } else { 'error' }
                value  = $text
                detail = $detail
            }
            [Console]::Out.Write(($payload | ConvertTo-Json -Compress))
        }
        'type' {
            if ($status -eq 'OK') { [Console]::Out.Write($text) }
            else { [Console]::Out.Write($detail) }
        }
        'clipboard' {
            if ($status -eq 'OK') { Set-Clipboard -Value $text }
        }
        'popup' {
            if ($status -eq 'OK') { Set-Clipboard -Value $text }
            $body = if ($status -eq 'OK') { "Fabricante`n$text`n`nMAC  $rawShow`n(copiado para o clipboard)" }
                    else { "MAC Vendor`n$detail`n`nMAC  $rawShow" }
            Show-Popup $body
        }
    }
    exit 0
}

if ($hex.Length -lt 6) {
    Finish 'ERROR' '' ("MAC invalido: '" + $rawShow + "'" + "`nPreciso de pelo menos 6 digitos hex (ex.: 00:1A:2B)")
}

$oui = $hex.Substring(0, 6)

# bit U/L (0x02 do primeiro octeto) ligado => endereco local/aleatorio, sem OUI
$firstOctet = [Convert]::ToByte($oui.Substring(0, 2), 16)
if (($firstOctet -band 0x02) -ne 0) {
    Finish 'ERROR' '' ("Endereco localmente administrado (" + $oui + ")`nMAC aleatorio/privado: nao existe fabricante na base OUI")
}

# --------------------------------------------------------------------- lookup
$cache = Read-Cache
if (-not $NoCache -and $cache.ContainsKey($oui)) {
    $cached = $cache[$oui]
    if ($cached -eq '__NOTFOUND__') {
        Finish 'ERROR' '' ("Sem fabricante para a OUI " + $oui)
    }
    Finish 'OK' $cached ''
}

$attempt  = 0
$deadline = (Get-Date).AddSeconds(9)
$result   = $null

while ($true) {
    $attempt++
    try {
        $resp = Invoke-WebRequest -Uri ($ApiBase + $oui) -UseBasicParsing -TimeoutSec 7
        $vendor = ([string]$resp.Content).Trim()
        if (-not $vendor) { throw 'resposta vazia' }
        $cache[$oui] = $vendor
        Write-Cache $cache
        $result = @{ Status = 'OK'; Text = $vendor }
        break
    } catch {
        $statusCode = 0
        $bodyText   = ''
        $r = $_.Exception.Response
        if ($r) {
            try { $statusCode = [int]$r.StatusCode } catch {}
            # ler o corpo direto do stream: ErrorDetails.Message nao vem preenchido no PS 5.1
            try {
                $stream = $r.GetResponseStream()
                if ($stream) {
                    $reader = New-Object System.IO.StreamReader($stream)
                    $bodyText = $reader.ReadToEnd()
                    $reader.Close()
                }
            } catch {}
        }

        if ($statusCode -eq 404) {
            $detail404 = ''
            if ($bodyText) {
                try {
                    $j = $bodyText | ConvertFrom-Json
                    if ($j.errors.detail) { $detail404 = [string]$j.errors.detail }
                } catch {}
            }
            $cache[$oui] = '__NOTFOUND__'
            Write-Cache $cache
            $msg404 = "Sem fabricante para a OUI " + $oui
            if ($detail404 -and $detail404 -notmatch '(?i)^not\s*found$') { $msg404 += "`n(" + $detail404 + ")" }
            $result = @{ Status = 'ERROR'; Detail = $msg404 }
            break
        }

        if ($statusCode -eq 429) {
            $wait = 2
            $h = $r.Headers['Retry-After']
            if ($h) { try { $wait = [int]$h } catch {} }
            if ($wait -lt 1) { $wait = 1 }
            if ($wait -gt 4) { $wait = 4 }
            if ($attempt -lt 3 -and (Get-Date).AddSeconds($wait) -lt $deadline) {
                Start-Sleep -Seconds $wait
                continue
            }
            $result = @{ Status = 'ERROR'; Detail = 'Limite de requisicoes da API (429)' + "`n" + 'Aguarde alguns segundos e tente de novo' }
            break
        }

        if ($attempt -lt 2 -and (Get-Date).AddSeconds(1) -lt $deadline) {
            Start-Sleep -Milliseconds 800
            continue
        }

        $msg = $_.Exception.Message
        $result = @{ Status = 'ERROR'; Detail = ('Falha ao consultar a API' + "`n" + $msg) }
        break
    }
}

if ($result.Status -eq 'OK') { Finish 'OK' $result.Text '' }
Finish 'ERROR' '' $result.Detail
