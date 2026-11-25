# Patch log — Analista Hub

> Formato: o resumo conta o que mudou para quem usa; o detalhamento fica separado por
> **Adicionados**, **Corrigido** e **Removidos**. Cada item diz como foi verificado.

## 2.3.0 — em preparação (não publicado)

**Resumo.** Três entregas que tiram trabalho manual de quem instala e de quem usa o HUB em máquina de
terceiro: (1) um **instalador `.exe`** com o wizard padrão do Windows, que baixa o pacote publicado,
confere o SHA-256 e cria os atalhos; (2) a **lista de NMs passa a ser descoberta sozinha** — sem a
chave no `config.local.json`, o app procura a pasta compartilhada da equipe, e quando não acha avisa
na tela em vez de ficar sem NM em silêncio; (3) **assinatura local dos scripts das ferramentas**, para
quando a política de execução do PowerShell (`AllSigned`) vem por GPO e o `-ExecutionPolicy Bypass`
não resolve. Nesta rodada do mesmo patch entram ainda: (4) o **ícone do executável embutido** no
binário, com gate que barra release com ícone genérico; (5) **janela principal de tamanho fixo** —
acabou o reflow a cada arrasto e a área branca que aparecia ao esticar; (6) **clique da bandeja
configurável**, **SEM SN sem valor solto**, **host do inventário e lista de NMs editáveis na própria
ferramenta** e **busca da Consulta SSE priorizando sigla**.

### Adicionados

- **Instalador `AnalistaHub-Setup-<versão>.exe` (INST-001)** — wizard MUI2 em pt-BR com 5 páginas
  (boas-vindas, destino, opções, progresso, conclusão). Não embute o aplicativo: baixa o `.zip` do
  canal, confere `sha256` **e** tamanho do manifesto antes de gravar qualquer arquivo, e extrai na
  pasta escolhida. Dois modos no **mesmo** `.exe`: elevado → `C:\Program Files\Analista Hub` (todos os
  usuários); sem elevação → `%LOCALAPPDATA%\Programs\Analista Hub` (só para mim). Menu Iniciar é
  sempre criado; área de trabalho e barra de tarefas são checkboxes na mesma etapa (o pin é
  *best-effort*, e a conclusão ensina a fixar à mão quando o Windows recusa). Desinstalador registrado
  em *Aplicativos e recursos*, que **preserva** `%LOCALAPPDATA%\AnalistaHub` salvo se o usuário marcar
  a opção de remover. Linha de comando: `/S /D=<pasta>`. Build sem administrador e sem Visual Studio
  (`build\build-installer.ps1`, NSIS 3 portátil baixado uma vez).
- **Descoberta automática da lista de NMs (NET-005)** — ordem: `sse-rede.path` → `sse-rede.candidatos`
  (lista) → variável `ANALISTAHUB_SSE_REDE` (arquivo **ou** pasta) → `<app>\data\sse-rede.local.json` →
  `%LOCALAPPDATA%\AnalistaHub\sse-rede.local.json`. Cada candidato é **validado** antes de ser aceito
  (JSON, `k`/`uf`, linhas de acesso); candidato inválido é registrado e o próximo é testado. A
  descoberta tem orçamento de tempo (700 ms por candidato, 1,5 s no total) para a janela abrir mesmo
  com o share fora do ar, e a origem efetiva aparece na página da ferramenta, no ⚙ do painel e no
  `--check-tools`. Botão **Recarregar dados** relê sem fechar o app (`SseCatalog.Reload`).
- **Assinatura local das ferramentas (SIG-001)** — o app (ou o instalador, no modo todos os usuários)
  cria um certificado de assinatura de código com chave **não exportável**, ancora como raiz e
  publicador confiável no repositório do usuário (ou da máquina) e assina os `tools\*.ps1`. Roda no
  startup e assina só o que mudou; `--sign-tools [--force] [--machine]` faz o mesmo pela linha de
  comando. Estado em `%LOCALAPPDATA%\AnalistaHub\assinatura.json` e bloco *Configurações → Assinatura
  local* com o botão **Reassinar ferramentas**.
