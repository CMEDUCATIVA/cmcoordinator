"""Compara, elemento a elemento por ElementId, lo que lee rvt-rs del .rvt con lo que envía Cmcoordinator (modelo en vivo)."""
import json, sys, collections

rvt = json.load(open(sys.argv[1], encoding="utf-8"))
live = json.load(open(sys.argv[2], encoding="utf-8"))

def field(e, name):
    for f in e.get("fields", []):
        if f.get("name") == name:
            return f.get("value", f.get("id"))
    return None

R = {int(e["id"]): e for e in rvt}
L = {int(e["elementId"]): e for e in live if str(e.get("elementId", "")).lstrip("-").isdigit()}
both = [i for i in L if i in R]
print(f"ELEMENTOS en vivo {len(L)} · en rvt-rs {len(R)} · coinciden por ElementId {len(both)} ({100*len(both)/max(1,len(L)):.1f} % del vivo)")

miss = collections.Counter(L[i]["category"] for i in L if i not in R)
print("SOLO EN VIVO por categoría:", ", ".join(f"{k}={v}" for k, v in miss.most_common(12)) or "ninguno")
extra = collections.Counter(R[i]["class_name"] for i in R if i not in L)
print("SOLO EN rvt-rs por clase:", ", ".join(f"{k}={v}" for k, v in extra.most_common(12)))

pairs = collections.Counter((L[i]["category"], R[i]["class_name"]) for i in both)
print("CATEGORÍA vivo → clase rvt-rs:", ", ".join(f"{a}→{b}={n}" for (a, b), n in pairs.most_common(20)))

def same(a, b):
    return a is not None and b is not None and str(a).strip().lower() == str(b).strip().lower()

type_ok = sum(same(L[i].get("typeName"), field(R[i], "m_type_name")) for i in both)
type_have = sum(field(R[i], "m_type_name") is not None for i in both)
fam_ok = sum(same(L[i].get("familyName"), field(R[i], "m_family_name")) for i in both)
fam_have = sum(field(R[i], "m_family_name") is not None for i in both)
print(f"TIPO: rvt-rs lo da en {type_have}/{len(both)} · coincide {type_ok}")
print(f"FAMILIA: rvt-rs la da en {fam_have}/{len(both)} · coincide {fam_ok}")

# Level: rvt-rs gives a level ElementId; map each id to the live level name by majority, then measure agreement.
votes = collections.defaultdict(collections.Counter)
for i in both:
    lid = field(R[i], "m_levelId")
    if lid is not None and L[i].get("levelName"):
        votes[lid][L[i]["levelName"]] += 1
mapping = {lid: c.most_common(1)[0][0] for lid, c in votes.items()}
lvl_have = sum(field(R[i], "m_levelId") is not None for i in both)
lvl_ok = sum(mapping.get(field(R[i], "m_levelId")) == L[i].get("levelName") for i in both if field(R[i], "m_levelId") is not None)
print(f"NIVEL: rvt-rs lo da en {lvl_have}/{len(both)} · coherente con el vivo {lvl_ok} · niveles distintos {len(mapping)}")

# Parameters: live keys are BuiltInParameter ids (negative) or shared/project ids; rvt-rs gives parameter_id.
live_ids = rvt_ids = common = 0
val_eq = val_cmp = 0
named = total_rp = 0
for i in both:
    lp = {k: v for k, v in L[i]["params"].items() if k.lstrip("-").isdigit()}
    rp = {}
    for p in R[i].get("parameters", []):
        total_rp += 1
        if p.get("name"): named += 1
        if p.get("parameter_id") is not None:
            rp[str(p["parameter_id"])] = p
    live_ids += len(lp); rvt_ids += len(rp)
    shared = set(lp) & set(rp)
    common += len(shared)
    for k in shared:
        p = rp[k]
        if p.get("storage_type") == "String" and lp[k]:
            val_cmp += 1; val_eq += same(lp[k], p.get("value"))
print(f"PARÁMETROS: vivo {live_ids} · rvt-rs {rvt_ids} (con nombre {named}/{total_rp}) · mismos ids {common} ({100*common/max(1,live_ids):.1f} % de los del vivo)")
print(f"VALORES de texto comparables {val_cmp} · iguales {val_eq}")
geo = sum(L[i]["triangles"] > 0 for i in both)
print(f"GEOMETRÍA en el vivo para {geo}/{len(both)} de los emparejados")
