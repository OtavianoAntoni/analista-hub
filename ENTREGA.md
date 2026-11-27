# Entrega da aplicação — Analista Hub 0.1.0

Documento de passagem para a sessão que vai **empacotar/instalar**. Aqui está o que o app é,
o que ele espera do ambiente e como provar que funciona.

---

## 1. O que foi construído

Aplicação Windows nativa (WinUI 3 + Windows App SDK 1.8, .NET 8) que reúne as ferramentas do
analista. Duas superfícies:

- **HUD de comando** — janela flutuante, sem moldura, sempre no topo. Abre com **Ctrl+Alt+Espaço**
  de qualquer aplicativo. Busca por nome, gatilho (`:mkt`, `:macv`) ou descrição; `Enter` executa.
- **Janela principal** estilo PowerToys — lista à esquerda, e cada ferramenta mostra os quatro
  itens do `IDEA.md`: para que serve, como executar, configurações/credenciais exigidas e como o
  resultado é apresentado.

Além disso: ícone na bandeja (clique = busca, botão direito = janela), aviso flutuante de
resultado perto do cursor, histórico local de execuções, verificação de conexão por ferramenta.

Ferramentas já plugadas: **SN da RB pelo IP** (`:mkt`) e **Fabricante pelo MAC** (`:macv`), ambas
executando os scripts PowerShell que já existiam — nada foi reescrito.

**Formato de distribuição:** pasta portátil self-contained. Não precisa instalar .NET, nem
Windows App SDK, nem pedir administrador. São ~1.000 arquivos / ~230 MB por causa do runtime
embutido (pode ser comprimido no instalador).

---

## 2. Onde está o quê

```
Atalhos de Analista/
  SPEC.md                  comportamento especificado (fonte de verdade do produto)
  ENTREGA.md               este documento
  src/AnalistaHub/         código-fonte
  launcher/                lançadores .cmd de duplo-clique (copiados para o dist)
  build/publish.ps1        gera dist/AnalistaHub
  dist/AnalistaHub/        pacote pronto  (AnalistaHub.exe + Analista Hub.cmd + tools/ + data/)
```

Ferramenta **Consulta SSE** (`:sse`): a base vai embutida em `dist\AnalistaHub\data\sse-cidades.json`
(schema 3, 492 KB — 5577 cidades: 161 com NM/OLT/VPN e o resto só cadastro). Nada é consultado na
rede. Para atualizar os dados **sem reinstalar**, o instalador (ou o analista) copia o novo
`sse-cidades.json` para `%LOCALAPPDATA%\AnalistaHub\` — esse caminho tem precedência sobre o embutido.
Gerar/regerar: `build_merge.py` em `Documents\Network\ANATEL Tarifacao\_build`
(planilhas do SSE + cadastro de municípios da ANATEL).
Spec: `docs/FERRAMENTA-CONSULTA-SSE.md`.

Saída de dados do app (nunca dentro da pasta do programa):

| Caminho | Conteúdo |
|---|---|
| `%LOCALAPPDATA%\AnalistaHub\settings.json` | preferências (atalho, tema, modo de saída por ferramenta) |
| `%LOCALAPPDATA%\AnalistaHub\tools\*.json` | manifestos de ferramentas do usuário (sobrepõem os embutidos) |
| `%LOCALAPPDATA%\AnalistaHub\historico.jsonl` | últimas execuções (sem segredos) |
| `%LOCALAPPDATA%\AnalistaHub\hub.log` | diagnóstico |

Credenciais **não** ficam no repositório nem em arquivo de texto do HUB: são digitadas na aba da
ferramenta e guardadas cifradas com DPAPI em `%LOCALAPPDATA%\AnalistaHub\credenciais.dat`, sendo
entregues ao script por variável de ambiente (`ANALISTAHUB_USER` / `ANALISTAHUB_PASS`). O cofre
markdown que os scripts antigos leem continua funcionando como fallback — o HUB não exige que ele
exista, e não mostra caminho de arquivo para o operador.

---

## 3. O que o instalador precisa fazer

1. Copiar `dist\AnalistaHub\` para a pasta final (sugestão: `%LOCALAPPDATA%\Programs\AnalistaHub\`
   — gravação do usuário, sem elevação).
2. Criar atalho no Menu Iniciar e na Área de Trabalho apontando para `AnalistaHub.exe`
   (ícone já embutido no exe; `Assets\app.ico` está na pasta).
3. **Auto-start:** a máquina corporativa **nega** gatilho de boot/logon no Task Scheduler. O
   caminho que funciona é um atalho `.lnk` na pasta Inicializar
   (`%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup`) — e o próprio app já oferece isso em
   Configurações → Inicialização (`WScript.Shell` + `CreateShortcut`). O instalador pode ligar essa
   opção criando o mesmo atalho, ou deixar para o usuário.
4. Registrar desinstalação: basta apagar a pasta + o atalho de Inicializar. Os dados em
   `%LOCALAPPDATA%\AnalistaHub` devem ser oferecidos como "remover também?".
5. Não escrever em `HKLM`, não instalar serviço, não pedir elevação.

---

## 4. Como provar que a instalação funcionou (smoke test)

Rodar depois de instalar, sem abrir a interface:

```powershell
$exe = "$env:LOCALAPPDATA\Programs\AnalistaHub\AnalistaHub.exe"

