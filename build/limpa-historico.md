# Limpeza de histórico — deixar o repositório público

O código, os manifestos e os dados **já estão limpos** no commit atual. O que ainda carrega
identificador de infraestrutura é o **histórico**: o endereço do serviço entrou no primeiro commit.

Enquanto o repositório for **privado**, nada precisa ser feito. Para torná-lo **público**, o histórico
tem de ser reescrito — senão qualquer pessoa lê `git log -p` e vê o que tiramos do código.

## Pré-requisitos

```
pip install --user git-filter-repo
```

## 1. Substituições

Arquivo `build/replacements.txt` (cada linha: `antigo==>novo`):

```
10.0.0.1==>10.0.0.1
10.0.0.1==>10.0.0.1
Inventário==>Inventário
cofre.md==>cofre.md
OPERADORA-A==>OPERADORA-A
OPERADORA-B==>OPERADORA-B
GESTOR-A==>GESTOR-A
OLT-A==>OLT-A
```

## 2. Backup e reescrita

```bash
# backup completo antes (o filter-repo apaga o origin)
git clone --mirror . ../analista-hub-backup.git

# reescreve (precisa rodar na raiz do repo, com o clone limpo)
python -m git_filter_repo --replace-text build/replacements.txt --force

# confere que não sobrou nada
git log --all -p | grep -c "201\.20\.85\|10\.168\.176"
# esperado: 0
```

## 3. Publicar

```bash
git remote add origin https://github.com/<usuario>/analista-hub.git
git push --force --all origin
git push --force --tags origin
```

## 4. Depois

- Todo clone antigo fica inválido (SHAs mudam). Quem tiver cópia precisa clonar de novo.
- Se o repositório já tiver sido público em algum momento, trate os endereços como conhecidos:
  o histórico de terceiros (forks, cache do GitHub, `git clone` de alguém) não é apagável por nós.
- O `config.local.json` e o `sse-rede.local.json` **nunca** estiveram no repositório — a auditoria
  confirmou isso em todos os commits.

## Estado da auditoria (working tree)

| Verificação | Resultado |
|---|---|
| JWT / chave / `.env` / cofre no histórico | nenhum |
| Endereço interno / nome de rede / nome de empresa em arquivo versionado | nenhum |
| `config.local.json`, `sse-rede.local.json`, `*token*.json` no índice | nenhum (`.gitignore`) |
| Arquivos versionados que ainda casam com o padrão de IP privado | só exemplos genéricos (`10.0.0.1`) |
