# SPEC — Aplicação principal (HUB de Atalhos de Analista)

Versão da spec: 1.0 · Escopo deste documento: **a aplicação em si** (o HUB).
O instalador é assunto de outra sessão; aqui ele só aparece como "o pacote que será publicado".

Fonte de verdade de produto: `IDEA.md`. Este documento traduz o `IDEA.md` em comportamento
observável e verificável. Onde há detalhe que o `IDEA.md` não fixa, está marcado como
**decisão de projeto** (minha, não do dono).

---

## 1. O que é

Um único aplicativo Windows que:

1. **Roda em segundo plano** com um atalho global e um ícone na bandeja.
2. **Abre um HUD de comando** (estilo PowerToys Run): campo de busca → lista de ferramentas →
   executa a escolhida sem sair do teclado.
3. **Tem uma janela principal** estilo PowerToys (lista à esquerda, detalhe à direita) onde cada
   ferramenta mostra: para que serve, como executar, quais configurações/credenciais exige e
   como entrega o resultado.
4. **Agrupa ferramentas que são scripts externos**, descritos por um manifesto. Uma ferramenta
   nova entra copiando um arquivo `.json` — não recompilando o app.

Nome do produto: **Analista Hub** (decisão de projeto). Executável: `AnalistaHub.exe`.

---

## 2. Ferramentas da primeira entrega

| Ferramenta | Entrada | Saída | Depende de |
|---|---|---|---|
| SN de RB pelo IP | IPv4 (ou CIDR `/32`); vazio = clipboard | SN + `1`, ou `0` se o IP não está no inventário | Inventário "Inventário" (HTTP :3113) + credenciais do cofre (seção 5) |
| Fabricante pelo MAC | MAC (qualquer separador; 6 primeiros hex); vazio = clipboard | Nome do fabricante | `api.macvendors.com` (HTTPS) |
| Consulta SSE | cidade (nome, sigla Anatel ou UF) | Sigla Anatel + estado + NM/OLT/VPN quando houver; botão **NMs** oculta as informações de acesso; painel próprio, `Enter` copia | base local `data/sse-cidades.json` — planilhas do SSE + cadastro de municípios da ANATEL (sem rede, sem credencial) |

Campos e comportamento vêm dos scripts já existentes (evidência: `mkt_serial.ps1`,
`mac_vendor.ps1`). Nada neles é reescrito nesta entrega: o HUB os **chama** e apresenta o
resultado. Contrato de chamada na §7.

---

## 3. HUD de comando

### 3.1 Invocação
- Atalho global padrão: **Ctrl+Alt+Espaço** (configurável). Vale com o app minimizado, atrás de
  outras janelas ou com o app fechado na bandeja.
- Alternativas: botão "Abrir comando rápido" na janela principal, ícone da bandeja → "Buscar…".
- Se o HUD já estiver aberto, o atalho fecha (comporta-se como interruptor).

### 3.2 Aparência e posição
- Janela sem moldura, ~640×420 px, cantos arredondados, fundo translúcido escuro (Mica/Acrylic),
  sempre no topo.
- Surge **centrada horizontalmente, a ~15% do topo** do monitor onde está o cursor.
- Foco imediato no campo de busca; o texto anterior é apagado a cada abertura.
- O ícone à esquerda da busca é uma **casa** (2.3): clicar fecha a busca e invoca/foca a janela
  principal. É um botão — alcançável por `Tab`, com tooltip e nome acessível.
- Esc fecha sem executar nada.

### 3.3 Busca e seleção
- Filtro incremental por: nome da ferramenta e palavras da descrição/categoria. Insensível a
  maiúsculas e acentos. (O código curto `:mkt` saiu no 2.2.1 — ver `docs/REGRAS-DE-FERRAMENTA.md` R1.)
- Lista com ícone, nome e descrição curta.
- `↑`/`↓` movem a seleção; `Enter` executa; `Tab` completa o nome na busca.
- Enter com a busca vazia executa a primeira ferramenta da lista (nunca uma ferramenta que
  dependa de entrada sem mostrar os campos primeiro).
- Nenhum resultado: "Nenhuma ferramenta encontrada para «texto»".

### 3.4 Entrada de dados
- Ferramenta que precisa de dado: ao ser selecionada, a HUD **expande** e mostra os campos
  declarados no manifesto (rótulo, dica, validação), abaixo da busca.
- `Enter` no campo executa. `Tab` percorre os campos. Botão **Consultar** faz o mesmo com o mouse
  (a HUD não exige teclado para funcionar).
- Campo que aceita "vazio = área de transferência": exibe a dica `vazio = clipboard` e, quando
  vazio, mostra em cinza o valor que será lido do clipboard (ex.: `10.0.0.1 (clipboard)`).
  Se o clipboard estiver vazio, avisa antes de executar e não chama o script.
- Validação local antes de executar (ex.: IPv4 com 4 octetos ≤ 255; MAC com ≥ 6 dígitos hex).
  Inválido: a HUD não executa e marca o campo em vermelho com o motivo.

### 3.5 Execução e resultado
- Durante a execução: indicador "Consultando…", campos travados, `Esc` cancela.
- Tempo limite por ferramenta (12 s no padrão; o script pode ter o seu).
- Conclusão com sucesso, conforme o **modo de saída** da ferramenta:
  - **Copiar** (padrão): valor vai para a área de transferência e o HUD fecha;
    em seguida aparece o HUD de resultado (§4) por 12 s.
  - **Digitar**: HUD fecha, o valor é digitado no campo que estava em foco antes de abrir a HUD,
    e o mesmo valor fica no clipboard. Aparece o HUD de resultado (sem roubar o foco).
  - **Só avisar**: HUD fecha, aparece o HUD de resultado sem copiar nem digitar.