- **Ícone do executável embutido (PKG-001)** — `<ApplicationIcon>Assets\app.ico</ApplicationIcon>` no
  `.csproj`; `build\check-icon.ps1` extrai o ícone do PE e o `publish.ps1` **aborta** quando ele é o
  genérico (25 cores). Antes o ícone só existia em runtime (`AppWindow.SetIcon`), então Explorer e
  barra de tarefas mostravam ícones diferentes.
- **Clique esquerdo da bandeja configurável (CFG-004)** — Configurações → **Ícone da bandeja**, com os
  rótulos "Abrir janela principal" / "Abrir busca rápida" e descrição que muda junto. Padrão: janela
  principal. Duplo clique espelha a escolha; a ação é idempotente e uma guarda de 300 ms evita duas
  execuções. O tooltip do ícone acompanha o modo.
- **Casa na busca rápida (SQ-005)** — a lupa virou botão de casa ("Abrir janela principal"), acessível
  por Tab e com nome acessível; clicar fecha a busca e foca a janela principal.
- **Servidor do inventário pela UI (MKT-002)** — campo + **Salvar** + **Testar conexão** na página da
  ferramenta; grava `mkt-serial.host` e mantém `mkt-serial.url` coerente, com validação de IPv4/host
  e confirmação antes de substituir valor existente.
- **Importar a lista de NMs pela UI (SSE-002, absorve NET-002)** — botão **Importar
  sse-rede.local.json…** na página da Consulta SSE: valida o JSON, pede confirmação, guarda a lista
  anterior como `.bak` e recarrega. Diagnóstico: `--validate-rede` e `--import-rede [--force]`.
- **Atalho abre a ferramenta com o campo focado (UX-001)** — no modo "Aguardar input" o foco passou a
  ser garantido e registrado (`hud: … foco em campo-ip`), provado para `mkt-serial` e `mac-vendor`;
  `consulta-sse` continua abrindo o painel.

### Corrigido

- **Janela principal de tamanho fixo (UI-004)** — 1120×760 DIP (1400×950 px em 125 %), sem borda de
  redimensionar e sem maximizar; o conteúdo preenche a janela e acabou o branco puro que cobria até
  76 % da área ao esticar. Em 1366×768 o tamanho é limitado a 94 % da área útil. O retângulo real é
  guardado quando o DPI assenta e restaurado por um vigia de 300 ms se algo o mudar (cobre Win+↑).
- **SEM SN limpo (MKT-001)** — o aviso mostra **SEM SN** (azul) e `<IP> · Não registrado`; nenhum `0`
  solto e **nada é copiado** para a área de transferência nesse estado (o valor anterior é
  preservado). O vocabulário vem do manifesto (`output.zeroValue`).
- **Botões de erro (MKT-003 / MAC-001)** — IP inválido e "Endereço localmente administrado" não
  mostram mais "Copiar detalhe" (nem "Tentar de novo", no MAC); sobra a ação **Fechar**, e a faixa
  ocupa o lugar da lista para caber inteira.
- **Ranking da Consulta SSE (SSE-001)** — termo com cara de sigla (2–5, maiúsculo, sem espaço/dígito)
  prioriza a SIGLA: `ITA` traz ITATIBA/ITAARA/ITAPUCA/ITATI antes dos nomes que contêm "ita". Busca
  por nome completo inalterada. Diagnóstico: `--search-sse <termo> [--limit N]`.

### Decisão registrada — Program Files × atualização automática (§8)

