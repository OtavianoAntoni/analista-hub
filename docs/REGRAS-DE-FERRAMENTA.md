# Regras de ferramenta — permanentes, valem para toda ferramenta do HUB

Este documento é a regra única do projeto. Vale para as ferramentas que já existem **e para todas
as que surgirem depois** — não é sugestão, é o contrato. Se uma ferramenta nova não cumprir um item
daqui, o build de publicação **falha** (gate automático, ver `build/check-tools.ps1`).

Fonte no código: `src/AnalistaHub/Services/TriggerPolicy.cs` (gatilho),
`CredentialValidator`/`CredentialStore` (credenciais), `MainWindow` (status).

---

## R1 — Gatilho: uma regra só, aplicada a todas as ferramentas

Todo gatilho — de ferramenta nova ou existente — passa por `TriggerPolicy`:

| Regra | Detalhe |
|---|---|
| Forma | começa com `:`, minúsculas, sem espaço |
| Caracteres | `a-z`, `0-9`, `-`, `_` depois dos dois-pontos |
| Tamanho | 2 a 32 caracteres no total |
| Unicidade | não pode repetir o de outra ferramenta do catálogo |

O que **não** pode existir: tela com campo de gatilho livre sem validação, manifesto com gatilho em
outro formato, ou ferramenta sem gatilho. O normalizador é o mesmo para todas (`TriggerPolicy.Normalize`).

## R2 — Interação do gatilho: igual em toda ferramenta (espelha o atalho global)

1. O campo mostra o gatilho atual e o botão **Definir gatilho**.
2. A captura só acontece **armada** — tecla digitada fora de captura não altera nada.
3. Ao confirmar (`Enter` ou sair do campo), a regra R1 valida e o resultado é mostrado:
   - verde + `— ok · gatilho ativo :x` quando aceito;
   - amarelo + `recusado: <motivo>` quando não.
4. `Esc` cancela. O gatilho antigo continua valendo.
5. Gravado em `settings.json` (`TriggerOverrides`) e aplicado na hora: busca do HUD, cartões e
   histórico usam o valor efetivo (`SettingsService.TriggerFor`).

O atalho global (Ctrl+Alt+Espaço) segue o mesmo desenho — armar, capturar, validar, gravar — com a
regra do Windows (precisa de modificador). Nenhuma das duas capturas fica "solta" no projeto.

## R3 — Ferramenta nova: checklist obrigatório

1. Manifesto `.json` em `src/AnalistaHub/tools/` (ou em `%LOCALAPPDATA%\AnalistaHub\tools\`).
2. `id`, `name`, `description`, `script`, `trigger` preenchidos.
3. Gatilho cumprindo R1 — o gate barra duplicado e formato inválido.
4. Saída declarada (`output.valueLabel`) dizendo **o que** é copiado.
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
- gatilho fora da R1, duplicado ou ausente;
- credenciais declaradas sem campos.

Ou seja: não é possível publicar uma ferramenta que não siga a R1–R5 por distração.
Rodar manualmente a qualquer momento:

```powershell
dist\AnalistaHub\AnalistaHub.exe --check-tools
```

---

## Como isso chega às próximas sessões

- `SPEC.md` §7 (contrato), §5.1 (atalho), §6.5/§6.6 (credenciais), §13 (gate).
- `README.md` — seção "Adicionar uma ferramenta".
- Skill `winui3-unpackaged-sem-visual-studio` — armadilhas de captura e validação.
- Este arquivo é citado pelos três: quem duvidar da regra, confere aqui.
