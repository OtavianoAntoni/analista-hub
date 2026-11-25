# Regras de ferramenta — permanentes, valem para toda ferramenta do HUB

Este documento é a regra única do projeto. Vale para as ferramentas que já existem **e para todas
as que surgirem depois** — não é sugestão, é o contrato. Se uma ferramenta nova não cumprir um item
daqui, o build de publicação **falha** (gate automático, ver `build/check-tools.ps1`).

Fonte no código: `src/AnalistaHub/Services/ToolCatalog.cs` (busca por nome/descrição),
`Services/HotkeyService.cs` + `App.ApplyToolHotkey` (atalhos),
`CredentialValidator`/`CredentialStore` (credenciais), `MainWindow` (status).

---

## R1 — Invocação: nome na busca ou atalho da ferramenta (não existe mais "gatilho")

O conceito de **gatilho de ferramenta** (`:mkt`, `:sse`) foi removido no 2.2.1. Sobraram duas formas de
invocar:

| Forma | Como funciona |
|---|---|
| Busca rápida | atalho global (`Ctrl+Alt+Espaço` por padrão) → digite parte do **nome** ou da **descrição** → `Enter` |
| Atalho da ferramenta | combinação global própria, gravada em `settings.json` (`ToolHotkeys`) e definida na página da ferramenta |

O que **não** pode existir: campo de gatilho na UI, chave `trigger` no manifesto, `TriggerOverrides` em
`settings.json`. Configuração de versão anterior carrega sem erro — a chave é ignorada, o log registra
`migração 2.2.1: N gatilho(s) de ferramenta ignorado(s)` e a primeira gravação limpa o arquivo.

Não há mais unicidade de token para validar: a busca pontua **nome** primeiro e depois
descrição/categoria, então um código curto repetido não torna a busca ambígua — nomes repetidos são
problema de catálogo, não de digitação.

## R2 — Atalho da ferramenta: mesmo desenho do atalho global

1. A página da ferramenta mostra o atalho atual e o botão **Definir atalho**; a captura só acontece
   **armada** — tecla digitada fora de captura não altera nada.
2. A combinação precisa de pelo menos um modificador (Ctrl, Alt, Shift ou Win). `Esc` cancela.
3. Gravação **transacional** (`App.ApplyToolHotkey`): se o Windows recusar (1409 = já em uso) ou se a
   combinação for a da busca, o atalho anterior continua valendo e a tela diz o motivo.
4. **Comportamento do atalho** (por ferramenta, em `settings.json`):
   - **Consultar direto** — usa o valor da área de transferência **no momento do atalho**;
   - **Aguardar input** (padrão) — abre a ferramenta com o primeiro campo focado.
   Clipboard vazio ou valor inválido em *Consultar direto* cai para *Aguardar input*, com **uma**
   mensagem dizendo o motivo.
5. O valor copiado passa pela **mesma** validação da digitação (`Services/FieldValidation.cs`).

## R3 — Ferramenta nova: checklist obrigatório