Escolhida a opção **A**: no modo *todos os usuários* o instalador concede **Modify** ao grupo `Users`
na pasta do aplicativo (`icacls … /grant *S-1-5-32-545:(OI)(CI)M`). Sem isso a atualização automática
recusava a pasta (`UpdateService` responde "peça ao instalador para atualizar") e o modo Program Files
quebrava a atualização diária que hoje funciona no portátil. O efeito colateral está declarado em
`docs/INSTALADOR.md`: qualquer usuário do grupo `Users` pode alterar os arquivos da pasta — a proteção
"outros usuários não mexem" deixa de valer, e quem precisa dela deve usar o modo *só para mim*.

### Verificado (comando e resultado real)

- `build\build-installer.ps1 -Version 2.2.1` → `dist\setup\AnalistaHub-Setup-2.2.1.exe`, ProductName
  `Analista Hub`, ProductVersion `2.2.1`, sem admin.
- Instalação silenciosa `/S /D=<pasta>`: extraiu, criou atalho do Menu Iniciar, gravou a chave de
  desinstalação; `sha256` adulterado → **exit 2**, log `ERRO: hash nao confere com o publicado` e
  **nada gravado** no destino; desinstalação removeu pasta + atalho + registro e **preservou**
  `%LOCALAPPDATA%\AnalistaHub`.
- `--test consulta-sse taguatinga` com `sse-rede.candidatos` apontando primeiro para UNC inexistente e
  um JSON quebrado, depois para a pasta boa: usou o terceiro candidato (NM de teste), registrou os
  dois primeiros com o motivo, em 833 ms.
- Share fora do ar **sem** arquivo local: base pública, `rede_interna` = "lista de NMs inacessível —
  usando base pública (…)" em 598 ms.
- `--sign-tools`: `Get-AuthenticodeSignature` = **Valid** nos dois scripts reais; segunda execução
  "nenhum precisava"; arquivo alterado à mão foi reassinado sozinho; `--sign-tools` com política
  `Restricted` devolve `politica_bloqueia` (exit 1) sem tentar assinar; `ExportPolicy=None` e
  `ExportParameters` recusado (chave não exportável).
- Script novo sob `-ExecutionPolicy AllSigned`: **PSSecurityException** antes de assinar, roda depois
  (`script executou`).
- **PKG-001** — `check-icon.ps1` no `dist` 2.2.1: `cores=24, ok=false` (exit 1, ícone genérico); no
  build novo: `cores=138, ok=true` (exit 0) após `-t:Rebuild`.
- **UI-004** — antes (dist 2.2.1): conteúdo travado em ~1112×676 e branco puro de 6,45 % a 75,67 %
  ao crescer; depois: janela 1120×684, `WS_THICKFRAME`/`WS_MAXIMIZEBOX` ausentes, **0,28 % de branco
  puro**, `SC_MAXIMIZE` revertido em ≤ 300 ms (`evidence/ui-004-fixo-0453aed8-20261125_1435`).
- **CFG-004** — `--tray-click` com `"search"` → `ShowHud: visivel=True`; com `"window"` →
  `ShowMainWindow: hwnd=… visivel=True`. Captura do bloco "Ícone da bandeja" com os dois rótulos e
  sem "Ativado/Desativado"; UIA do interruptor: `ControlType.Button, Toggle=Off` (padrão).
- **SQ-005** — UIA `Invoke` no botão "Abrir janela principal" da busca → log `hud: casa clicada` +
  `ShowMainWindow`, e a HUD fecha (1 janela com título "Analista Hub" depois do clique).
- **MKT-001** — fixture devolvendo `value:"0"`: cartão com **SEM SN** azul, `10.0.0.1 · Não
  registrado`, rodapé "somente aviso" e clipboard ainda com a sentinela `SENTINELA-NAO-MEXER`.
- **MKT-003 / MAC-001** — UIA na HUD: `Tentar de novo` achados=0, `Copiar detalhe` achados=0;
  capturas com a faixa inteira e o botão **Fechar**.
