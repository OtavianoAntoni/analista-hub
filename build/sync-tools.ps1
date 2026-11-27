# Sincroniza as duas pastas de ferramentas do projeto, nos dois sentidos:
#   src\AnalistaHub\tools   (repositório — o que vai para o git, o zip e o instalador)
#   dist\AnalistaHub\tools  (o que o HUB aberto realmente lê)
#
# Por que existe: editar só uma das duas não aparece na tela (se você editou src) nem sobrevive à
# próxima publicação (se você editou dist). Rode isto no lugar de copiar na mão.
#
# Regras (nada é apagado — arquivo que existe só de um lado é copiado para o outro):
#   .json/.ico/.png  dois sentidos — o arquivo mais RECENTE vence; conteúdo igual não copia.
#   .ps1             um sentido (src → dist) — a cópia em dist leva a assinatura desta MÁQUINA,
#                    que nunca pode voltar para o repositório. Quando o conteúdo muda, o HUB
#                    reassina o script ao abrir.
#   token/cache/log  nunca — artefato de runtime (token é JWT de sessão).
#
# Uso:
#   powershell -ExecutionPolicy Bypass -File build\sync-tools.ps1
#   powershell -ExecutionPolicy Bypass -File build\sync-tools.ps1 -DryRun      # só mostra
#   powershell -ExecutionPolicy Bypass -File build\sync-tools.ps1 -NoRestart   # não reabre o HUB
param(
    [switch]$DryRun,
    [switch]$NoRestart,
    [switch]$Abrir,
    [switch]$SemGate
)

$ErrorActionPreference = 'Stop'

$root    = Split-Path -Parent $PSScriptRoot
$origem  = Join-Path $root 'src\AnalistaHub\tools'
$destino = Join-Path $root 'dist\AnalistaHub\tools'
$exe     = Join-Path $root 'dist\AnalistaHub\AnalistaHub.exe'

Write-Host "== origem  : $origem"
Write-Host "== destino : $destino"
if (-not (Test-Path -LiteralPath $origem))  { throw "pasta de origem nao encontrada: $origem" }
if (-not (Test-Path -LiteralPath $destino)) { throw "pasta publicada nao encontrada: $destino  -> rode build\publish.ps1 uma vez" }

# Artefato de runtime que nasce em tools/: mesma lista que publish.ps1 e package-release.ps1 usam.
$lixo = '(?i)(token.*\.json$|_cache\.json$|\.log$|\.bak$)'

function HashTexto([string]$texto) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($texto)
        return [BitConverter]::ToString($sha.ComputeHash($bytes)).Replace('-', '').ToLowerInvariant()
    }
    finally { $sha.Dispose() }
}

# Corpo do script sem o bloco de assinatura: CRLF x LF e newline final não fazem script "diferente",
# e a assinatura da máquina não conta como mudança de conteúdo.
function CorpoDoScript([string]$caminho) {
    $texto = Get-Content -LiteralPath $caminho -Raw
    if ($null -eq $texto) { return '' }
    $marca = $texto.IndexOf('# SIG # Begin signature block', [System.StringComparison]::OrdinalIgnoreCase)
    if ($marca -ge 0) { $texto = $texto.Substring(0, $marca) }
    return ($texto -replace "`r`n", "`n").TrimEnd()
}

function Copiar($de, $para, $motivo) {
    if ($DryRun) { Write-Host "   (simulado) $motivo"; return }
    Copy-Item -LiteralPath $de -Destination $para -Force
    Write-Host "   $motivo"
}

$nomes = @()
foreach ($pasta in @($origem, $destino)) {
    $nomes += (Get-ChildItem -LiteralPath $pasta -File | ForEach-Object { $_.Name })
}
$nomes = $nomes | Sort-Object -Unique

$copiados = 0
$scriptsCopiados = 0
$iguais = 0
$ignorados = @()

