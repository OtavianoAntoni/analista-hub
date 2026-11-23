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
  dist/AnalistaHub/        pacote pronto  (AnalistaHub.exe + Analista Hub.cmd + tools/)
```

Saída de dados do app (nunca dentro da pasta do programa):

| Caminho | Conteúdo |
|---|---|
| `%LOCALAPPDATA%\AnalistaHub\settings.json` | preferências (atalho, tema, modo de saída por ferramenta) |
| `%LOCALAPPDATA%\AnalistaHub\tools\*.json` | manifestos de ferramentas do usuário (sobrepõem os embutidos) |
| `%LOCALAPPDATA%\AnalistaHub\historico.jsonl` | últimas execuções (sem segredos) |
| `%LOCALAPPDATA%\AnalistaHub\hub.log` | diagnóstico |

Credenciais **não** são copiadas nem duplicadas: o app lê o cofre que já existe em
`%USERPROFILE%\Documents\Processos\Analyser\credenciais\cofre.md` (seção 5) e só mostra
"credenciais encontradas / faltando". Nenhum segredo é gravado, logado ou exibido.

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
& $exe --test mac-vendor "F4:8E:38"   # {"ok":true,"valor":"Dell Inc."}
& $exe --test mkt-serial "10.0.0.1"   # {"ok":true,"valor":"<SN>1"}
```

Depois, na interface: `Analista Hub.cmd` → janela principal; `Ctrl+Alt+Espaço` → HUD;
digitar `:macv` → `Enter` → campo aparece → `Enter` de novo com o MAC no clipboard.

---

## 5. Pontos de atenção conhecidos

- **Atalho global:** se outra ferramenta já usar `Ctrl+Alt+Espaço`, o app avisa na barra lateral e
  em Configurações → Atalho global dá para trocar. (PowerToys Run usa `Alt+Espaço`; a Paleta de
  Comandos do PowerToys usa `Win+Alt+Espaço` — nenhum conflito com o padrão escolhido.)
- **Duplo aviso de erro:** os scripts atuais abrem o próprio popup quando falham. Enquanto não
  migrarem para o contrato `-Mode hub` (descrito na SPEC §7), a falha aparece no HUD **e** no popup
  antigo. Não é bug do app; é transição.
- **`mac_vendor.ps1` devolve erro no stdout com exit 0** — tratado por `stdoutErrorPatterns` no
  manifesto. Ferramenta nova deve usar `-Mode hub` (JSON) para não precisar disso.
- **Antivírus/SmartScreen:** executável não assinado, self-contained, extraído de pasta do usuário.
  Se o SmartScreen reclamar, é assinatura de código — assunto do instalador.
- **Um único .exe não basta:** a pasta inteira é necessária (runtime + Windows App SDK embutidos).

---

## 6. O que ficou fora desta entrega

- Instalador, assinatura, atualização automática (esta entrega termina em `dist/AnalistaHub`).
- Sincronização de configurações entre pessoas da equipe (previsto no `IDEA.md`, não implementado).
- Migração dos dois scripts existentes para `-Mode hub`.
- Ações que alteram equipamentos: o HUB só consulta.