- **MKT-002** — UIA digitou `999.999.999.999` → "IPv4 inválido…"; `10.0.0.1` → "Já configurado como
  …, clique em Salvar de novo"; 2º clique gravou `host=10.0.0.1` e
  `url=http://10.0.0.1:3113/api/auth_ep`; **Testar conexão** respondeu "sem resposta …:3113" em 3 s.
  `config.local.json` original restaurado ao fim.
- **SSE-002** — `--validate-rede` válido → `ok=true, 1 cidade`; inexistente → `ok=false` (exit 1);
  `--import-rede` sem `--force` → recusa; com `--force` → copiou e o `--check-tools` passou a ler
  **1 cidade**; lista original (161) restaurada.
- **SSE-001** — `--search-sse ITA` (250 resultados): topo ITA(SC, nome exato), ITATIBA, ITAARA,
  ITAPUCA, ITATI, ITANHAÉM; `sao paulo` → SPO primeiro; `taguatinga` → TAGA; `ALUMINIO` → AMIO.
- **UX-001** — `--tool-hotkey mkt-serial` → `campos abertos, foco em campo-ip`; `mac-vendor` →
  `foco em campo-mac`.

### Limites deste patch

- O `.exe` do app e o Setup continuam **sem assinatura Authenticode** → SmartScreen mostra "aplicativo
  não reconhecido". Fica para o item 5 do Patch Request 2.3 (certificado de CA).
- A assinatura local **não** é controle de segurança: qualquer processo do mesmo usuário pode assinar
  o que quiser com a mesma âncora. Ela satisfaz a política de execução, não prova autoria.
- Modo *todos os usuários* (Program Files) **não foi testado** nesta máquina: exige elevação, e a
  política de domínio não a concede ao usuário. O caminho `--sign-tools --machine` existe e está
  documentado, mas roda sem prova de execução.
- O modo `AllSigned` foi simulado com `-ExecutionPolicy AllSigned` no processo; falta validar em
  máquina com GPO equivalente (é o cenário real do `mac_vendor.ps1` bloqueado no 2.3).
- **DPI 125 %** da janela de tamanho fixo: medido no monitor 2 — 1400×950 px, **0,31 % de branco
  puro**, `SC_MAXIMIZE` revertido pelo vigia em ≤ 300 ms, e o retângulo é adotado ao trocar de
  monitor (`evidence/ui-004-fixo-dpi/`). Falta 150 %/200 %, que esta máquina não tem.
- **R7 (dívida antiga, não deste patch)** — `SC_CLOSE` (o caminho do Alt+F4 com a janela em foco)
  fecha a janela tanto no 2.2.1 quanto no 2.3; o app segue vivo na bandeja e reabre. O
  `test-janela-branca.py` cai nesse caminho quando não consegue focar e reporta `sobreviveu=False`.

## 2.2.1 — 24/11/2026

**Resumo.** Correções de invocação e de mensagem, mais duas mudanças pedidas no patch: o atalho da
ferramenta passa a ter comportamento explícito por ferramenta (abrir pedindo o valor ou consultar
direto a área de transferência), e o conceito de *gatilho* (`:mkt`, `:sse`) saiu do produto — a
ferramenta é encontrada pelo nome na busca ou pelo próprio atalho. O pacote também leva o **ping do
destino (NET-001)** da rodada 2.2, que não teve release própria (ver seção 2.2 abaixo).

### Corrigido

- **Consulta SSE pela busca rápida (SQ-004, P0)**: ciclar com ↑↓ a partir de "SN da RB pelo IP" e
  confirmar com `Enter` mandava a Consulta SSE para o caminho de execução de script e devolvia
  `Script da ferramenta não encontrado:` — truncado, porque ela é base local e não tem script.
  Agora ela fica só destacada na lista, o foco volta à busca e o `Enter` abre o painel.
  Verificado com `--hud "SN da RB" --fill 10.0.0.1 --cycle consulta-sse --submit` em 3 execuções:
  `fica na seleção (dataset ou sem campo)` + `ConsultaSse.ShowPanel: 30 resultado(s)`, sem linha de
  execução no histórico.
