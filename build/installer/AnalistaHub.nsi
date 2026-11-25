/*
 * Instalador do Analista Hub — wizard padrão do Windows (MUI2, pt-BR).
 *
 * Desenho (ver docs/INSTALADOR.md):
 *   • não embute o aplicativo: baixa o .zip publicado na release e confere SHA-256 + tamanho;
 *   • dois modos no MESMO .exe: elevado → C:\Program Files (todos os usuários);
 *     sem elevação → %LOCALAPPDATA%\Programs\Analista Hub (só para mim);
 *   • sem admin no build: makensis portátil, sem Visual Studio; sem plugin externo
 *     (curl.exe, certutil.exe e tar.exe já existem no Windows 10 1803+).
 */

Unicode true

!include "MUI2.nsh"
!include "nsDialogs.nsh"
!include "LogicLib.nsh"
!include "FileFunc.nsh"
!include "StrFunc.nsh"
!include "x64.nsh"

; gera a função StrStr de StrFunc (usada para procurar chaves/valores no JSON e no certutil)
${Using:StrFunc} StrStr

!define APPNAME "Analista Hub"
!define PUBLISHER "Analista Hub"
!define REGKEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\AnalistaHub"

; ---- parâmetros de build (build-installer.ps1)
!ifndef VERSION
  !define VERSION "0.0.0"
!endif
!ifndef CHANNEL
  !define CHANNEL "https://github.com/OtavianoAntoni/analista-hub/releases/latest/download/latest.json"
!endif
; Quando o build conhece o pacote (CI ou teste local), estes valores evitam a consulta ao canal.
; Vazios = consulta latest.json em tempo de instalação.
!ifndef ASSET_URL
  !define ASSET_URL ""
!endif
!ifndef ASSET_SHA256
  !define ASSET_SHA256 ""
!endif
!ifndef ASSET_SIZE
  !define ASSET_SIZE "0"
!endif

Name "${APPNAME}"
OutFile "AnalistaHub-Setup-${VERSION}.exe"
InstallDir "$PROGRAMFILES64\${APPNAME}"
RequestExecutionLevel user          ; o modo "todos os usuários" depende de já estar elevado
SetCompressor /SOLID lzma
ShowInstDetails show
ShowUninstDetails show
BrandingText "${APPNAME} ${VERSION}"

VIProductVersion "${VERSION}.0"
VIAddVersionKey "ProductName" "${APPNAME}"
VIAddVersionKey "ProductVersion" "${VERSION}"
VIAddVersionKey "FileVersion" "${VERSION}.0"
VIAddVersionKey "CompanyName" "${PUBLISHER}"
VIAddVersionKey "FileDescription" "Instalador do ${APPNAME}"
VIAddVersionKey "LegalCopyright" ""

Icon "app.ico"
UninstallIcon "app.ico"

; ------------------------------------------------------------------ estado
Var ModoMaquina        ; 1 = todos os usuários (elevado), 0 = só para mim
Var PastaDados         ; pasta de dados da equipe (opcional) — vira sse-rede.path
Var UrlPacote
Var VersaoCanal
Var HashEsperado
Var BytesEsperados
Var ZipPath
Var InstaladoVersao
Var FalhouDownload
Var PinFalhou
Var RemoverDados       ; desinstalador (checkbox)
Var Prog
Var SaidaComando       ; saída do último RodarCodigo (diagnóstico no log)
Var CampoPasta
Var CampoDados

!define MUI_WELCOMEPAGE_TITLE "Instalar o ${APPNAME} ${VERSION}"
!define MUI_WELCOMEPAGE_TEXT "Este assistente instala o ${APPNAME} ${VERSION} nesta máquina.$\r$\n$\r$\nO aplicativo é baixado do canal de atualização (cerca de 82 MB) e conferido por SHA-256 antes de qualquer arquivo ser gravado.$\r$\n$\r$\nNão é preciso ter .NET nem Windows App Runtime instalados: o pacote é autocontido.$\r$\n$\r$\nClique em Próximo para continuar."
!insertmacro MUI_PAGE_WELCOME

Page custom PaginaDestinoCreate PaginaDestinoLeave
!insertmacro MUI_PAGE_COMPONENTS
!insertmacro MUI_PAGE_INSTFILES

