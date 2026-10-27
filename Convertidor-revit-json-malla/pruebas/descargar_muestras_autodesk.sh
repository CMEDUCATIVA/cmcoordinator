#!/bin/bash
# Descarga los proyectos de ejemplo oficiales de Autodesk (2023–2027) que rvt-rs usa en sus mediciones,
# desde revit.downloads.autodesk.com, y comprueba su SHA256 con la tabla publicada por rvt-rs.
# No se guardan en el repositorio: sus condiciones de uso no están comprobadas (solo pruebas locales).
D=~/corpus_autodesk
TSV=https://raw.githubusercontent.com/DrunkOnJava/rvt-rs/main/research/autodesk-sample-projects.tsv
mkdir -p "$D"
curl -sSfL "$TSV" | grep -v '^#' | while IFS=$'\t' read -r year file url sha bytes; do
  case "$year" in 2023|2024|2025|2026|2027) ;; *) continue ;; esac
  case "$file" in racbasicsampleproject.rvt|rstbasicsampleproject.rvt|racadvancedsampleproject.rvt) ;; *) continue ;; esac
  out="$D/$year-$file"
  [ -s "$out" ] || curl -sSfL -o "$out" "$url"
  got=$(sha256sum "$out" | cut -d' ' -f1)
  [ "$got" = "$sha" ] && echo "OK    $year-$file" || { echo "HASH! $year-$file"; rm -f "$out"; }
done
