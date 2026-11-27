@echo off
rem Sincroniza as duas pastas tools (src e dist) e roda o gate do catalogo.
rem Duplo clique: sincroniza, valida e reabre o HUB. Nada e apagado.
rem Opcoes: passe depois do nome do arquivo, ex.: -DryRun   -NoRestart   -SemGate
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build\sync-tools.ps1" %*
set CODIGO=%ERRORLEVEL%
echo.
if not "%CODIGO%"=="0" echo [ATENCAO] terminou com codigo %CODIGO% - leia as mensagens acima.
pause
