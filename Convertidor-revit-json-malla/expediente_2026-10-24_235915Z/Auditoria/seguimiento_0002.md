---
version: "0002"
---
# Seguimiento del convertidor `cmconv`

Versión del documento: 0002 (sustituye a la 0001: añade la fase 3, la auditoría del corpus 2023–2027 y sus correcciones).

Medición: `pruebas/auditar_salida.py` (salida de `cmconv` frente al oráculo de Cmcoordinator, elemento a elemento por ElementId). Corpus completo: `pruebas/corpus.sh` y `pruebas/resumen_corpus.py`. Comprobación visual: `pruebas/visor/` (xeokit 2.6.5, el mismo visor que el CDE).

## 2026-10-25 · Fase 1 — lectura y empaquetado

`cmconv` (.NET 9, ejecutable Linux autónomo; el plan 0001 decía .NET 8) lee con rvt-rs (`rvt-info`, `rvt-elements`, `rvt-gltf`, `rvt-ifc`), traduce con las tablas aprendidas de los oráculos y empaqueta con el `SqliteOutbox` de Cmcoordinator (fuentes enlazados). Validador propio con las reglas de `protocol.rb`.

Arquitectura 2026, mallas en cajas: 99,9 % de elementos emparejados; categoría, clase IFC y tipo predefinido al 100 %; nivel 99,5 %; tipo 98,3 %; parámetros con nombre 97,5 %; válido; 31 s.

## 2026-10-25 · Fase 2 — semántica

Familia 98,3 % (tabla de familias de sistema en español) · piezas con su conjunto padre 95,7 % · GlobalId IFC 100 % · Psets y cantidades: 27,2 % de las propiedades presentes, 99,9 % de los valores iguales.

Límites confirmados: rvt-rs no lee las áreas y volúmenes calculados de los ejemplares de familia (Revit los calcula al abrir el modelo); por la regla de Cmcoordinator no se estiman a partir de cajas.

## 2026-10-25 · Fase 3 — mallas reales

**Modo mixto (por defecto):** el elemento que rvt-rs solo sabe dibujar como caja se dibuja con la malla que Revit guarda en el archivo (`rvt-gltf --saved-meshes`), simplificada.

Simplificación en dos pasos (`Lectura/Simplificador.cs`):
1. **Sin pérdida, siempre.**
   - Se sueldan los vértices.
   - Se quitan las caras repetidas: Revit guarda muchos sólidos a dos caras, con cada triángulo dos veces.
   - Se colapsa todo vértice que no aporta forma: las subdivisiones dentro de caras planas y las franjas con que Revit corta los lados curvos, por ejemplo un pilar redondo cortado en 13 alturas.
   - Un vértice solo se colapsa si el vecino queda en el plano de todas sus caras (0,1 mm), ninguna cara se invierte y la superficie sigue siendo cerrada. El contorno no cambia.
2. **Agrupación de vértices por celdas**, solo si el elemento sigue por encima de 1.500 triángulos.

**Vegetación y entorno RPC:** el archivo solo guarda unas cajas, porque Revit dibuja la planta real al renderizar.
- Si la malla final tiene 48 triángulos o menos, se dibujan como cajas translúcidas.
- El resumen lo avisa.
- No se inventa una forma de árbol.

**Visor local** (`pruebas/visor/visor.html`): carga los paquetes desde disco con el mismo xeokit que el CDE y convierte Z arriba (Revit) en Y arriba, igual que el visor en vivo. Capturas en `pruebas/visor/capturas/`:
- `arquitectura-2026-mixtas.png`: edificio completo, árboles translúcidos;
- `estructura-2024-mixtas.png`: pilares redondos, pilotes, forjados y vigas.

| Caso | Triángulos Cmcoordinator | Antes (solo presupuesto) | Ahora (sin pérdida + presupuesto) |
|---|---|---|---|
| Arquitectura 2026 | 366.321 | 535.958 (1,46×) | **222.210 (0,61×)** |
| Estructura 2024 | 83.144 | 546.796 (6,6×) | **162.194 (1,95×)** |
| MEP 2024 | — | 1.574.162 | **754.983** |

Criterio de la fase (≤ 3 veces los triángulos de Cmcoordinator): **cumplido** en todos los casos con oráculo.

## 2026-10-25 · Auditoría del corpus 2023–2027 (modo mixto)

| Caso | Código | Elementos · con malla | Triángulos · MB · s | Emparejados | Clase IFC | Nivel | Tipo | Agregados | GUID |
|---|---|---|---|---|---|---|---|---|---|
| casa-2023 | 0 ✅ | 450 · 450 | 92.140 · 2,9 · 37 | 95,8 % | 97,9 % | 99,8 % | **43,8 %** | **0 %** | 100 % |
| arquitectura-2024 | 0 ✅ | 5.566 · 5.468 | 199.784 · 13,2 · 76 | 99,9 % | 100 % | **100 %** | 99,3 % | 95,7 % | 100 % |
| estructura-2024 | 0 ✅ | 905 · 905 | 162.194 · 3,2 · 46 | 98,4 % | 98,5 % | **99,8 %** | 100 % | 0 % (43) | 100 % |
| mep-2024 | 0 ✅ | 6.609 · 6.578 | 754.983 · 27,9 · 168 | sin oráculo | | | | | |
| vivienda-est-2025 | **3 ❌** | 0 | — | sin oráculo | | | | | |
| arquitectura-2026 | 0 ✅ | 5.566 · 5.527 | 222.210 · 13,7 · 76 | 99,9 % | 100 % | 99,5 % | 98,3 % | 95,7 % | 100 % |
| casa-2026 | 0 ✅ | 450 · 438 | 76.763 · 2,6 · 53 | 95,8 % | 100 % | 99,8 % | 97,2 % | 97,4 % | 100 % |
| rstadvanced-2026 | 0 ✅ | 905 · 905 | 206.003 · 3,7 · 50 | sin oráculo | | | | | |
| 2027 | — | **Sin archivo** | | | | | | | |

