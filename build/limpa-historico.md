# Limpeza de histórico — feito em 24/11/2026

O histórico foi reescrito com `git filter-repo` e republicado. Este documento registra **como** foi
feito e o que observar se algum dia for preciso repetir.

Resultado desta execução: 13 commits, `main` + tag `v2.1.0`, e um clone novo do GitHub não contém
nenhum dos identificadores antigos (working tree **e** histórico).

## Por que foi necessário

O código, os manifestos e os dados já estavam limpos no commit corrente, mas o endereço do serviço
entrou no **primeiro** commit. Enquanto o repositório é privado isso é irrelevante; para torná-lo
público, o histórico também precisa estar limpo — senão `git log -p` entrega o que tiramos do código.

## Procedimento (repetível)

```bash
# 0. backup completo (o filter-repo apaga o origin e não tem volta sem backup)
git clone --mirror . ../analista-hub-backup-$(date +%Y%m%d).git

# 1. ferramenta
python -m pip install git-filter-repo

# 2. arquivo de substituições FORA do repositório, no formato:
#      valor_antigo==>valor_novo
#    uma linha por par. Mantenha em %TEMP%: se estiver dentro do repositório, o próprio
#    filter-repo reescreve o arquivo e ele vira um no-op (foi o que aconteceu na primeira tentativa).
$env:TEMP\substituicoes.txt

# 3. reescrita
python -m git_filter_repo --replace-text "$env:TEMP\substituicoes.txt" --force

# 4. conferir ANTES de publicar
git log --all --oneline -S "<valor_antigo>"     # deve sair vazio
git log --all -p | grep -c "<valor_antigo>"     # deve dar 0

# 5. publicar TUDO (branch + tags — só a branch não basta)
git remote add origin https://github.com/<usuario>/<repo>.git
git push --force origin main
git push --force --tags origin
```

## Duas armadilhas que custaram tempo

1. **Só `main` não basta**: a tag `v0.2.0` continuou apontando para o commit antigo no remoto, e
   `git clone` traz tags por padrão — ou seja, o histórico sujo voltava por ela. Aqui a tag antiga foi
   **apagada** (apontava para uma versão anterior à limpeza) e criada `v2.1.0` no commit limpo.
2. **O arquivo de substituições dentro do repositório** é reescrito junto e deixa de funcionar em uma
   segunda execução. Sempre em `%TEMP%`.

## Verificação final (o que realmente prova)

Não confie em `git log` local: clone do remoto e varra o clone.

```bash
git clone https://github.com/<usuario>/<repo>.git verif
cd verif
git rev-list --all --count                       # confere a contagem
git for-each-ref --format='%(refname:short)'      # confere que só há main e a tag nova
git log --all -p | grep -c "<valor_antigo>"       # deve dar 0
```

## O que não é apagável

- Forks, caches do GitHub e clones de terceiros feitos **enquanto** o repositório tinha o histórico
  antigo. A reescrita limpa o nosso lado, não o dos outros.
- Se o repositório já foi público alguma vez, trate os valores como conhecidos: rotacione as
  credenciais em vez de confiar na limpeza.
