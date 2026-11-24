# Instruções para quem edita este projeto

## Regra número 1 — preservar o que o usuário escreveu

Este projeto é editado pelo usuário **e** por sessões de agente em paralelo (inclusive ao mesmo
tempo). Antes de alterar qualquer arquivo:

1. **Releia o arquivo do disco** (`read_file` / `search_files`) imediatamente antes de editar. Nunca
   edite de memória, de contexto antigo ou de uma versão que você lembra ter escrito.
2. **O disco vence.** Se o texto no disco divergir do que você esperava, é porque alguém mudou —
   adapte a sua alteração ao texto atual, não o contrário.
3. **Edite por trecho** (`patch` com contexto), nunca reescrevendo o arquivo inteiro. Arquivo novo
   pode ser escrito inteiro; arquivo existente, não.
4. Se o `patch` falhar porque o texto mudou, **releia e reaplique** sobre o conteúdo atual. Não
   force, não "recrie" o arquivo, não reverta.
5. **Textos de interface pertencem ao usuário.** Rótulos, títulos, descrições e mensagens
   (ex.: `SIGLA ANATEL` → `ANATEL`) não devem ser renomeados, reformatados nem "corrigidos" por
   iniciativa própria. Se um rótulo atrapalha uma mudança, pergunte.

Exemplo real: se o usuário trocar o rótulo `SIGLA ANATEL` por `ANATEL` em
`src/AnalistaHub/Views/ConsultaSseWindow.xaml`, qualquer edição posterior nesse arquivo mantém
`ANATEL`.

## Regra número 2 — verificar antes de dizer que está pronto

- `dotnet build -c Debug` sem erro (SDK em `%USERPROFILE%\.dotnet`, fora do PATH).
- `AnalistaHub.exe --check-tools` verde (gate das regras de ferramenta).
- Para mudanças de janela/bandeja: rodar `build/test-janela-branca.py <exe> <ciclos>`.
- Publicar com `build/publish.ps1` (copia `.xbf`/`.pri` e roda o gate).

## Regra número 3 — as regras de ferramenta são permanentes

Ler `docs/REGRAS-DE-FERRAMENTA.md` antes de criar ou alterar ferramenta. Gatilho, validação de
credencial, bolinha de status e comportamento da janela principal estão definidos lá e são
verificados pelo gate.

## Ambiente

- Windows corporativo, **sem administrador**; tudo no perfil do usuário.
- Scripts `.ps1` com acento **precisam de UTF-8 com BOM** (PowerShell 5.1 lê ANSI sem BOM e o
  parser quebra). `.cmd`/`.bat` ficam em ASCII puro, sem BOM.
- Não mover nem apagar: `dist/` é gerado; dados do usuário ficam em `%LOCALAPPDATA%\AnalistaHub\`.
