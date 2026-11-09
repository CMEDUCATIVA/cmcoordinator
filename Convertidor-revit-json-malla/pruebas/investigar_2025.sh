#!/bin/bash
# Investiga por qué rvt-rs no lee elementos del modelo de Revit 2025 del docente.
B=~/rvtrs/rvt-rs-v0.7.0-x86_64-unknown-linux-musl
D="/mnt/d/1-NUBE/ALMACENAMIENTO CENTRAL/CM/1-2-CMEDUCATIVA/1-2-6-Docentes/ABEL BAZAN"
for f in "$D/PROYECTO VIVIENDA  UNIFAMILIAR/WIP/ESTRUCTURA/MODELO/123456-CME-PER-EST-M3D-001.rvt" "$D/PLANTILLA DE REVIT - ESTRUCTURAS.rvt"; do
  echo "######## $(basename "$f")"
  "$B/rvt-info" --redact "$f" 2>&1 | grep -iE "version|build|workshar|central|cloud|increment|title" | head -8
  "$B/rvt-elements" --counts "$f" 2>/dev/null | head -c 600; echo
  echo "-- diagnóstico"
  "$B/rvt-elements" -h >/dev/null
  "$B/rvt-gltf" --verbose "$f" -o /tmp/r2025.glb 2>&1 | tail -4
done
echo "== capacidades relacionadas con 2025"
"$B/rvt-capabilities" --matrix -f text 2>&1 | grep -iA3 "2025\|second-prologue" | head -40
