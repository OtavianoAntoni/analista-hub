<#
  mkt_serial.ps1 - Consulta o Serial (SN) de uma RB MikroTik no inventario
                   "Inventário" (http://10.0.0.1:3113) a partir do IP.

  Modos (-Mode):
    type       -> imprime o resultado no stdout; o Espanso digita no campo. USADO pelo :mkt.
    clipboard  -> so coloca o resultado no clipboard (nao imprime nada)
    popup      -> mostra popup com o resultado (e copia pro clipboard)

  Entrada: -Ip 10.0.0.1. Sem -Ip / vazio -> le o IP do clipboard.

  Falha de rede / login / servidor NAO grava "0": o clipboard fica intacto e
  aparece um popup de erro (senao "0" mentiria dizendo "nao cadastrada").

  Credenciais: lidas do cofre local (secao 5 de cofre.md), nunca
  embutidas aqui. Token JWT fica em cache ao lado do script.
#>
[CmdletBinding()]
param(
    [string]$Ip = '',
    [ValidateSet('clipboard', 'type', 'popup')][string]$Mode = 'clipboard',
    [string]$User = '',
    [string]$Passw = '',
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
        Add-Type -Namespace MktSerial -Name Dpi -MemberDefinition `
            '[DllImport("user32.dll")] public static extern bool SetProcessDPIAware();' -ErrorAction Stop
        [void][MktSerial.Dpi]::SetProcessDPIAware()
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
$TokenFile = Join-Path $ScriptDir 'mkt_serial_token.json'
$ApiPort   = '3113'
# Caminho do cofre NÃO fica no código (o repositório é público). Quem chama informa, se quiser:
#   $env:ANALISTAHUB_COFRE = 'C:\...\credenciais.md'
# O Analista Hub não usa cofre: manda usuário e senha por ANALISTAHUB_USER / ANALISTAHUB_PASS.
$VaultFile = $env:ANALISTAHUB_COFRE

function Write-Log([string]$msg) {
    try {
        $log = Join-Path $env:TEMP 'mkt_serial.log'
        ("{0} {1}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $msg) | Add-Content -LiteralPath $log -Encoding UTF8
    } catch {}
}

function Get-TextFromClipboard {
    try {
        $t = Get-Clipboard -Raw -ErrorAction Stop
        if ($null -eq $t) { return '' }
        return ([string]$t).Trim()
    } catch { return '' }
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

# credenciais do cofre (OPCIONAL): secao "## 5." (Servidor credenciais MKT), pares chave = valor
function Get-VaultCred {
    if ([string]::IsNullOrWhiteSpace($VaultFile)) { return $null }
    if (-not (Test-Path -LiteralPath $VaultFile)) { return $null }
    $lines = Get-Content -LiteralPath $VaultFile -Encoding UTF8
    $inSec = $false
    $kv = @{}
    foreach ($line in $lines) {
        if ($line -match '^\s*##\s*5\.') { $inSec = $true; continue }
        if ($inSec -and $line -match '^\s*##\s*\d+\.') { break }
        if (-not $inSec) { continue }
        if ($line -match '^\s*([A-Za-z_][\w ]*?)\s*[:=]\s*(\S.*?)\s*$') {
            $k = $matches[1].Trim().ToLowerInvariant()
            $kv[$k] = $matches[2].Trim()
        }
    }
    if (-not $kv.ContainsKey('user') -or -not $kv.ContainsKey('passwd')) { return $null }
    return @{ User = $kv['user']; Passw = $kv['passwd'] }
}

# credenciais efetivas: parametro explicito > variaveis do HUB > cofre markdown
function Get-EffectiveCred {
    $u = $User
    $p = $Passw
    if (-not $u) { $u = $env:ANALISTAHUB_USER }
    if (-not $p) { $p = $env:ANALISTAHUB_PASS }
    if ($u -and $p) { return @{ User = $u; Passw = $p } }
    return Get-VaultCred
}

function Read-TokenCache {
    if ($NoCache) { return $null }
    if (-not (Test-Path -LiteralPath $TokenFile)) { return $null }
    try {
        $o = Get-Content -LiteralPath $TokenFile -Raw | ConvertFrom-Json
        if (-not $o.token) { return $null }
        $age = ((Get-Date).ToUniversalTime() - [datetime]::Parse($o.saved_utc).ToUniversalTime()).TotalMinutes
        if ($age -gt 45) { return $null }
        return [string]$o.token
    } catch { return $null }
}

function Save-Token([string]$token) {
    try {
        $o = [ordered]@{
            token     = $token
            saved_utc = (Get-Date).ToUniversalTime().ToString('o')
        }
        ($o | ConvertTo-Json -Compress) | Set-Content -LiteralPath $TokenFile -Encoding UTF8
    } catch {}
}

function Invoke-Api([string]$path, $body, [string]$token, [int]$timeout = 8) {
    $url = 'http://10.0.0.1:' + $ApiPort + $path
    $json = ($body | ConvertTo-Json -Compress)
    $headers = @{}
    if ($token) { $headers['Authorization'] = 'Bearer ' + $token }
    try {
        $resp = Invoke-WebRequest -Uri $url -Method POST -ContentType 'application/json' `
                                  -Headers $headers -Body $json -UseBasicParsing -TimeoutSec $timeout
        $content = [string]$resp.Content
        Write-Log ("API " + $path + " status=" + [int]$resp.StatusCode + " len=" + $content.Length)
        return @{ Status = 'OK'; Code = [int]$resp.StatusCode; Body = $content }
    } catch {
        $code = 0
        $text = ''
        $r = $_.Exception.Response
        if ($r) {
            try { $code = [int]$r.StatusCode } catch {}
            try {
                $stream = $r.GetResponseStream()
                if ($stream) {
                    $reader = New-Object System.IO.StreamReader($stream)
                    $text = $reader.ReadToEnd()
                    $reader.Close()
                }
            } catch {}
        }
        return @{ Status = 'ERROR'; Code = $code; Body = $text; Detail = $_.Exception.Message }
    }
}

