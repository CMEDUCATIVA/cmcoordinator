"""Auditoría de rvt-rs (línea base del convertidor) sobre los .rvt de Revit 2023-2027.

Para cada archivo del corpus ejecuta rvt-rs (versión, elementos, glTF con cajas y con mallas guardadas),
mide tiempo y memoria y, si hay oráculo (modelo en vivo de Cmcoordinator), compara elemento a elemento
por ElementId. Escribe auditoria.json y un resumen por consola.

Uso (WSL): python3 -I auditoria.py <carpeta de salida>
"""
import collections, json, os, re, struct, subprocess, sys, time

BIN = os.path.expanduser("~/rvtrs/rvt-rs-v0.7.0-x86_64-unknown-linux-musl")
HERE = os.path.dirname(os.path.abspath(__file__))
ORACLES = os.path.join(HERE, "oraculos")
SAMPLES = "/mnt/d/LIBRE PRUEBA/cad2data-Revit-IFC-DWG-DGN-pipeline-with-conversion-validation-qto-main/cad2data-Revit-IFC-DWG-DGN-pipeline-with-conversion-validation-qto-main/Sample_Projects"
DOWNLOADS = "/mnt/c/Users/Administrador/Downloads"
EDU = "/mnt/d/1-NUBE/ALMACENAMIENTO CENTRAL/CM/1-2-CMEDUCATIVA/1-2-6-Docentes/ABEL BAZAN/PROYECTO VIVIENDA  UNIFAMILIAR/WIP/ESTRUCTURA/MODELO"

CORPUS = [
    ("casa-2023", f"{SAMPLES}/2023 racbasicsampleproject.0001.rvt", "canal_8.json"),
    ("arquitectura-2024", f"{DOWNLOADS}/REVIT/Arquitectura (1).0001.rvt", "canal_9.json"),
    ("estructura-2024", f"{DOWNLOADS}/REVIT/Estructura.rvt", "canal_10.json"),
    ("mep-2024", f"{DOWNLOADS}/REVIT/MEP.rvt", None),
    ("vivienda-est-2025", f"{EDU}/123456-CME-PER-EST-M3D-001.rvt", None),
    ("arquitectura-2026", f"{DOWNLOADS}/REVIT/Arquitectura (1).rvt", "canal_9.json"),
    ("casa-2026", f"{SAMPLES}/2023 racbasicsampleproject.rvt", "canal_8.json"),
    ("rstadvanced-2026", f"{SAMPLES}/2022 rstadvancedsampleproject.rvt", None),
]

def run(cmd, out_path=None):
    """Runs a command under /usr/bin/time; returns (exit, seconds, max_rss_mb, stderr tail)."""
    timing = "/tmp/rvtrs_time.txt"
    t0 = time.time()
    with open(out_path or os.devnull, "wb") as out:
        p = subprocess.run(["/usr/bin/time", "-f", "%e %M", "-o", timing] + cmd, stdout=out, stderr=subprocess.PIPE)
    secs, rss = (open(timing).read().split() + ["0", "0"])[:2]
    return p.returncode, float(secs or time.time() - t0), int(rss) // 1024, p.stderr.decode(errors="ignore")[-400:]

def field(e, name):
    for f in e.get("fields", []):
        if f.get("name") == name:
            return f.get("value", f.get("id"))
    return None

def load_glb(path):
    data = open(path, "rb").read()
    length = struct.unpack_from("<I", data, 8)[0]
    off = 12
    while off < length:
        clen, ctype = struct.unpack_from("<II", data, off)
        if ctype == 0x4E4F534A:
            return json.loads(data[off + 8: off + 8 + clen])
        off += 8 + clen
    return {}

def glb_by_element(path):
    """Triangles and IFC type per ElementId from a rvt-gltf .glb (nodes '<Family>:<Type>:<id>' or '<Class>-<id>')."""
    g = load_glb(path)
    acc, meshes = g.get("accessors", []), g.get("meshes", [])
    tris = []
    for m in meshes:
        t = 0
        for p in m.get("primitives", []):
            idx = p.get("indices")
            t += (acc[idx]["count"] if idx is not None else acc[p["attributes"]["POSITION"]]["count"]) // 3
        tris.append(t)
    per = collections.defaultdict(lambda: {"tris": 0, "ifc": None})
    for n in g.get("nodes", []):
        if "mesh" not in n:
            continue
        name = n.get("name", "")
        m = re.search(r":(\d+)$", name) or re.search(r"-(\d+)", name)
        if not m:
            continue
        e = per[int(m.group(1))]
        e["tris"] += tris[n["mesh"]]
        e["ifc"] = e["ifc"] or (n.get("extras") or {}).get("ifcType")
    return per

def same(a, b):
    return a is not None and b is not None and str(a).strip().lower() == str(b).strip().lower()

