---
version: "0001"
---
# Auditoría de línea base: rvt-rs v0.7.0 con Revit 2023–2027

Versión del documento: 0001

Fecha UTC: 2026-10-25. Herramienta: `pruebas/auditoria.py` (WSL Ubuntu, rvt-rs v0.7.0 `x86_64-unknown-linux-musl`). Datos crudos: `~/rvtrs/auditoria-base/auditoria.json` en WSL. Oráculos: modelos en vivo de Cmcoordinator (`pruebas/oraculos/`).

## 1. Corpus

| Caso | Versión | Archivo | Oráculo |
|---|---|---|---|
| casa-2023 | 2023 | `2023 racbasicsampleproject.0001.rvt` (18 MB) | canal 8, 453 elementos |
| arquitectura-2024 | 2024 | `Arquitectura (1).0001.rvt` (16 MB) | canal 9, 5.570 |
| estructura-2024 | 2024 | `Estructura.rvt` (14 MB) | canal 10, 920 |
| mep-2024 | 2024 | `MEP.rvt` (37 MB) | — |
| vivienda-est-2025 | 2025 | `123456-CME-PER-EST-M3D-001.rvt` (7 MB, docente de CMEDUCATIVA) | — |
| arquitectura-2026 | 2026 | `Arquitectura (1).rvt` (16 MB) | canal 9 |
| casa-2026 | 2026 | `2023 racbasicsampleproject.rvt` (19 MB) | canal 8 |
| rstadvanced-2026 | 2026 | `2022 rstadvancedsampleproject.rvt` (14 MB) | — |
| — | **2027** | **Ningún archivo en el equipo** | — |

## 2. Resultados

| Caso | Elementos leídos · tiempo · RAM | Emparejados con el oráculo | Tipo | Nivel | Clase IFC de rvt-rs | Parámetros (mismos ids) | Con malla |
|---|---|---|---|---|---|---|---|
| casa-2023 | 639 · 7 s · 0,24 GB | 434 / 453 (95,8 %) | 190 / 197 | 412 / 420 | 397 / 434 (91 %) | 4.414 / 9.529 (46 %) | 425 / 425 |
| arquitectura-2024 | 5.868 · 21 s · 0,5 GB | 5.566 / 5.570 (99,9 %) | 5.519 / 5.519 | 5.421 / 5.491 | 5.438 / 5.468 (99,5 %) | 50.087 / 102.518 (49 %) | 5.465 / 5.524 |
| estructura-2024 | 1.044 · 11 s · 0,2 GB | 905 / 920 (98,4 %) | 905 / 905 | **606 / 904 (67 %)** | **585 / 905 (65 %)** | 11.264 / 30.061 (37 %) | 905 / 905 |
| mep-2024 | 6.883 · 43 s · 0,9 GB | — | 5.357 con tipo | 6.604 con nivel | — | 115.282 leídos | 6.659 elementos |
| **vivienda-est-2025** | **129, todos «Material» · 3 s** | — | **0** | **0** | — | — | **0** |
| arquitectura-2026 | 5.843 · 15 s · 0,5 GB | 5.566 / 5.570 (99,9 %) | 5.463 / 5.463 | 5.421 / 5.491 | 5.497 / 5.527 (99,5 %) | 50.087 / 102.518 (49 %) | 5.524 / 5.524 |
| casa-2026 | 639 · 13 s · 0,27 GB | 434 / 453 (95,8 %) | 422 / 422 | 412 / 422 | 394 / 422 (93 %) | 4.414 / 9.529 (46 %) | 422 / 425 |
| rstadvanced-2026 | 1.045 · 10 s · 0,2 GB | — | 905 con tipo | 905 con nivel | — | 17.385 leídos | 905 elementos |

**Geometría (triángulos de los elementos emparejados):**

| Caso | Cmcoordinator | rvt-rs, cajas | rvt-rs, mallas guardadas (`--saved-meshes`) |
|---|---|---|---|
| casa | 205.558 | 6.000 | 1.476.778 (**7×**) |
| arquitectura | 366.321 | 72.376 | 1.628.868 (**4,5×**) |
| estructura | 83.144 | 11.468 | 2.586.712 (**31×**) |

Con mallas guardadas, el glTF ocupa entre 22 y 63 MB y la conversión usa entre 1 y 2,7 GB de RAM.

## 3. Hallazgos

| Id | Hallazgo | Impacto | Tratamiento previsto |
|---|---|---|---|
| H1 | 2023, 2024 y 2026: elementos, categorías y tipos casi idénticos a Cmcoordinator (del 95,8 % al 99,9 %) | Base sólida | Fase 1 |
| H2 | **Revit 2025, `vivienda-est-2025`: rvt-rs no lee ningún elemento constructivo** (solo 129 materiales) | Un modelo real de un docente no se convertiría | Investigar en la fase 1: probar otros archivos de 2025 y la opción de rvt-rs para registros sin ElementId; si no se resuelve, avisar «versión no convertible» y comunicarlo al proyecto rvt-rs |
| H3 | Estructura: nivel coherente solo en el 67 % y clase IFC de rvt-rs igual en el 65 % | Clasificación pobre en modelos estructurales | La clase la pone nuestra semántica (fase 2), no rvt-rs; el nivel, por la cota del elemento si rvt-rs no lo da |
| H4 | Parámetros: entre el 37 % y el 49 % de los identificadores, casi sin nombre | Inspector pobre | Diccionario id → nombre desde los oráculos (fase 2) |
| H5 | Faltan en rvt-rs: barandales superiores, topografía, emplazamiento, sistemas de mobiliario y sistemas de vigas | Pocos elementos, pero visibles | Documentar; aportar a rvt-rs |
| H6 | Mallas guardadas entre 4,5 y 31 veces más pesadas que las de Cmcoordinator; con cajas, demasiado simples | Fluidez o fidelidad | Fase 3: mallas guardadas simplificadas |
| H7 | No hay archivo de Revit 2027 | 2027 sin medir | Pedir al usuario que guarde uno |

## 4. Conclusión

rvt-rs es una base válida para **2023, 2024 y 2026**. **2025 falla** con el único modelo real disponible y **2027 está sin medir**. El convertidor debe detectar la versión y avisar cuando no pueda convertir, en vez de producir un modelo vacío.