Write-Host "== comparando $($nomes.Count) arquivo(s)"
foreach ($nome in $nomes) {
    if ($nome -match $lixo) { $ignorados += $nome; continue }

    $ext = [System.IO.Path]::GetExtension($nome).ToLowerInvariant()
    $a = Join-Path $origem $nome
    $b = Join-Path $destino $nome
    $temA = Test-Path -LiteralPath $a
    $temB = Test-Path -LiteralPath $b

    if ($ext -eq '.ps1') {
        # Script: o repositório manda. A cópia de dist é reassinada pelo HUB quando o corpo muda.
        if (-not $temA) { Write-Host "   atencao: $nome existe so em dist (script assinado) — mantido, sem copiar para src"; continue }
        if (-not $temB) { Copiar $a $b "src -> dist  $nome (script novo)"; $copiados++; $scriptsCopiados++; continue }
        if ((HashTexto (CorpoDoScript $a)) -ne (HashTexto (CorpoDoScript $b))) {
            Copiar $a $b "src -> dist  $nome (script mudou)"
            $copiados++
            $scriptsCopiados++
        }
        else { $iguais++ }
        continue
    }

    if (@('.json', '.ico', '.png') -notcontains $ext) {
        $ignorados += $nome
        continue
    }

    if ($temA -and -not $temB) { Copiar $a $b "src -> dist  $nome (novo no repositorio)"; $copiados++; continue }
    if ($temB -and -not $temA) { Copiar $b $a "dist -> src  $nome (novo na pasta publicada)"; $copiados++; continue }

    $ha = (Get-FileHash -LiteralPath $a -Algorithm SHA256).Hash
    $hb = (Get-FileHash -LiteralPath $b -Algorithm SHA256).Hash
    if ($ha -eq $hb) { $iguais++; continue }

    # Conteúdo diferente: vence o mais recente; empate fica com o repositório.
    $ta = (Get-Item -LiteralPath $a).LastWriteTimeUtc
    $tb = (Get-Item -LiteralPath $b).LastWriteTimeUtc
    if ($ta -ge $tb) { Copiar $a $b "src -> dist  $nome (src mais recente)"; $copiados++ }
    else { Copiar $b $a "dist -> src  $nome (dist mais recente)"; $copiados++ }
}

Write-Host "== resultado: $copiados copiado(s), $iguais igual(es)"
if ($ignorados.Count -gt 0) { Write-Host "== fora do escopo (runtime): $($ignorados -join ', ')" }
if ($DryRun) {
    Write-Host "== DryRun: nada foi gravado."
    exit 0
}
if ($copiados -eq 0) { Write-Host "== as duas pastas ja estavam iguais" }

# Script que mudou chega em dist SEM assinatura (a do repo nao tem). Assinar aqui evita a armadilha:
# o gate recusaria "ferramenta ainda nao assinada" e, com -NoRestart, ninguem reassinaria.
if ($scriptsCopiados -gt 0) {
    Write-Host "== assinando os scripts que mudaram (SIG-001)"
    & $exe --sign-tools | Out-Host
}

# Gate: nenhum manifesto invalido passa — e "material:icone" desconhecido tambem falha aqui.
$codigo = 0
if ($SemGate) {
    Write-Host "== gate pulado (-SemGate)"
}
else {
    Write-Host "== gate do catalogo (AnalistaHub.exe --check-tools)"
    $saida = & $exe --check-tools 2>&1 | Out-String
    $codigo = $LASTEXITCODE
    if ($codigo -ne 0) {
        Write-Host $saida
        Write-Host "== GATE FALHOU (exit $codigo): corrija o que esta acima. O HUB nao foi reaberto." -ForegroundColor Red
        exit $codigo
    }
    Write-Host "== gate ok"
}

# Reabrir só faz sentido depois do gate verde: o catálogo é lido UMA vez, na inicialização.
$rodando = @(Get-Process -Name 'AnalistaHub' -ErrorAction SilentlyContinue)
if ($NoRestart) {
    if ($rodando.Count -gt 0) { Write-Host "== HUB aberto continua com o catalogo antigo: feche e abra para ver" }
    exit 0
}
if ($rodando.Count -eq 0 -and -not $Abrir) {
    Write-Host "== HUB estava fechado: abra quando quiser (nada a reiniciar)"
    exit 0
}

if ($rodando.Count -gt 0) {
    Write-Host "== fechando o HUB (pid $($rodando.Id -join ', '))"
    $rodando | Stop-Process -Force
    for ($i = 0; $i -lt 20; $i++) {
        Start-Sleep -Milliseconds 250
        if (-not (Get-Process -Name 'AnalistaHub' -ErrorAction SilentlyContinue)) { break }
    }
}

Start-Process -FilePath $exe -WorkingDirectory (Split-Path -Parent $exe) | Out-Null
Write-Host "== HUB reaberto com o catalogo novo"
exit 0
