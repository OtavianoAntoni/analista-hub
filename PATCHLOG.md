# Patch log — Analista Hub

> Formato: o resumo conta o que mudou para quem usa; o detalhamento fica separado por
> **Adicionados**, **Corrigido** e **Removidos**. Cada item diz como foi verificado.

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