- **Mensagem duplicada no SN da RB pelo IP (NET-004)**: com IP inválido o analista via o popup do
  próprio script **e** um aviso "Não foi possível"; o detalhe vinha do fim de `%TEMP%\mkt_serial.log`
  sem marca de início, então citava a consulta anterior (`IP 255.255.255.252 sem SN (retorna 0)` para
  um IP nunca digitado — confirmado no histórico real). Agora: uma mensagem só (`Erro de consulta` +
  motivo), IP inválido recusado **antes** de executar e log lido apenas a partir da linha em que a
  execução começou. Verificado nos casos vazio, malformado e válido sem SN, com captura por HWND do
  aviso e histórico com a origem.
- **`0` sem contexto**: `0` é resposta, não erro. O aviso passou a mostrar `<IP> sem SN` (texto vindo
  do script) em vez de um zero solto, e a falha usa o cartão de alerta — antes ia pelo componente de
  sucesso, com ícone verde.
- **"Verificar conexão" em erro de digitação**: o botão passou a aparecer só em falha de rede.

### Adicionados

- **Comportamento do atalho por ferramenta (CFG-003)**: `Aguardar input` (padrão) ou
  `Consultar direto`, gravado em `settings.json` (`ShortcutBehavior`). Em *Consultar direto* a área
  de transferência é lida **no momento do atalho**; vazio/inválido cai para *Aguardar input* com um
  único aviso. Feedback `Consultando <valor>…` antes do resultado e histórico com a origem
  (`atalho/consult`, `atalho/ask`, `hud`, `painel`), mascarando campos declarados `sensitive`.
  Verificado: `ask` abre os campos sem executar; `consult` executou e gravou `origem: "atalho/consult"`.
- **Diagnósticos**: `--cycle <id>` (cicla até a ferramenta pelas mesmas setas) e `--tool-hotkey <id>`
  (dispara o atalho pelo caminho de produção). Os timers de diagnóstico ganharam referência forte: o
  `--submit` de 3800 ms não disparava em parte das execuções por coleta do GC.

### Removidos

- **Gatilho de ferramenta (CFG-002, breaking)**: campo na página da ferramenta, `trigger` nos
  manifestos, `TriggerPolicy`, `TriggerOverrides` em `settings.json` e `--set-trigger`. Configuração
  antiga carrega sem erro — o log registra `migração 2.2.1: N gatilho(s) de ferramenta ignorado(s)` e
  a primeira gravação limpa o arquivo. Verificado com o `settings.json` real (3 gatilhos): reescrito
  sem o campo e **sem nenhuma outra diferença**. `--check-tools` passou a recusar manifesto que ainda
  declare `trigger` (verificado: `ok:false`, exit 1; volta a `ok:true` sem o manifesto).
- **`legacyErrorPopup`** nos dois manifestos: `mkt_serial.ps1` e `mac_vendor.ps1` ganharam `-Mode hub`
  (JSON `{status,value,detail}`) — sem popup próprio e sem escrever erro como se fosse valor.
- **Serial real no `SPEC.md`** trocado por valor mascarado: o repositório é público.

### Limitações desta rodada

- "IP válido **com** SN" não foi executado (não há IP de inventário conhecido nesta máquina); o
  caminho é o mesmo do "sem SN", que foi provado.
- A gravação da preferência pelo clique no radio da tela não foi exercitada (WinUI não dispara
  `Click` sintético); leitura e persistência foram provadas pelo arquivo e pelo comportamento.
- `docs/REGRAS-DE-FERRAMENTA.md` R1/R2 reescritos: a unicidade de código curto deixou de existir.

## 2.2 — integrado no 2.2.1 (não publicado separadamente)

> Esta rodada de trabalho entrou no pacote **2.2.1**; não existe release v2.2.0.

