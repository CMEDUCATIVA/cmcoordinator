"""Triángulos por categoría de una salida de cmconv frente al oráculo. Uso: triangulos_por_categoria.py <salida> [oráculo.json]"""
import json, glob, sys, collections
out = collections.defaultdict(lambda: [0, 0])
for p in sorted(glob.glob(sys.argv[1] + "/parts/*.json")):
    part = json.load(open(p, encoding="utf-8"))
    if part.get("kind") != "elements": continue
    shapes = part.get("geometries") or {}
    for e in part["updated"]:
        t = sum(len(m.get("indices") or shapes[m["geometry"]]["indices"]) // 3 for m in e["meshes"])
        out[e.get("category")][0] += 1; out[e.get("category")][1] += t
ora = collections.defaultdict(lambda: [0, 0])
if len(sys.argv) > 2:
    for e in json.load(open(sys.argv[2], encoding="utf-8")):
        ora[e["category"]][0] += 1; ora[e["category"]][1] += e.get("triangles") or 0
print(f"{'categoría':32} {'n':>6} {'tri cmconv':>11} {'tri/elem':>9} {'tri vivo':>9} {'vivo/elem':>9}")
for k, (n, t) in sorted(out.items(), key=lambda kv: -kv[1][1])[:15]:
    on, ot = ora.get(k, [0, 0])
    print(f"{str(k)[:32]:32} {n:6} {t:11} {t // max(n, 1):9} {ot:9} {ot // max(on, 1):9}")
