# Analista Hub

Aplicação Windows que reúne as ferramentas do analista em um só lugar: um **HUD de comando**
(estilo PowerToys Run, aberto por atalho global) e uma **janela principal** onde cada ferramenta
declara para que serve, como executar, o que exige e como entrega o resultado.

A ferramenta em si continua sendo um script (PowerShell, Python, .exe). O HUB cuida de entrada,
execução, credenciais, verificação de conexão e apresentação do resultado.

- **Stack:** C# / .NET 8 + WinUI 3 (Windows App SDK 1.8), aplicativo **unpackaged, self-contained** —
  não pede administrador, não instala runtime e roda a partir da pasta do usuário.
- **Interface:** HUD flutuante sem moldura, janela principal com navegação lateral, ícone na
  bandeja e aviso de resultado flutuante perto do cursor.

## Como compilar

Sem Visual Studio e sem administrador:

```powershell
# 1. SDK do .NET no perfil do usuário (uma vez)
curl -sSL -o "$env:LOCALAPPDATA\Temp\dotnet-install.ps1" https://dot.net/v1/dotnet-install.ps1
powershell -ExecutionPolicy Bypass -File "$env:LOCALAPPDATA\Temp\dotnet-install.ps1" `
  -Channel 8.0 -Quality GA -InstallDir "$env:USERPROFILE\.dotnet" -NoPath

# 2. build
$env:PATH = "$env:USERPROFILE\.dotnet;$env:PATH"
dotnet build src\AnalistaHub\AnalistaHub.csproj -c Release

