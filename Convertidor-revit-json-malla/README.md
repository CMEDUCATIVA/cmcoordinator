# Convertidor de Revit a JSON con mallas (`cmconv`)

Convierte un `.rvt` de Revit 2023–2027 en el formato del modelo en vivo de Cmcoordinator: paquetes del protocolo 3 con catálogo BIM, elementos, clase IFC, Psets y mallas. Funciona sin Revit, sin IFC y en Linux. Lee el archivo con [rvt-rs](https://github.com/DrunkOnJava/rvt-rs) v0.7.0 (Apache 2.0) como programa externo.

**Estado:** probado en local (WSL Ubuntu). Aún no está integrado en el CDE.
- Medidas y límites por versión: `expediente_2026-10-24_235915Z/Auditoria/seguimiento_0002.md`.
- Pruebas con modelos públicos y muestras oficiales de Autodesk 2023–2027: `corpus_externo_0001.md`.

| Versión de Revit | Resultado |
|---|---|
| 2023 | Convierte; tipo y conjuntos incompletos (límite de rvt-rs) |
| 2024 | Convierte; en algunos archivos las escaleras salen sin geometría (quedan como datos) |
| 2025 | Convierte: RE1 al 100 % frente al IFC de Revit. **Falla la compilación 20250815** (código 3 con mensaje) |
| 2026 | Convierte; 99,9 % de elementos, clase IFC al 100 % |
| 2027 | Convierte las tres muestras oficiales; 21 montantes con otro nivel, pendiente de comprobar con Revit 2027 |

## Uso

```bash
cmconv convertir <archivo.rvt> [-o <carpeta>] [--mallas mixtas|cajas|guardadas] [--rvtrs <carpeta de rvt-rs>]
cmconv validar <carpeta>
```

- `mixtas` (por defecto): mallas reales guardadas por Revit, simplificadas.
- `cajas`: rápido y ligero.
- `guardadas`: mallas sin simplificar (diagnóstico).

Salida: `manifest.json`, `parts/*.json` y `resumen.json`. Códigos de salida:
- 0: convertido y válido;
- 2: argumentos;
- 3: versión no admitida o sin elementos;
- 4: salida no válida;
- 1: otro error.

Compilar el ejecutable Linux autónomo:

```bash
cd src/CmConv && dotnet publish -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true -o bin/linux-x64
```

## Carpetas

| Ruta | Qué es |
|---|---|
| `src/CmConv/` | Código. `Lectura/` (lector rvt-rs, glTF, plantas IFC, simplificador), `Semantica/` (clase IFC, parámetros, Psets), `Salida/` (empaquetado con el `SqliteOutbox` de Cmcoordinator y validador con las reglas de `protocol.rb`), `Datos/` (tablas aprendidas de los oráculos) |
| `pruebas/` | `corpus.sh` (corpus del equipo), `corpus_autodesk.sh` y `corpus_github.sh` (corpus externo, con sus guiones `descargar_*`), `auditar_contra_ifc.py` (comparación con el IFC de Revit), `resumen_corpus.py`, `probar.sh`, `auditar_salida.py` (comparación con el oráculo), `triangulos_por_categoria.py`, `auditoria.py` (línea base de rvt-rs) y exploradores `rvtrs_*` / `explorar_*` |
| `pruebas/visor/` | Visor local con xeokit (`visor.html`), captura con Playwright (`captura.js`) y `capturas/` |
| `pruebas/oraculos/` | Modelos en vivo de Cmcoordinator exportados del CDE DEV. **Datos de clientes: excluidos de Git** |
| `terceros/rvt-rs/` | Binarios de rvt-rs para Linux x64 y `SHA256SUMS`. Excluida de Git |
| `expediente_2026-10-24_235915Z/` | Expediente: solicitud, informe de motores, plan, auditoría base y seguimiento |

Vistas, láminas y filtros: el convertidor no puede darlos con rvt-rs. Hay una alternativa en GitHub (rvt-reader y rvt-sheets), pendiente de evaluar: ver `consultas/informe_0004.md`.

Revit Batch Processor y DDC se estudiaron y se descartaron: el primero necesita Revit; el segundo no abre Revit 2026, no trae parámetros y su licencia no permite usarlo en el CDE. Las conclusiones están en `consultas/informe_0003.md`.
