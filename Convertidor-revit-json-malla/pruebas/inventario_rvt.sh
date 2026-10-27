#!/bin/bash
# Inventario de los .rvt del equipo con su versión real de Revit (rvt-info, sin Revit). Solo lectura.
B=~/rvtrs/rvt-rs-v0.7.0-x86_64-unknown-linux-musl
OUT=~/rvtrs/out/inventario.csv
"$B/rvt-info" --redact -f csv \
  "/mnt/c/Users/Administrador/Downloads" \
  "/mnt/c/Users/Administrador/Documents" \
  "/mnt/c/Users/Administrador/Desktop" \
  "/mnt/d/LIBRE PRUEBA" \
  "/mnt/d/1-NUBE" > "$OUT" 2> ~/rvtrs/out/inventario.err
head -1 "$OUT"
python3 -I - "$OUT" <<'PY'
import csv, sys, collections, os
rows = list(csv.DictReader(open(sys.argv[1], encoding="utf-8")))
cols = rows[0].keys() if rows else []
vcol = next((c for c in cols if c.lower() in ("version", "revit_version", "release")), None)
pcol = next((c for c in cols if "path" in c.lower() or c.lower() in ("file", "name")), None)
scol = next((c for c in cols if "size" in c.lower() or "bytes" in c.lower()), None)
print("columnas:", list(cols)[:14])
by = collections.defaultdict(list)
for r in rows:
    p = r.get(pcol, "")
    if not p.lower().endswith(".rvt") or "backup" in p.lower(): continue
    by[r.get(vcol, "?")].append(r)
for v in sorted(by):
    print(f"== Revit {v}: {len(by[v])} archivos")
    for r in by[v][:8]:
        print("   ", r.get(scol, ""), r.get(pcol, ""))
PY
