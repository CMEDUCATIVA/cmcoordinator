#!/bin/bash
# Qué sabe leer rvt-rs de vistas, láminas y tablas (sin Revit). Solo lectura.
B=~/rvtrs/rvt-rs-v0.7.0-x86_64-unknown-linux-musl
F=${1:-$HOME/corpus_autodesk/2026-racbasicsampleproject.rvt}
echo "== rvt-sheet --help"; "$B/rvt-sheet" --help 2>&1 | head -30
echo "== rvt-schedule --help"; "$B/rvt-schedule" --help 2>&1 | head -20
echo "== capacidades (vistas, láminas, planos)"; "$B/rvt-capabilities" --matrix -f text 2>&1 | grep -iE 'view|sheet|plan|svg|section|camera|filter|schedule' | head -30
echo "== rvt-sheet sobre $(basename "$F")"; timeout 120 "$B/rvt-sheet" "$F" 2>&1 | head -40