**Resumo.** O campo de IP passa a pingar o destino enquanto o analista digita: uma linha com a latência
ao lado do campo e os dois últimos retornos no aviso de resultado. O ping é feito pela API do Windows —
nenhum processo é criado, então cancelar não deixa `ping.exe` órfão —, espera 400 ms depois da última
tecla, recomeça a cada edição e para sozinho depois de 100 disparos, para não virar tempestade de ICMP.

### Adicionados

- **Ping do destino (NET-001)**: campo de IP com `"ping": true` no manifesto (hoje o `:mkt`) mostra a
  latência ao lado do campo com opacidade 75% e leva os 2 últimos retornos para o aviso de resultado.
  Debounce e timeout configuráveis em Configurações → **Ping do destino**.
  Verificado com `--ping-sim --debounce 400` (debounce medido em ~412 ms, 1 eco cancelado por edição,
  trava em 100/100 com mensagem), `--ping 127.0.0.1 --count 2` (ICMP real, 0 ms) e captura por HWND
  das duas telas (HUD com `0 ms` e aviso com `ping · 0 ms · 0 ms`).
- **`--ping` e `--ping-sim`**: diagnóstico de ICMP real e da política de debounce/cancelamento/limite,
  este último com emissor falso (não depende de rede).
- **`docs/SUBFERRAMENTA-PING.md`** + regra **R14** em `docs/REGRAS-DE-FERRAMENTA.md`; gate
  `--check-tools` passa a recusar `ping` declarado fora de campo de IP.
- **Diagnóstico de tela**: `--hud <id> --fill <valor|a|b> [--submit]` preenche o campo (ou simula
  edições) e executa pelo mesmo caminho do Enter — prova a UI sem teclado sintético.

## 2.1 — 24/11/2026

**Resumo.** Esta versão tira o último resto de dependência do Espanso e prepara o projeto para ser
público. As ferramentas passam a viver dentro do próprio HUB (`tools/`), com um script ao lado do manifesto; a Consulta SSE deixa de carregar dado de rede interna na base versionada e passa a ler esses dados de um arquivo local; endereços de serviço e caminho de cofre saem dos manifestos e vão para a configuração da máquina. Na tela, a busca rápida para de repetir texto, a consulta abre limpa (sigla e estado primeiro) e o atalho global não deixa mais o usuário sem atalho quando o Windows
recusa a combinação escolhida.

### Adicionados

- **Scripts dentro do projeto**: `src/AnalistaHub/tools/*.ps1`, ao lado do manifesto; o campo `script` traz só o nome do arquivo e o app resolve na pasta da ferramenta.
- **Ícone por ferramenta**: campo `icon` aceita arquivo `.ico`/`.png` na pasta da ferramenta, com o glifo como reserva. Vale para barra lateral, título da ferramenta e lista da busca.
- **Configuração local**: `%LOCALAPPDATA%\AnalistaHub\config.local.json`, com `config.example.json` versionado; manifestos usam `${CFG:chave}` para endereço de serviço, url de validação e cofre.
- **Consulta SSE — dados extras** (menu ⚙ no painel **e** bloco na página da ferramenta): registro de NM por cidade, informação de fonte, resumo da base e **resultados por busca** (padrão 30, de 10 a 500).
- **Busca rápida — ocultar contagem** de ferramentas no rodapé (Configurações).
- **Base pública × rede interna**: `data/sse-cidades.json` (5.577 municípios, versionado) +
  `sse-rede.local.json` (161 cidades com NM · OLT · VPN, local e fora do git).
- **Comandos de diagnóstico**: `--try-hotkey <combo>`, `--hud [id]`, `--check-tools` passa a devolver `avisos` (o que falta de configuração local).
- **Regras permanentes** em `docs/REGRAS-DE-FERRAMENTA.md`: R9 (onde mora o script), R10 (ícone), R11 (troca de atalho transacional), R12 (descrição é frase, não painel de status).
- **Gate de publicação** ampliado: script fora da pasta da ferramenta, referência a `espanso`,
  ícone ausente, descrição com metadado e gatilho duplicado **bloqueiam** a publicação.
