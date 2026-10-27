#!/bin/bash
# Ayuda de las herramientas de rvt-rs que interesan para el convertidor.
cd ~/rvtrs/rvt-rs-v0.7.0-x86_64-unknown-linux-musl || exit 1
chmod +x rvt-* gen-fixture
for t in rvt-capabilities rvt-info rvt-elements rvt-gltf rvt-ifc-compare; do echo "######## $t"; ./$t --help 2>&1 | head -30; done
