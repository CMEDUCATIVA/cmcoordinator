#!/bin/bash
# ¿Trae la exportación IFC de rvt-rs los niveles con nombre, cota y ElementId?
B=~/rvtrs/rvt-rs-v0.7.0-x86_64-unknown-linux-musl
f="/mnt/c/Users/Administrador/Downloads/REVIT/Arquitectura (1).rvt"
"$B/rvt-ifc" --help 2>&1 | head -25
/usr/bin/time -f "tiempo %e s · memoria %M KB" "$B/rvt-ifc" "$f" -o /tmp/arq.ifc 2>&1 | tail -3
echo "== niveles"
grep -E "IFCBUILDINGSTOREY\(" /tmp/arq.ifc | head -8
grep -c "IFCBUILDINGSTOREY(" /tmp/arq.ifc