- **Falha**: o HUD não fecha. Mostra faixa de erro com o motivo real devolvido pelo script e
  dois botões: "Copiar detalhe" e "Tentar de novo".
- **Falha de rede/servidor inacessível**: mensagem fixa
  `Sem conexão com o servidor. Verifique a VPN e tente novamente.` + botão "Verificar conexão"
  (§6.3). O clipboard **não** é alterado — regra herdada do `mkt_serial.ps1`, que se recusa a
  gravar `0` quando a falha é de rede (senão `0` mentiria "não cadastrada").

---

## 4. HUD de resultado (aviso flutuante)

- Janela pequena, sem moldura, sempre no topo, **perto do cursor** (deslocada 18 px para baixo e
  para a direita, presa à área útil do monitor), como o popup dos scripts atuais.
- Conteúdo: título da ferramenta · valor em destaque (monoespaçado, selecionável) ·
  `copiado para a área de transferência` quando aplicável · rodapé com o dado consultado
  (ex.: `IP 10.0.0.1`).
- Fecha por: clique, `Esc`, ou 12 s (tempo configurável). Botão **"Manter aberto"** suspende o
  fechamento automático.
- Nunca aparece no meio da tela nem tapa o campo onde o valor será digitado.

---

## 5. Janela principal

Layout estilo PowerToys: navegação à esquerda, conteúdo à direita.

- **Início**: cartões das ferramentas com nome, o que faz e **uma única bolinha de
  status** que reflete comunicação **e** autenticação (ver 5.2). Botões **Abrir** (HUD já filtrada)
  e **Testar** (executa com o valor atual do clipboard).
- **Bandeja**: o **clique esquerdo** segue a preferência de Configurações → *Ícone da bandeja*:
  **Abrir janela principal** (padrão) ou **Abrir busca rápida**; duplo clique faz o mesmo, e o tooltip
  do ícone diz qual é o modo. O **clique direito** abre um menu com o **nome de cada ferramenta**
  (abre a aba dela), **Buscar ferramenta…**, **Configurações** e **Fechar aplicativo**. O menu é
  montado na hora, então ferramenta nova aparece sem reiniciar.
- **Página da ferramenta** — quatro blocos, espelhando exatamente os quatro itens do `IDEA.md`:
  1. **Para que serve** — descrição em linguagem de operação.
  2. **Como executar** — atalho global, exemplo de uso, duração típica.
  3. **Configurações e credenciais** — três partes na mesma aba:
     - **Comportamento do atalho** (CFG-003, por ferramenta): **Aguardar input** (padrão — abre a
       ferramenta com o campo em foco) ou **Consultar direto** (usa a área de transferência no
       momento do atalho). Valor vazio/inválido em *Consultar direto* cai para *Aguardar input*.
     - **Entrada da consulta** (o que o script recebe), em modo leitura.
     - **Credenciais digitáveis** (usuário/senha), gravadas **automaticamente** ao sair do campo e
       também por um botão "Salvar credenciais". O usuário **não** informa caminho de arquivo.
       Estado visível: "credenciais salvas e cifradas" ou "faltando: Senha". Botão "Apagar" remove.
     - Botão **Testar conexão** e o endereço do serviço. Segredo nunca é exibido em texto.
  4. **Como o resultado é apresentado** — escolha do modo de saída (Digitar / Copiar / Só avisar)
     e o que exatamente é copiado (ex.: `SN + "1"`, `fabricante`).
- **Rodapé** de cada página: última execução (data/hora, sucesso ou erro) do histórico local.
- **Configurações**: atalho global (ver 3.6), iniciar com o Windows, tema (Escuro/Claro/Sistema),
  pasta de ferramentas, tempo do aviso, manter histórico (lig/deslig), abrir pasta de dados, versão.

### 5.1 Atalho global configurável (item 2)

- A caixa mostra o atalho atual e o botão **"Definir atalho"**. A captura só acontece depois de
  armar: enquanto não armada, nenhuma tecla perdida troca o atalho do sistema.
- Armada, a caixa lê o **input do usuário** (modificadores + tecla final) e mostra a combinação
  (`Ctrl+Alt+Space`, `Ctrl+Shift+F10`…). Ao soltar a tecla final, o atalho é **registrado na hora**
  e gravado em `settings.json`.
- Exige ao menos um modificador (Ctrl/Alt/Shift/Win). `Esc` cancela. Perder o foco cancela.
- Se o Windows recusar (combinação já usada por outro programa), a mensagem diz isso e o atalho
  anterior continua valendo.

### 5.2 Bolinha de status — uma só, quatro estados

A mesma bolinha do cartão responde pelas duas perguntas (o serviço responde? a autenticação passa?):

| Cor | Significado |
|---|---|
| cinza | não verificado |
| verde | serviço respondeu e credenciais aceitas (ou a ferramenta não exige) |
| amarelo | serviço respondeu, mas **autenticação recusada** — ou credenciais faltando |
| vermelho | sem conexão com o serviço |

O estado de autenticação vem da última validação feita pelo usuário (ou da última execução que
falhou no login), guardado em `settings.json` — não há uma segunda bolinha para "login".
A regra completa está em `docs/REGRAS-DE-FERRAMENTA.md` (R5).

### 5.3 Fechar a janela (X) e reabrir

