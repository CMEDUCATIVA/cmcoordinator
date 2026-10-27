"""Resumen de un .glb de rvt-gltf: nodos, mallas, triángulos y cómo identifica cada elemento; y comparación con el vivo."""
import json, struct, sys, collections

def load(path):
    data = open(path, "rb").read()
    length = struct.unpack_from("<I", data, 8)[0]
    off, gltf = 12, None
    while off < length:
        clen, ctype = struct.unpack_from("<II", data, off)
        if ctype == 0x4E4F534A:
            gltf = json.loads(data[off + 8: off + 8 + clen])
        off += 8 + clen
    return gltf

g = load(sys.argv[1])
acc = g.get("accessors", [])
tri_by_mesh = []
for m in g.get("meshes", []):
    t = 0
    for p in m.get("primitives", []):
        if "indices" in p:
            t += acc[p["indices"]]["count"] // 3
        else:
            t += acc[p["attributes"]["POSITION"]]["count"] // 3
    tri_by_mesh.append(t)
nodes = g.get("nodes", [])
with_mesh = [n for n in nodes if "mesh" in n]
print(f"NODOS {len(nodes)} · con malla {len(with_mesh)} · mallas {len(tri_by_mesh)} · triángulos {sum(tri_by_mesh[n['mesh']] for n in with_mesh)}")
print("MUESTRA NODO", json.dumps(with_mesh[0], ensure_ascii=False)[:600] if with_mesh else "-")

def eid(n):
    ex = n.get("extras", {}) or {}
    for k in ("element_id", "elementId", "id", "revit_id"):
        if k in ex:
            return int(ex[k])
    # Node names are "<Class>-<ElementId>" with optional suffixes ("-0", "_part"): take the first number after a hyphen.
    # Most nodes are "<Family>:<Type>:<ElementId>"; a few are "<Class>-<ElementId>".
    import re
    name = n.get("name", "")
    m = re.search(r":(\d+)$", name) or re.search(r"-(\d+)", name)
    return int(m.group(1)) if m else None

tri_by_eid = collections.Counter()
for n in with_mesh:
    e = eid(n)
    if e is not None:
        tri_by_eid[e] += tri_by_mesh[n["mesh"]]
print(f"ELEMENTOS identificados en el glb {len(tri_by_eid)}")
print("NOMBRES (muestra):", [n.get("name") for n in with_mesh[100:108]])
print("CLASES IFC en extras:", dict(collections.Counter((n.get("extras") or {}).get("ifcType") for n in with_mesh).most_common(15)))
if len(sys.argv) > 2:
    live = {int(e["elementId"]): e for e in json.load(open(sys.argv[2], encoding="utf-8")) if str(e.get("elementId", "")).lstrip("-").isdigit()}
    both = [i for i in live if i in tri_by_eid and live[i]["triangles"] > 0]
    print(f"CON GEOMETRÍA en ambos {len(both)} de {sum(1 for e in live.values() if e['triangles'] > 0)} del vivo")
    ratio = collections.Counter()
    for i in both:
        r = tri_by_eid[i] / live[i]["triangles"]
        ratio["igual (±10 %)" if 0.9 <= r <= 1.1 else "menos" if r < 0.9 else "más"] += 1
    print("TRIÁNGULOS glb frente al vivo:", dict(ratio))
    by_cat = collections.defaultdict(lambda: [0, 0])
    for i in both:
        c = live[i]["category"]; by_cat[c][0] += tri_by_eid[i]; by_cat[c][1] += live[i]["triangles"]
    print("POR CATEGORÍA (glb/vivo):", ", ".join(f"{c}={a}/{b}" for c, (a, b) in sorted(by_cat.items(), key=lambda x: -x[1][1])[:12]))
