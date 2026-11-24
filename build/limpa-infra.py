"""Troca identificadores de infraestrutura por valores genéricos nos arquivos VERSIONADOS.

Não imprime os valores antigos nem os novos: só a contagem por arquivo.
"""
import pathlib
import subprocess

repo = pathlib.Path(r"C:\Users\antonio.neto\Documents\hermes\Atalhos de Analista")

# (antigo, novo) — novos são genéricos de documentação
SUBS = [
    ("10.0.0.1", "10.0.0.1"),
    ("10.0.0.1", "10.0.0.1"),
    ("Inventário", "Inventário"),
    ("cofre.md", "cofre.md"),
    ("OPERADORA-A", "OPERADORA-A"),
    ("OPERADORA-B", "OPERADORA-B"),
    ("GESTOR-A", "GESTOR-A"),
    ("OLT-A", "OLT-A"),
]

arquivos = subprocess.run(["git", "-C", str(repo), "ls-files"], capture_output=True, text=True, encoding="utf-8").stdout.splitlines()
total = 0
for rel in arquivos:
    p = repo / rel
    if not p.is_file():
        continue
    try:
        txt = p.read_text(encoding="utf-8")
    except (UnicodeDecodeError, OSError):
        continue
    original = txt
    n = 0
    for old, new in SUBS:
        c = txt.count(old)
        if c:
            txt = txt.replace(old, new)
            n += c
    if txt != original:
        p.write_text(txt, encoding="utf-8")
        print(f"  {rel}: {n} ocorrência(s)")
        total += n

print(f"total substituído: {total}")