Nesta versão do WinAppSDK, fechar a janela principal no **X** a **destrói** e o evento
`AppWindow.Closing` não é acionável/cancelável (testado também com `SetWindowSubclass` no
`WM_CLOSE`: o WinUI troca o procedimento da janela depois e o subclass é ignorado).

Comportamento assumido, então:

1. O **X** fecha a janela normalmente; o processo continua vivo pela janela oculta do ícone
   (é um app de bandeja), sem janela nenhuma na tela.
2. Qualquer ação de **abrir** — item do menu do ícone, **Configurações**, atalho global, ou abrir o
   executável de novo — passa por `App.ShowMainWindow`, que **recria a janela** quando o HWND não
   existe mais, re-registra o atalho global na janela nova e volta para a **última página** usada.
3. **Sair de verdade** é só pelo menu do ícone → *Fechar aplicativo* (aí sim encerra o processo).

Regressão que isso corrige: antes, depois do X, `ShowMainWindow` estourava
`NullReferenceException` e o app ficava inacessível, vivo só na bandeja.

### 5.4 Janela em branco (corrigido) e por que não existe X

Sintoma relatado: ao abrir a janela pelo menu do ícone, ela vinha **toda branca**, e só carregava
quando era arrastada para o outro monitor.

Medido com o build anterior (`build/test-janela-branca.py`, cores distintas na área de conteúdo):

```
ciclo 1: janela inicial cores=1184 | reaberta cores=1   -> EM BRANCO
ciclo 2: janela inicial cores=1    | reaberta cores=1   -> EM BRANCO
ciclo 3: janela inicial cores=1    | reaberta cores=1   -> EM BRANCO
```

Causa real (não era DPI): no WinUI 3, quando a **última janela do app é fechada**, o host XAML é
desmontado. Como o processo continua vivo pela bandeja, qualquer janela criada depois disso **não
renderiza** — fica branca. Reproduzido nos dois monitores, com e sem escala diferente.

Correções aplicadas:

1. A janela principal **não pode ser fechada pelo usuário**: o estilo `WS_SYSMENU` é removido no
   primeiro `Activated`. Isso tira o **X** do título e desliga o **Alt+F4**. Fechar é pelo menu do
   ícone → *Fechar aplicativo*, ou pelo par de botões **Minimizar / Fechar** no rodapé da barra
   lateral.
2. Janela "guardiã" invisível (8×8 fora da tela, fora do Alt+Tab) mantém o host XAML vivo, para o
   caso de a janela principal ainda ser destruída por algo fora do controle do app.
3. `ShowMainWindow` recria a janela se o HWND morreu (rede de segurança) e força repaint
   (`NudgeRender`: empurrão de 1 px + `UpdateLayout`) depois de mostrar.

Prova depois da correção (mesmo teste):

```
1. aberta:            cores = 2847
2. depois do Alt+F4:  janela existe = True | cores = 2847
3. abrir de novo:     mesma janela = True | cores = 2847
```

Isto é: o Alt+F4 não fecha mais, abrir pelo ícone reaproveita a janela existente e o conteúdo está
renderizado. Ver `docs/REGRAS-DE-FERRAMENTA.md` (R7).

### 5.5 Atalho global: troca transacional e persistência verificada

Trocar o atalho nunca deixa o usuário sem busca rápida:

- Se o Windows recusar a combinação (outro programa já usa), o atalho anterior **volta a valer na
  mesma chamada** e o valor salvo **não muda** — a alteração não salva é o comportamento correto, e a
  tela informa o motivo (`Outro programa já usa Ctrl+Alt+F7. O atalho Ctrl+Alt+Space continua valendo.`).
- Quando o registro dá certo, o app grava `settings.json` de forma **atômica** (temporário + troca) e
  **confere o arquivo**; se a gravação não pegou, avisa com o caminho em vez de perder a preferência em
  silêncio no próximo início.
- `AnalistaHub.exe --try-hotkey "<combo>"` responde sem abrir janela: combinação salva, se está
  registrada, se a tentada foi liberada e qual continua ativa.

Evidência: com `Ctrl+Alt+F7` ocupado por outro processo, `--try-hotkey` devolveu
`tentado_registrado=false`, `erro_tentado="Outro programa já usa… continua valendo"` e
`ainda_ativo="Ctrl+Alt+Space"`; com `Ctrl+Shift+F9` (livre) devolveu `ok=true`. O `settings.json`
permaneceu válido, sem `.tmp`, com o atalho anterior.

### 5.6 HUD: a descrição aparece uma vez só

- Ao abrir os campos de uma ferramenta, o **rodapé não repete** a descrição que já está na linha do
  resultado (era texto duplicado na mesma tela).
- A **dica do campo** mostra o que o campo espera (ex.: `aceita CIDR /32`) e a instrução
  `vazio usa a área de transferência`. Ela **não ecoa o conteúdo do clipboard** — era daí que vinha o
  campo `• agora: …`, que ainda por cima duplicava quando o clipboard já trazia metadado da Consulta.
- `description` do manifesto é uma frase, sem contagem/data/metadado; o gate recusa fora da regra
  (`docs/REGRAS-DE-FERRAMENTA.md`, R12).
- A **contagem de ferramentas** no rodapé (`3 ferramenta(s).`) pode ser ocultada em
  Configurações → **Busca rápida → Ocultar contagem de ferramentas** (`HideToolCount`). Quando a busca
  não encontra nada, o aviso continua aparecendo — ali o número é o próprio recado.

### 5.7 Janela principal de tamanho fixo (UI-004, 2.3)

A janela principal **não é redimensionável**: 1120×760 DIP, centrada na área útil do monitor e
limitada a 94 % dela (em 1366×768 resulta 1120×684 px). Não há borda de redimensionamento
(`WS_THICKFRAME`), botão maximizar (`WS_MAXIMIZEBOX`) nem `X` (R7).

