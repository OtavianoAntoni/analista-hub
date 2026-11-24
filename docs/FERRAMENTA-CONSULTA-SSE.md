# SPEC — Ferramenta "Consulta SSE" (cidade → Sigla Anatel, estado, NM/VPN)

Versão 2.0 · Implementada em `Services/SseCatalog.cs` + `Views/ConsultaSseWindow.*`
Base de dados: `data/sse-cidades.json` (schema 3), gerada por
`Documents\Network\ANATEL Tarifacao\_build\build_merge.py`.

---

## 1. Objetivo

Um único campo de busca onde o analista digita **cidade, sigla Anatel ou UF** e recebe:

| Campo exibido | Origem |
|---|---|
| **Sigla Anatel** | ANATEL (oficial) — cai para a planilha SSE quando a cidade não está no cadastro da ANATEL |
| **Estado** (UF) | ANATEL / planilha SSE |
| **NM · OLT · VPN** | planilha CONSULTA SSE — só existe nas cidades onde temos OLT |

Uma cidade pode ter **várias combinações** NM/OLT/VPN — todas aparecem juntas.
Cidade que só existe no cadastro da ANATEL aparece com sigla e estado, marcada
`somente cadastro · sem NM`.

**Decisão de produto:** uma ferramenta só, não duas. Consulta por cidade resolve o caminho
inteiro (cadastro + acesso), sem o analista ter de lembrar qual base consultar.

## 2. Base de dados (schema 3)

Duas partes, por sensibilidade do dado:

| Parte | Arquivo | Versionado | Conteúdo |
|---|---|---|---|
| pública | `data/sse-cidades.json` | sim | 5.577 municípios: `n` (nome), `k` (nome sem acento), `uf`, `sigla` ANATEL, `rows: []` |
| interna | `%LOCALAPPDATA%\AnalistaHub\sse-rede.local.json` | **não** | 161 cidades com `rows` (NM · OLT · VPN), 181 linhas |

O app carrega a base pública e, se o arquivo interno existir, mescla por **`k` + UF** (nome sozinho
colide entre estados). Sem o arquivo interno a consulta funciona: mostra sigla e estado e o log
registra `rede local ausente`.

Gerador: `build/split-sse-local.py` (a partir de uma base completa, com as redes).

`data/sse-cidades.json` — 492 KB, gerado por `build_merge.py` a partir de duas fontes:

| Fonte | Linhas | Papel |
|---|---|---|
| `CONSULTA SSE` (7 planilhas, uma por UF) | 161 cidades | NM, OLT e VPN de cada rede |
| `ANATEL-areas-tarifacao-municipios.csv` | 5564 municípios | sigla oficial + estado |

Junção por **nome normalizado + UF** (mesma `Text.Fold` do app):

- cidade nas duas fontes → 1 registro, com redes e sigla oficial;
- cidade só na SSE (13: regiões do DF como CEILANDIA/TAGUATINGA, mais BOTICÃO/MG,
  MONTE SANTO/MG, PRESIDENTE DUTRA/MG, TERMÓPOLIS/MG e NOVA PONTE/MS) → mantém a sigla
  da planilha, porque na ANATEL essas localidades não são município;
- cidade só na ANATEL (5416) → entra com sigla e estado, `rows` vazio.

Resultado: **5577 cidades, 161 com NM, 181 linhas de acesso**.

`src` no registro diz a origem (`sse` | `anatel`). Quando a sigla da planilha divergia da
oficial, o JSON guarda `sigla_sse` (auditoria) e a ferramenta exibe a da ANATEL —
dois casos corrigidos: `PRATÁPOLIS/MG` (estava com o nome da cidade na coluna da sigla,
virou `PRO`) e `JUSSARA/PR` (estava com a sigla de JUSSARA/GO, virou `JSA`).

Custo: carga única no arranque; busca em memória sobre 5577 itens.

### Atualização dos dados

Rodar `build_merge.py` e copiar o JSON para `%LOCALAPPDATA%\AnalistaHub\sse-cidades.json`
(vence o embutido no pacote) — não precisa reinstalar o Hub.

## 3. Comportamento

### 3.1 Chamada
- O nome **"Consulta SSE"** (a busca aceita "consulta", "sse" e a categoria). Não existe código curto
  desde o 2.2.1 — o antigo `:sse` saiu junto com o conceito de gatilho.
- Card na janela principal, categoria **SSE**; item no menu da bandeja.
- `AnalistaHub.exe --sse [termo]` abre o painel já com a busca preenchida (diagnóstico).
- Sem configuração e sem credencial.

### 3.2 Busca
Campo único, foco automático, filtro incremental insensível a caixa e acento. Prioridade:

1. nome idêntico (com acento) · 2. nome idêntico sem acento · **3. UF digitada sozinha
(ex.: `MG` = consulta por estado)** · 4. nome começa com · 5. termo no meio do nome ·
6. começa com a sigla · 7. começa com a UF.

Empate: **cidade com NM/OLT/VPN primeiro** e depois ordem alfabética. Assim
`TAGUATINGA` mostra a do DF (com redes) antes da do TO (só cadastro), e `MG` lista
Minas Gerais antes de MAGÉ/RJ.

A lista mostra no máximo **300 cartões** por busca (a base tem 5577); o rodapé diz
`mostrando 300 de N — refine a busca`. `↑`/`↓` navega, `Enter` copia, `Esc` fecha.

