"""Qué referencias a otros elementos da rvt-rs en montantes, paneles, tramos, mesetas, zancas y vigas (para los agregados)."""
import collections, json, sys

elements = json.load(open(sys.argv[1], encoding="utf-8"))
oracle = {str(o["elementId"]): o for o in json.load(open(sys.argv[2], encoding="utf-8"))}
by_class = collections.defaultdict(list)
for e in elements:
    by_class[e["class_name"]].append(e)
for cls in ("CurtainWallMullion", "CurtainWallPanel", "StairsRun", "StairsLanding", "StairsStringer", "Railing", "StructuralFraming"):
    sample = by_class.get(cls, [])
    if not sample:
        continue
    refs = collections.Counter()
    hits = collections.Counter()
    for e in sample:
        parent = (oracle.get(str(e["id"])) or {}).get("aggregateParentId")
        parent_eid = None
        if parent:
            # Oracle parents are Revit UniqueIds; the element id is the last hyphen group in hexadecimal.
            try: parent_eid = int(parent.split("-")[-1], 16)
            except ValueError: pass
        for f in e.get("fields", []):
            if f.get("kind") == "element_id" or f["name"].endswith("Id") or f["name"].endswith("Ids"):
                refs[f["name"]] += 1
                if parent_eid is not None and f.get("id") == parent_eid:
                    hits[f["name"]] += 1
    print(f"== {cls} ({len(sample)}) · campos de referencia: {dict(refs.most_common(12))}")
    print(f"   coinciden con el padre del oráculo: {dict(hits)}")
