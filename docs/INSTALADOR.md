# Instalador do Analista Hub

Este documento descreve o instalador (`AnalistaHub-Setup-<versão>.exe`), os dois modos de instalação,
onde cada coisa fica no disco e o que a desinstalação preserva. A assinatura local das ferramentas
(SIG-001) tem seção própria no fim.

> **Ideia em uma frase:** o `.exe` é pequeno e não embute o aplicativo — ele baixa o pacote publicado
> na release, confere o SHA-256 e o tamanho, e só então grava alguma coisa na máquina.

---

## 1. Como gerar

```powershell
# uma vez: baixa o NSIS 3 portátil para %LOCALAPPDATA%\AnalistaHub\buildtools
powershell -ExecutionPolicy Bypass -File build\build-installer.ps1 -Version 2.2.1
```

Saída: `dist\setup\AnalistaHub-Setup-2.2.1.exe` + `.sha256`. Requisitos: **nenhum** privilégio de
administrador, **nenhum** Visual Studio. O build usa `makensis` (NSIS 3.10 portátil) e o script
`build\installer\AnalistaHub.nsi`; o ícone vem de `src\AnalistaHub\Assets\app.ico`.

Parâmetros úteis:

| Parâmetro | Para quê |
|---|---|
| `-Version 2.2.1` | versão do instalador (vai para `ProductVersion` e para o nome do arquivo) |
| `-ChannelUrl <url>` | manifesto consultado em tempo de instalação (padrão: `releases/latest/download/latest.json`) |
| `-AssetUrl/-Sha256/-Size` | congela o pacote no build (o instalador não consulta o canal) — usado pelo CI e em teste |
| `-NoDownload` | usa o NSIS que já está em `buildtools` |
| `-Out <pasta>` | destino do `.exe` (padrão `dist\setup`) |

No CI (`release.yml`) o instalador é gerado **depois** do `.zip`, com `-AssetUrl/-Sha256/-Size` lidos
do `latest.json` recém-gerado: o Setup da release instala exatamente o pacote daquela release, e o
teste de hash continua valendo porque os números vêm do mesmo manifesto.

---

## 2. Dois modos no mesmo `.exe`

O executável **não** pede elevação (`RequestExecutionLevel user`). O que ele faz depende de quem está
rodando:

| Situação | Onde instala | Atalhos | Registro | Quem assina os scripts |
|---|---|---|---|---|
| Executado **elevado** (admin) | `C:\Program Files\Analista Hub` | `%ProgramData%` (Menu Iniciar de todos) | `HKLM` | o **instalador** (`--sign-tools --machine`) |
| Executado **sem elevação** (padrão do parque) | `%LOCALAPPDATA%\Programs\Analista Hub` | `%APPDATA%` (só este usuário) | `HKCU` | o **app**, na primeira execução |

Regras que não mudam:

- o caminho **digitado** na página de destino sempre vale (o `/D=<pasta>` vence tudo no modo silencioso);
- nunca se grava em `Program Files` sem permissão — a página valida escrita **de fato** (cria e apaga
  um arquivo temporário) antes de deixar avançar;
- se já existe instalação, ela é atualizada no lugar (mesmo caminho), e a versão do canal igual à
  instalada pergunta antes de reinstalar.

### Páginas do wizard

1. **Boas-vindas** — nome, versão e o aviso de que o aplicativo é baixado (~82 MB).
2. **Pasta de instalação** — caminho do programa e, no mesmo passo, o campo opcional **Pasta de dados
   da equipe** (o que for digitado vira `sse-rede.path` no `config.local.json` desta máquina).
3. **Escolher componentes** — dois checkboxes: **Área de trabalho** e **Fixar na barra de tarefas**.
   (O atalho do Menu Iniciar não é opcional e é criado sempre.)
4. **Progresso** — download com % e MB, verificação de SHA-256, extração, atalhos, assinatura.
5. **Conclusão** — opção de abrir o aplicativo e, quando o pin é recusado, a instrução para fixar à mão.

### Barra de tarefas é *best-effort*

O Windows 11 removeu o verbo de pin programático e a política corporativa pode bloquear o resto. O
instalador tenta uma vez, registra o resultado no log e **nunca falha por causa disso**; a conclusão
explica como fixar pelo Menu Iniciar. Área de trabalho e Menu Iniciar não têm fallback (funcionam
sempre).

### Linha de comando

```
AnalistaHub-Setup-<versão>.exe [/S] [/D=<pasta>]
```

