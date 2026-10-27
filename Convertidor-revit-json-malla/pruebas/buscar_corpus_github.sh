#!/bin/bash
# Lista los .rvt/.ifc de repositorios públicos de GitHub (API sin autenticar) y si usan Git LFS. Solo lectura.
for r in Drshelden/IFC-ECS magnetar-io/revit-test-datasets phi-ag/rvt; do
  echo "=== $r"
  b=$(curl -s "https://api.github.com/repos/$r" | python3 -c 'import json,sys; print(json.load(sys.stdin).get("default_branch","main"))')
  curl -s "https://api.github.com/repos/$r/git/trees/$b?recursive=1" | python3 -c '
import json, sys
d = json.load(sys.stdin)
for t in d.get("tree", []):
    p = t["path"]
    if p.lower().endswith((".rvt", ".ifc")): print(str(t.get("size", 0)).rjust(12), p)
if d.get("truncated"): print("(árbol truncado)")
if "message" in d: print(d["message"])'
  echo "-- .gitattributes:"; curl -s "https://raw.githubusercontent.com/$r/$b/.gitattributes" | head -5
done
