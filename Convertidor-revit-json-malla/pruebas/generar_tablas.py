"""Genera las tablas de datos del convertidor a partir de los oráculos de Cmcoordinator.

- tabla_clases.json: clase de rvt-rs -> categoría (como la nombra Revit en español), clase IFC y tipo predefinido,
  con el número de casos y el porcentaje de acuerdo. Solo se usa la opción mayoritaria.
- diccionario_parametros.json: id de parámetro de Revit -> nombre, grupo, almacenamiento y spec.

Uso (WSL): python3 -I generar_tablas.py <carpeta auditoría base> <carpeta destino>
"""
import collections, json, os, sys

base, dest = os.path.expanduser(sys.argv[1]), sys.argv[2]
HERE = os.path.dirname(os.path.abspath(__file__))
pairs = [("arquitectura-2026", "canal_9.json"), ("casa-2026", "canal_8.json"), ("estructura-2024", "canal_10.json")]

votes = collections.defaultdict(collections.Counter)
families = collections.defaultdict(collections.Counter)
params = {}
for case, oracle_file in pairs:
    rvt = {int(e["id"]): e for e in json.load(open(os.path.join(base, f"{case}.elements.json"), encoding="utf-8"))}
    oracle = json.load(open(os.path.join(HERE, "oraculos", oracle_file), encoding="utf-8"))
    for o in oracle:
        for pid, info in (o.get("paramNames") or {}).items():
            if pid not in params and info and info[0]:
                params[pid] = {"name": info[0], "group": info[1], "storage": info[2], "spec": info[3]}
        try:
            e = rvt.get(int(o["elementId"]))
        except (TypeError, ValueError):
            continue
        if e:
            votes[e["class_name"]][(o["category"], o.get("ifcClass"), o.get("ifcPredefined"))] += 1
            theirs = next((f.get("value") for f in e.get("fields", []) if f.get("name") == "m_family_name"), None)
            if theirs and o.get("familyName") and theirs != o["familyName"]:
                families[theirs][o["familyName"]] += 1

table = {}
for cls, counter in sorted(votes.items()):
    (category, ifc, predefined), n = counter.most_common(1)[0]
    total = sum(counter.values())
    table[cls] = {"category": category, "ifcClass": ifc, "ifcPredefined": predefined,
                  "casos": total, "acuerdo": round(100 * n / total, 1),
                  "alternativas": [list(k) + [v] for k, v in counter.most_common(4)[1:]]}
os.makedirs(dest, exist_ok=True)
json.dump(table, open(os.path.join(dest, "tabla_clases.json"), "w", encoding="utf-8"), ensure_ascii=False, indent=1)
json.dump(params, open(os.path.join(dest, "diccionario_parametros.json"), "w", encoding="utf-8"), ensure_ascii=False, indent=0)
# System families: rvt-rs names them in English (Basic Wall), Revit in the project's language (Muro básico).
family_map = {k: c.most_common(1)[0][0] for k, c in families.items() if c.most_common(1)[0][1] >= 2}
json.dump(family_map, open(os.path.join(dest, "tabla_familias.json"), "w", encoding="utf-8"), ensure_ascii=False, indent=1)
print(f"FAMILIAS {family_map}")
print(f"CLASES {len(table)} · PARÁMETROS {len(params)}")
for cls, v in table.items():
    alt = f" · otras: {v['alternativas']}" if v["acuerdo"] < 100 else ""
    print(f"  {cls:26} → {v['category']:30} {v['ifcClass']} {v['ifcPredefined'] or ''} ({v['casos']}, {v['acuerdo']} %){alt}")