- **Higiene de pacote**: `publish.ps1` e `package-release.ps1` removem cache de token (JWT), cache de OUI, logs e backups antes de gerar o pacote.
- **`AGENTS.md`**: regra de reler o arquivo do disco antes de editar e preservar rótulos do usuário.

### Corrigido

- **Atalho global não ficava salvo ao abrir nova instância.** Causa real: a troca desregistrava o
  atalho antes de tentar o novo; quando o Windows recusava (outro programa já usa), o app ficava sem atalho nenhum e a mensagem dizia que o anterior continuava valendo. Agora a troca é transacional, o erro do Windows é traduzido, a gravação é atômica (temporário + troca) e o app relê o disco para confirmar.
- **Janela em branco ao voltar pelo ícone**: fechar a janela destruía o host XAML e qualquer janela
  recriada saía em branco. A janela principal não tem mais X (o fechamento real é pelo botão lateral ou pelo menu do ícone) e, se a janela morrer, o app reinicia o processo.
- **Busca rápida repetia a descrição** no rodapé ao abrir os campos e **ecoava o clipboard** na dica
  (`• agora: …`) — origem do campo duplicado que apareceu em `:mkt`, `:macv` e `:sse`.
- **Consulta SSE**: `refine a busca` saiu do rodapé; resumo da base, fonte e "somente cadastro · sem NM" passaram a ficar ocultos por padrão.
- **Catálogo**: arquivos de cache que nascem em `tools/` (`mkt_serial_token.json`, `mac_vendor_cache.json`) apareciam como "ferramenta com problema"; agora são ignorados.
- **Junção da rede interna** era por nome e colidia entre municípios homônimos de UFs diferentes (dava 160 cidades e 184 com NM); passou a usar nome + UF e voltou a 161 / 181.
- **Diagnóstico visual**: capturas por coordenada liam a janela de outro programa; passou a usar
  captura por janela (`PrintWindow`), imune a z-order.

### Removidos

- **Dependência do Espanso**: nenhum caminho `%APPDATA%\espanso` no catálogo. Os originais ficaram guardados em `%APPDATA%\espanso\scripts\_migrados-para-o-hub-20261124\` (recuperáveis), e os gatilhos `:mkt`/`:macv` **do Espanso** deixaram de funcionar por decisão do usuário.
- **Identificadores de infraestrutura** dos arquivos versionados: endereço do inventário, url de
  validação, caminho do cofre, nome da empresa nos rótulos e nomes de NM/OLT/VPN.
- **Caminho do cofre no script**: `mkt_serial.ps1` lê `$env:ANALISTAHUB_COFRE`; o HUB passa usuário e senha por variável de ambiente.
- **`-SkipScripts` no CI**: os scripts agora são versionados, então o gate do runner valida que eles
  existem.

### Como verificar

```powershell
& .\dist\AnalistaHub\AnalistaHub.exe --version                     # 2.1.0
& .\dist\AnalistaHub\AnalistaHub.exe --check-tools                 # ok + avisos vazios
& .\dist\AnalistaHub\AnalistaHub.exe --try-hotkey "Ctrl+Alt+F9"    # diz se a combinação está livre
& .\dist\AnalistaHub\AnalistaHub.exe --test mac-vendor "F4:8E:38"  # Dell Inc.
& .\dist\AnalistaHub\AnalistaHub.exe --sse sao                     # consulta com 30 resultados
```

### Decisões e limites

- O rótulo **`SIGLA ANATEL`** foi mantido (pedido do usuário para não alterar).
- O histórico do git foi reescrito nesta versão para remover os identificadores antigos; clones
  anteriores precisam ser refeitos.
- Endereços internos continuam existindo **apenas** na máquina do analista
  (`config.local.json` e `sse-rede.local.json`) — nem no repositório, nem no pacote publicado.
