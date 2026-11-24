# Patch log — Analista Hub

> Formato: o resumo conta o que mudou para quem usa; o detalhamento fica separado por
> **Adicionados**, **Corrigido** e **Removidos**. Cada item diz como foi verificado.

## 2.1 — 24/11/2026

**Resumo.** Esta versão tira o último resto de dependência do Espanso e prepara o projeto para ser
público. As ferramentas passam a viver dentro do próprio HUB (`tools/`), com um script ao lado do manifesto; a Consulta SSE deixa de carregar dado de rede interna na base versionada e passa a ler esses dados de um arquivo local; endereços de serviço e caminho de cofre saem dos manifestos e vão para a configuração da máquina. Na tela, a busca rápida para de repetir texto, a consulta abre limpa (sigla e estado primeiro) e o atalho global não deixa mais o usuário sem atalho quando o Windows
recusa a combinação escolhida.

### Adicionados

- **Scripts dentro do projeto**: `src/AnalistaHub/tools/*.ps1`, ao lado do manifesto; o campo `script` traz só o nome do arquivo e o app resolve na pasta da ferramenta.
- **Ícone por ferramenta**: campo `icon` aceita arquivo `.ico`/`.png` na pasta da ferramenta, com o glifo como reserva. Vale para barra lateral, título da ferramenta e lista da busca.
- **Configuração local**: `%LOCALAPPDATA%\AnalistaHub\config.local.json`, com `config.example.json` versionado; manifestos usam `${CFG:chave}` para endereço de serviço, url de validação e cofre.
- **Consulta SSE — dados extras** (menu ⚙ no painel **e** bloco na página da ferramenta): registro de NM por cidade, informação de fonte, resumo da base e **resultados por busca** (padrão 30, de 10 a 500).
- **Busca rápida — ocultar contagem** de ferramentas no rodapé (Configurações).
- **Base pública × rede interna**: `data/sse-cidades.json` (5.577 municípios, versionado) +
  `sse-rede.local.json` (161 cidades com NM · OLT · VPN, local e fora do git).
- **Comandos de diagnóstico**: `--try-hotkey <combo>`, `--hud [id]`, `--check-tools` passa a devolver `avisos` (o que falta de configuração local).
- **Regras permanentes** em `docs/REGRAS-DE-FERRAMENTA.md`: R9 (onde mora o script), R10 (ícone), R11 (troca de atalho transacional), R12 (descrição é frase, não painel de status).
- **Gate de publicação** ampliado: script fora da pasta da ferramenta, referência a `espanso`,
  ícone ausente, descrição com metadado e gatilho duplicado **bloqueiam** a publicação.
- **Higiene de pacote**: `publish.ps1` e `package-release.ps1` removem cache de token (JWT), cache de OUI, logs e backups antes de gerar o pacote.
- **`AGENTS.md`**: regra de reler o arquivo do disco antes de editar e preservar rótulos do usuário.

### Corrigido

- **Atalho global não ficava salvo ao abrir nova instância.** Causa real: a troca desregistrava o
  atalho antes de tentar o novo; quando o Windows recusava (outro programa já usa), o app ficava sem atalho nenhum e a mensagem dizia que o anterior continuava valendo. Agora a troca é transacional, o erro do Windows é traduzido, a gravação é atômica (temporário + troca) e o app relê o disco para confirmar.
- **Janela em branco ao voltar pelo ícone**: fechar a janela destruía o host XAML e qualquer janela
  recriada saía em branco. A janela principal não tem mais X (o fechamento real é pelo botão lateral ou pelo menu do ícone) e, se a janela morrer, o app reinicia o processo.
- **Busca rápida repetia a descrição** no rodapé ao abrir os campos e **ecoava o clipboard** na dica
  (`• agora: …`) — origem do campo duplicado que apareceu em `:mkt`, `:macv` e `:sse`.
- **Consulta SSE**: `refine a busca` saiu do rodapé; resumo da base, fonte e "somente cadastro · sem NM" passaram a ficar ocultos por padrão.
- **Catálogo**: arquivos de cache que nascem em `tools/` (`mkt_serial_token.json`, `mac_vendor_cache.json`) apareciam como "ferramenta com problema"; agora são ignorados.
- **Junção da rede interna** era por nome e colidia entre municípios homônimos de UFs diferentes (dava 160 cidades e 184 com NM); passou a usar nome + UF e voltou a 161 / 181.
- **Diagnóstico visual**: capturas por coordenada liam a janela de outro programa; passou a usar
  captura por janela (`PrintWindow`), imune a z-order.

### Removidos

- **Dependência do Espanso**: nenhum caminho `%APPDATA%\espanso` no catálogo. Os originais ficaram guardados em `%APPDATA%\espanso\scripts\_migrados-para-o-hub-20261124\` (recuperáveis), e os gatilhos `:mkt`/`:macv` **do Espanso** deixaram de funcionar por decisão do usuário.
- **Identificadores de infraestrutura** dos arquivos versionados: endereço do inventário, url de
  validação, caminho do cofre, nome da empresa nos rótulos e nomes de NM/OLT/VPN.
- **Caminho do cofre no script**: `mkt_serial.ps1` lê `$env:ANALISTAHUB_COFRE`; o HUB passa usuário e senha por variável de ambiente.
- **`-SkipScripts` no CI**: os scripts agora são versionados, então o gate do runner valida que eles
  existem.

### Como verificar

```powershell
& .\dist\AnalistaHub\AnalistaHub.exe --version                     # 2.1.0
& .\dist\AnalistaHub\AnalistaHub.exe --check-tools                 # ok + avisos vazios
& .\dist\AnalistaHub\AnalistaHub.exe --try-hotkey "Ctrl+Alt+F9"    # diz se a combinação está livre
& .\dist\AnalistaHub\AnalistaHub.exe --test mac-vendor "F4:8E:38"  # Dell Inc.
& .\dist\AnalistaHub\AnalistaHub.exe --sse sao                     # consulta com 30 resultados
```

### Decisões e limites

- O rótulo **`SIGLA ANATEL`** foi mantido (pedido do usuário para não alterar).
- O histórico do git foi reescrito nesta versão para remover os identificadores antigos; clones
  anteriores precisam ser refeitos.
- Endereços internos continuam existindo **apenas** na máquina do analista
  (`config.local.json` e `sse-rede.local.json`) — nem no repositório, nem no pacote publicado.
