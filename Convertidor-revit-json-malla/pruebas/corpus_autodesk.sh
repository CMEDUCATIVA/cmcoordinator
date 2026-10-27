#!/bin/bash
# Convierte las muestras oficiales de Autodesk 2023–2027 (descargar_muestras_autodesk.sh).
# rac basic es el modelo «casa»: se audita contra el oráculo del canal 8 en todas las versiones (los ElementId se conservan al actualizar).
DIR=/mnt/d/Cmcoordinator/Convertidor-revit-json-malla/pruebas
B=~/rvtrs/rvt-rs-v0.7.0-x86_64-unknown-linux-musl
MALLAS=${1:-mixtas}
for f in ~/corpus_autodesk/*.rvt; do
  n=$(basename "$f" .rvt)
  echo "=== $n ($("$B/rvt-info" --json "$f" 2>/dev/null | python3 -c 'import json,sys; d=json.load(sys.stdin); print(d.get("version"), d.get("build"))'))"
  oraculo=""
  case "$n" in *racbasicsampleproject) oraculo="$DIR/oraculos/canal_8.json" ;; esac
  bash "$DIR/probar.sh" "$f" "ad-$n-$MALLAS" "$MALLAS" $oraculo
done
