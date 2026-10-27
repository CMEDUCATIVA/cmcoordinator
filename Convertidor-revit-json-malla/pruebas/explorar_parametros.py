"""Para cada clase, qué parámetros (id, ámbito) trae rvt-rs y qué Psets/cantidades tiene el oráculo; y dónde está el IFC GUID."""
import collections, json, sys

elements = json.load(open(sys.argv[1], encoding="utf-8"))
oracle = {str(o["elementId"]): o for o in json.load(open(sys.argv[2], encoding="utf-8"))}
names = json.load(open(sys.argv[3], encoding="utf-8"))
by_class = collections.defaultdict(list)
for e in elements:
    by_class[e["class_name"]].append(e)

guid_like = collections.Counter()
for e in elements:
    o = oracle.get(str(e["id"]))
    if not o or not o.get("ifcGuid"): continue
    for p in e.get("parameters", []):
        if p.get("value") == o["ifcGuid"]: guid_like[(p.get("parameter_id"), p.get("level"))] += 1
    for f in e.get("fields", []):
        if f.get("value") == o["ifcGuid"]: guid_like[("campo", f["name"])] += 1
print("DÓNDE ESTÁ EL IFC GUID:", dict(guid_like.most_common(5)))
sample = next(e for e in elements if e["class_name"] == "Wall")
print("PARÁMETRO -1019000 en un muro:", [p for p in sample.get("parameters", []) if p.get("parameter_id") == -1019000][:2])

for cls in ("CurtainWallMullion", "CurtainWallPanel", "StructuralColumn", "Door", "Ceiling", "StructuralFraming"):
    group = by_class.get(cls, [])
    ids = collections.Counter((p.get("parameter_id"), p.get("level"), p.get("storage_type")) for e in group for p in e.get("parameters", []))
    print(f"== {cls} ({len(group)}) · parámetros más comunes:")
    for (pid, scope, storage), n in ids.most_common(14):
        print(f"     {pid} {scope} {storage} ×{n} · {(names.get(str(pid)) or {}).get('name')}")
    o = next((oracle.get(str(e["id"])) for e in group if oracle.get(str(e["id"]))), None)
    if o: print("   oráculo:", json.dumps({k: v for k, v in (o.get("psets") or {}).items() if k.startswith("qto")}, ensure_ascii=False)[:400])