& $exe --version                      # {"ok":true,"version":"0.1.0"}
& $exe --list-tools                   # as duas ferramentas, "estado":"ok", script_existe:true
& $exe --probe mkt-serial             # servico_responde:true e credenciais completo:true
& $exe --validate-cred mkt-serial     # {"ok":true,"estado":"ok","mensagem":"auth_success"}
& $exe --check-tools                  # gate das regras: {"ok":true,"problemas":[]}
& $exe --try-hotkey "Ctrl+Alt+F9"     # diz se o Windows libera o atalho agora, sem abrir janela
& $exe --test mac-vendor "F4:8E:38"   # {"ok":true,"valor":"Dell Inc."}
& $exe --test mkt-serial "10.0.0.1"   # {"ok":true,"valor":"<SN>1"}
& $exe --set-cred mkt-serial passwd   # valor vem do STDIN (não aparece no histórico do shell)
```

Depois, na interface: `Analista Hub.cmd` → janela principal; `Ctrl+Alt+Espaço` → HUD;
digitar `:macv` → `Enter` → campo aparece → `Enter` de novo com o MAC no clipboard.

---

## 5. Pontos de atenção conhecidos

- **Atalho de ativação:** se outra ferramenta já usar `Ctrl+Alt+Espaço`, o app avisa na barra lateral e
  em Configurações → Atalho de ativação dá para trocar. (PowerToys Run usa `Alt+Espaço`; a Paleta de
  Comandos do PowerToys usa `Win+Alt+Espaço` — nenhum conflito com o padrão escolhido.)
- **Duplo aviso de erro:** os scripts atuais abrem o próprio popup quando falham. Enquanto não
  migrarem para o contrato `-Mode hub` (descrito na SPEC §7), a falha aparece no HUD **e** no popup
  antigo. Não é bug do app; é transição.
- **`mac_vendor.ps1` devolve erro no stdout com exit 0** — tratado por `stdoutErrorPatterns` no
  manifesto. Ferramenta nova deve usar `-Mode hub` (JSON) para não precisar disso.
- **Antivírus/SmartScreen:** executável não assinado, self-contained, extraído de pasta do usuário.
  Se o SmartScreen reclamar, é assinatura de código — assunto do instalador.
- **Um único .exe não basta:** a pasta inteira é necessária (runtime + Windows App SDK embutidos).
- **Atualização automática:** o app se atualiza sozinho (ver §7). A pasta do programa precisa ser
  **gravável pelo usuário** — instalar em `Program Files` quebra a auto-atualização e o app avisa
  "a pasta do programa não é gravável".

---

## 6. O que ficou fora desta entrega

- Instalador e assinatura de código (a atualização automática está na §7).
- Sincronização de configurações entre pessoas da equipe (previsto no `IDEA.md`, não implementado).
- Migração dos dois scripts existentes para `-Mode hub`.
- Ações que alteram equipamentos: o HUB só consulta.

---

## 7. Atualização automática (a partir da 0.2.0)

O app verifica **uma vez por dia** se há versão nova, no endereço do manifesto da última release:

```
https://github.com/<owner>/<repo>/releases/latest/download/latest.json
```

- Avisa uma vez por versão e mostra o estado em **Configurações → Atualizações**.
- **Procurar por atualização agora** existe no bloco de Configurações e no menu do ícone da bandeja.
- O pacote é conferido por SHA-256; a troca fecha o app, substitui a pasta e reabre. Se a versão
  nova não subir em 60 s, a anterior volta sozinha.
- Estado em `%LOCALAPPDATA%\AnalistaHub\`: `update\<versao>\` (pacote) e `update\apply.log`
  (o que o script de troca fez). Preferências em `settings.json` (`AutoCheckUpdates`,
  `LastUpdateCheckUtc`, `UpdateFeedUrl`).

Publicar versão nova: sobe a `<Version>` no `AnalistaHub.csproj`, commita e empurra a tag
`v<x.y.z>` — o workflow `.github/workflows/release.yml` builda, empacota e publica (zip +
`latest.json`). Detalhe em `SPEC.md` §7.2.

**Dois canais (2.4+).** O app resolve a versão em **GitHub → GitLab** (`updater.github-repo` /
`updater.gitlab-repo` no `config.local.json`, aceitando também GitLab self-hosted), e o instalador usa
a mesma ordem (`CHANNEL` → `CHANNEL_FALLBACK`). Publicar nos dois:

1. **GitHub:** empurre a tag `v<x.y.z>` — o workflow publica zip + `latest.json`.
2. **GitLab:** o espelho recebe código e tags, mas **release não é automática**. Suba os arquivos no
   *generic package registry* e crie a release com *direct asset links*:

```
PUT  /projects/<id>/packages/generic/analista-hub/<versao>/<arquivo>
POST /projects/<id>/releases
{"tag_name":"v<versao>","assets":{"links":[
  {"name":"latest.json","url":"https://gitlab.com/api/v4/projects/<id>/packages/generic/analista-hub/<versao>/latest.json",
   "link_type":"package","direct_asset_path":"/latest.json"}]}}
```

O `url` do asset **dentro** do `latest.json` do GitLab precisa apontar para
`https://gitlab.com/<grupo>/<projeto>/-/releases/permalink/latest/downloads/AnalistaHub-<versao>-win-x64.zip`
— é o endereço que responde sem autenticação. Conferir com
`curl -L …/-/releases/permalink/latest/downloads/latest.json`.

**Atenção (27/11/2026):** a conta `OtavianoAntoni` responde **404 para acesso anônimo** (repositório,
`raw` e perfil), mesmo com `visibility: public` na API. Na prática o canal GitHub não entrega nada a
terceiros e o app cai no GitLab — se o objetivo é usar o GitHub como primário, isso precisa ser
resolvido no GitHub antes.

**Requisito do canal:** o manifesto e o zip precisam ser legíveis **sem autenticação**. Enquanto o
repositório for privado, o `GET` responde 404 e o app mostra "nenhuma versão publicada ainda".
Alternativas sem expor nada: `UpdateFeedUrl` apontando para uma **share de rede**
(`\\servidor\share\AnalistaHub\latest.json` — o app lê a pasta direto, e o `url` do asset pode ser
relativo), `file://`, ou repositório público só de releases.