!define MUI_FINISHPAGE_RUN "$INSTDIR\AnalistaHub.exe"
!define MUI_FINISHPAGE_RUN_TEXT "Abrir o ${APPNAME} agora"
!define MUI_FINISHPAGE_TITLE "Instalação concluída"
!define MUI_FINISHPAGE_TEXT "${APPNAME} ${VERSION} foi instalado em $INSTDIR."
!insertmacro MUI_PAGE_FINISH

!insertmacro MUI_UNPAGE_CONFIRM
UninstPage custom un.PaginaOpcoes un.PaginaOpcoesLeave
!insertmacro MUI_UNPAGE_INSTFILES

!insertmacro MUI_LANGUAGE "PortugueseBR"

; ------------------------------------------------------------------ utilidades


Function TamanhoDoArquivo
  Exch $0
  Push $1
  Push $2
  StrCpy $2 0
  System::Call 'kernel32::CreateFileW(w "$0", i 0x80000000, i 1, p 0, i 3, i 0, p 0) p .r1'
  ${If} $1 != "-1"
  ${AndIf} $1 != "0"
    System::Call 'kernel32::GetFileSize(p r1, p 0) i .r2'
    System::Call 'kernel32::CloseHandle(p r1)'
  ${EndIf}
  StrCpy $0 $2
  Pop $2
  Pop $1
  Exch $0
FunctionEnd

; Rodar: roda um comando escondido (nsExec) e devolve a saída.
;   Push 'linha de comando' ; Call Rodar ; Pop $saida   (o código de saída fica em $Prog)
Function Rodar
  Exch $0
  nsExec::ExecToStack '$0'
  Pop $Prog
  Pop $1
  StrCpy $0 $1
  Exch $0
FunctionEnd

; Igual a Rodar, mas devolve o CÓDIGO DE SAÍDA (Rodar devolve a saída, que serve para tar mas não
; para decidir se o curl terminou). A saída do comando fica em $SaidaComando para o log.
Function RodarCodigo
  Exch $0
  nsExec::ExecToStack '$0'
  Pop $1
  Pop $2
  StrCpy $SaidaComando $2
  StrCpy $0 $1
  Exch $0
FunctionEnd


Function StrValorJson
  ; pilha: texto, chave (já com aspas: '"version"'). Devolve o valor — texto entre aspas ou número.
  Exch $1
  Exch
  Exch $0
  Push $2
  Push $3
  Push $4
  Push $5
  Push $6

  StrCpy $2 ""
  ${StrStr} $3 $0 $1
  ${If} $3 != ""
    StrLen $4 $1
    StrCpy $3 $3 "" $4              ; depois da chave
    ${StrStr} $3 $3 ":"
    ${If} $3 != ""
      StrCpy $3 $3 "" 1             ; depois dos dois-pontos

      ; pula espaços
      espacos:
        StrCpy $5 $3 1
        ${If} $5 == " "
          StrCpy $3 $3 "" 1
          Goto espacos
        ${EndIf}

      ${If} $5 == '"'
        StrCpy $3 $3 "" 1           ; depois da aspas de abertura
        ${StrStr} $4 $3 '"'
        StrLen $4 $4
        StrCpy $2 $3 -$4
      ${Else}
        ; valor sem aspas (o "size" do manifesto): até vírgula, fecha-chave ou fim de linha
        StrCpy $6 0
        numeros:
          StrCpy $5 $3 1 $6
          ${If} $5 == ""
          ${OrIf} $5 == ","
          ${OrIf} $5 == "}"
          ${OrIf} $5 == "$\r"
          ${OrIf} $5 == "$\n"
            Goto fimNumeros
          ${EndIf}
          StrCpy $2 "$2$5"
          IntOp $6 $6 + 1
          Goto numeros
        fimNumeros:
      ${EndIf}
    ${EndIf}
  ${EndIf}

  StrCpy $0 $2
  Pop $6
  Pop $5
  Pop $4
  Pop $3
  Pop $2
  Pop $1
  Exch $0
FunctionEnd


Function StrLimpar
  Exch $0
  Push $1
  Push $2
  Push $3
  StrCpy $1 ""
  StrCpy $2 0
  ${Do}
    StrCpy $3 $0 1 $2
    StrCmp $3 "" done
    StrCmp $3 " " +4 0
    StrCmp $3 "$\r" +3 0
    StrCmp $3 "$\n" +2 0
    StrCmp $3 ":" +1 0
      StrCpy $1 "$1$3"
    IntOp $2 $2 + 1
  ${Loop}
  done:
  StrCpy $0 $1
  Pop $3
  Pop $2
  Pop $1
  Exch $0