function ConvertFrom-JsonSafe([string]$s) {
    try { return ($s | ConvertFrom-Json) } catch { return $null }
}

# --------------------------------------------------------------------- entrada
$rawIp = $Ip
if (-not $rawIp -or -not $rawIp.Trim() -or $rawIp.Trim() -eq '-') { $rawIp = Get-TextFromClipboard }
$rawIp = $rawIp.Trim()
$ipShow = if ($rawIp) { $rawIp } else { '(vazio)' }

function Finish([string]$status, [string]$text, [string]$detail) {
    switch ($Mode) {
        'type' {
            # o stdout vira a substituicao do Espanso ({{out}}); nada de lixo no campo
            if ($status -eq 'OK') {
                [Console]::Out.Write($text)
                Set-Clipboard -Value $text
            } else {
                Show-Popup ("MKT Serial`n" + $detail + "`n`nIP  " + $ipShow)
            }
        }
        'clipboard' {
            if ($status -eq 'OK') { Set-Clipboard -Value $text }
            elseif ($detail) { Show-Popup ("MKT Serial`n" + $detail + "`n`nIP  " + $ipShow) }
        }
        'popup' {
            if ($status -eq 'OK') { Set-Clipboard -Value $text }
            $body = if ($status -eq 'OK') { "MKT Serial`n" + $text + "`n`nIP  " + $ipShow + "`n(copiado para o clipboard)" }
                    else { "MKT Serial`n" + $detail + "`n`nIP  " + $ipShow }
            Show-Popup $body
        }
    }
    exit 0
}

# aceita "10.0.0.1", "10.0.0.1/32", com espacos
if (-not $rawIp) {
    Finish 'ERROR' '' ("Clipboard vazio" + "`nCopie o IP da RB antes de usar :mkt")
}
if ($rawIp -match '^\s*((?:\d{1,3}\.){3}\d{1,3})(?:/\d{1,2})?\s*$') {
    $ip = $matches[1]
} else {
    Finish 'ERROR' '' ("IP invalido: '" + $ipShow + "'" + "`nEsperado um IPv4, ex.: 10.0.0.1")
}
$octets = $ip.Split('.')
foreach ($o in $octets) { if ([int]$o -gt 255) { Finish 'ERROR' '' ("IP invalido: " + $ip) } }