### 3.3 Cartão de resultado
`CIDADE` + badge `UF` + badge destacado `SIGLA ANATEL`; abaixo, uma linha por rede com
`NM | OLT | VPN`. Cidade sem planilha de OLT só mostra `somente cadastro · sem NM` **se** a opção
"Dados extras → Registro de NM por cidade" estiver ligada (padrão: oculta).

### 3.4 Botão "NMs" (toggle)
No cabeçalho, à direita do título. Liga e desliga a exibição das **informações de acesso**
(NM · OLT · VPN) de todos os cartões, para deixar a tela limpa em atendimento.

- estado **ligado** = mostra as redes (padrão de fábrica);
- estado **desligado** = o cartão fica em cidade + UF + sigla;
- a escolha é gravada em `settings.json` (`SseShowNodes`) e volta na próxima abertura;
- o texto **copiado não muda** com o botão: cópia sempre o que existe (sigla + redes).

### 3.4.1 Menu "Dados extras" (engrenagem)

Botão `⚙` ao lado do "NMs". Abre um painel com o que fica **fora** da leitura principal — tudo
nasce desligado, então a tela abre limpa:

| Opção | Chave em `settings.json` | Padrão | O que faz |
|---|---|---|---|
| Registro de NM por cidade | `SseShowNoNodes` | desligado | mostra `somente cadastro · sem NM` nos cartões sem rede |
| Informação de fonte | `SseShowSource` | desligado | mostra a linha `fonte: <caminho da base>` no rodapé |
| Resumo da base | `SseShowBaseInfo` | desligado | mostra `5577 cidades · 161 com NM · base de …` no cabeçalho |
| Resultados por busca | `SseMaxRows` | **30** | quantos cartões montar por busca (10 a 500) |

O rodapé mostra `mostrando 30 de 367` quando a busca rende mais que o limite — sem o antigo
`refine a busca`, que era instrução redundante (digitar já filtra).

**As mesmas opções existem na página da ferramenta, na janela principal** (bloco "Dados extras da
consulta", logo abaixo de "Base de dados"). É um painel único (`Views/SseExtras.cs`) usado nos dois
lugares: uma preferência, dois pontos de acesso. Mudar na janela principal atualiza um painel de
consulta que já esteja aberto.

### 3.5 Texto copiado (contrato estável)

Cidade com redes (valores de exemplo — a rede real fica no `sse-rede.local.json` da máquina):
```
TAGUATINGA (DF) — Sigla Anatel: TAGA
NM: NM-EXEMPLO-1 | OLT: OLT-01 | VPN: VPN-EXEMPLO-1
NM: NM-EXEMPLO-2 | OLT: OLT-02 | VPN: VPN-EXEMPLO-2
NM: NM-EXEMPLO-3 | OLT: OLT-03 | VPN: VPN-EXEMPLO-3
```

Cidade só com cadastro:
```
ALUMINIO (SP) — Sigla Anatel: AMIO
Sem NM cadastrado para esta cidade.
```

## 4. Integração técnica

1. JSON em `src/AnalistaHub/data/sse-cidades.json`, `CopyToOutputDirectory=PreserveNewest`.
2. `SseCatalog.Current` carrega uma vez (singleton); `--sse` não recarrega a cada busca.
3. `Views/ConsultaSseWindow.xaml` — cabeçalho com o `ToggleButton NmsToggle`.
4. Tool id `consulta-sse`, `kind: "dataset"` (sem `script`/`interpreter`).
5. `AppSettings.SseShowNodes` guarda a preferência do botão.

## 5. Verificação executada

| Prova | Comando | Resultado |
|---|---|---|
| Compilação | `dotnet build -c Release` | 0 erro, 0 aviso |
| Gate de catálogo | `build/publish.ps1` → `--check-tools` | `ok: true`, 3 ferramentas, 0 problemas |
| Base carregada | `AnalistaHub.exe --test consulta-sse TAGUATINGA` | 5577 cidades, 161 com NM |
| Ordem com acesso primeiro | idem | TAGUATINGA/DF (TAGA, com NM) antes de TAGUATINGA/TO (TGG, sem NM) |
| Consulta por estado | `--test consulta-sse MG` | 875 resultados, mineiras primeiro |
| Cidade só cadastro | `--test consulta-sse ALUMINIO` | AMIO, `acesso: false`, `origem: anatel` |
| Sigla corrigida | `--test consulta-sse PRATAPOLIS` | `PRO` (a planilha dizia `PRATAPOLIS`) |
| Sede ausente no cadastro | `--test consulta-sse JUSSARA` | JSA/PR (com NM), JSS/BA, JUS/GO |

## 6. Aberto / limitações

- O clique no botão "NMs" **não foi verificado visualmente**: `cua-driver` respondeu
  "Connection closed" e a captura de janela por script não foi autorizada. O caminho do
  estado (preferência → `_showNodes` → cartões) está compilado e é o mesmo usado ao abrir
  o painel; falta a confirmação com o app na tela.
- A junção é por nome + UF: cidade grafada de forma diferente nas duas fontes não casa e
  aparece em dois registros (um com NM, outro só cadastro).
- As 13 cidades da SSE sem par na ANATEL não têm sigla oficial para conferir.
- Planilha do SSE é manual: OLT nova só aparece depois de rodar `build_merge.py`.