FunctionEnd


; Barra: move a barra de progresso da página de instalação (0–100).
; Push 42 ; Call Barra ; Pop $R9
Function Barra
  Exch $0
  Push $1
  Push $2
  FindWindow $1 "#32770" "" $HWNDPARENT
  GetDlgItem $2 $1 1004          ; controle da barra na página instfiles
  SendMessage $2 0x0402 $0 0     ; PBM_SETPOS
  Pop $2
  Pop $1
  Exch $0
FunctionEnd


; EscreverLog: acrescenta uma linha em %TEMP%\AnalistaHub-Setup.log
; Push "texto" ; Call EscreverLog ; Pop $R9
Function EscreverLog
  Exch $0
  Push $1
  ClearErrors
  FileOpen $1 "$TEMP\AnalistaHub-Setup.log" a
  ${IfNot} ${Errors}
    FileSeek $1 0 END
    FileWrite $1 "$0$\r$\n"
    FileClose $1
  ${EndIf}
  Pop $1
  Exch $0
FunctionEnd

; ------------------------------------------------------------------ inicialização

Function .onInit
  ${IfNot} ${RunningX64}
    MessageBox MB_ICONSTOP "O ${APPNAME} é um aplicativo 64 bits e não roda nesta máquina."
    Abort
  ${EndIf}

  ; elevado = todos os usuários; sem elevação = só para mim
  ; (nunca gravar em Program Files sem permissão: o padrão acompanha o que o usuário pode fazer)
  UserInfo::GetAccountType
  Pop $0
  ${If} $0 == "Admin"
    StrCpy $ModoMaquina 1
    SetShellVarContext all
  ${Else}
    StrCpy $ModoMaquina 0
    SetShellVarContext current
  ${EndIf}

  ; versão instalada (para o aviso de "já instalado")
  ReadRegStr $InstaladoVersao HKCU "${REGKEY}" "DisplayVersion"
  ${If} $ModoMaquina == 1
    ReadRegStr $R3 HKLM "${REGKEY}" "DisplayVersion"
    ${If} $R3 != ""
      StrCpy $InstaladoVersao $R3
    ${EndIf}
  ${EndIf}

  ; "/D=<pasta>" vence tudo — mas o NSIS o remove do $CMDLINE, então a única pista é o próprio
  ; $INSTDIR já ter vindo diferente do valor padrão do script (InstallDir).
  ${If} $INSTDIR == "$PROGRAMFILES64\${APPNAME}"
    ${If} $ModoMaquina == 1
      StrCpy $INSTDIR "$PROGRAMFILES64\${APPNAME}"
    ${Else}
      StrCpy $INSTDIR "$LOCALAPPDATA\Programs\${APPNAME}"
    ${EndIf}

    ; instalação anterior manda no caminho (upgrade no lugar)
    ReadRegStr $R5 HKCU "${REGKEY}" "InstallLocation"
    ${If} $ModoMaquina == 1
      ReadRegStr $R6 HKLM "${REGKEY}" "InstallLocation"
      ${If} $R6 != ""
        StrCpy $R5 $R6
      ${EndIf}
    ${EndIf}
    ${If} $R5 != ""
      StrCpy $INSTDIR $R5
    ${EndIf}
  ${EndIf}

  StrCpy $FalhouDownload 0
  StrCpy $PinFalhou 0
  StrCpy $RemoverDados 0
  StrCpy $PastaDados ""
  InitPluginsDir
  StrCpy $ZipPath "$PLUGINSDIR\pacote.zip"
  Push "=== inicio: instalador ${VERSION} · modo=$ModoMaquina · destino=$INSTDIR · instalado=$InstaladoVersao"
  Call EscreverLog
  Pop $R9
FunctionEnd

; ------------------------------------------------------------------ página: destino

