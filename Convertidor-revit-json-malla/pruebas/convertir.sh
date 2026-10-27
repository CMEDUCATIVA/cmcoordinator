#!/bin/bash
# Ejecuta cmconv (Linux) en WSL. Uso: convertir.sh <archivo.rvt> <nombre de salida> [cajas|guardadas]
B=/mnt/d/Cmcoordinator/Convertidor-revit-json-malla/src/CmConv/bin/linux-x64
export CMCONV_RVTRS=~/rvtrs/rvt-rs-v0.7.0-x86_64-unknown-linux-musl
chmod +x "$B/cmconv"
/usr/bin/time -f "TIEMPO TOTAL %e s ?? MEMORIA M??X %M KB" "$B/cmconv" convertir "$1" -o "$HOME/cmconv/$2" --mallas "${3:-mixtas}"
echo "CODIGO=$?"
