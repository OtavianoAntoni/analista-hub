"""Divide a base da Consulta SSE em duas:

  • pública  (versionada)  -> cidade/UF/sigla, sem NM/OLT/VPN
  • interna  (local, ignorada pelo git) -> só as 161 cidades com rede

Escreve:
  %LOCALAPPDATA%\\AnalistaHub\\sse-rede.local.json   (novo, com as redes)
  %LOCALAPPDATA%\\AnalistaHub\\sse-cidades.json      (sobrescrito, sem redes)
  src\\AnalistaHub\\data\\sse-cidades.json            (sobrescrito, sem redes)

Uso: python build/split-sse-local.py <raiz_do_repo>
"""
import json
import os
import pathlib
import sys

repo = pathlib.Path(sys.argv[1] if len(sys.argv) > 1 else ".")
data_dir = pathlib.Path(os.environ["LOCALAPPDATA"]) / "AnalistaHub"
user_base = data_dir / "sse-cidades.json"
repo_base = repo / "src" / "AnalistaHub" / "data" / "sse-cidades.json"
rede_out = data_dir / "sse-rede.local.json"

origem = pathlib.Path(sys.argv[2]) if len(sys.argv) > 2 else (user_base if user_base.exists() else repo_base)
base = json.loads(origem.read_text(encoding="utf-8"))
cidades = base["cidades"]

com_rede = [c for c in cidades if c.get("rows")]
linhas = sum(len(c["rows"]) for c in com_rede)

# 1) arquivo interno: só o que é de rede. A chave leva a UF porque o nome sozinho colide
#    (existem municípios homônimos em estados diferentes).
rede = {
    "schema": 1,
    "generated": base.get("generated", ""),
    "nota": "Redes internas (NM/OLT/VPN), chave = nome sem acento + UF. Fora do controle de versão.",
    "cidades": [
        {"k": c["k"], "uf": c["uf"], "n": c["n"], "rows": c["rows"]}
        for c in com_rede
    ],
}
rede_out.write_text(json.dumps(rede, ensure_ascii=False, separators=(",", ":")), encoding="utf-8")

# 2) base pública: sem as redes, sem marcação de quem tinha planilha
publico = json.loads(origem.read_text(encoding="utf-8"))
for c in publico["cidades"]:
    c["rows"] = []
    c["src"] = "anatel"
    c.pop("sigla_sse", None)
publico["total_com_acesso"] = 0
publico["total_linhas"] = 0
publico["nota"] = ("Base pública: cadastro de municípios + sigla Anatel. "
                   "As redes internas (NM/OLT/VPN) ficam em sse-rede.local.json, na pasta de dados "
                   "do usuário, e nunca são versionadas.")

texto = json.dumps(publico, ensure_ascii=False, separators=(",", ":"))
user_base.write_text(texto, encoding="utf-8")
repo_base.write_text(texto, encoding="utf-8")

print(f"origem lida: {'dados do usuário' if origem == user_base else 'repositório'}")
print(f"cidades: {len(publico['cidades'])} | com rede movidas para o arquivo local: {len(com_rede)} | linhas: {linhas}")
print(f"interno : {rede_out} ({rede_out.stat().st_size / 1024:.0f} KB)")
print(f"público : {repo_base} ({repo_base.stat().st_size / 1024:.0f} KB)")
print(f"público : {user_base} ({user_base.stat().st_size / 1024:.0f} KB)")
