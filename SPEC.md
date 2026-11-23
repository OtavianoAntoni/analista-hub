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
| SN de RB pelo IP (gatilho `:mkt`) | IPv4 (ou CIDR `/32`); vazio = clipboard | SN + `1`, ou `0` se o IP não está no inventário | Inventário "Inventário" (HTTP :3113) + credenciais do cofre (seção 5) |
| Fabricante pelo MAC (gatilho `:macv`/`:macc`/`:mact`) | MAC (qualquer separador; 6 primeiros hex); vazio = clipboard | Nome do fabricante | `api.macvendors.com` (HTTPS) |
| Consulta SSE (gatilho `:sse`) | cidade (nome, sigla Anatel ou UF) | Sigla Anatel + estado + NM/OLT/VPN quando houver; botão **NMs** oculta as informações de acesso; painel próprio, `Enter` copia | base local `data/sse-cidades.json` — planilhas do SSE + cadastro de municípios da ANATEL (sem rede, sem credencial) |

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
- Esc fecha sem executar nada.

### 3.3 Busca e seleção
- Filtro incremental por: nome da ferramenta, gatilho (aceita `:mkt`, `mkt`, `:mk`) e palavras
  da descrição. Insensível a maiúsculas e acentos.
- Lista com ícone, nome, descrição curta e gatilho à direita.
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

- **Início**: cartões das ferramentas com nome, o que faz, gatilho e **uma única bolinha de
  status** que reflete comunicação **e** autenticação (ver 5.2). Botões **Abrir** (HUD já filtrada)
  e **Testar** (executa com o valor atual do clipboard).
- **Bandeja**: clique esquerdo abre o HUD; clique direito abre um menu com o **nome de cada
  ferramenta** (abre a aba dela), **Buscar ferramenta…**, **Configurações** e **Fechar aplicativo**.
  O menu é montado na hora, então ferramenta nova aparece sem reiniciar.
- **Página da ferramenta** — quatro blocos, espelhando exatamente os quatro itens do `IDEA.md`:
  1. **Para que serve** — descrição em linguagem de operação.
  2. **Como executar** — gatilho, atalho global, exemplo de uso, duração típica.
  3. **Configurações e credenciais** — três partes na mesma aba:
     - **Gatilho da ferramenta**, editável (ex.: `:mkt` → `:sn`), salvo ao sair do campo ou pelo
       botão "Salvar"; passa a valer na busca do HUD e nos cartões.
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

1. `mac_vendor.ps1` escreve a **mensagem de erro no stdout** e sai com código 0 no `-Mode type`.
   O manifesto declara `stdoutErrorPatterns`; o que casa com um padrão é falha, não valor.
2. Os dois scripts abrem o **próprio popup** quando falham (`legacyErrorPopup`). Enquanto não
   migrarem para `-Mode hub`, a falha aparece duas vezes: no HUD e no popup do script.

Manifesto declara: id, nome, descrição, categoria, gatilho, ícone, script + interpretador,
campos de entrada, modos de saída suportados, requisito de credencial (arquivo + seção), host/porta
para teste de conexão, tempo limite, log de diagnóstico e os padrões de erro do contrato legado.

---

## 7.1 Modo linha de comando (sem janela)

Serve para o instalador e para diagnóstico. Sempre imprime JSON e sai com 0/1:

```
AnalistaHub.exe --list-tools
AnalistaHub.exe --test mkt-serial 10.0.0.1
AnalistaHub.exe --probe mkt-serial
AnalistaHub.exe --page mkt-serial      (abre a janela já na página da ferramenta)
AnalistaHub.exe --preview-toast        (mostra um aviso de exemplo, para teste visual)
AnalistaHub.exe --version | --help
```

`--probe` devolve `servico_responde` (TCP) e `credenciais` (arquivo presente / completo / chaves
faltando) **sem expor nenhum segredo**.

---

## 8. Requisitos não funcionais

- **Sem administrador.** Instala e roda a partir da pasta do usuário; sem serviço, sem chave de
  registro em HKLM, sem driver.
- **Uma instância só**: abrir de novo traz a janela existente para frente.
- **Sem rede além dos serviços das ferramentas.** Sem telemetria.
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
- Substituir o Espanso: os gatilhos `:mkt`, `:macv` continuam funcionando como hoje.

---

## 13. Gate de catálogo (torna as regras permanentes)