Todas las salidas convertidas son **válidas** con las reglas de `protocol.rb`. Memoria máxima: 2,8 GB (MEP).

### Correcciones hechas en esta auditoría

| Id | Hallazgo | Corrección | Resultado |
|---|---|---|---|
| C1 | Estructura con 6,6 veces los triángulos de Cmcoordinator: sólidos a dos caras y pilares y pilotes cortados en franjas | Simplificación sin pérdida (fase 3) | 1,95× |
| C2 | Vigas un nivel por debajo (estructura: nivel 63,5 %) | rvt-rs da el nivel de referencia de Revit en `m_level_parameter`; se usa cuando coincide con el nombre de un nivel | Estructura **99,8 %**, Arquitectura 2024 **100 %** |
| C3 | Riostras como vigas (`IfcBeam` en vez de `IfcMember BRACE`) | Armazón con eje inclinado más de 5° = riostra, que es lo que Revit marca como `StructuralType.Brace` | 12 de 22 corregidas (las otras 10 no tienen eje en el archivo); clase IFC 97,1 % → 98,5 % |
| C4 | Escaleras, tramos, mesetas, zancas y barridos de muro de 2024 descartados por no tener malla | Un elemento constructivo de la tabla se conserva aunque rvt-rs no lo dibuje: mantiene datos, nivel y agrupación; el resumen lista `sin_malla` por categoría | Arquitectura 2024: 98,9 % → **99,9 %** emparejados |
| C5 | Masas incluidas que Cmcoordinator no envía | Se omiten: Revit las oculta en las vistas por defecto | Sin elementos de más |
| C6 | Árboles RPC como cajas macizas de hasta 2.046 m³ | Cajas translúcidas y aviso | Ver captura |
| C7 | Elementos sin nivel en el archivo | Último recurso: el nivel más alto bajo la base del elemento (la cara superior en el armazón) | Casa +1. En Arquitectura 2026, 21 vigas con un desfase de −3,8 m respecto a su nivel quedan en el nivel donde están de verdad, no en el de referencia (el archivo no guarda ni la referencia ni el desfase) |

### Límites que quedan (no dependen de `cmconv`)

| Id | Límite | Causa | Tratamiento |
|---|---|---|---|
| L1 | **Revit 2025 (compilación 20250815): ningún elemento** | rvt-rs v0.7.0 solo lee 129 materiales de ese archivo | `cmconv` responde con el código 3 y un mensaje claro, sin salida vacía. Pendiente: probar otros archivos de 2025 y comunicarlo al proyecto rvt-rs |
| L2 | **Revit 2027 sin medir** | No hay ningún archivo de 2027 | `corpus.sh` ya busca `Downloads/REVIT/Arquitectura-2027.rvt` |
| L3 | Revit 2023: tipo y familia al 43,8 %, agregados al 0 %, 9 muros cortina como `IfcWall` | rvt-rs decodifica en 2023 muchos menos campos (sin `m_type_id` en la mitad, sin `m_aggregate_whole`, sin ejes de muro) | Documentado; mejora del lector |
| L4 | Escaleras y barridos de 2024 sin geometría | rvt-rs no los dibuja en archivos de 2024 | Se conservan como datos (C4) |
| L5 | Sistemas de vigas, rigidizadores, barandales superiores, topografía, emplazamiento y sistemas de mobiliario | rvt-rs no los lee | Documentado (H5 de la auditoría base) |
| L6 | Cantidades de ejemplares de familia (Qto de montantes, paneles, pilares, vigas, puertas y ventanas) | Revit las calcula al abrir; no están en el archivo | No se estiman (regla de Cmcoordinator) |
| L7 | 10 riostras y 4 cordones sin eje en el archivo | Sin «Uso estructural» (-1001381) ni eje | Siguen como `IfcBeam` |
| L8 | Vegetación de la casa que Cmcoordinator no envía (11) | La vista 3D de Revit la ocultaba; el convertidor no tiene vista | Correcto: el archivo la contiene |

## Siguiente

- Revit 2027: guardar un modelo desde Revit 2027 en `Downloads\REVIT\Arquitectura-2027.rvt` y repetir `corpus.sh`.
- Revit 2025: más archivos de prueba y aviso al proyecto rvt-rs.
- Fase 4: materiales y colores por material; conjuntos que faltan (sistemas de vigas).
- Fase 5: integración en el CDE (Almacenamiento → conversión en segundo plano → caja BCF), cuando se decida.
