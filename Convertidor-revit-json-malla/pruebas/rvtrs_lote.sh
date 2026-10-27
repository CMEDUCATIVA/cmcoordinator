#!/bin/bash
# Prueba de evaluación de rvt-rs v0.7.0 (Linux, WSL) con los .rvt de prueba: versión, elementos, propiedades y mallas glTF.
B=~/rvtrs/rvt-rs-v0.7.0-x86_64-unknown-linux-musl
OUT=~/rvtrs/out; mkdir -p "$OUT"
SAMPLES="/mnt/d/LIBRE PRUEBA/cad2data-Revit-IFC-DWG-DGN-pipeline-with-conversion-validation-qto-main/cad2data-Revit-IFC-DWG-DGN-pipeline-with-conversion-validation-qto-main/Sample_Projects"
declare -a files=(
  "arquitectura2026|/mnt/c/Users/Administrador/Downloads/REVIT/Arquitectura (1).rvt"
  "estructura2024|/mnt/c/Users/Administrador/Downloads/REVIT/Estructura.rvt"
  "mep2024|/mnt/c/Users/Administrador/Downloads/REVIT/MEP.rvt"
  "casa2023|$(ls "$SAMPLES"/2023*rac*.rvt | head -1)"
)
for entry in "${files[@]}"; do
  name="${entry%%|*}"; f="${entry#*|}"
  echo "################ $name"
  "$B/rvt-info" --redact "$f" 2>&1 | grep -iE "version|build|format|workshar|element|class" | head -8
  echo "-- recuento por clase"
  /usr/bin/time -f "   tiempo %e s · memoria %M KB" "$B/rvt-elements" --counts "$f" > "$OUT/$name.counts.json" 2> "$OUT/$name.counts.err"; tail -1 "$OUT/$name.counts.err"
  python3 -I -c "import json,sys; d=json.load(open(sys.argv[1])); bc=d.get('by_class',{}); print('   total', d.get('total'), '·', ', '.join(f'{k}={v}' for k,v in sorted(bc.items(), key=lambda x:-x[1])[:25]))" "$OUT/$name.counts.json" 2>&1 | head -3
  echo "-- elementos completos"
  /usr/bin/time -f "   tiempo %e s · memoria %M KB" "$B/rvt-elements" "$f" > "$OUT/$name.elements.json" 2> "$OUT/$name.elements.err"; tail -1 "$OUT/$name.elements.err"
  echo "   tamaño $(du -h "$OUT/$name.elements.json" | cut -f1)"
  echo "-- glTF con cajas"
  /usr/bin/time -f "   tiempo %e s · memoria %M KB" "$B/rvt-gltf" --verbose "$f" -o "$OUT/$name.glb" 2>&1 | tail -3
  echo "-- glTF con mallas guardadas por Revit (--saved-meshes)"
  /usr/bin/time -f "   tiempo %e s · memoria %M KB" "$B/rvt-gltf" --verbose --saved-meshes "$f" -o "$OUT/$name.saved.glb" 2>&1 | tail -3
done
ls -la "$OUT"/*.glb 2>/dev/null
