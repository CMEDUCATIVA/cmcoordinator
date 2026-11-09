#!/bin/bash
# Convierte y audita todo el corpus 2023–2027 con cmconv (de uno en uno, por memoria). Uso: corpus.sh [mixtas|cajas|guardadas]
DIR=/mnt/d/Cmcoordinator/Convertidor-revit-json-malla/pruebas
SAMPLES="/mnt/d/LIBRE PRUEBA/cad2data-Revit-IFC-DWG-DGN-pipeline-with-conversion-validation-qto-main/cad2data-Revit-IFC-DWG-DGN-pipeline-with-conversion-validation-qto-main/Sample_Projects"
DOWNLOADS=/mnt/c/Users/Administrador/Downloads
EDU="/mnt/d/1-NUBE/ALMACENAMIENTO CENTRAL/CM/1-2-CMEDUCATIVA/1-2-6-Docentes/ABEL BAZAN/PROYECTO VIVIENDA  UNIFAMILIAR/WIP/ESTRUCTURA/MODELO"
MALLAS=${1:-mixtas}
while IFS='|' read -r nombre archivo oraculo; do
  [ -f "$archivo" ] || { echo "=== $nombre: NO EXISTE $archivo"; continue; }
  echo "=== $nombre"
  bash "$DIR/probar.sh" "$archivo" "$nombre-$MALLAS" "$MALLAS" ${oraculo:+"$DIR/oraculos/$oraculo"}
done <<EOF
casa-2023|$SAMPLES/2023 racbasicsampleproject.0001.rvt|canal_8.json
arquitectura-2024|$DOWNLOADS/REVIT/Arquitectura (1).0001.rvt|canal_9.json
estructura-2024|$DOWNLOADS/REVIT/Estructura.rvt|canal_10.json
mep-2024|$DOWNLOADS/REVIT/MEP.rvt|
vivienda-est-2025|$EDU/123456-CME-PER-EST-M3D-001.rvt|
arquitectura-2026|$DOWNLOADS/REVIT/Arquitectura (1).rvt|canal_9.json
casa-2026|$SAMPLES/2023 racbasicsampleproject.rvt|canal_8.json
rstadvanced-2026|$SAMPLES/2022 rstadvancedsampleproject.rvt|
arquitectura-2027|$DOWNLOADS/REVIT/Arquitectura-2027.rvt|canal_9.json
EOF