# 3. pacote portátil em dist\AnalistaHub
powershell -ExecutionPolicy Bypass -File build\publish.ps1
```

O `publish.ps1` copia os recursos XAML compilados (`.xbf`/`.pri`) que o `dotnet publish` do WinUI 3
**não** leva — sem eles o executável abre e morre com `XamlParseException`.

## Estrutura

```
src/AnalistaHub/            aplicação (C# + XAML)
  Models/ToolManifest.cs    contrato do manifesto de ferramenta
  Services/                 catálogo, executor, credenciais, hotkey, bandeja, diagnóstico
  Services/UpdateService.cs canal de atualização (manifesto, download, troca de pasta)
  Views/                    HUD de comando, painel da Consulta SSE e aviso de resultado
  tools/*.json              manifestos embutidos
  data/*.json               bases locais das ferramentas do tipo "dataset"
build/publish.ps1           gera dist/AnalistaHub (self-contained, enxuga idiomas)
build/package-release.ps1   gera dist/release (zip + sha256 + latest.json) para publicar
.github/workflows/release.yml  publica a release ao empurrar uma tag v*
launcher/                   .cmd de duplo clique (copiado para o dist)
docs/FERRAMENTA-CONSULTA-SSE.md  spec da ferramenta de consulta de cidades
SPEC.md                     comportamento especificado (fonte de verdade do produto)
ENTREGA.md                  passagem para a sessão que empacota/instala
```

## Atualização automática

O app consulta **uma vez por dia** o manifesto da última release

```
https://github.com/<owner>/<repo>/releases/latest/download/latest.json
```

e, se houver versão maior que a instalada, avisa (uma vez por versão) e oferece instalar em
**Configurações → Atualizações** — o mesmo comando está no menu da bandeja como
**Procurar atualização agora**. O pacote é conferido por SHA-256 antes de qualquer coisa, e a troca
de versão fecha o app, substitui a pasta e reabre; se a versão nova não subir, a anterior volta.

Publicar uma versão nova:

```bash
# 1. suba a versão no csproj e commite
# 2. crie a tag — o workflow builda, empacota e publica
git tag v0.2.1 && git push origin v0.2.1
```

Para gerar o pacote à mão (sem CI): `powershell -File build\package-release.ps1 -Version 0.2.1`
e suba `dist\release\{*.zip, *.sha256, latest.json}` numa release do GitHub.

O canal precisa ser **legível sem autenticação**: em repositório privado o manifesto responde 404.
Nesse caso use `UpdateFeedUrl` (em `settings.json`) apontando para um endereço interno, ou publique
os artefatos num repositório público só de releases.


## Ferramenta de base local (`kind: "dataset"`)

Além das ferramentas que chamam um script, o HUB aceita uma ferramenta que **só consulta uma base
local** — sem processo, sem rede, sem credencial. Exemplo: **Consulta SSE** (`:sse`), que devolve
Sigla Anatel + NM + OLT + VPN de uma cidade. Ela abre um painel próprio (mesmo visual e mesmas
teclas do HUD: digitar → `↑↓` → `Enter` copia → `Esc` fecha), porque a consulta tem um passo de
escolha entre vários resultados.

```json
{
  "id": "consulta-sse",
  "name": "Consulta SSE",
  "kind": "dataset",
  "dataset": "data/sse-cidades.json",
  "trigger": ":sse"
}
```

O arquivo da base é procurado primeiro em `%LOCALAPPDATA%\AnalistaHub\` (atualização sem reinstalar)
e depois em `data\` ao lado do executável. Detalhes em `docs/FERRAMENTA-CONSULTA-SSE.md`.

## Adicionar uma ferramenta (sem recompilar)

**Leia `docs/REGRAS-DE-FERRAMENTA.md` antes.** As regras de gatilho, validação de credencial e status
são permanentes e valem para toda ferramenta nova; o publish roda um gate (`--check-tools`) e falha
se o manifesto não seguir a regra.

Um arquivo `.json` em `%LOCALAPPDATA%\AnalistaHub\tools\` (sobrepõe o embutido de mesmo `id`):

```json
{
  "id": "minha-ferramenta",
  "name": "Nome que aparece na busca",
  "category": "Rede",
  "description": "Uma linha explicando o que faz.",
  "trigger": ":minha",
  "icon": "\uE968",
  "script": "%APPDATA%/caminho/do/script.ps1",
  "interpreter": "powershell",
  "modeArgs": [ "-Mode", "hub" ],
  "inputs": [
    { "key": "alvo", "label": "IP", "arg": "-Ip", "type": "ip",
      "required": false, "emptyUsesClipboard": true, "placeholder": "10.0.0.1" }
  ],
  "output": { "default": "clipboard", "allowed": [ "clipboard", "type", "notify" ],
              "valueLabel": "o que é copiado" },
  "credentials": {
    "label": "Sistema X",
    "fields": [
      { "key": "user",   "label": "Usuário", "env": "ANALISTAHUB_USER", "secret": false },
      { "key": "passwd", "label": "Senha",   "env": "ANALISTAHUB_PASS", "secret": true  }
    ]
  },
  "network": { "label": "Sistema X", "host": "10.0.0.1", "port": 443 },
  "timeoutSeconds": 12
}
```

### Contrato recomendado com o script

`-Mode hub` — o script imprime **um** objeto JSON no stdout:

```json
{ "status": "ok", "value": "ABC1231", "detail": "", "meta": { "ip": "10.0.0.1" } }
```

`status: "error"` exige `detail` com a mensagem para o operador. Exit code 0 nos dois casos.

O contrato antigo (`-Mode type`, valor no stdout) também é aceito: stdout vazio = falha, e o
manifesto pode declarar `stdoutErrorPatterns` para scripts que escrevem a **mensagem de erro no
stdout** com exit 0.

## Credenciais

Digitadas na aba da ferramenta, gravadas sozinhas e cifradas com **DPAPI** (escopo do usuário do
Windows) em `%LOCALAPPDATA%\AnalistaHub\credenciais.dat`. Na execução vão para o script por
**variável de ambiente** (`ANALISTAHUB_USER` / `ANALISTAHUB_PASS`) — nunca por linha de comando,
nunca em log, nunca no repositório.

## Linha de comando (diagnóstico e instalação)

```
AnalistaHub.exe --list-tools
AnalistaHub.exe --test <id> [valor]
AnalistaHub.exe --probe <id>
AnalistaHub.exe --set-cred <id> <campo>    (valor vem do STDIN)
AnalistaHub.exe --clear-cred <id>
AnalistaHub.exe --set-trigger <id> :gatilho
AnalistaHub.exe --page <id|home|settings>
```

## Aviso

Os manifestos embutidos apontam para serviços **internos** de uma rede corporativa. Este repositório
existe para desenvolvimento e não deve ser tornado público sem revisar esses endereços.

Especificação de comportamento: `SPEC.md`. Empacotamento/instalação: `ENTREGA.md`.
