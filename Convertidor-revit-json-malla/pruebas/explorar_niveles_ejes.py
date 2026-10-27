"""Exploración previa al desarrollo: dónde da rvt-rs los nombres de niveles y en qué unidades/ejes está su glTF."""
import collections, json, re, struct, sys

elements = json.load(open(sys.argv[1], encoding="utf-8"))
glb_path = sys.argv[2]

def field(e, name):
    for f in e.get("fields", []):
        if f.get("name") == name:
            return f.get("value", f.get("id"))

# 1. Campos que mencionan niveles y elementos que podrían ser niveles.
names = collections.Counter(f["name"] for e in elements for f in e.get("fields", []) if "level" in f["name"].lower() or "storey" in f["name"].lower())
print("CAMPOS DE NIVEL:", dict(names))
level_ids = collections.Counter(field(e, "m_levelId") for e in elements if field(e, "m_levelId") is not None)
print("IDS DE NIVEL USADOS:", dict(level_ids.most_common(10)))
byid = {e["id"]: e for e in elements}
for lid in list(level_ids)[:3]:
    e = byid.get(lid)
    print("  elemento con ese id:", e and (e["class_name"], [(f["name"], f.get("value")) for f in e.get("fields", [])][:8]))

# 2. glTF: escena, extras y la transformación del muro 139854 frente a su posición en rvt-elements.
data = open(glb_path, "rb").read()
off, g = 12, None
while off < struct.unpack_from("<I", data, 8)[0]:
    clen, ctype = struct.unpack_from("<II", data, off)
    if ctype == 0x4E4F534A: g = json.loads(data[off + 8: off + 8 + clen])
    off += 8 + clen
print("ASSET:", g.get("asset"), "· EXTRAS RAIZ:", json.dumps(g.get("extras"), ensure_ascii=False)[:600])
print("ESCENAS:", [(s.get("name"), len(s.get("nodes", []))) for s in g.get("scenes", [])], "· nodos raíz con hijos:", sum(1 for n in g["nodes"] if n.get("children")))
storeys = [n for n in g["nodes"] if (n.get("extras") or {}).get("ifcType") in ("IFCBUILDINGSTOREY", "IFCBUILDING", "IFCSITE")]
print("NODOS DE NIVEL EN GLB:", [(n.get("name"), (n.get("extras") or {})) for n in storeys[:8]])
target = 139854
w = byid.get(target)
print("MURO rvt-elements:", w and {k: field(w, k) for k in ("m_locationX", "m_locationY", "m_locationZ", "m_bboxWidth", "m_bboxDepth", "m_bboxHeight")})
for n in g["nodes"]:
    if re.search(rf"[:\-]{target}$", n.get("name", "")):
        acc = g["accessors"][g["meshes"][n["mesh"]]["primitives"][0]["attributes"]["POSITION"]]
        print("MURO glb:", n.get("name"), "matriz", n.get("matrix") or (n.get("translation"), n.get("rotation"), n.get("scale")), "min", acc.get("min"), "max", acc.get("max"))
        break