Function PaginaDestinoCreate
  !insertmacro MUI_HEADER_TEXT "Pasta de instalação" "Escolha onde instalar e, se houver, a pasta de dados da equipe."

  nsDialogs::Create 1018
  Pop $0
  ${If} $0 == error
    Abort
  ${EndIf}

  ${NSD_CreateLabel} 0 0 100% 26u "O ${APPNAME} será instalado na pasta abaixo.$\r$\nO caminho digitado sempre vale — o padrão é só uma sugestão."
  Pop $0

  ${NSD_CreateDirRequest} 0 30u 100% 14u "$INSTDIR"
  Pop $1

  ${NSD_CreateLabel} 0 54u 100% 34u "Pasta de dados da equipe (opcional)$\r$\nSe a lista de NMs fica em uma pasta compartilhada, informe o caminho: o aplicativo passa a ler de lá. Em branco, ele procura sozinho."
  Pop $0

  ${NSD_CreateDirRequest} 0 92u 100% 14u "$PastaDados"
  Pop $2

  ${If} $ModoMaquina == 1
    ${NSD_CreateLabel} 0 116u 100% 22u "Modo: todos os usuários (o instalador está elevado)."
  ${Else}
    ${NSD_CreateLabel} 0 116u 100% 40u "Modo: só para mim — a instalação vai para a pasta do seu usuário, sem senha de administrador.$\r$\nPara instalar em Program Files, execute este arquivo com o botão direito → Executar como administrador."
  ${EndIf}
  Pop $0

  ; guarda os handles nos nossos vars (nsDialogs devolve em $1/$2 acima, que são temporários)
  Push $1
  Push $2
  Pop $CampoDados
  Pop $CampoPasta

  nsDialogs::Show
FunctionEnd

Function PaginaDestinoLeave
  ${NSD_GetText} $CampoPasta $0
  ${NSD_GetText} $CampoDados $PastaDados

  ${If} $0 == ""
    MessageBox MB_ICONEXCLAMATION "Informe a pasta de instalação."
    Abort
  ${EndIf}
  StrCpy $INSTDIR $0

  ; permissão de escrita: testa de verdade
  ClearErrors
  CreateDirectory "$INSTDIR"
  ${If} ${Errors}
    MessageBox MB_ICONSTOP "Sem permissão nesta pasta.$\r$\n$\r$\nEscolha uma pasta do seu usuário ou execute o instalador como administrador."
    Abort
  ${EndIf}
  ClearErrors
  FileOpen $9 "$INSTDIR\.escrita.tmp" w
  ${If} ${Errors}
    MessageBox MB_ICONSTOP "Sem permissão para gravar em $INSTDIR.$\r$\n$\r$\nEscolha uma pasta do seu usuário ou execute o instalador como administrador."
    Abort
  ${EndIf}
  FileClose $9
  Delete "$INSTDIR\.escrita.tmp"

  ; espaço livre (zip + extração)
  ${DriveSpace} "$INSTDIR" "/D=F /S=M" $3
  ${If} $3 < 250
    MessageBox MB_ICONEXCLAMATION "Espaço insuficiente em $INSTDIR: são necessários cerca de 250 MB livres."
    Abort
  ${EndIf}

  ; aplicativo aberto no destino
  ${If} ${FileExists} "$INSTDIR\AnalistaHub.exe"
    Push '"$SYSDIR\tasklist.exe" /FI "IMAGENAME eq AnalistaHub.exe" /NH'
    Call Rodar
    Pop $4
    ${StrStr} $5 $4 "AnalistaHub.exe"
    ${If} $5 != ""
      MessageBox MB_YESNO|MB_ICONQUESTION "O ${APPNAME} está aberto. Fechar o aplicativo para continuar?" IDYES fechar
      MessageBox MB_ICONEXCLAMATION "Feche o aplicativo para continuar."
      Abort
      fechar:
      Push '"$SYSDIR\taskkill.exe" /IM AnalistaHub.exe /F'
    Call Rodar
    Pop $4
      Sleep 1200
    ${EndIf}
  ${EndIf}

  ; canal: versão publicada × versão instalada
  Call LerCanal
  ${If} $UrlPacote == ""
    MessageBox MB_ICONSTOP "Sem conexão com o canal.$\r$\n$\r$\nO endereço consultado foi:$\r$\n${CHANNEL}$\r$\n$\r$\nVerifique a conexão/proxy e clique em Voltar e Próximo para tentar de novo."
    Abort
  ${EndIf}

  ${If} $InstaladoVersao != ""
  ${AndIf} $InstaladoVersao == $VersaoCanal
    MessageBox MB_YESNO|MB_ICONQUESTION "Já instalado: versão $InstaladoVersao (o canal publica a mesma).$\r$\n$\r$\nReinstalar por cima?" IDYES +2
    Abort
  ${EndIf}
