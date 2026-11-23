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
  Views/                    HUD de comando e aviso de resultado
  tools/*.json              manifestos embutidos
build/publish.ps1           gera dist/AnalistaHub (self-contained, enxuga idiomas)
launcher/                   .cmd de duplo clique (copiado para o dist)
SPEC.md                     comportamento especificado (fonte de verdade do produto)
ENTREGA.md                  passagem para a sessão que empacota/instala
```

## Adicionar uma ferramenta (sem recompilar)

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