`build/publish.ps1` roda `AnalistaHub.exe --check-tools` no pacote gerado e **aborta** a publicação
se alguma ferramenta violar as regras de `docs/REGRAS-DE-FERRAMENTA.md`:

- manifesto inválido ou script inexistente;
- gatilho fora da regra (formato, tamanho) ou repetido;
- credenciais declaradas sem campos.

Rodar a qualquer momento: `build\check-tools.ps1` ou `AnalistaHub.exe --check-tools`
(exit 0 = ok, exit 1 = problema, com a lista em JSON).

---

## 14. Evidência usada

| Afirmação | Evidência |
|---|---|
| Campos e modos do SN por IP | `%APPDATA%\espanso\scripts\mkt_serial.ps1` (303 linhas) |
| Gatilhos `:mkt`, `:macv`, `:macc`, `:mact` | `espanso\match\mkt_serial.yml`, `espanso\match\mac_vendor.yml` |
| Campos e cache do MAC vendor | `%APPDATA%\espanso\scripts\mac_vendor.ps1` (252 linhas) |
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
| Consulta real (MikroTik) | `AnalistaHub.exe --test mkt-serial 10.0.0.1` | `ok: true`, valor `E3210E3C4EE71` |
| Consulta real (MAC) | `AnalistaHub.exe --test mac-vendor F4:8E:38` | `ok: true`, valor `Dell Inc.` |
| MAC localmente administrado | `--test mac-vendor 02:11:22` | `ok: false` com explicação (não vira valor) |
| MAC inválido | `--test mac-vendor zz` | `ok: false` com explicação |
| Atalho global abre a HUD | `keybd_event Ctrl+Alt+Space` | HUD aparece em 0,2 s, com foco no campo de busca |
| Captura de atalho (item 2) | `--arm-hotkey` + teclado real `^%{F9}` | log `key=F9 mods=[Ctrl+Alt] combo=Ctrl+Alt+F9`; `settings.json` grava `Ctrl+Alt+F9` |
| Captura recusa tecla pura | tecla sem modificador | mensagem "use Ctrl, Alt, Shift ou Win" e atalho anterior mantido |
| Credenciais cifradas (item 3) | `--set-cred` (valor via STDIN) + `--probe` | `completo: true`; `credenciais.dat` gravado com DPAPI |
| Senha errada no HUB | `--set-cred passwd` com valor inválido + `--test` | falha de login com mensagem "Usuário ou senha recusados pelo inventário" |
| Senha correta no HUB | `--set-cred` com valor do cofre + `--test` | `ok: true`, valor `E3210E3C4EE71` |
| Cofre antigo continua válido | script chamado direto, sem env | responde `E3210E3C4EE71` (Espanso não quebra) |
| Gatilho configurável (item 4) | `--set-trigger mkt-serial :sn` + `--list-tools` | `gatilho: ":sn"`, persistido em `settings.json`, volta a `:mkt` |
| Clique direito na bandeja (item 1) | `PostMessage(WM_TRAYICON, WM_RBUTTONUP)` | menu com 7 itens (2 ferramentas, Buscar, Configurações, Fechar); `tray: menu aberto (7 itens), escolhido=0` ao cancelar |
| Gate de catálogo | manifesto com gatilho duplicado (`:MKT` vs `:mkt`) + `--check-tools` | `ok:false`, exit 1, motivo "o gatilho :mkt já é da ferramenta …"; volta a `ok:true` sem o manifesto |
| Validação de credencial (aceita) | `--validate-cred mkt-serial` | `estado: ok`, `mensagem: auth_success` |
| Validação de credencial (recusada) | senha errada + `--validate-cred` | `estado: refused`, `mensagem: auth_failed`, exit 1 |
| Feedback na tela (aceita) | `--page mkt-serial --validate-once` | rótulos `Usuário — ok` / `Senha — ok` e contorno verde |
| Feedback na tela (recusada) | senha errada + `--validate-once` | rótulos `— recusado`, contorno amarelo, `serviço: auth_failed` |
| Bolinha única (item 4) | Início com estado `refused` persistido | bolinha **amarela** em "SN da RB pelo IP" e **verde** em "Fabricante pelo MAC" |
| Janela cabe na tela | abertura em monitor 1451×864 físico | janela enquadrada em 94 % da área útil (antes estourava e cortava a direita) |
| Busca por gatilho + campos | digitar `:macv` + `Enter` | filtra para 1 ferramenta e abre o campo "MAC do equipamento" |
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
