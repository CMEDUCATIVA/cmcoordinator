---
version: "0001"
---
# Corpus externo: modelos públicos de GitHub y muestras oficiales de Autodesk 2023–2027

Versión del documento: 0001

Fecha UTC: 2026-10-25. Encargo: «busca en internet, quizá en algún GitHub, descargas y comparas». Complementa `seguimiento_0002.md`.

## 1. Fuentes

| Fuente | Contenido | Licencia | Dónde queda |
|---|---|---|---|
| [Drshelden/IFC-ECS](https://github.com/Drshelden/IFC-ECS) `data/RE1/` | 4 modelos RE1 de **Revit 2025** (compilación 20250515): Architecture, Electrical, Mechanical, Plumbing, cada uno con **el IFC exportado por Revit** | MIT | WSL `~/corpus_github/ifc-ecs/` |
| [magnetar-io/revit-test-datasets](https://github.com/magnetar-io/revit-test-datasets) `Revit/` (Git LFS) | `2024_Core_Interior.rvt` (2024) y `Revit_IFC5_Einhoven.rvt` (2023) | MIT | WSL `~/corpus_github/magnetar/` |
| Autodesk, `revit.downloads.autodesk.com/download/<año>RVT_RTM/Docs/InProd/`. Enlaces y SHA256 tomados de [`research/autodesk-sample-projects.tsv` de rvt-rs](https://github.com/DrunkOnJava/rvt-rs/blob/main/research/autodesk-sample-projects.tsv) | `racbasicsampleproject`, `rstbasicsampleproject` y `racadvancedsampleproject` de **2023 a 2027** (15 archivos; los 15 SHA256 coinciden) | Condiciones de Autodesk no comprobadas | WSL `~/corpus_autodesk/`, **nunca en el repositorio** |

Guiones:
- `pruebas/buscar_corpus_github.sh`;
- `pruebas/descargar_corpus_github.sh`;
- `pruebas/descargar_muestras_autodesk.sh`, que comprueba el SHA256;
- `pruebas/corpus_github.sh` y `pruebas/corpus_autodesk.sh`;
- `pruebas/auditar_contra_ifc.py` (nuevo).

**Oráculos reutilizados.** Las muestras de Autodesk son los mismos modelos de nuestros oráculos, y Revit conserva los ElementId al actualizar el archivo:
- `racbasicsampleproject` es la «casa» (canal 8);
- `racadvancedsampleproject` es «Arquitectura» (canal 9).

Por eso cada versión se mide contra el mismo oráculo.

**Auditor nuevo `auditar_contra_ifc.py`.** Compara con el IFC que exportó Revit, emparejando por GlobalId: clase, tipo predefinido y planta, con las piezas heredando la planta a través de sus agregados. En IFC2X3 acepta como iguales la clase IFC4 y su superclase IFC2X3 (por ejemplo `IfcPipeSegment` = `IFCFLOWSEGMENT`).

## 2. Resultados

### 2.1 Revit 2025 con referencia de Revit (RE1, IFC2X3)

| Modelo | Elementos | Emparejados | Clase IFC | Planta | Tipo predefinido |
|---|---|---|---|---|---|
| RE1-Architecture | 74 | 73/73 (100 %) | 100 % | 100 % | 8/8 |
| RE1-Electrical | 49 | 49/49 (100 %) | 100 % | 100 % | — |
| RE1-Mechanical | 74 | 74/74 (100 %) | 100 % | 100 % | — |
| RE1-Plumbing | 124 | 123/123 (100 %) | 100 % | 100 % | — |

### 2.2 Mismo modelo en las cinco versiones

**«Casa» (`racbasicsampleproject`) frente al canal 8:**

| Versión (compilación) | Código | Emparejados | Clase IFC | Nivel | Tipo | Agregados | Triángulos |
|---|---|---|---|---|---|---|---|
| 2023 (20220401) | 0 ✅ | 95,8 % | 97,9 % | 99,8 % | 43,8 % | 0 % | 92.140 |
| 2024 (20230308) | 0 ✅ | 95,8 % | 100 % | **100 %** | 97,5 % | 97,4 % | 76.482 |
| 2025 (Development Build) | 0 ✅ | 95,8 % | 100 % | 99,8 % | 97,2 % | 97,4 % | 76.824 |
| 2026 (20250227) | 0 ✅ | 95,8 % | 100 % | 99,8 % | 97,2 % | 97,4 % | 76.770 |
| **2027 (20251222)** | **0 ✅** | 95,8 % | 100 % | **94,7 %** | 97,2 % | 97,4 % | 76.788 |

**«Arquitectura» (`racadvancedsampleproject`) frente al canal 9:**

| Versión | Código | Emparejados | Clase IFC | Nivel | Tipo | Agregados | Triángulos |
|---|---|---|---|---|---|---|---|
| 2023 | 0 ✅ | 99,9 % | 99,4 % | 99,5 % | **14,1 %** | **0 %** | 227.463 |
| 2024 | 0 ✅ | 99,9 % | 100 % | 100 % | 99,3 % | 95,7 % | 199.784 |
| 2025 | 0 ✅ | 99,9 % | 100 % | 99,5 % | 98,3 % | 95,7 % | 222.582 |
| 2026 | 0 ✅ | 99,9 % | 100 % | 99,5 % | 98,3 % | 95,7 % | 222.210 |
| **2027** | **0 ✅** | 99,9 % | 100 % | 99,5 % | 98,3 % | **94,0 %** | 222.210 |

**Estructura básica (`rstbasicsampleproject`, sin oráculo):** 454 elementos en las cinco versiones; todas válidas.

### 2.3 Otros modelos públicos

| Modelo | Versión | Antes | Ahora |
|---|---|---|---|
| 2024_Core_Interior | 2024 | **Error** «Can not convert Null to Int64» | ✅ 854 elementos válidos; 3.777 registros sin ElementId omitidos y avisados |
| Revit_IFC5_Einhoven | 2023 | **Error** (mismo) | ✅ 35 elementos válidos |

El IFC publicado de Core Interior es una versión reducida con un solo elemento: no sirve de referencia.

Captura de la casa 2027: `pruebas/visor/capturas/casa-2027-mixtas.png`.

## 3. Hallazgos

| Id | Hallazgo | Tratamiento |
|---|---|---|
| E1 | **Revit 2027 convierte** (las tres muestras oficiales) con la misma calidad que 2026 | Cierra L2 de `seguimiento_0002.md` |
| E2 | **Revit 2025 convierte**: RE1 (20250515) al 100 % frente al IFC de Revit y las muestras de Autodesk iguales que 2026. Lo que falla es solo el archivo del docente (compilación **20250815**) | L1 se acota: es una compilación concreta, no la versión. Pendiente: avisar al proyecto rvt-rs con ese número de compilación |
| E3 | Error del convertidor: registros de rvt-rs sin `id` hacían fallar toda la conversión | **Corregido** en `RvtRsReader`: se omiten y se avisan |
| E4 | 2027, casa: 21 montantes en otro nivel. Son justo los 21 que en el oráculo tienen un nivel distinto del de su muro cortina; en el archivo de 2027 rvt-rs les da el nivel del muro | Sin cambios: falta una referencia de Revit 2027 para saber si cambió el archivo o la lectura. Cómo comprobarlo: abrir la muestra de 2027 en Revit 2027, sincronizar con Cmcoordinator y exportar el oráculo |
| E5 | 2027, Arquitectura: agregados 95,7 % → 94,0 % (unas 80 piezas sin padre) | Mismo origen probable que E4 (lectura experimental de 2027 en rvt-rs); misma comprobación |
| E6 | 2023: confirma el límite L3 (tipo entre el 14 % y el 44 %, sin agregados, muros cortina como `IfcWall`) en dos modelos distintos | Límite del lector rvt-rs para 2023 |
| E7 | La compatibilidad 2023–2027 no depende de `cmconv`: el mismo código da el mismo resultado en todas las versiones cuando rvt-rs lee el archivo | Mejorar el soporte de una versión = actualizar rvt-rs |
