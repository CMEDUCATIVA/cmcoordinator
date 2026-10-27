"""Compara la salida de cmconv con el IFC que exportó Revit del mismo archivo, elemento a elemento por GlobalId.

Sirve de oráculo cuando no hay modelo en vivo de Cmcoordinator (archivos públicos de GitHub).
Uso (WSL): python3 -I auditar_contra_ifc.py <carpeta de salida de cmconv> <archivo.ifc> [--json resultado.json]
"""
import collections, json, os, re, sys

out, ifc_path = os.path.expanduser(sys.argv[1]), sys.argv[2]

# --- Salida de cmconv
catalog, elements = None, {}
for name in sorted(os.listdir(os.path.join(out, "parts"))):
    part = json.load(open(os.path.join(out, "parts", name), encoding="utf-8"))
    if part["kind"] == "catalog":
        catalog = part["bim"]
    elif part["kind"] == "elements":
        for e in part["updated"]:
            elements[e["id"]] = e
levels = catalog.get("levels", {}) if catalog else {}
by_guid = {e["bim"].get("ifcGuid"): e for e in elements.values() if e["bim"].get("ifcGuid")}

# --- IFC (STEP): argumentos de primer nivel respetando cadenas y paréntesis
def split_args(text):
    args, depth, cur, quoted = [], 0, [], False
    i = 0
    while i < len(text):
        c = text[i]
        if quoted:
            cur.append(c)
            if c == "'":
                if i + 1 < len(text) and text[i + 1] == "'": cur.append("'"); i += 1
                else: quoted = False
        elif c == "'": quoted = True; cur.append(c)
        elif c == "(": depth += 1; cur.append(c)
        elif c == ")": depth -= 1; cur.append(c)
        elif c == "," and depth == 0: args.append("".join(cur).strip()); cur = []
        else: cur.append(c)
        i += 1
    args.append("".join(cur).strip())
    return args

def unquote(a): return a[1:-1].replace("''", "'") if a.startswith("'") else None
def refs(a): return re.findall(r"#(\d+)", a)

entities = {}
text = open(ifc_path, encoding="utf-8", errors="replace").read()
for m in re.finditer(r"#(\d+)\s*=\s*([A-Z0-9_]+)\s*\((.*?)\);\s*(?=#\d+\s*=|ENDSEC)", text, re.S):
    entities[m.group(1)] = (m.group(2), split_args(m.group(3)))

storey_of, parent_of = {}, {}
for eid, (t, a) in entities.items():
    if t == "IFCRELCONTAINEDINSPATIALSTRUCTURE":
        for r in refs(a[4]): storey_of[r] = a[5].lstrip("#")
    elif t == "IFCRELAGGREGATES":
        for r in refs(a[5]): parent_of[r] = a[4].lstrip("#")

def storey_name(eid, seen=0):
    while eid and seen < 10:
        if eid in storey_of:
            s = entities.get(storey_of[eid])
            return unquote(s[1][2]) if s and s[0] == "IFCBUILDINGSTOREY" else None
        eid = parent_of.get(eid); seen += 1
    return None

SPATIAL = {"IFCPROJECT", "IFCSITE", "IFCBUILDING", "IFCBUILDINGSTOREY", "IFCSPACE", "IFCOPENINGELEMENT", "IFCZONE", "IFCGRID", "IFCANNOTATION"}
ifc = {}
for eid, (t, a) in entities.items():
    if t in SPATIAL or t.startswith(("IFCREL", "IFCPROPERTY", "IFCELEMENTQUANTITY")) or t.endswith(("TYPE", "STYLE")):
        continue
    guid = unquote(a[0]) if a else None
    if not guid or len(guid) != 22: continue
    if eid not in storey_of and eid not in parent_of: continue
    predefined = a[-1][1:-1] if a[-1].startswith(".") and a[-1].endswith(".") else None
    ifc[guid] = {"class": "Ifc" + t[3:].title(), "raw": t, "predefined": predefined if predefined not in ("NOTDEFINED", None) else None,
                 "storey": storey_name(eid), "name": unquote(a[2]) if len(a) > 2 else None}

