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

- **Início**: cartões das ferramentas com nome, o que faz, gatilho, estado do serviço
  (● acessível / ● sem conexão / ○ não verificado) e botões **Abrir** (abre a HUD já filtrada na
  ferramenta) e **Testar** (executa com o valor de exemplo/atual do clipboard).
- **Página da ferramenta** — quatro blocos, espelhando exatamente os quatro itens do `IDEA.md`:
  1. **Para que serve** — descrição em linguagem de operação.
  2. **Como executar** — gatilho, atalho global, exemplo de uso, duração típica.
  3. **Configurações e credenciais** — campos editáveis + estado de cada requisito
     (credencial encontrada/ausente, serviço alcançável, cache de token válido), com botões
     **Testar conexão** e **Abrir cofre**. Segredo nunca é exibido, só o estado.
  4. **Como o resultado é apresentado** — escolha do modo de saída (Digitar / Copiar / Só avisar)
     e o que exatamente é copiado (ex.: `SN + "1"`, `fabricante`).
- **Rodapé** de cada página: última execução (data/hora, sucesso ou erro) do histórico local.
- **Configurações**: atalho global, iniciar com o Windows, tema (Escuro/Claro/Sistema), pasta de
  ferramentas, tempo do aviso, manter histórico (lig/deslig), abrir pasta de dados, versão.

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

## 11. Evidência usada

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