FunctionEnd

; ------------------------------------------------------------------ canal

Function LerCanal
  StrCpy $VersaoCanal "${VERSION}"
  StrCpy $UrlPacote "${ASSET_URL}"
  StrCpy $HashEsperado "${ASSET_SHA256}"
  StrCpy $BytesEsperados ${ASSET_SIZE}

  ; build sem pacote conhecido: lê o manifesto do canal
  ${If} $UrlPacote != ""
    Return
  ${EndIf}

  Delete "$PLUGINSDIR\latest.json"
  Push '"$SYSDIR\curl.exe" -sS -L --max-time 30 --retry 1 -o "$PLUGINSDIR\latest.json" "${CHANNEL}"'
    Call Rodar
    Pop $0
  ${IfNot} ${FileExists} "$PLUGINSDIR\latest.json"
    Return
  ${EndIf}

  ; o manifesto é indentado: lê linha a linha (o campo "notes" é enorme e estouraria o buffer)
  FileOpen $1 "$PLUGINSDIR\latest.json" r
  ${Do}
    FileRead $1 $2
    ${If} $2 == ""
      ${Break}
    ${EndIf}
    Push $2
    Push '"version"'
    Call StrValorJson
    Pop $3
    ${If} $3 != ""
      StrCpy $VersaoCanal $3
    ${EndIf}
    Push $2
    Push '"url"'
    Call StrValorJson
    Pop $3
    ${If} $3 != ""
      StrCpy $UrlPacote $3
    ${EndIf}
    Push $2
    Push '"sha256"'
    Call StrValorJson
    Pop $3
    ${If} $3 != ""
      StrCpy $HashEsperado $3
    ${EndIf}
    Push $2
    Push '"size"'
    Call StrValorJson
    Pop $3
    ${If} $3 != ""
      StrCpy $BytesEsperados $3
    ${EndIf}
  ${Loop}
  FileClose $1
  Delete "$PLUGINSDIR\latest.json"

  ${If} $HashEsperado == ""
    StrCpy $UrlPacote ""
  ${EndIf}
FunctionEnd

; ------------------------------------------------------------------ seções

