#!/bin/bash
# Matriz de soporte declarada por rvt-rs v0.7.0 y su tabla de versiones del README.
B=~/rvtrs/rvt-rs-v0.7.0-x86_64-unknown-linux-musl
"$B/rvt-capabilities" -f text 2>&1 | head -60
echo "==================== MATRIZ"
"$B/rvt-capabilities" --matrix -f text 2>&1 | head -120
echo "==================== README (versiones)"
grep -n -iE "201[0-9]|202[0-9]|version|support|partial|experimental|not decoded" "$B/README.md" | head -60