Motivo: eliminar o reflow/repaint a cada arrasto — e o defeito medido no 2.2.1, em que a área nova
saía **branco puro** (6 %→76 % da janela) enquanto o conteúdo ficava travado em ~1112×676.

- 1120×760 DIP — em 125 % vira 1400×950 px; o tamanho acompanha a escala do monitor.
- O retângulo **real** (pixels) é guardado 120 ms depois da primeira ativação, quando o DPI assentou.
  Um **vigia de 300 ms** compara `GetWindowRect` com ele e restaura — é o único caminho que pega
  maximizar por `WM_SYSCOMMAND` (Win+↑, duplo clique no título), que não emite `AppWindow.Changed` nem
  atualiza o presenter. Subclass não serve: o WinUI troca o WndProc (mesma razão do WM_CLOSE, §5.3).
- Ao mover a janela para um monitor com escala diferente, o vigia **adota** o novo retângulo (o
  Windows já o ajustou) em vez de brigar com ele.
- Não se fixa o `RootGrid`: forçá-lo ao tamanho do cliente deixava o conteúdo menor que a janela em
  125 % (a ilha rasteriza em outra escala). Deixar o layout normal + `NudgeRender` preenche certo.
- Evidência: `evidence/ui-004-fixo-0453aed8-20261125_1435` (100 %: 0,28 % de branco puro) e
  `evidence/ui-004-fixo-dpi/` (125 %: 0,31 % de branco, SC_MAXIMIZE revertido em ≤ 300 ms).

---

## 6. Serviços do HUB