- `/S` — instalação silenciosa, sem UI. Mesmo download, mesma verificação de hash, mesmo registro.
- `/D=<pasta>` — último parâmetro, **sem aspas** (regra do NSIS).
- Código de saída: `0` sucesso · `2` instalação abortada (falha de download, hash divergente, pacote
  sem `AnalistaHub.exe`) · outros = erro do instalador.

Log de cada etapa: `%TEMP%\AnalistaHub-Setup.log` (versão, modo, destino, URL, hash, extração,
atalhos, resultado). É o primeiro lugar para olhar quando a instalação silenciosa falha.

---

## 3. O que o instalador grava

| Item | Local |
|---|---|
| Programa | `<pasta escolhida>` (a raiz do `.zip`, sem subpasta) |
| Desinstalador | `<pasta escolhida>\Desinstalar AnalistaHub.exe` |
| Atalho Menu Iniciar | `<Menu Iniciar>\Analista Hub\Analista Hub.lnk` (sempre) |
| Atalho área de trabalho | `<Área de trabalho>\Analista Hub.lnk` (checkbox) |
| Registro | `HKLM` ou `HKCU` `Software\Microsoft\Windows\CurrentVersion\Uninstall\AnalistaHub` |
| Log do Setup | `%TEMP%\AnalistaHub-Setup.log` |

**Nada é escrito em `%LOCALAPPDATA%\AnalistaHub` pelo instalador**, com uma exceção: quando o campo
*Pasta de dados da equipe* é preenchido, ele chama `AnalistaHub.exe --set-rede-path "<pasta>"`, que
grava **uma chave** no `config.local.json` preservando as demais. Preferências, histórico, credenciais
e a lista de NMs locais continuam sendo criados pelo aplicativo.

### Erros com mensagem própria

| Situação | O que o wizard faz |
|---|---|
| sem conexão / proxy | "Sem conexão com o canal" + URL tentada; Voltar/Próximo tenta de novo |
| hash divergente | "Pacote corrompido… Nada foi instalado" — a pasta de destino nem é criada |
| sem permissão na pasta | "Sem permissão nesta pasta" (com a saída: pasta do usuário ou admin) |
| app aberto no destino | oferece fechar; se recusar, volta para a página de destino |
| versão igual à do canal | "Já instalado" e pergunta se quer reinstalar |
| pin recusado | não é erro — nota na conclusão |

---

## 4. Desinstalação

*Aplicativos e recursos* → **Analista Hub** → Desinstalar (ou
`"<pasta>\Desinstalar AnalistaHub.exe" /S`).

Remove: pasta do programa, atalhos (Menu Iniciar e área de trabalho, deste e de todos os usuários) e a
chave de desinstalação.

**Preserva** `%LOCALAPPDATA%\AnalistaHub` (preferências, histórico, credenciais DPAPI, `config.local.json`
e `sse-rede.local.json`). Há um checkbox, **desmarcado por padrão**, para remover também esses dados.

O ícone fixado na barra de tarefas pode sobrar apontando para um caminho inexistente — o desinstalador
avisa para desafixar com o botão direito.

---

## 5. Atualização automática no modo todos os usuários (§8, decisão A)

Em `C:\Program Files` a pasta não é gravável pelo usuário, e o `ApplyAndRestart` recusa pasta não
gravável ("peça ao instalador para atualizar"). Para o modo todos os usuários não perder a atualização
diária, o instalador concede **Modify** ao grupo `Users` na pasta do aplicativo:

```
icacls "<pasta>" /grant *S-1-5-32-545:(OI)(CI)M /T /C /Q
```

**O que isso custa:** a pasta deixa de ser protegida contra escrita pelo próprio grupo `Users` — quem
precisa de isolamento entre usuários deve usar o modo *só para mim* (`%LOCALAPPDATA%`). O SID
`S-1-5-32-545` é usado em vez do nome para funcionar em Windows em qualquer idioma.

---

## 6. Assinatura local das ferramentas (SIG-001)

### O problema que ela resolve

O app já chama o PowerShell com `-ExecutionPolicy Bypass`. Quando a política vem por **GPO**
(`MachinePolicy`/`UserPolicy`), a flag não sobrepõe nada — por design. Sob `AllSigned`, um `.ps1` sem
assinatura válida de certificado confiável não roda, e nenhuma flag contorna isso.

Sem CA interna e sem admin, a saída é a própria máquina criar a âncora de confiança e assinar os
scripts que o app executa: `<pasta do app>\tools\*.ps1`.

### Quem cria o certificado