schema = (re.search(r"FILE_SCHEMA\s*\(\s*\(\s*'([^']+)'", text) or [None, "?"])[1].upper()
# IFC2X3 has no typed distribution classes: an IFC4 subtype counts as its IFC2X3 supertype.
IFC2X3_SUPERTYPE = {
    "IFCDUCTSEGMENT": "IFCFLOWSEGMENT", "IFCPIPESEGMENT": "IFCFLOWSEGMENT", "IFCCABLECARRIERSEGMENT": "IFCFLOWSEGMENT", "IFCCABLESEGMENT": "IFCFLOWSEGMENT",
    "IFCDUCTFITTING": "IFCFLOWFITTING", "IFCPIPEFITTING": "IFCFLOWFITTING", "IFCCABLECARRIERFITTING": "IFCFLOWFITTING", "IFCJUNCTIONBOX": "IFCFLOWFITTING",
    "IFCAIRTERMINAL": "IFCFLOWTERMINAL", "IFCSANITARYTERMINAL": "IFCFLOWTERMINAL", "IFCLIGHTFIXTURE": "IFCFLOWTERMINAL", "IFCELECTRICAPPLIANCE": "IFCFLOWTERMINAL",
    "IFCOUTLET": "IFCFLOWTERMINAL", "IFCFIRESUPPRESSIONTERMINAL": "IFCFLOWTERMINAL", "IFCSTACKTERMINAL": "IFCFLOWTERMINAL", "IFCWASTETERMINAL": "IFCFLOWTERMINAL",
    "IFCAUDIOVISUALAPPLIANCE": "IFCFLOWTERMINAL", "IFCCOMMUNICATIONSAPPLIANCE": "IFCFLOWTERMINAL",
    "IFCVALVE": "IFCFLOWCONTROLLER", "IFCDAMPER": "IFCFLOWCONTROLLER", "IFCSWITCHINGDEVICE": "IFCFLOWCONTROLLER", "IFCPROTECTIVEDEVICE": "IFCFLOWCONTROLLER",
    "IFCPUMP": "IFCFLOWMOVINGDEVICE", "IFCFAN": "IFCFLOWMOVINGDEVICE", "IFCTANK": "IFCFLOWSTORAGEDEVICE", "IFCELECTRICDISTRIBUTIONBOARD": "IFCFLOWCONTROLLER",
    "IFCALARM": "IFCDISTRIBUTIONCONTROLELEMENT", "IFCSENSOR": "IFCDISTRIBUTIONCONTROLELEMENT", "IFCACTUATOR": "IFCDISTRIBUTIONCONTROLELEMENT",
    "IFCFURNITURE": "IFCFURNISHINGELEMENT", "IFCSYSTEMFURNITUREELEMENT": "IFCFURNISHINGELEMENT",
}

def same_class(ours, raw):
    o = ours.upper()
    if schema == "IFC2X3": o = IFC2X3_SUPERTYPE.get(o, o)
    return o == raw or o + "STANDARDCASE" == raw or (raw.endswith("STANDARDCASE") and o == raw[:-12])

def ratio(ok, total): return f"{ok}/{total} ({100 * ok / max(1, total):.1f} %)"
both = [g for g in ifc if g in by_guid]
res = collections.OrderedDict()
res["esquema_ifc"] = schema
res["ifc_elementos"] = len(ifc)
res["emparejados"] = ratio(len(both), len(ifc))
res["solo_ifc"] = dict(collections.Counter(ifc[g]["raw"] for g in ifc if g not in by_guid).most_common(10))
res["solo_convertidor"] = dict(collections.Counter(e["category"] for g, e in by_guid.items() if g not in ifc).most_common(10))
res["sin_guid_convertidor"] = sum(1 for e in elements.values() if not e["bim"].get("ifcGuid"))
res["clase_ifc"] = ratio(sum(same_class(by_guid[g]["bim"]["ifcClass"], ifc[g]["raw"]) for g in both), len(both))
res["diferencias_clase"] = [list(k) + [v] for k, v in collections.Counter(
    (by_guid[g]["category"], ifc[g]["raw"], by_guid[g]["bim"]["ifcClass"]) for g in both if not same_class(by_guid[g]["bim"]["ifcClass"], ifc[g]["raw"])).most_common(10)]
pre = [g for g in both if ifc[g]["predefined"]]
res["tipo_predefinido"] = ratio(sum((by_guid[g]["bim"].get("ifcPredefinedType") or None) == ifc[g]["predefined"] for g in pre), len(pre))
st = [g for g in both if ifc[g]["storey"]]
def our_level(g): return levels.get(by_guid[g]["bim"].get("levelId") or "", {}).get("name")
res["planta"] = ratio(sum(our_level(g) == ifc[g]["storey"] for g in st), len(st))
res["diferencias_planta"] = [list(k) + [v] for k, v in collections.Counter(
    (by_guid[g]["category"], ifc[g]["storey"], our_level(g)) for g in st if our_level(g) != ifc[g]["storey"]).most_common(8)]
res["con_malla"] = ratio(sum(1 for g in both if by_guid[g]["meshes"]), len(both))

print(json.dumps(res, ensure_ascii=False, indent=1))
if "--json" in sys.argv:
    json.dump(res, open(sys.argv[sys.argv.index("--json") + 1], "w", encoding="utf-8"), ensure_ascii=False, indent=1)
