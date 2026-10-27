#!/bin/bash
# Repite solo la comparación con el IFC de Revit de los modelos RE1 ya convertidos.
DIR=/mnt/d/Cmcoordinator/Convertidor-revit-json-malla/pruebas
for n in RE1-Architecture RE1-Electrical RE1-Mechanical RE1-Plumbing; do
  echo "=== $n"
  python3 -I "$DIR/auditar_contra_ifc.py" "$HOME/cmconv/gh-$n-mixtas" "$HOME/corpus_github/ifc-ecs/$n.ifc" --json "$HOME/cmconv/gh-$n-mixtas/auditoria_ifc.json" \
    | python3 -c 'import json,sys; d=json.load(sys.stdin); print(d["esquema_ifc"], "emparejados", d["emparejados"], "clase", d["clase_ifc"], "planta", d["planta"], "predef", d["tipo_predefinido"]); [print("   ", x) for x in d["diferencias_clase"]]'
done