# ------------------------------------------------------------------ autenticacao
$cred = Get-EffectiveCred
if (-not $cred) {
    Write-Log "credenciais nao encontradas (nem por variavel de ambiente, nem no cofre)"
    Finish 'ERROR' '' ("Credenciais do inventario nao configuradas" + "`nPreencha usuario e senha na aba da ferramenta, no Analista Hub")
}

$tokenFromCache = $false
$token = Read-TokenCache
if ($token) { $tokenFromCache = $true }
if (-not $token) {
    $auth = Invoke-Api '/api/auth_ep' @{ type = 'auth'; email = $cred.User; passw = $cred.Passw } '' 8
    if ($auth.Status -ne 'OK') {
        Write-Log ("AUTH falhou: " + $auth.Code + " " + $auth.Detail)
        Finish 'ERROR' '' ("Falha no login do inventario" + "`n" + $(if ($auth.Code) { "HTTP " + $auth.Code } else { $auth.Detail }))
    }
    $aj = ConvertFrom-JsonSafe $auth.Body
    if (-not $aj -or -not $aj.st -or -not $aj.data.jwt_token) {
        Write-Log ("AUTH resposta inesperada: " + $auth.Body)
        Finish 'ERROR' '' ("Login recusado pelo inventario" + "`n" + $aj.msg)
    }
    $token = [string]$aj.data.jwt_token
    Save-Token $token
}

# ---------------------------------------------------------------------- consulta
$search = Invoke-Api '/api/front_inv_search' @{ type = 'ip'; regex_filter = $ip } $token 10

# O servidor nao devolve 401: com JWT invalido ele simplesmente fecha a conexao
# (excecao sem Response => Code 0). Por isso, qualquer falha com token vindo do
# cache dispara UMA renovacao + retry; falha de rede real cai no erro logo depois.
if ($search.Status -ne 'OK' -and ($search.Code -eq 401 -or $tokenFromCache)) {
    Write-Log ("SEARCH falhou com token de cache (HTTP " + $search.Code + "); renovando login")
    if (Test-Path -LiteralPath $TokenFile) { try { Remove-Item -LiteralPath $TokenFile -Force } catch {} }
    $auth = Invoke-Api '/api/auth_ep' @{ type = 'auth'; email = $cred.User; passw = $cred.Passw } '' 8
    $aj = if ($auth.Status -eq 'OK') { ConvertFrom-JsonSafe $auth.Body } else { $null }
    if ($aj -and $aj.st -and $aj.data.jwt_token) {
        $token = [string]$aj.data.jwt_token
        Save-Token $token
        $search = Invoke-Api '/api/front_inv_search' @{ type = 'ip'; regex_filter = $ip } $token 10
    }
}

if ($search.Status -ne 'OK') {
    Write-Log ("SEARCH falhou: HTTP " + $search.Code + " " + $search.Detail)
    $why = if ($search.Code) { "HTTP " + $search.Code } else { $search.Detail }
    Finish 'ERROR' '' ("Inventario indisponivel" + "`n" + $why)
}

$sj = ConvertFrom-JsonSafe $search.Body
if (-not $sj -or $sj.st -ne $true) {
    Write-Log ("SEARCH resposta inesperada: " + $search.Body)
    Finish 'ERROR' '' "Resposta inesperada do inventario"
}

$items = @(@($sj.data) | Where-Object { $_ -ne $null })
$sn = ''

if ($items.Count -gt 0) {
    # prioriza o registro cujo IP consultado aparece em actual_ips
    $exact = @($items | Where-Object { @($_.actual_ips) -contains $ip })
    $pick = if ($exact.Count -gt 0) { $exact[0] } else { $items[0] }
    if ($pick.unique) { $sn = ([string]$pick.unique).Trim().ToUpperInvariant() }
    if ($items.Count -gt 1) { Write-Log ("IP $ip retornou " + $items.Count + " registros; usado key=" + $pick.key) }
}

if (-not $sn) {
    Write-Log "IP $ip sem SN (retorna 0)"
    Finish 'OK' '0' ''
}

$out = $sn + '1'
Write-Log "IP $ip -> $out"
Finish 'OK' $out ''
