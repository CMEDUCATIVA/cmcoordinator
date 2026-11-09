---
version: "0001"
---
# El convertidor de Archivo RVT a modelo en vivo depende del lector rvt-rs para cada versión de Revit

Versión del documento: 0001

**Ámbito:** convertidor `cmconv` (`Convertidor-revit-json-malla/`), .NET 9, ejecutable Linux autónomo, con rvt-rs v0.7.0. Comprobado en local (WSL Ubuntu) el 2026-10-25 con 26 Archivos RVT de Revit 2023 a 2027. Aún no está integrado en el CDE.

**Regla.** El convertidor `cmconv` transforma un Archivo RVT, sin Revit, sin Exportación IFC y en Linux, en los mismos paquetes del protocolo 3 que envía Cmcoordinator para una Revisión en Vivo:
- catálogo BIM;
- elementos con clase IFC, Pset y Cantidades Qto_;
- mallas.

Lo que puede dar de cada versión de Revit lo decide el lector rvt-rs, no el convertidor. El mismo código da el mismo resultado en todas las versiones cuando rvt-rs decodifica el archivo, así que mejorar una versión significa actualizar rvt-rs. El convertidor no inventa lo que el archivo no trae: avisa.

## Por qué

**Resultados medidos frente a Cmcoordinator.** Se comparó elemento a elemento por ElementId con los modelos en vivo de referencia (canales 8, 9 y 10 del CDE DEV), y por GlobalId con la Exportación IFC del propio Revit.

| Versión de Revit | Resultado |
|---|---|
| 2024, 2025, 2026 y 2027 | 95,8–99,9 % de elementos encontrados; clase IFC 98,5–100 %; nivel 99,5–100 %; tipo y familia 97–100 %; piezas con su conjunto 94–97 %; GlobalId 100 % |
| 2025 (modelos RE1, compilación 20250515) | 100 % en elementos, clase, planta y tipo predefinido frente a la Exportación IFC de Revit |
| 2023 | Convierte, pero rvt-rs decodifica muchos menos campos: tipo y familia solo entre el 14 % y el 44 %, sin piezas agregadas, muros cortina como `IfcWall` |
| 2025, compilación 20250815 | rvt-rs no reconoce ningún elemento. `cmconv` termina con código 3 y un mensaje claro en vez de producir un modelo vacío |

Todas las conversiones pasan las reglas de validación del receptor del CDE (`protocol.rb`).

**Lo que el Archivo RVT no contiene** (por eso no hay valores que copiar):
- áreas y volúmenes de ejemplares de familia, que Revit calcula al abrir el modelo (ver la ficha sobre cantidades);
- forma real de árboles y personas RPC: solo cajas. Se dibujan translúcidas y con aviso.

**Lo que rvt-rs v0.7.0 no lee:** sistemas de vigas, topografía, emplazamiento y barandales superiores. Además, en algunos archivos de 2024 no dibuja las escaleras; se conservan como datos sin malla.

## Cómo se aplica

- Uso: `cmconv convertir <archivo.rvt> -o <carpeta> [--mallas mixtas|cajas|guardadas]`. Por defecto, mallas mixtas: las mallas reales guardadas por Revit, simplificadas.
- Códigos de salida:
  - 0: convertido y válido;
  - 2: argumentos;
  - 3: versión no admitida o sin elementos;
  - 4: salida no válida;
  - 1: otro error.
- Antes de prometer una versión, ejecutar la auditoría del corpus (ver la ficha sobre auditoría) y leer los avisos de `resumen.json`.
- Para ampliar el soporte de una versión, sustituir la carpeta de rvt-rs y repetir el corpus. rvt-rs se ejecuta como programa externo y no se enlaza, por su licencia Apache 2.0.

## Qué pasa si no

Si se atribuye al convertidor un límite del lector, se corrige donde no está: el fallo de 2025 era de una sola compilación, no de la versión. Si se rellena lo que el archivo no trae, el CDE muestra datos que parecen de Revit sin serlo.

## Relacionado

Archivo RVT, Revit, Cmcoordinator, CDE, Revisión en Vivo, Modelos IFC, Exportación IFC, GlobalId, Cantidades Qto_, rvt-rs, convertidor cmconv.

Fuente:
- `Convertidor-revit-json-malla/README.md`;
- `Convertidor-revit-json-malla/expediente_2026-10-24_235915Z/Auditoria/seguimiento_0002.md` (apartados «Auditoría del corpus 2023–2027» y «Límites que quedan»);
- `Convertidor-revit-json-malla/expediente_2026-10-24_235915Z/Auditoria/corpus_externo_0001.md` (apartado «Resultados»);
- comprobado el 2026-10-25.