1. Manifesto `.json` em `src/AnalistaHub/tools/` (ou em `%LOCALAPPDATA%\AnalistaHub\tools\`).
2. `id`, `name`, `description` preenchidos e **ou** `script` (ferramenta que executa
   programa) **ou** `kind: "dataset"` + `dataset` (ferramenta que consulta uma base local, sem
   processo e sem rede — o painel é próprio, ver `docs/FERRAMENTA-CONSULTA-SSE.md`).
3. Nome que o analista consiga achar: a busca é por nome/descrição, então `name` e `description`
   são a interface de invocação. Nada de código curto no manifesto.
4. Saída declarada (`output.valueLabel`) dizendo **o que** é copiado. Se a ferramenta tem um valor de
   "sem resultado" (ex.: `0` = IP fora do inventário), declare também
   `output.zeroValue` (`title` + `note`): o HUB mostra o título, escreve `<dado> · <note>` e **não
   copia nada** nesse estado (MKT-001). Sem o bloco, `0` é copiado como qualquer valor.
5. Se o script pede credencial: `credentials.fields` com `env` (nunca argumento de linha de
   comando) e, quando o serviço permitir, `credentials.validation` para validar ao digitar.
6. Se depende de serviço: `network.host`/`port` para a bolinha de status e a mensagem de VPN.
7. `dotnet build` + `AnalistaHub.exe --check-tools` verdes antes de publicar.

## R4 — Credenciais (quando a ferramenta exige)

| Etapa | Regra |
|---|---|
| Entrada | digitadas na aba da ferramenta; o usuário nunca informa caminho de arquivo |
| Gravação | automática ao sair do campo + botão "Salvar credenciais" |
| Armazenamento | DPAPI (escopo do usuário) em `%LOCALAPPDATA%\AnalistaHub\credenciais.dat` |
| Entrega ao script | variável de ambiente declarada no manifesto (`ANALISTAHUB_USER` etc.) |
| Validação | só quando o usuário interage; espera **320 ms** depois da última tecla; consulta o serviço declarado em `validation` |
| Feedback | travessão ao lado do rótulo (`— ok` / `— recusado` / `— sem resposta`) e contorno verde/amarelo no campo |
| Espera | um único indicador circular de "testando" por ferramenta, usado por toda validação |

Ferramenta que exige credencial e **não** declara `validation` aparece com o aviso "o HUB guarda,
mas não confere" — e isso deve ser tratado como dívida, não como estado final.

## R5 — Status: uma bolinha, quatro estados

A mesma bolinha da aba Início reflete comunicação **e** autenticação:

| Cor | Significado |
|---|---|
| cinza | não verificado |
| verde | serviço respondeu e credenciais aceitas (ou não exige) |
| amarelo | serviço respondeu, mas autenticação recusada — ou credenciais faltando |
| vermelho | sem conexão com o serviço |

Proibido criar uma segunda bolinha para "status de login": o estado de autenticação entra na mesma.

## R6 — Gate automático (é o que torna a regra permanente)
`build/publish.ps1` roda `AnalistaHub.exe --check-tools` no pacote gerado e **aborta** se houver:

- manifesto inválido ou script inexistente;
- `trigger` presente no manifesto (o conceito não existe mais — o gate aponta o campo a remover);
- credenciais declaradas sem campos.

Ou seja: não é possível publicar uma ferramenta que não siga a R1–R5 por distração.
Rodar manualmente a qualquer momento:

```powershell
dist\AnalistaHub\AnalistaHub.exe --check-tools
```

---

## R7 — Janela principal e bandeja (não regredir)

O app vive na bandeja e a janela principal **não pode ser fechada pelo usuário**:

| Regra | Motivo |
|---|---|
| sem `WS_SYSMENU` na janela principal | sem X e sem Alt+F4: fechar a última janela faz o WinUI desmontar o host XAML e **toda janela criada depois abre em branco** |
| botões **Minimizar** e **Fechar** no rodapé da barra lateral | devolvem a affordance que o X fazia |
| sair de verdade = menu do ícone → *Fechar aplicativo* | é o único caminho que encerra o processo (`App.ExitApp` + `ForcedExit`) |
| janela "guardiã" invisível viva | mantém o host XAML de pé se a janela principal morrer por fora |
| **tamanho fixo** — 1120×760 DIP, sem `WS_THICKFRAME`/`WS_MAXIMIZEBOX` | reflow a cada arrasto era caro e a área nova saía branca pura (UI-004, 2.3) |
| `ShowMainWindow` recria + `NudgeRender` | rede de segurança: reaproveita, recria se preciso e força repaint |

Teste de regressão: `build/test-janela-branca.py <exe> <ciclos>` — janela com menos de 80 cores
distintas na área de conteúdo é considerada **em branco** (falha).

## R9 — Onde mora o script de uma ferramenta

- O script vive **ao lado do manifesto**: `src/AnalistaHub/tools/` (ou a pasta de ferramentas do
  usuário, quando o manifesto é do usuário). O campo `script` traz **só o nome do arquivo** — sem
  caminho, sem `%APPDATA%`, sem variável de ambiente.
- Proibido apontar para pasta de outro programa (Espanso, OneDrive, Área de Trabalho…). O HUB não
  depende do Espanso nem de arquivos dele: os scripts foram migrados em 24/11/2026.
- `--check-tools` **bloqueia**: caminho absoluto, caminho fora da pasta do manifesto e qualquer
  referência a `espanso`.
- Artefatos que o script cria ao lado de si (`*token*.json`, `*_cache.json`, `*.log`, `*.bak`) estão
  no `.gitignore` e **nunca** podem ser versionados — o `mkt_serial_token.json` é um JWT de sessão.

## R10 — Ícone de cada ferramenta

Cada ferramenta tem ícone próprio, e trocá-lo não exige código:

| Forma | Campo `icon` no manifesto | Precisa de arquivo? |
|---|---|---|
| Glifo do sistema | `"\uE968"` (Segoe MDL2 Assets) | não |
| Imagem da ferramenta | `"nome.ico"` ou `"nome.png"` | sim, **ao lado do manifesto** |

- O arquivo mora em `src/AnalistaHub/tools/` (ou na pasta de ferramentas do usuário), nunca por
  caminho absoluto — mesma regra do script (R9).
- `.ico` com 16, 24, 32, 48 e 256 px (o app desenha entre 18 e 22 px; o Windows usa o resto).
- Arquivo ausente ou ilegível ⇒ o ícone cai no glifo genérico e `--check-tools` **falha** apontando
  o caminho completo que faltou.
- O build copia `tools/*.ico` e `tools/*.png`; ícone novo entra no pacote sem mudar o `.csproj`.

## R11 — Trocar o atalho global nunca deixa o usuário sem atalho

- `HotkeyService.Register` é **transacional**: se o Windows recusar a combinação nova (erro 1409 =
  outro programa já usa), o atalho anterior volta a valer dentro da mesma chamada.
- O valor em `settings.json` só muda quando o novo atalho foi **registrado com sucesso**. Uma recusa
  não sobrescreve a preferência — "a alteração não foi salva" é o comportamento correto nesse caso, e
  a tela diz o motivo.
- Depois de gravar, o app **confere o arquivo**; se a gravação não pegou, avisa com o caminho e o
  efeito ("volta ao anterior quando reiniciar").
- `settings.json` é gravado de forma **atômica** (arquivo temporário + troca): nunca fica truncado,
  que faria o app voltar aos padrões.
- Diagnóstico por linha de comando: `AnalistaHub.exe --try-hotkey "Ctrl+Alt+F9"` informa se o Windows
  libera a combinação agora, qual é a salva e se ela continua ativa após a recusa.

## R12 — Descrição é frase, não painel de status

`description` do manifesto responde **o que a ferramenta faz**, em uma frase. Ela aparece na linha da
HUD e no cartão do Início — e em nenhum outro lugar, uma vez por tela.

Proibido na descrição (o gate `--check-tools` bloqueia):

| Não pode | Onde esse dado deve ficar |
|---|---|
| contagem (`161 com NM`, `5577 cidades`) | rodapé/estado da ferramenta, ao abrir |
| data (`2026-11-23`, `base de …`) | rodapé/estado |
| rótulos como `agora:`, `fonte:` | dica do campo, se for o caso — sem eco de conteúdo |
| separadores `•`, `·`, ` \| ` | a frase é uma só |
| quebra de linha | — |
| mais de 140 caracteres | use `longDescription`, que existe para isso |

Regra irmã (mesma família de defeito): a HUD **não repete** a descrição no rodapé ao abrir os campos,
e a dica do campo **não mostra o conteúdo da área de transferência**. Foi exatamente isso que produziu
o campo `• agora: • agora: cidades • 161 com NM • 2026-11-23` no `:mkt`, `:macv` e `:sse`.

## R13 — Tema: nenhuma cor fixa de tema

- Toda cor de **superfície ou texto** vem de `ThemeDictionaries` (`HubHudBackground`, `HubCardBackground`,
  `HubStroke`, `HubTile`, `HubMonoValue`, `HubDimText`, `HubOk/Warn/Bad/Unknown`) ou de `ThemeResource`
  do sistema. Cor hexadecimal fixa só é aceita quando é a cor da própria marca (ex.: selo de sigla).
- Nenhuma janela define `RequestedTheme` com valor fixo: usa `UiKit.ApplyTheme(RootGrid)` **antes** de
  `Activate` (aplicar depois pisca).
- Elementos criados em código usam `UiKit.Ok/Warn/Bad/Unknown/DimText/SubtleStroke`, que escolhem a
  variante pelo tema efetivo — não existe `SetResourceReference` no WinUI 3.
- Trocar o tema em Configurações reaplica em **todas** as janelas abertas (`HudController`,
  `ToastController`, `SsePanelController`).
- Contraste mínimo: 4,5:1 (WCAG AA) para texto nos dois temas, medido por cálculo de luminância.

## R14 — Ping do destino: declarado no manifesto, sem processo `ping.exe`

Subferramenta (NET-001): campo de IP pode pingar o destino **enquanto o analista digita**. Especificação
completa em `docs/SUBFERRAMENTA-PING.md`; fonte em `Services/IpPing.cs`.

| Regra | Detalhe |
|---|---|
| Declaração | `"ping": true` no campo de `type: "ip"` do manifesto. Em outro tipo de campo o gate **falha** |
| Gatilho | IPv4 completo e válido (CIDR aceito: `10.0.0.1/32` pinga o endereço) |
| Espera | debounce de `PingDebounceMs` (padrão **400 ms**, Configurações) depois da última tecla |
| Edição | qualquer alteração no texto cancela o ping em curso e reagenda o debounce |
| Pacote | 1 eco ICMP por disparo, timeout `PingTimeoutMs` (padrão **1000 ms**, Configurações) |
| Exibição | **1 linha** ao lado do campo, opacidade **75%**: `1 ms`, `sem resposta`, `destino inalcançável` |
| Resultado | o aviso da ferramenta mostra os **2 últimos retornos** do endereço consultado (`ping · 3 ms · 4 ms`) |
| Trava | **100 disparos por invocação** (reset ao reabrir a ferramenta); ao estourar, para e avisa no rodapé |
| Sem processo | ICMP pela API do Windows (`System.Net.NetworkInformation.Ping`) — proibido chamar `ping.exe`, que deixaria processo órfão ao cancelar |

Provas: `--ping <ip>` (ICMP real e timeout), `--ping-sim` (debounce, cancelamento e trava de 100 sem
tocar na rede) e `--check-tools` (declaração no manifesto).

## Como isso chega às próximas sessões

- `SPEC.md` §7 (contrato), §5.1 (atalho), §6.5/§6.6 (credenciais), §6.7 (ping), §13 (gate).
- `docs/SUBFERRAMENTA-PING.md` — comportamento e API do ping, para a próxima ferramenta que usar.
- `README.md` — seção "Adicionar uma ferramenta".
- Skill `winui3-unpackaged-sem-visual-studio` — armadilhas de captura e validação.
- Este arquivo é citado pelos três: quem duvidar da regra, confere aqui.
