---
version: "0001"
---
# Seguimiento del convertidor `cmconv`

Versión del documento: 0001

Medición: `pruebas/auditar_salida.py` (salida de `cmconv` frente al oráculo de Cmcoordinator). Caso de referencia: Arquitectura (1).rvt, Revit 2026, mallas en cajas.

## 2026-10-25 · Fase 1 — lectura y empaquetado

`cmconv` (.NET 9, ejecutable Linux autónomo) lee con rvt-rs (`rvt-info`, `rvt-elements`, `rvt-gltf`, `rvt-ifc`), traduce con las tablas aprendidas de los oráculos y empaqueta con el `SqliteOutbox` de Cmcoordinator (fuentes enlazados). Validador propio con las reglas de `protocol.rb`.

| Medida | Resultado |
|---|---|
| Elementos emparejados | 5.566 / 5.570 (99,9 %) |
| Categoría · clase IFC · tipo predefinido | 100 % · 100 % · 100 % |
| Nivel (nombre) | 99,5 % |
| Tipo | 98,3 % |
| Parámetros con nombre | 97,5 % |
| Validación con las reglas del CDE | ✅ 3 paquetes, 11,4 MB |
| Tiempo | 31 s (1,1 GB de RAM) |

## 2026-10-25 · Fase 2 — semántica

| Medida | Antes | Después |
|---|---|---|
| Familia | 93,7 % | **98,3 %** (tabla de familias de sistema en español) |
| Piezas con el mismo conjunto padre | 0 | **95,7 %** (`m_aggregate_whole`) |
| GlobalId IFC | 0 | **100 %** (leído de `rvt-ifc`) |
| Psets y cantidades: propiedades presentes | 0 | 27,2 % |
| Psets y cantidades: valores iguales | — | **99,9 %** |

**Límites confirmados:**
- rvt-rs no lee las áreas y volúmenes calculados de los ejemplares de familia (montantes, paneles, pilares, vigas, puertas, techos): Revit los calcula al abrir el modelo, no están en el archivo. Siguiendo la regla de Cmcoordinator, no se estiman a partir de cajas.
- La longitud de los pilares necesita sus niveles base y superior, que rvt-rs no da.
- 3 valores distintos de `Pset_DoorCommon.IsExternal`: la función se toma del tipo de la puerta y no del muro anfitrión.
