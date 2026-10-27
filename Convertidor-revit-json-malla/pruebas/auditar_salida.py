"""Compara la salida de cmconv (paquetes protocolo 3) con el oráculo de Cmcoordinator, elemento a elemento por ElementId.

Uso (WSL): python3 -I auditar_salida.py <carpeta de salida de cmconv> <oráculo .json> [--json resultado.json]
"""
import collections, json, os, sys

out, oracle_path = os.path.expanduser(sys.argv[1]), sys.argv[2]
manifest = json.load(open(os.path.join(out, "manifest.json"), encoding="utf-8"))
catalog, elements, shapes = None, {}, {}
for name in sorted(os.listdir(os.path.join(out, "parts"))):
    part = json.load(open(os.path.join(out, "parts", name), encoding="utf-8"))
    if part["kind"] == "catalog":
        catalog = part["bim"]
    elif part["kind"] == "elements":
        geometries = part.get("geometries") or {}
        for e in part["updated"]:
            tris = 0
            for m in e["meshes"]:
                idx = geometries.get(m["geometry"], {}).get("indices") if "geometry" in m else m.get("indices")
                tris += len(idx or []) // 3
            e["_tris"] = tris
            elements[e["elementId"]] = e

oracle = {str(o["elementId"]): o for o in json.load(open(oracle_path, encoding="utf-8"))}
both = [i for i in oracle if i in elements]
levels = catalog.get("levels", {}); types = catalog.get("types", {})

def ratio(ok, total): return f"{ok}/{total} ({100 * ok / max(1, total):.1f} %)"
res = collections.OrderedDict()
res["emparejados"] = ratio(len(both), len(oracle))
res["solo_oraculo"] = dict(collections.Counter(oracle[i]["category"] for i in oracle if i not in elements).most_common(10))
res["solo_convertidor"] = dict(collections.Counter(elements[i]["category"] for i in elements if i not in oracle).most_common(10))
res["categoria"] = ratio(sum(elements[i]["category"] == oracle[i]["category"] for i in both), len(both))
res["clase_ifc"] = ratio(sum(elements[i]["bim"]["ifcClass"] == oracle[i].get("ifcClass") for i in both), len(both))
res["tipo_predefinido"] = ratio(sum((elements[i]["bim"].get("ifcPredefinedType") or None) == (oracle[i].get("ifcPredefined") or None) for i in both), len(both))
diff_class = collections.Counter((oracle[i]["category"], oracle[i].get("ifcClass"), oracle[i].get("ifcPredefined"), elements[i]["bim"]["ifcClass"], elements[i]["bim"].get("ifcPredefinedType"))
                                 for i in both if (elements[i]["bim"]["ifcClass"], elements[i]["bim"].get("ifcPredefinedType") or None) != (oracle[i].get("ifcClass"), oracle[i].get("ifcPredefined") or None))
res["diferencias_clase"] = [list(k) + [v] for k, v in diff_class.most_common(8)]
lvl = [i for i in both if oracle[i].get("levelName")]
res["nivel"] = ratio(sum(levels.get(elements[i]["bim"].get("levelId") or "", {}).get("name") == oracle[i]["levelName"] for i in lvl), len(lvl))
def level_of(i): return levels.get(elements[i]["bim"].get("levelId") or "", {}).get("name")
res["diferencias_nivel"] = [list(k) + [v] for k, v in collections.Counter((oracle[i]["category"], oracle[i]["levelName"], level_of(i)) for i in lvl if level_of(i) != oracle[i]["levelName"]).most_common(8)]
typ = [i for i in both if oracle[i].get("typeName")]
res["tipo"] = ratio(sum(types.get(elements[i]["bim"].get("typeId") or "", {}).get("name") == oracle[i]["typeName"] for i in typ), len(typ))
fam = [i for i in both if oracle[i].get("familyName")]
res["familia"] = ratio(sum(types.get(elements[i]["bim"].get("typeId") or "", {}).get("family") == oracle[i]["familyName"] for i in fam), len(fam))
op = sum(len(oracle[i].get("params") or {}) for i in both)
cp = sum(len(elements[i]["bim"]["parameters"]) for i in both)
same_ids = sum(len(set(oracle[i].get("params") or {}) & {p["id"] for p in elements[i]["bim"]["parameters"]}) for i in both)
named = sum(1 for i in both for p in elements[i]["bim"]["parameters"] if not p["name"].startswith("Parámetro "))
res["parametros"] = {"oraculo": op, "convertidor": cp, "mismos_ids": ratio(same_ids, op), "con_nombre": ratio(named, cp)}
def close(a, b):
    if isinstance(a, bool) or isinstance(b, bool): return a == b
    if isinstance(a, (int, float)) and isinstance(b, (int, float)):
        return abs(a - b) <= max(1e-4, 1e-3 * max(abs(a), abs(b)))
    return str(a) == str(b)
sets_o = sets_c = props_o = props_found = props_equal = 0
missing_sets, wrong_props = collections.Counter(), collections.Counter()
for i in both:
    ours = {f"{s['kind']}:{s['name']}": {p["name"]: p.get("value") for p in s["properties"]} for s in elements[i]["bim"].get("ifcSets") or []}
    sets_c += len(ours)
    theirs = oracle[i].get("psets") or {}
    if isinstance(theirs, list): theirs = {}
    for key, props in theirs.items():
        sets_o += 1
        if key not in ours:
            missing_sets[key] += 1
        for name, value in props.items():
            props_o += 1
            if key in ours and name in ours[key]:
                props_found += 1
                if close(ours[key][name], value): props_equal += 1
                else: wrong_props[f"{key}.{name}"] += 1
res["conjuntos_ifc"] = {"oraculo": sets_o, "convertidor": sets_c, "propiedades_presentes": ratio(props_found, props_o),
                        "valores_iguales": ratio(props_equal, props_found),
                        "conjuntos_que_faltan": dict(missing_sets.most_common(8)), "valores_distintos": dict(wrong_props.most_common(8))}
agg_o = [i for i in both if oracle[i].get("aggregateParentId")]
def oracle_parent(i):
    try: return str(int(oracle[i]["aggregateParentId"].split("-")[-1], 16))
    except (ValueError, AttributeError): return None
res["agregados"] = {"oraculo": len(agg_o),
                    "mismo_padre": ratio(sum(1 for i in agg_o if (elements[i]["bim"].get("aggregateParentId") or "").removeprefix("rvt-") == oracle_parent(i)), len(agg_o)),
                    "padres_de_mas": sum(1 for i in both if elements[i]["bim"].get("aggregateParentId") and not oracle[i].get("aggregateParentId"))}
guid = [i for i in both if oracle[i].get("ifcGuid")]
res["ifc_guid"] = ratio(sum(elements[i]["bim"].get("ifcGuid") == oracle[i]["ifcGuid"] for i in guid), len(guid))
geo = [i for i in both if oracle[i]["triangles"] > 0]
res["geometria"] = {"con_malla": ratio(sum(elements[i]["_tris"] > 0 for i in geo), len(geo)),
                    "triangulos_oraculo": sum(oracle[i]["triangles"] for i in geo),
                    "triangulos_convertidor": sum(elements[i]["_tris"] for i in geo)}
res["paquetes"] = manifest["chunkCount"]; res["mb"] = round(manifest["byteLength"] / 1048576, 1)
print(json.dumps(res, ensure_ascii=False, indent=1))
if "--json" in sys.argv:
    json.dump(res, open(sys.argv[sys.argv.index("--json") + 1], "w", encoding="utf-8"), ensure_ascii=False, indent=1)