Section "Aplicativo (obrigatório)" SecApp
  SectionIn RO

  ${If} $UrlPacote == ""
    Call LerCanal
  ${EndIf}
  ${If} $UrlPacote == ""
    Push "ERRO: canal sem pacote (${CHANNEL})"
    Call EscreverLog
    Pop $R9
    Abort "Não foi possível descobrir o pacote no canal."
  ${EndIf}
  Push "canal: versao=$VersaoCanal url=$UrlPacote sha256=$HashEsperado tamanho=$BytesEsperados"
  Call EscreverLog
  Pop $R9

  DetailPrint "Destino: $INSTDIR"
  DetailPrint "Versão no canal: $VersaoCanal"

  Push 2
  Call Barra
  Pop $R9
  DetailPrint "Baixando o pacote do canal…"
  Call BaixarPacote
  ${If} $FalhouDownload == 1
    Push "ERRO: download incompleto"
    Call EscreverLog
    Pop $R9
    Abort "O download não terminou. Nada foi instalado."
  ${EndIf}

  Push 72
  Call Barra
  Pop $R9
  DetailPrint "Conferindo o SHA-256 do pacote…"
  Call ConferirHash
  ${If} $FalhouDownload == 1
    Push "ERRO: hash nao confere com o publicado"
    Call EscreverLog
    Pop $R9
    Delete "$ZipPath"
    Abort "Pacote corrompido: o download não confere com o publicado. Nada foi instalado."
  ${EndIf}

  Push 80
  Call Barra
  Pop $R9
  CreateDirectory "$INSTDIR"
  DetailPrint "Extraindo em $INSTDIR…"
  Push '"$SYSDIR\tar.exe" -xf "$ZipPath" -C "$INSTDIR"'
    Call Rodar
    Pop $0
  Push "tar: exit=$Prog"
  Call EscreverLog
  Pop $R9
  ${IfNot} ${FileExists} "$INSTDIR\AnalistaHub.exe"
    ${If} ${FileExists} "$INSTDIR\AnalistaHub\AnalistaHub.exe"
      DetailPrint "Pacote com pasta única na raiz: achatando…"
      Push '"$SYSDIR\robocopy.exe" "$INSTDIR\AnalistaHub" "$INSTDIR" /MOVE /E /NFL /NDL /NJH /NJS'
    Call Rodar
    Pop $0
    ${EndIf}
  ${EndIf}
  ${IfNot} ${FileExists} "$INSTDIR\AnalistaHub.exe"
    Push "ERRO: pacote sem AnalistaHub.exe apos extrair"
    Call EscreverLog
    Pop $R9
    Abort "O pacote baixado não contém AnalistaHub.exe."
  ${EndIf}
  Push "instalado: AnalistaHub.exe presente"
  Call EscreverLog
  Pop $R9
  Delete "$ZipPath"

  Push 88
  Call Barra
  Pop $R9
  ; pasta de dados da equipe: gravada pelo próprio app (preserva as outras chaves do JSON)
  ${If} $PastaDados != ""
    Push '"$INSTDIR\AnalistaHub.exe" --set-rede-path "$PastaDados"'
    Call Rodar
    Pop $0
    DetailPrint "Pasta de dados da equipe: $PastaDados"
  ${EndIf}

  ; registro de desinstalação
  WriteRegStr SHELL_CONTEXT "${REGKEY}" "DisplayName" "${APPNAME}"
  WriteRegStr SHELL_CONTEXT "${REGKEY}" "DisplayVersion" "${VERSION}"
  WriteRegStr SHELL_CONTEXT "${REGKEY}" "Publisher" "${PUBLISHER}"
  WriteRegStr SHELL_CONTEXT "${REGKEY}" "DisplayIcon" "$INSTDIR\Assets\app.ico"
  WriteRegStr SHELL_CONTEXT "${REGKEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr SHELL_CONTEXT "${REGKEY}" "UninstallString" '"$INSTDIR\Desinstalar AnalistaHub.exe"'
  WriteRegStr SHELL_CONTEXT "${REGKEY}" "QuietUninstallString" '"$INSTDIR\Desinstalar AnalistaHub.exe" /S'
  WriteRegDWORD SHELL_CONTEXT "${REGKEY}" "NoModify" 1
  WriteRegDWORD SHELL_CONTEXT "${REGKEY}" "NoRepair" 1
  WriteUninstaller "$INSTDIR\Desinstalar AnalistaHub.exe"

  ; Menu Iniciar — obrigatório (requisito 5), independe dos checkboxes
  CreateDirectory "$SMPROGRAMS\${APPNAME}"
  CreateShortcut "$SMPROGRAMS\${APPNAME}\${APPNAME}.lnk" "$INSTDIR\AnalistaHub.exe" "" "$INSTDIR\Assets\app.ico" 0
  Push "atalho menu iniciar: $SMPROGRAMS\${APPNAME}\${APPNAME}.lnk"
  Call EscreverLog
  Pop $R9
  DetailPrint "Atalho no Menu Iniciar: $SMPROGRAMS\${APPNAME}\${APPNAME}.lnk"

  Push 96
  Call Barra
  Pop $R9
  ${If} $ModoMaquina == 1
    ; §8-A: mantém a atualização automática funcionando em Program Files. A pasta continua
    ; protegida contra escrita por usuários fora do grupo Users, mas não contra o próprio Users —
    ; é o preço de o analista poder aplicar a atualização diária sem administrador.
    Push '"$SYSDIR\icacls.exe" "$INSTDIR" /grant *S-1-5-32-545:(OI)(CI)M /T /C /Q'
    Call Rodar
    Push "acls: exit=$Prog (Users com Modify em $INSTDIR)"
    Call EscreverLog
    Pop $R9

    ; modo todos os usuários: quem assina é o instalador (elevado), com certificado de máquina
    DetailPrint "Assinando os scripts das ferramentas (certificado de máquina)…"
    Push '"$INSTDIR\AnalistaHub.exe" --sign-tools --machine'
    Call Rodar
    Pop $0
    DetailPrint "assinatura: $Prog"
  ${Else}
    DetailPrint "A assinatura dos scripts será feita na primeira abertura do aplicativo."
  ${EndIf}

  Push 100
  Call Barra
  Pop $R9
  Push "=== fim: instalacao concluida"
  Call EscreverLog
  Pop $R9
SectionEnd

