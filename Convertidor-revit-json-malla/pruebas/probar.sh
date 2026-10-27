#!/bin/bash
# Convierte un .rvt con cmconv y, si hay oráculo, lo audita. Uso: probar.sh <archivo.rvt> <nombre> <mallas> [oráculo.json]
DIR=/mnt/d/Cmcoordinator/Convertidor-revit-json-malla/pruebas
bash "$DIR/convertir.sh" "$1" "$2" "$3" > "/tmp/conv-$2.log" 2>&1
grep -E '"(version|elementos_convertidos|con_malla|triangulos|mb|valido|paquetes)"|dibujados|"total"|CODIGO|MEMORIA|"error"' "/tmp/conv-$2.log"
if [ -n "$4" ]; then python3 -I "$DIR/auditar_salida.py" "$HOME/cmconv/$2" "$4" --json "$HOME/cmconv/$2/auditoria.json"; fi