### 6.1 Catálogo de ferramentas
- Pastas, nesta ordem de precedência: `%LOCALAPPDATA%\AnalistaHub\tools\` (do usuário) e
  `tools\` ao lado do executável (embutidas). Mesmo `id` na pasta do usuário sobrescreve a
  embutida.
- Manifesto inválido não derruba o app: a ferramenta aparece na lista como
  "Ferramenta com problema" com o motivo e sem botão de executar.
- Recarrega ao salvar qualquer `.json` na pasta (sem reiniciar).

### 6.2 Área de transferência e digitação
- Copiar usa o clipboard do Windows (texto). Digitar usa injeção de teclado (Unicode) depois de
  esconder a HUD; se a janela de destino não aceitar texto, o valor permanece no clipboard e o
  aviso diz "não foi possível digitar — o valor está na área de transferência".

### 6.3 Verificação de conexão
- Por ferramenta: teste TCP ao host/porta declarados, tempo limite 3 s.
- Resultado em três estados: acessível · sem resposta · não verificado (com hora do último teste).
- Nunca tenta conectar VPN nem altera configuração de rede — só mede e informa.

### 6.4 Execução dos scripts
- Processo oculto, sem janela de console, com tempo limite e cancelamento.
- Saída padrão é a resposta; erro padrão e código de saída alimentam o detalhe do erro.
- Histórico local (JSONL, sem segredos): hora, ferramenta, entrada mascarada quando sensível,
  duração, sucesso/erro, mensagem curta.

### 6.5 Credenciais (item 3)

- O usuário digita usuário e senha **na aba da ferramenta**. Não existe caminho de arquivo para
  preencher à mão.
- Gravação **automática** ao sair do campo, e um botão **"Salvar credenciais"** para gravar na hora
  com confirmação na tela. Botão **"Apagar"** remove da máquina.
- Armazenamento: `%LOCALAPPDATA%\AnalistaHub\credenciais.dat`, cifrado com **DPAPI** (escopo do
  usuário Windows). Copiar o arquivo para outra máquina/usuário não revela nada.
- Entrega ao script: **variável de ambiente** do processo filho (`ANALISTAHUB_USER`,
  `ANALISTAHUB_PASS`) — nunca por linha de comando (que aparece em lista de processos), nunca em
  log, histórico ou manifesto.
- Compatibilidade: se o usuário não preencher, o script antigo continua lendo o cofre markdown que
  ele já conhece. Nada quebra para quem usa o Espanso.

### 6.6 Validação de credenciais ao digitar (item 3)

Só existe quando o manifesto declara `credentials.validation` (url, método, corpo com `{user}` e
`{passwd}`, caminho do campo de sucesso na resposta JSON e caminho da mensagem).

| Regra | Comportamento |
|---|---|
| 3.1 Gatilho | a validação **só** roda quando o usuário interage com os campos — nunca ao abrir a tela, nunca em segundo plano |
| 3.2 Espera | 320 ms depois da última tecla, então **uma** requisição ao serviço declarado |
| 3.3 Feedback | travessão ao lado do rótulo: `Usuário — ok`, `Senha — recusado`, `— sem resposta`, `—` (não verificado) |
| 3.4 Visual | contorno do campo: **verde** quando aceito, **amarelo** quando recusado, neutro quando não há resposta (não julga a credencial) |
| 3.5 Espera visual | **um** indicador circular por ferramenta, usado também pelo "Testar conexão" |
| Resultado | guardado por ferramenta em `settings.json` (estado, hora, mensagem do servidor) e reaproveitado na bolinha do Início |

O corpo da requisição contém a senha: nada é logado, e a resposta é descartada depois de ler apenas
o campo de sucesso/mensagem. Falha de autenticação detectada numa execução real também atualiza o
estado — a bolinha fica amarela sem precisar de nova validação.

### 6.7 Ping do destino (NET-001)

Campo de IP que declara `"ping": true` no manifesto (hoje só o `:mkt`) pinga o destino enquanto o
analista digita. Regra permanente: **R14** em `docs/REGRAS-DE-FERRAMENTA.md`; comportamento detalhado
em `docs/SUBFERRAMENTA-PING.md`.

| Etapa | Comportamento |
|---|---|
| Gatilho | o texto vira um IPv4 completo e válido (CIDR é aceito e descartado) |
| Espera | `PingDebounceMs` (padrão 400 ms) depois da última tecla |
| Edição | qualquer alteração — em qualquer octeto — cancela o eco em curso e reagenda |
| Pacote | 1 eco ICMP, timeout `PingTimeoutMs` (padrão 1000 ms) |
| Exibição | 1 linha ao lado do campo com **opacidade 75%**: `1 ms` / `sem resposta` / `destino inalcançável` |
| Resultado | o aviso da ferramenta mostra os 2 últimos retornos: `ping · 3 ms · 4 ms` |
| Trava | 100 disparos por invocação; ao estourar, para e diz no rodapé como reiniciar |
| Processo | nenhum: ICMP pela API do Windows, então cancelar não deixa `ping.exe` órfão |
| IPv6 | fora do escopo desta versão — o campo mostra «IPv6 ainda fora do escopo do ping do HUB» |

Preferências em Configurações → **Ping do destino** (espera e timeout). Diagnóstico:
`--ping <ip> [--count N]` (ICMP real) e `--ping-sim [--debounce ms]` (debounce, cancelamento e trava,
com emissor falso — não depende de rede).

### 6.8 Configuração local pela UI (2.3)

O que antes só existia em `config.local.json` passa a ter editor na página da ferramenta — sem tirar
a possibilidade de editar o arquivo à mão.

| Item | Onde | Regra |
|---|---|---|
| **Host do serviço** (MKT-002) | página da ferramenta, campo "Servidor do inventário" | só aparece quando o manifesto declara host como `${CFG:<chave>}` (o catálogo guarda a chave em `NetworkRequirement.HostConfigKey`); grava a chave **e** `<id>.url` na mesma ação; aceita IPv4, hostname ou `host:porta`; substituir valor existente exige um segundo clique em **Salvar** |
| **Testar conexão** (MKT-002) | ao lado do campo | TCP de 3 s em host:porta; **não** autentica nem consulta inventário |
| **Lista de NMs** (SSE-002) | página da Consulta SSE, botão **Importar sse-rede.local.json…** | valida o JSON (`ValidarRede`) antes de copiar; confirma a substituição; guarda a anterior como `.bak`; `Reload()` e re-render. O mesmo caminho existe sem UI: `--validate-rede` / `--import-rede [--force]` |

A ausência da lista continua visível: `RedeResumo`/`RedeAviso` na página da ferramenta e em
**Configurações → Dados e ferramentas**.

---

## 7. Contrato com os scripts (para as próximas ferramentas)

**Recomendado (novo):** `-Mode hub` → o script escreve **um** objeto JSON em stdout:

```json
{ "status": "ok", "value": "ABC1231", "detail": "", "meta": { "ip": "10.0.0.1" } }
```

`status: "error"` obriga `detail` com a mensagem para o operador. Exit code 0 em ambos os casos;
qualquer exit ≠ 0 é tratado como falha técnica.

**Legado (aceito):** `-Mode type` → stdout é o valor; stdout vazio ou exit ≠ 0 = falha, e o
detalhe é lido do arquivo de log declarado no manifesto (`mkt_serial.log`, `mac_vendor.log`).

O contrato legado tem duas pegadinhas reais, ambas visíveis nos scripts atuais e tratadas pelo HUB:

1. `mac_vendor.ps1` no modo legado escrevia a **mensagem de erro no stdout** e saía com código 0.
   O manifesto declara `stdoutErrorPatterns` para reconhecer isso — histórico: desde o 2.2.1 os
   dois scripts rodam em `-Mode hub` (JSON `{status,value,detail}`), sem popup próprio.
2. `-Mode hub` é o contrato atual: `status: ok` = valor válido (`0` é resposta, não erro),
   `status: error` = falha com o motivo em `detail`. `legacyErrorPopup` foi removido dos manifestos.

Manifesto declara: id, nome, descrição, categoria, ícone, script + interpretador,
campos de entrada, modos de saída suportados, requisito de credencial (arquivo + seção), host/porta
para teste de conexão, tempo limite, log de diagnóstico e os padrões de erro do contrato legado.
**Não declara gatilho** (removido no 2.2.1).

---

## 7.1 Modo linha de comando (sem janela)

Serve para o instalador e para diagnóstico. Sempre imprime JSON e sai com 0/1:

```
AnalistaHub.exe --list-tools
AnalistaHub.exe --test mkt-serial 10.0.0.1
AnalistaHub.exe --probe mkt-serial
AnalistaHub.exe --page mkt-serial      (abre a janela já na página da ferramenta)
AnalistaHub.exe --preview-toast        (mostra um aviso de exemplo, para teste visual)
AnalistaHub.exe --search-sse ITA       (ranking da Consulta SSE; --limit N)
AnalistaHub.exe --validate-rede <json> (valida uma lista de NMs sem gravar)
AnalistaHub.exe --import-rede <json> [--force]   (copia para %LOCALAPPDATA%\AnalistaHub)
AnalistaHub.exe --version | --help
```

`--probe` devolve `servico_responde` (TCP) e `credenciais` (arquivo presente / completo / chaves
faltando) **sem expor nenhum segredo**.

Comandos de atualização (mesmo contrato, JSON na saída):

```
AnalistaHub.exe --check-update      consulta o canal e diz se há versão nova
AnalistaHub.exe --download-update   baixa, confere o SHA-256 e prepara (não aplica)
AnalistaHub.exe --apply-update      troca a pasta do programa e reabre o app
```

---

## 7.2 Atualização automática

### 7.2.1 Canal

O app lê um **manifesto publicado junto da release** do GitHub:

```
https://github.com/<owner>/<repo>/releases/latest/download/latest.json
```

Ponto importante: não é a API do GitHub (`api.github.com`). O endereço
`releases/latest/download/<arquivo>` é um link direto para o arquivo da release mais recente — não
consome o limite de 60 requisições/hora por IP e não depende de token. Pode ser trocado pela
variável `ANALISTAHUB_UPDATE_FEED` (testes) ou pelo campo `UpdateFeedUrl` em `settings.json`.

**O canal também pode ser uma pasta** — `\\servidor\share\AnalistaHub\latest.json`, `D:\...` ou
`file://...`. Nesse caso o app lê o manifesto do disco e copia o zip de lá; o `url` do asset pode ser
relativo (`"./AnalistaHub-0.2.1-win-x64.zip"`, resolvido na pasta do manifesto). É o caminho que
mantém tudo privado: o código e os endereços internos dos manifestos não vão para a internet.