Section "Área de trabalho" SecDesktop
  CreateShortcut "$DESKTOP\${APPNAME}.lnk" "$INSTDIR\AnalistaHub.exe" "" "$INSTDIR\Assets\app.ico" 0
  DetailPrint "Atalho na área de trabalho: $DESKTOP\${APPNAME}.lnk"
SectionEnd

Section "Fixar na barra de tarefas" SecTaskbar
  ; D5: best-effort. O Windows 11 removeu o verbo e a política corporativa pode bloquear:
  ; falhar aqui NUNCA derruba a instalação — a conclusão mostra como fixar à mão.
  StrCpy $PinFalhou 0
  Push `"$SYSDIR\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -WindowStyle Hidden -Command "(New-Object -ComObject Shell.Application).NameSpace(0).ParseName('Analista Hub.lnk').InvokeVerb('taskbarpin')"`
  Call Rodar
  Pop $0
  ; O Windows 11 removeu o verbo e a política pode bloquear sem devolver erro: registramos a
  ; tentativa e SEMPRE mostramos como fixar à mão na conclusão (nunca é falha da instalação).
  StrCpy $PinFalhou 1
  DetailPrint "Fixar na barra de tarefas: solicitado ao Windows (código $Prog)."
SectionEnd

!insertmacro MUI_FUNCTION_DESCRIPTION_BEGIN
  !insertmacro MUI_DESCRIPTION_TEXT ${SecApp} "Baixa o pacote publicado (cerca de 82 MB, fora do instalador), confere o SHA-256 e extrai na pasta escolhida. O tamanho só aparece depois do download."
  !insertmacro MUI_DESCRIPTION_TEXT ${SecDesktop} "Cria o atalho do ${APPNAME} na sua área de trabalho (o atalho do Menu Iniciar é sempre criado)."
  !insertmacro MUI_DESCRIPTION_TEXT ${SecTaskbar} "Pede ao Windows para fixar o atalho na barra de tarefas (pode ser recusado pela política da máquina)."
!insertmacro MUI_FUNCTION_DESCRIPTION_END

; ------------------------------------------------------------------ download

Function BaixarPacote
  StrCpy $FalhouDownload 0
  Delete "$ZipPath"

  ; curl é retomável (-C -): cada chamada baixa por até 8 s e a seguinte continua de onde parou.
  ; Assim a barra do PRÓPRIO wizard mostra o progresso, sem plugin externo e sem janela de console.
  StrCpy $3 0
  StrCpy $9 0
  ${Do}
    Push '"$SYSDIR\curl.exe" -sS -L -C - --max-time 8 --connect-timeout 15 -o "$ZipPath" "$UrlPacote"'
    Call RodarCodigo
    Pop $4
    Push "$ZipPath"
  Call TamanhoDoArquivo
  Pop $5

    ${If} $BytesEsperados > 0
      IntOp $6 $5 * 100
      IntOp $6 $6 / $BytesEsperados
      ${If} $6 > 70
        StrCpy $6 70
      ${EndIf}
      IntOp $6 $6 + 2
      Push $6
  Call Barra
  Pop $R9
      IntOp $R8 $3 + 8
      ${If} $6 >= $R8
        StrCpy $3 $6
        IntOp $7 $5 / 1048576
        IntOp $8 $BytesEsperados / 1048576
        DetailPrint "Baixando… $6% ($7 MB de $8 MB)"
      ${EndIf}
    ${Else}
      Push 30
  Call Barra
  Pop $R9
    ${EndIf}

    ${If} $4 == "0"
    ${AndIf} $5 > 0
      ${Break}
    ${EndIf}

    ; 28/33/56 = transferência parcial (retomável); 0 com arquivo vazio = nada ainda
    ${If} $4 != "28"
    ${AndIf} $4 != "33"
    ${AndIf} $4 != "56"
    ${AndIf} $4 != "0"
      DetailPrint "curl devolveu $4 (verifique proxy/conexão)"
      Push "ERRO: curl devolveu $4 (bytes=$5 de $BytesEsperados): $SaidaComando"
      Call EscreverLog
      Pop $R9
      ${Break}
    ${EndIf}

    IntOp $9 $9 + 1
    ${If} $9 > 900          ; ~2 horas de tentativas de 8 s
      DetailPrint "Tempo esgotado no download."
      ${Break}
    ${EndIf}
    Sleep 120
  ${Loop}

  Push "$ZipPath"
  Call TamanhoDoArquivo
  Pop $5
  Push "download: obtido=$5 esperado=$BytesEsperados (curl=$4)"
  Call EscreverLog
  Pop $R9
  ${If} $5 == 0
    StrCpy $FalhouDownload 1
  ${EndIf}
  ${If} $BytesEsperados > 0
  ${AndIf} $5 != $BytesEsperados
    DetailPrint "tamanho obtido: $5 bytes; esperado: $BytesEsperados"
    StrCpy $FalhouDownload 1
  ${EndIf}