| Modo de instalação | Quem cria | Repositório da chave | Âncora | Quem assina |
|---|---|---|---|---|
| **Só para mim** | o **app**, na primeira execução | `CurrentUser\My` | `CurrentUser\Root` + `CurrentUser\TrustedPublisher` | o app, sempre que precisar |
| **Todos os usuários** | o **instalador**, já elevado | `LocalMachine\My` | `LocalMachine\Root` + `LocalMachine\TrustedPublisher` | o instalador (na instalação e na atualização) |

No modo todos os usuários o app **não tenta assinar**: ele registra `somente_leitura` e, se encontrar
script sem assinatura válida, mostra "a assinatura das ferramentas precisa do instalador". O app nunca
eleva (`app.manifest` é `asInvoker`).

Assim dois usuários nunca assinam a mesma pasta — assinar reescreve o arquivo, e um sobrescreveria a
assinatura do outro.

### O certificado

- `CertificateRequest` + **ECDSA P-256**, `KeyUsage.DigitalSignature`, EKU de assinatura de código
  (`1.3.6.1.5.5.7.3.3`), validade de **10 anos**.
- Chave criada no *Microsoft Software Key Storage Provider* com `ExportPolicy = None`: **não é
  exportável** e nenhum `.pfx` é gravado em disco.
- Assunto: `CN=Analista Hub — Assinatura Local (<usuario>@<maquina>)` — só produto, usuário e máquina.
  Nunca host, domínio ou nome de cliente.

### Quando roda

No startup do app (em segundo plano, antes de habilitar qualquer ferramenta) e sob demanda:

- primeira execução depois de instalar/atualizar → cria o certificado (se não existir) e assina todos
  os `.ps1`;
- `sha256` do arquivo difere do registrado (a atualização trocou o script) → assina só o que mudou;
- a assinatura do arquivo não confere → reassina;
- certificado a menos de 30 dias do vencimento → cria substituto, reancora e reassina tudo.

Estado em `%LOCALAPPDATA%\AnalistaHub\assinatura.json` (`thumbprint`, `subject`, `criadoEm`,
`validoAte`, escopo e a lista arquivo → sha256). Comandos:

```
AnalistaHub.exe --sign-tools            # assina o que falta/mudou
AnalistaHub.exe --sign-tools --force    # reassina todos
AnalistaHub.exe --sign-tools --machine  # força escopo de máquina (usado pelo instalador elevado)
AnalistaHub.exe --check-tools           # traz "assinatura": ok | ausente | invalida | politica_bloqueia
```

### Renovação (10 anos)

Quando o certificado estiver a menos de 30 dias do vencimento, o app cria um novo automaticamente na
próxima abertura — e o Windows **pede confirmação** para ancorar a nova raiz no repositório do usuário
(no `LocalMachine` a confirmação é administrativa). Se o usuário recusar, o app continua funcionando:
as ferramentas exibem a mensagem de política e o botão **Tentar novamente** existe em
*Configurações → Assinatura local*.

### Quando a assinatura não resolve (declarado)

| Situação | Comportamento |
|---|---|
| Política `Restricted` | nada roda, nem assinado: a mensagem diz que é preciso liberação do TI |
| `AllSigned` por GPO | **é o caso que o SIG-001 resolve** |
| `RemoteSigned` | a assinatura também resolve arquivos marcados como "vem da internet" |
| AppLocker/WDAC com regra de publicador | certificado local não entra na lista; a mensagem aponta a causa |
| Antivírus/EPP barrando o repositório | idem, com o motivo do erro |

**Isto não é controle de segurança.** A âncora é criada e confiada pelo próprio usuário: qualquer
processo rodando como ele pode assinar o que quiser. O objetivo é satisfazer a política de execução,
não provar autoria. Também **não** melhora o SmartScreen do `.exe`/Setup (aquilo é reputação de
binário) e **não** protege contra código malicioso rodando como o próprio usuário.

### Codificação dos scripts

Os `.ps1` do projeto têm acento e precisam estar em **UTF-8 com BOM** (`AGENTS.md`). A assinatura só
acrescenta o bloco `# SIG # Begin signature block` no fim; o conteúdo e o BOM são preservados byte a
byte. Detalhe importante: quando o `.ps1` **não** tem BOM, o Windows decodifica o arquivo como **ANSI**
(CP1252) para calcular o hash — o app faz a mesma coisa (`MultiByteToWideChar` com `CP_ACP`), senão a
assinatura sairia "hash não confere". Um `.ps1` com acento e sem BOM também aparece com acento errado
na execução; corrigir a codificação é tarefa do arquivo, não da assinatura.