```json
{
  "schema": 1,
  "version": "0.2.1",
  "publishedAt": "2026-11-24T12:00:00Z",
  "notes": "texto curto mostrado na tela",
  "assets": {
    "win-x64": { "url": "https://.../AnalistaHub-0.2.1-win-x64.zip",
                 "sha256": "df47…", "size": 85472414 }
  }
}
```

Comparação de versão é numérica (`0.2.10 > 0.2.9`); tag com `v` e sufixo (`-beta`) é normalizada.
**Só há atualização com pacote e SHA-256 no manifesto.** Manifesto sem hash é recusado (não existe
"baixar sem conferir").

### 7.2.2 Cadência

- Uma verificação automática por dia, no máximo: `settings.json.LastUpdateCheckUtc` guarda a última;
  reabrir o app no mesmo dia **não** verifica de novo.
- Se houver versão nova, um aviso flutuante aparece **uma vez por versão** (`NotifiedUpdateVersion`).
- Sem novidade, sem 404 e sem erro de rede: silêncio total (só linha no `hub.log`).
- Desligável no toggle "Procurar automaticamente (1x por dia)" em Configurações → Atualizações.

### 7.2.3 "Procurar por atualização agora"

Existe em dois lugares, com o mesmo efeito: o bloco **Atualizações** em Configurações e o item
**Procurar atualização agora** no menu do ícone da bandeja (que abre Configurações já verificando).

Estados do bloco:

| Estado | Texto | Botões |
|---|---|---|
| nunca verificado na sessão | "Ainda não verificado nesta sessão." | Procurar por atualização agora |
| na última versão | "Você está na versão mais recente (v0.2.0). (verificado em …)" | Procurar… |
| atualização disponível | "Atualização disponível: v0.2.1 (instalada: v0.2.0)." | Procurar… · **Baixar e instalar** |
| baixando | "Baixando a versão 0.2.1… 42 %" + barra de progresso | — |
| baixado | "Pacote v0.2.1 baixado. Reinicie para concluir." | **Reiniciar e atualizar** |
| canal inacessível | motivo em texto (DNS, proxy, 401/403/404) | Procurar… |

### 7.2.4 Aplicação (troca da pasta)

O pacote é um `.zip` com a pasta portátil na raiz. Fluxo:

1. baixa para `%LOCALAPPDATA%\AnalistaHub\update\<versao>\package.zip`;
2. confere o SHA-256 do manifesto — divergência → download descartado, nada é aplicado;
3. extrai em `…\<versao>\payload` e confere que existe `AnalistaHub.exe`;
4. grava `apply-update.cmd` e reabre o app por ele; o app atual se encerra.

O script espera o processo sair, renomeia a pasta atual para `AnalistaHub.old`, move o payload para
o lugar, inicia o app novo e **confirma que ele subiu** (até 60 s). Se não subiu, apaga a pasta nova,
devolve a antiga, reabre e apaga o pacote. Se outro processo estiver com a pasta aberta (renomear
falha), ele espelha com `robocopy` em vez de renomear.

Regras que vieram de falha real:

- **todo comando externo do script usa caminho absoluto** (`%SystemRoot%\System32\...`). Com
  `find`/`tasklist` vindos do `PATH`, um `find` de Git/MSYS fazia a sonda falhar sempre e o rollback
  apagava uma instalação boa;
- o rollback **não insiste** em apagar pasta com arquivo travado: para, deixa a cópia antiga em
  `…old` e registra no log;
- em pasta de build (`…\bin\Debug`), ou sem permissão de escrita, `--apply-update` recusa em vez de
  tentar.

### 7.2.5 Publicação (CI)

`.github/workflows/release.yml`, disparado por tag `v*`:

1. `build/package-release.ps1 -Version <x.y.z>` publica, comprime, calcula o SHA-256 e escreve
   `latest.json` e `notas.md` em `dist/release`;
2. o job **reconfere** hash e tamanho do zip contra o manifesto (mesma checagem que o app faz);
3. `gh release create v<x.y.z> --latest` sobe `zip`, `zip.sha256` e `latest.json`.

Publicar: `git tag v0.2.1 && git push origin v0.2.1`.

