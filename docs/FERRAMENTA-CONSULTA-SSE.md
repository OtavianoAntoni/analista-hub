# SPEC — Ferramenta "Consulta SSE" no Analista Hub

Status: **pronta para implementação, bloqueada por decisão do dono** (outra sessão está
alterando o projeto do HUB; nada foi modificado em `Atalhos de Analista`).
Base de dados compilada e protótipo validados nesta pasta (`_build/`).

---

## 1. Objetivo

Dado o nome de uma **cidade**, devolver na hora:

| Campo exibido | Origem no CSV |
|---|---|
| **Sigla Anatel** | `SIGLA ANATEL` |
| **NM** | `NMSs` (renomeado) |
| **OLT** | `MODELO OLT` (renomeado) |
| **VPN** | `VPN PARA NMEs` (renomeado) |

Uma cidade pode ter **várias combinações** NM/OLT/VPN (19 das 161 cidades). Todas são
exibidas juntas, na mesma cidade.

Fonte única e exclusiva: `Documents\Network\CONSULTA SSE\*.csv` (7 arquivos, um por UF).
Nada de rede, VPN, credencial ou API. A ferramenta funciona 100% offline.

## 2. Base compilada (já pronta)

`_build/sse-cidades.json` — 23 KB, gerado por `_build/build_sse.py`:

- 183 linhas lidas → 2 duplicatas exatas colapsadas → **181 registros** → **161 cidades**.
- Formato: `{schema, generated, total_cidades, total_linhas, sources[], cidades[]}`,
  cada cidade `{n, k, uf, sigla, rows:[{nm, olt, vpn}]}`.
- `k` = nome sem acento em maiúsculas → busca O(1) por substring, sem normalizar em tempo de uso.
- `sources[]` guarda nome, linhas, encoding e SHA-256 de cada CSV → rastreabilidade.

**Custo de hardware:** ~24 KB residentes, carga única na inicialização (<5 ms),
busca em memória sobre 161 itens (abaixo de 1 ms). Sem processo, sem porta, sem serviço.

### Atualização dos dados
Quando a planilha mudar: `python build_sse.py` (regenera o JSON e o `INDEX.md`).
Cabeçalho é localizado pela célula `SIGLA ANATEL`, não por número de linha — sobrevive a
linhas inseridas no topo da planilha.

## 3. Comportamento esperado no HUB

**Decisão de projeto:** a ferramenta não é um "script que devolve um valor" — ela tem um
passo interativo (escolher a cidade entre os resultados). Por isso entra como **ferramenta
com painel próprio**, reaproveitando o visual do HUD, e não pelo caminho
`script`/`interpreter` do manifesto.

### 3.1 Chamada
- Gatilho no HUD: `:sse` (aceita `:ss`, `sse`) e pelo nome "Consulta SSE".
- Também aparece como card na janela principal, categoria **SSE**.
- Não exige configuração nem credencial: nenhuma seção "Configurações".

### 3.2 Busca
- Campo único, foco automático, **lista tudo** (161 cidades) enquanto vazio.
- Filtro incremental, insensível a caixa e acento, com prioridade:
  1. cidade começa com o termo → 2. termo no meio do nome → 3. sigla Anatel
  4. dentro da UF (ex.: `mg`) → 5. resto.
- `↑`/`↓` navega, `Enter` copia, `Esc` fecha. Clique também copia.
- Rodapé mostra `posição/total` ou "sem resultado".

### 3.3 Cartão de resultado
`CIDADE` + badge `UF` + badge destacado `SIGLA ANATEL`; abaixo, uma linha por combinação:
`NM | OLT | VPN`. Cidades com múltiplas combinações mostram todas as linhas.

### 3.4 Texto copiado (contrato estável)
```
TAGUATINGA (DF) — Sigla Anatel: TAGA
NM: NCE GESTOR-A | OLT: REDE-INTERNA | VPN: VPN OPERADORA-A / CLICK
NM: AMS OPERADORA-A | OLT: REDE-INTERNA | VPN: VPN OPERADORA-A
NM: NETNUMEN OPERADORA-A | OLT: ZTE | VPN: VPN OPERADORA-A
```
Uma linha de cabeçalho + uma linha por combinação. É o que vai para a área de transferência.

## 4. Integração técnica (para a sessão que estiver no projeto)

1. Copiar `sse-cidades.json` para `src/AnalistaHub/data/` e marcar
   `CopyToOutputDirectory=PreserveNewest` no `.csproj`.
2. Carregar uma vez (singleton) na inicialização; nunca reler a cada busca.
3. Nova view `Views/ConsultaSsePanel.xaml` (mesmo tema do `CommandHud`), aberta pelo gatilho.
4. Registrar o tool id `consulta-sse` no `ToolCatalog` com `kind: "dataset"` (sem
   `script`/`interpreter`), para que `ToolRunner` não tente executar processo.
5. Nenhuma alteração em `mkt-serial.json` / `mac-vendor.json`.

**Ponto de atenção:** se a arquitetura atual só aceita manifesto com `script`, é preciso
um `kind` novo — os arquivos `.json` de manifesto continuam valendo para as ferramentas
existentes (a promessa "nova ferramenta entra copiando um .json" segue verdadeira para
ferramentas de script).

## 5. Verificação já executada

| Prova | Comando | Resultado |
|---|---|---|
| Compilação da base | `python build_sse.py` | 7 arquivos, 183 linhas, 2 duplicatas, 161 cidades, 23 KB |
| Estrutura do JSON | inspeção das 3 primeiras cidades + `TAGUATINGA` | 3 combinações em TAGUATINGA |
| Busca (código real do protótipo) | `node test_busca.js` | 8/8 casos OK |
| Render | protótipo aberto no painel de preview | 161 cidades listadas com NM/OLT/VPN |

## 6. Aberto / limitações

- Planilha é manual: cidade nova ou troca de OLT só aparece após rodar `build_sse.py`.
- Sigla Anatel não é validada contra fonte externa (não há fonte no diretório).
- Duas linhas idênticas foram descartadas (não muda o que o usuário vê).
- Busca por UF e por sigla é decisão de projeto, não pedido explícito.
