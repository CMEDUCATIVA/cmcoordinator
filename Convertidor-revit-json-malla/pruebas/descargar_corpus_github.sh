#!/bin/bash
# Descarga los .rvt públicos de GitHub (MIT) usados por rvt-rs en su CI y muestra su versión real de Revit.
# Datos externos no confiables: cada repositorio va a su propia carpeta y solo se leen con rvt-rs.
D=~/corpus_github
B=~/rvtrs/rvt-rs-v0.7.0-x86_64-unknown-linux-musl
mkdir -p "$D/ifc-ecs" "$D/magnetar"
for f in RE1-Architecture RE1-Electrical RE1-Mechanical RE1-Plumbing; do
  for x in rvt ifc; do
    [ -s "$D/ifc-ecs/$f.$x" ] || curl -sSfL -o "$D/ifc-ecs/$f.$x" "https://raw.githubusercontent.com/Drshelden/IFC-ECS/main/data/RE1/$f.$x"
  done
done
# Git LFS: media.githubusercontent.com sirve el contenido real.
for f in "Revit/2024_Core_Interior.rvt" "Revit/Revit_IFC5_Einhoven.rvt" "Revit/2024_Core_Interior.ifc"; do
  n=$(basename "$f")
  [ -s "$D/magnetar/$n" ] || curl -sSfL -o "$D/magnetar/$n" "https://media.githubusercontent.com/media/magnetar-io/revit-test-datasets/main/${f// /%20}"
done
ls -la "$D"/*/
for f in "$D"/*/*.rvt; do
  printf '%s\t' "$(basename "$f")"
  "$B/rvt-info" --json "$f" 2>/dev/null | python3 -c 'import json,sys; d=json.load(sys.stdin); print(d.get("version"), d.get("build"))'
done
