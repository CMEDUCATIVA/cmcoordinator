"""¿Las piezas (montantes, paneles, tramos) tienen en Cmcoordinator el mismo nivel que su conjunto padre? Uso: nivel_piezas.py <oráculo.json>"""
import collections, json, sys

o = json.load(open(sys.argv[1], encoding="utf-8"))
by_uid = {e["uid"]: e for e in o}
c = collections.Counter()
for e in o:
    p = by_uid.get(e.get("aggregateParentId") or "")
    if p and p.get("levelName") and e.get("levelName"):
        c[(e["category"], e["levelName"] == p["levelName"])] += 1
for (cat, same), n in sorted(c.items()): print(f"{cat:35} {'igual que el padre' if same else 'DISTINTO del padre':20} {n}")