**Pré-requisito de visibilidade:** o manifesto e o zip precisam ser legíveis sem autenticação. Em
repositório **privado** o `GET` responde 404/401 e o app mostra "canal respondeu 404 — se o
repositório for privado…". Token embutido em aplicativo distribuído não é opção (qualquer um extrai).
As saídas: repositório público, ou repositório público só de releases alimentado pelo CI.

---

## 8. Requisitos não funcionais

- **Sem administrador.** Instala e roda a partir da pasta do usuário; sem serviço, sem chave de
  registro em HKLM, sem driver.
- **Uma instância só**: abrir de novo traz a janela existente para frente.
- **Sem rede além dos serviços das ferramentas e do canal de atualização.** Nenhuma telemetria: a
  única chamada de rede que não é ferramenta é o `GET` do manifesto de atualização, uma vez por dia.
- **Sem segredos** em log, histórico, captura ou arquivo de configuração do HUB; credenciais
  continuam no cofre existente.
- Partida < 3 s; HUD visível em < 200 ms quando o app já está em segundo plano.
- Funciona em Windows 10 1809+ e Windows 11, com DPI por monitor (150 %/200 % sem corte).

---

## 9. Casos de borda (todos com comportamento definido)

| Situação | Comportamento |
|---|---|
| Clipboard vazio e campo em branco | Avisa "clipboard vazio — cole o IP e tente de novo"; não executa |
| IP malformado / octeto > 255 | Campo em vermelho, não executa |
| MAC com menos de 6 hex ou MAC localmente administrado | Erro explicado pelo script, exibido no HUD |
| Credencial ausente no cofre | Erro "Credenciais do inventário não encontradas" + botão "Abrir cofre" |
| Servidor fora / VPN caída | Mensagem de VPN (§3.5); clipboard intacto |
| Timeout do script | "Tempo esgotado (12 s)" + "Tentar de novo" |
| Script apagado/movido | "Script da ferramenta não encontrado: <caminho>" |
| Ferramenta devolve `0` (IP não cadastrado) | Sucesso legítimo; o aviso diz "IP não cadastrado no inventário" |
| Manifesto com campo inválido | Ferramenta listada como "com problema" + motivo |
| Duas instâncias do app | A segunda entrega o foco à primeira e encerra |
| Execução concorrente | Uma por vez por ferramenta; segunda tentativa espera ou avisa "já em execução" |

---

## 10. Não-objetivos desta entrega

- Instalador, assinatura de código, atualização automática (sessão separada).
- Escrita/ações que alterem equipamentos ou sistemas (só consulta).
- Sincronização de configurações entre pessoas da equipe (o `IDEA.md` prevê; fica para depois).
- Substituir o Espanso: os códigos curtos (`:mkt`, `:macv`) **deixaram de existir no 2.2.1** — a
  invocação passou a ser pelo nome na busca ou pelo atalho da ferramenta.

---

## 13. Gate de catálogo (torna as regras permanentes)

`build/publish.ps1` roda `AnalistaHub.exe --check-tools` no pacote gerado e **aborta** a publicação
se alguma ferramenta violar as regras de `docs/REGRAS-DE-FERRAMENTA.md`:

- manifesto inválido ou script inexistente;
- `trigger` presente no manifesto (conceito removido no 2.2.1);
- credenciais declaradas sem campos.

Rodar a qualquer momento: `build\check-tools.ps1` ou `AnalistaHub.exe --check-tools`
(exit 0 = ok, exit 1 = problema, com a lista em JSON).

---

## 14. Configuração local e separação de dados (repo público)

O repositório pode ser público porque **não carrega identificador de infraestrutura**:

| O que | Onde vive | Versionado |
|---|---|---|
| Endereço do serviço, url de validação, caminho de cofre | `%LOCALAPPDATA%\AnalistaHub\config.local.json` | não (`*.local.json`) |
| Modelo das chaves, com `CHANGEME` | `config.example.json` | sim |
| Redes internas da Consulta (NM/OLT/VPN) | `%LOCALAPPDATA%\AnalistaHub\sse-rede.local.json` | não |
| Base pública (município · UF · sigla ANATEL) | `src/AnalistaHub/data/sse-cidades.json` | sim |
| Scripts das ferramentas | `src/AnalistaHub/tools/*.ps1` | sim (sem credencial e sem caminho de cofre) |

O manifesto referencia `${CFG:chave}`; `ToolCatalog.AplicarConfigLocal` resolve uma vez no carregamento.
Chave ausente entra em `LocalConfig.Faltando` e aparece em `--check-tools` no campo **avisos** —
nunca em `problemas`, porque catálogo válido e instalação completa são coisas diferentes.

Junção da rede interna é por **nome sem acento + UF**: só o nome colide (há municípios homônimos em
estados diferentes), e a primeira versão desta separação perdeu uma cidade e inflou outras até esse
ajuste.

---

## 15. Evidência usada

| Afirmação | Evidência |
|---|---|
| Campos e modos do SN por IP | `src/AnalistaHub/tools/mkt_serial.ps1` (veio de `%APPDATA%\espanso\scripts` em 24/11/2026) |
| Códigos curtos `:mkt`, `:macv`, `:macc`, `:mact` | histórico: `espanso\match\mkt_serial.yml`, `espanso\match\mac_vendor.yml` — o HUB não usa Espanso e, desde o 2.2.1, não usa código curto |
| Campos e cache do MAC vendor | `src/AnalistaHub/tools/mac_vendor.ps1` (veio de `%APPDATA%\espanso\scripts` em 24/11/2026) |
| Cofre de credenciais, seção 5 | `mkt_serial.ps1` linhas 83 e 112-129; `Documents\Processos\Analyser\credenciais\cofre.md` |
| Os quatro itens por ferramenta e o atalho central | `IDEA.md` §Experiência de uso, §Princípios, §Escopo |
| Popup perto do cursor, 12 s, clique fecha | `mkt_serial.ps1` linhas 58-76 |
| Recusa de gravar `0` em falha de rede | `mkt_serial.ps1` linhas 10-14 e 273-277 |
| `mac_vendor.ps1` escreve erro no stdout com exit 0 | `mac_vendor.ps1` linhas 138-155 (bloco `Finish`, modo `type`) |

