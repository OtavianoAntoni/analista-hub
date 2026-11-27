# O que mudou — Analista Hub

> Resumo para quem usa o app. Uma seção por versão, no máximo ~10 itens, agrupados por tema.
> Sem ID interno, sem nome de arquivo, sem detalhe técnico — o texto detalhado fica no `PATCHLOG.md`.

## 2.4.0 — 27/11/2026

**Novidades**

- A atualização acontece dentro do app: o aviso de versão nova tem o botão **Baixar** e, quando o
  download termina e é conferido, o mesmo botão vira **Instalar e reiniciar**.
- Se o repositório principal estiver fora do ar, o app — e o instalador — buscam a atualização no
  repositório alternativo.
- Na página inicial, cada ferramenta tem o campo de consulta direto no cartão: digite e clique em
  **Buscar** (ou pressione Enter).
- Na primeira vez que o app abre depois de atualizar, ele mostra sozinho o resumo do que mudou.
- Dá para escolher o tamanho dos ícones (Pequeno, Médio, Grande) em Configurações, com prévia na hora.

**Melhorias**

- O serial da RB aparece sem título repetido, com o **1** somado em destaque. Os botões **Copiar** e
  **Manter aberto** só aparecem quando a consulta traz um serial.
- O atalho de cada ferramenta agora se chama **Atalho de ativação**, e a tela ficou mais limpa.

**Correções**

- O atalho da ferramenta voltou a abrir pronto para digitar em **todas** as vezes — não só na
  primeira depois de ligar o computador.
- A busca rápida não tem mais a borda branca em volta, e a janela acompanha o conteúdo.
- As mensagens de erro da busca rápida ficaram só com o texto, sem botões.

## 2.3.0 — 25/11/2026

**Novidades**

- Instalador no padrão do Windows, com conferência de integridade do pacote antes de gravar.
- O app encontra sozinho a lista de NMs da equipe, sem precisar preencher caminho.

**Melhorias**

- Assinatura local das ferramentas, para quando a política do PowerShell exige scripts assinados.
- Ícone próprio no executável e janela principal de tamanho fixo.
- A busca da Consulta SSE passou a priorizar a sigla digitada.
