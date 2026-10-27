#!/bin/bash
# Convierte los modelos públicos de GitHub (descargar_corpus_github.sh) y los compara con el IFC que exportó Revit, si lo hay.
DIR=/mnt/d/Cmcoordinator/Convertidor-revit-json-malla/pruebas
MALLAS=${1:-mixtas}
for f in ~/corpus_github/*/*.rvt; do
  n=$(basename "$f" .rvt)
  echo "=== $n"
  bash "$DIR/probar.sh" "$f" "gh-$n-$MALLAS" "$MALLAS"
  ifc="${f%.rvt}.ifc"
  if [ -s "$ifc" ] && [ "$(stat -c %s "$ifc")" -gt 1000 ]; then
    python3 -I "$DIR/auditar_contra_ifc.py" "$HOME/cmconv/gh-$n-$MALLAS" "$ifc" --json "$HOME/cmconv/gh-$n-$MALLAS/auditoria_ifc.json"
  fi
done