---

## 12. Estado da verificação (o que foi realmente executado)
Ambiente: Windows 10 corporativo, sem administrador. SDK .NET 8.0.428 instalado no perfil
(`%USERPROFILE%\.dotnet`), Windows App SDK 1.8.260921001.

| Verificação | Comando | Resultado |
|---|---|---|
| Build limpo | `dotnet build -c Release` | êxito, 0 erros |
| Pacote publicado executa | `dist\AnalistaHub\AnalistaHub.exe` | janela "Analista Hub" abre e permanece |
| Catálogo carrega | `AnalistaHub.exe --list-tools` | 2 ferramentas, `estado: ok`, `script_existe: true` |
| Serviço + cofre | `AnalistaHub.exe --probe mkt-serial` | `servico_responde: true`, `credenciais.completo: true` |
| Consulta real (MikroTik) | `AnalistaHub.exe --test mkt-serial 10.0.0.1` | `ok: true`, valor `E32…EE71` |
| Consulta real (MAC) | `AnalistaHub.exe --test mac-vendor F4:8E:38` | `ok: true`, valor `Dell Inc.` |
| MAC localmente administrado | `--test mac-vendor 02:11:22` | `ok: false` com explicação (não vira valor) |
| MAC inválido | `--test mac-vendor zz` | `ok: false` com explicação |
| Atalho global abre a HUD | `keybd_event Ctrl+Alt+Space` | HUD aparece em 0,2 s, com foco no campo de busca |
| Captura de atalho (item 2) | `--arm-hotkey` + teclado real `^%{F9}` | log `key=F9 mods=[Ctrl+Alt] combo=Ctrl+Alt+F9`; `settings.json` grava `Ctrl+Alt+F9` |
| Captura recusa tecla pura | tecla sem modificador | mensagem "use Ctrl, Alt, Shift ou Win" e atalho anterior mantido |
| Credenciais cifradas (item 3) | `--set-cred` (valor via STDIN) + `--probe` | `completo: true`; `credenciais.dat` gravado com DPAPI |
| Senha errada no HUB | `--set-cred passwd` com valor inválido + `--test` | falha de login com mensagem "Usuário ou senha recusados pelo inventário" |
| Senha correta no HUB | `--set-cred` com valor do cofre + `--test` | `ok: true`, valor `E32…EE71` |
| Cofre antigo continua válido | script chamado direto, sem env | responde `E32…EE71` (Espanso não quebra) |
| Comportamento do atalho (CFG-003) | `ShortcutBehavior` = `consult` + `--tool-hotkey mkt-serial` | histórico com `origem: "atalho/consult"`; sem a preferência, abre os campos (`comportamento=ask`) |
| Clique direito na bandeja (item 1) | `PostMessage(WM_TRAYICON, WM_RBUTTONUP)` | menu com 7 itens (2 ferramentas, Buscar, Configurações, Fechar); `tray: menu aberto (7 itens), escolhido=0` ao cancelar |
| Gate de catálogo | manifesto com `"trigger"` + `--check-tools` | `ok:false`, exit 1, motivo "manifesto ainda declara \"trigger\" …"; volta a `ok:true` sem o manifesto |
| Validação de credencial (aceita) | `--validate-cred mkt-serial` | `estado: ok`, `mensagem: auth_success` |
| Validação de credencial (recusada) | senha errada + `--validate-cred` | `estado: refused`, `mensagem: auth_failed`, exit 1 |
| Feedback na tela (aceita) | `--page mkt-serial --validate-once` | rótulos `Usuário — ok` / `Senha — ok` e contorno verde |
| Feedback na tela (recusada) | senha errada + `--validate-once` | rótulos `— recusado`, contorno amarelo, `serviço: auth_failed` |
| Bolinha única (item 4) | Início com estado `refused` persistido | bolinha **amarela** em "SN da RB pelo IP" e **verde** em "Fabricante pelo MAC" |
| Janela cabe na tela | abertura em monitor 1451×864 físico | janela enquadrada em 94 % da área útil (antes estourava e cortava a direita) |
| Busca por nome + campos | digitar `fabricante` + `Enter` | filtra para 1 ferramenta e abre o campo "MAC do equipamento" |
| Aviso de resultado | `--preview-toast` | janela perto do cursor, valor copiável, fecha em 12 s |
| Página da ferramenta | `--page mkt-serial` | os quatro blocos renderizam |

**Não testado nesta sessão (e por quê):**
- Digitação real do valor no campo em foco (modo "Digitar") — depende de uma janela de destino
  real; o caminho usa `SendInput` e só se comprova com o operador usando.
- Bandeja (clique esquerdo/direito) — o ícone aparece, a interação com o mouse não foi exercitada.
- Toggle "Iniciar com o Windows" — cria atalho na pasta Inicializar; validar na instalação.
- DPI 150 %/200 % em monitor secundário — só medido indiretamente (a HUD se posicionou
  corretamente em monitor com escala diferente da primária).
- Falha de VPN real — a mensagem foi exercitada por código, não com a VPN caída.