def compare(rvt, boxes, saved, oracle):
    R = {int(e["id"]): e for e in rvt}
    L = {int(e["elementId"]): e for e in oracle if str(e.get("elementId", "")).lstrip("-").isdigit()}
    both = [i for i in L if i in R]
    res = {"vivo": len(L), "emparejados": len(both), "solo_vivo": dict(collections.Counter(L[i]["category"] for i in L if i not in R).most_common(10))}
    res["categoria_1a1"] = len({(L[i]["category"], R[i]["class_name"]) for i in both}) <= len({L[i]["category"] for i in both}) + 2
    have_t = [i for i in both if field(R[i], "m_type_name") is not None]
    res["tipo"] = {"presente": len(have_t), "coincide": sum(same(L[i].get("typeName"), field(R[i], "m_type_name")) for i in have_t)}
    votes = collections.defaultdict(collections.Counter)
    for i in both:
        lid = field(R[i], "m_levelId")
        if lid is not None and L[i].get("levelName"):
            votes[lid][L[i]["levelName"]] += 1
    mapping = {k: c.most_common(1)[0][0] for k, c in votes.items()}
    have_l = [i for i in both if field(R[i], "m_levelId") is not None]
    res["nivel"] = {"presente": len(have_l), "coincide": sum(mapping.get(field(R[i], "m_levelId")) == L[i].get("levelName") for i in have_l)}
    ifc_ok = ifc_have = 0
    for i in both:
        ours = (L[i].get("ifcClass") or "").upper()
        theirs = (saved.get(i, {}).get("ifc") or boxes.get(i, {}).get("ifc") or "").upper()
        if ours and theirs:
            ifc_have += 1; ifc_ok += ours == theirs
    res["clase_ifc"] = {"comparables": ifc_have, "coincide": ifc_ok}
    live_ids = common = 0
    for i in both:
        lp = {k for k in L[i]["params"] if k.lstrip("-").isdigit()}
        rp = {str(p["parameter_id"]) for p in R[i].get("parameters", []) if p.get("parameter_id") is not None}
        live_ids += len(lp); common += len(lp & rp)
    res["parametros"] = {"vivo": live_ids, "mismos_ids": common}
    geo_live = [i for i in both if L[i]["triangles"] > 0]
    res["geometria"] = {"vivo_con_malla": len(geo_live), "rvtrs_con_malla": sum(1 for i in geo_live if boxes.get(i, {}).get("tris") or saved.get(i, {}).get("tris")),
                        "tri_vivo": sum(L[i]["triangles"] for i in geo_live),
                        "tri_cajas": sum(boxes.get(i, {}).get("tris", 0) for i in geo_live),
                        "tri_guardadas": sum(saved.get(i, {}).get("tris", 0) for i in geo_live)}
    return res

def main(outdir):
    os.makedirs(outdir, exist_ok=True)
    report = []
    for name, path, oracle_file in CORPUS:
        item = {"nombre": name, "archivo": os.path.basename(path)}
        print(f"######## {name}", flush=True)
        if not os.path.exists(path):
            item["error"] = "no existe"; report.append(item); print("   no existe"); continue
        info = subprocess.run([f"{BIN}/rvt-info", "--json", path], capture_output=True, text=True)
        try:
            item["version"] = json.loads(info.stdout).get("version") or json.loads(info.stdout).get("revit_version")
        except Exception:
            m = re.search(r"20\d\d", info.stdout); item["version"] = m.group(0) if m else "?"
        item["mb"] = round(os.path.getsize(path) / 1048576, 1)
        ej = os.path.join(outdir, f"{name}.elements.json")
        code, s, mb, err = run([f"{BIN}/rvt-elements", path], ej)
        item["elementos"] = {"salida": code, "s": s, "mb_ram": mb, "error": err if code else None}
        rvt = json.load(open(ej)) if code == 0 else []
        item["elementos"]["total"] = len(rvt)
        item["elementos"]["por_clase"] = dict(collections.Counter(e["class_name"] for e in rvt).most_common(12))
        item["elementos"]["con_tipo"] = sum(field(e, "m_type_name") is not None for e in rvt)
        item["elementos"]["con_nivel"] = sum(field(e, "m_levelId") is not None for e in rvt)
        item["elementos"]["parametros"] = sum(len(e.get("parameters", [])) for e in rvt)
        boxes, saved = {}, {}
        for key, extra in (("glb_cajas", []), ("glb_guardadas", ["--saved-meshes"])):
            glb = os.path.join(outdir, f"{name}.{key}.glb")
            code, s, mb, err = run([f"{BIN}/rvt-gltf"] + extra + [path, "-o", glb])
            item[key] = {"salida": code, "s": s, "mb_ram": mb, "mb_archivo": round(os.path.getsize(glb) / 1048576, 1) if code == 0 and os.path.exists(glb) else None}
            if code == 0:
                per = glb_by_element(glb)
                item[key]["elementos_con_malla"] = len(per)
                item[key]["triangulos"] = sum(v["tris"] for v in per.values())
                (boxes if key == "glb_cajas" else saved).update(per)
        if oracle_file and rvt:
            oracle = json.load(open(os.path.join(ORACLES, oracle_file), encoding="utf-8"))
            item["comparacion"] = compare(rvt, boxes, saved, oracle)
        report.append(item)
        print(json.dumps(item, ensure_ascii=False)[:1500], flush=True)
    json.dump(report, open(os.path.join(outdir, "auditoria.json"), "w", encoding="utf-8"), ensure_ascii=False, indent=1)

if __name__ == "__main__":
    main(os.path.expanduser(sys.argv[1] if len(sys.argv) > 1 else "~/rvtrs/auditoria"))
