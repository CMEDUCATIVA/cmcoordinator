#!/bin/bash
# rac advanced es el modelo «Arquitectura»: se audita contra el oráculo del canal 9 en todas las versiones.
DIR=/mnt/d/Cmcoordinator/Convertidor-revit-json-malla/pruebas
for y in 2023 2024 2025 2026 2027; do
  o="$HOME/cmconv/ad-$y-racadvancedsampleproject-mixtas"
  python3 -I "$DIR/auditar_salida.py" "$o" "$DIR/oraculos/canal_9.json" --json "$o/auditoria.json" > /dev/null
done
python3 -I "$DIR/resumen_corpus.py" mixtas ad-20 | grep -A4 racadvanced