FunctionEnd

Function ConferirHash
  Push '"$SYSDIR\certutil.exe" -hashfile "$ZipPath" SHA256'
    Call Rodar
    Pop $0
  Push $0
  Call StrLimpar
  Pop $1
  ${StrStr} $2 $1 "$HashEsperado"
  ${If} $2 == ""
    StrCpy $FalhouDownload 1
    DetailPrint "hash esperado: $HashEsperado"
    DetailPrint "hash obtido:   $1"
  ${Else}
    DetailPrint "SHA-256 confere: $HashEsperado"
  ${EndIf}
FunctionEnd

; ------------------------------------------------------------------ finalização

Function .onInstSuccess
  IfSilent done            ; em modo silencioso não há tela para clicar OK
  ${If} $PinFalhou == 1
    MessageBox MB_ICONINFORMATION "Para fixar na barra de tarefas: abra o Menu Iniciar, clique com o botao direito em ${APPNAME} e escolha Fixar na barra de tarefas. Se o icone ja estiver na barra, nada a fazer."
  ${EndIf}
  done:
FunctionEnd

; ------------------------------------------------------------------ desinstalação

Function un.onInit
  SetShellVarContext current
  ReadRegStr $0 HKCU "${REGKEY}" "InstallLocation"
  ${If} $0 == ""
    SetShellVarContext all
    ReadRegStr $0 HKLM "${REGKEY}" "InstallLocation"
  ${EndIf}
  ${If} $0 != ""
    StrCpy $INSTDIR $0
  ${EndIf}
  StrCpy $RemoverDados 0
FunctionEnd

Function un.PaginaOpcoes
  !insertmacro MUI_HEADER_TEXT "Desinstalar o ${APPNAME}" "Confirme o que deve ser removido."
  nsDialogs::Create 1018
  Pop $0
  ${NSD_CreateLabel} 0 0 100% 30u "A pasta do programa, os atalhos e o registro de instalação serão removidos de $INSTDIR."
  Pop $0
  ${NSD_CreateCheckbox} 0 44u 100% 24u "Remover também configurações, histórico e lista de redes deste usuário"
  Pop $RemoverDados
  ${NSD_CreateLabel} 0 74u 100% 30u "Desmarcado (recomendado): preferências, histórico e lista de NMs continuam em %LOCALAPPDATA%\AnalistaHub."
  Pop $0
  nsDialogs::Show
FunctionEnd

Function un.PaginaOpcoesLeave
  ${NSD_GetState} $RemoverDados $0
  StrCpy $RemoverDados $0
FunctionEnd

Section "Uninstall"
  SetShellVarContext current
  Delete "$SMPROGRAMS\${APPNAME}\${APPNAME}.lnk"
  RMDir "$SMPROGRAMS\${APPNAME}"
  Delete "$DESKTOP\${APPNAME}.lnk"
  SetShellVarContext all
  Delete "$SMPROGRAMS\${APPNAME}\${APPNAME}.lnk"
  RMDir "$SMPROGRAMS\${APPNAME}"
  Delete "$DESKTOP\${APPNAME}.lnk"
  SetShellVarContext current

  DeleteRegKey HKCU "${REGKEY}"
  DeleteRegKey HKLM "${REGKEY}"

  RMDir /r "$INSTDIR"

  ${If} $RemoverDados != "1"
    DetailPrint "Configurações, histórico e lista de redes preservados em $LOCALAPPDATA\AnalistaHub"
  ${Else}
    DetailPrint "Removendo configurações, histórico e lista de redes…"
    RMDir /r "$LOCALAPPDATA\AnalistaHub"
  ${EndIf}

  ; o alfinete da barra de tarefas pode sobrar apontando para um caminho que não existe
  DetailPrint "Se o ícone continuar na barra de tarefas, clique com o botão direito e escolha Desafixar."
SectionEnd
