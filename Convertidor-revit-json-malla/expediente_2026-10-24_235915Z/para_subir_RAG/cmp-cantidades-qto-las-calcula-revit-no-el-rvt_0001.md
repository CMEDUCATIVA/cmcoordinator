---
version: "0001"
---
# Las Cantidades Qto_ las calcula Revit al sincronizar; el Archivo RVT no guarda las de ejemplares de familia

Versión del documento: 0001

**Ámbito:** Cmcoordinator (Revit 2026/2027), visor del CDE (modelo en vivo e IFC) y convertidor `cmconv`. Comprobado en el código el 2026-10-25.

**Regla.** Ni Cmcoordinator ni el CDE calculan cantidades a partir de la geometría:

- **Cmcoordinator** toma de Revit los valores que Revit ya tiene calculados y los envía como Cantidades Qto_ (`AddDirectQuantities` y `AddHostQuantities`):
  - `HOST_AREA_COMPUTED`, `HOST_VOLUME_COMPUTED` y `HOST_PERIMETER_COMPUTED` de muros, losas y cubiertas;
  - longitud del eje del muro y ancho de su tipo;
  - línea de ubicación de los elementos rectos;
  - altura del pilar por sus niveles base y superior y sus desfases;
  - `CURVE_ELEM_LENGTH` de tuberías, conductos y bandejas;
  - ancho y alto de puertas y ventanas (del ejemplar o del tipo).

  En Revit 2026/2027 lee además los `Qto_` del exportador IFC de Revit (`IfcNativeSets`). Un valor derivado nunca sustituye a uno declarado por el autor.
- **El CDE** solo muestra:
  - los `Qto_` declarados (`element_measurements.rb`);
  - los parámetros de Revit que son longitud, área o volumen, agrupados en «Revit_CalculatedQuantities» (`revit-quantities.ts`);
  - en la ficha, el ancho, el fondo y el alto de la caja envolvente medidos sobre la malla, sin presentar el volumen de esa caja.

  Mantiene la regla de no aplicar el Cálculo de Volumen por Caja Envolvente.
- **El Archivo RVT** guarda el área y el volumen de muros, losas y cubiertas (el convertidor los copia: 99,9 % iguales a Cmcoordinator). No guarda las áreas y volúmenes de los ejemplares de familia (pilares, vigas, montantes, paneles, puertas, ventanas), porque Revit los calcula al abrir el modelo.

Por eso un convertidor sin Revit no puede copiar esas cantidades. Si se añaden, son un cálculo nuevo sobre la malla real (nunca sobre la caja) y tienen que cumplir esto:
- calcularse antes de simplificar la malla;
- solo con malla real y cerrada;
- dar solo volumen neto, porque los huecos ya están recortados;
- medir antes su error contra los valores de Revit de la referencia del modelo en vivo;
- ir marcadas como «calculadas sobre la malla».

## Por qué

En la auditoría del convertidor, los Pset y las Cantidades Qto_ coincidieron en el 99,9 % de los valores presentes, pero solo estaba presente el 27 % de las propiedades. Los conjuntos que faltaban eran casi todos de ejemplares de familia: `Qto_MemberBaseQuantities`, `Qto_PlateBaseQuantities`, `Qto_ColumnBaseQuantities`, `Qto_DoorBaseQuantities`. Esos valores no están en el archivo.

## Cómo se aplica

- Para saber de dónde sale una cantidad que muestra el CDE, mirar el `source` de la propiedad: «Parámetro de origen · id» o el conjunto `Qto_` declarado.
- En el convertidor:
  - copiar solo lo que el archivo trae;
  - no estimar a partir de cajas (`Semantica/ConjuntosIfc.cs`);
  - cualquier cantidad calculada sobre la malla necesita la medición de error por categoría y un umbral acordado antes de activarse.

## Qué pasa si no

Si se calcula el volumen con la caja envolvente, es erróneo en cuanto el elemento tiene huecos o no es un prisma: por eso está prohibido. Si se presenta un cálculo sobre la malla como si fuera de Revit, el CDE mezcla dos orígenes con precisión distinta (las piezas curvas tienen error de teselado) sin que el usuario lo sepa.

## Relacionado

Cantidades Qto_, Cálculo de Volumen por Caja Envolvente, Cmcoordinator, CDE, Revit, Archivo RVT, Revisión en Vivo, Exportación IFC, convertidor cmconv.

Fuente:
- `src/OpenProject.Revit/LiveSync/Bim/BimElementReader.cs` (`AddDirectQuantities`, `AddHostQuantities`, `AddComputed`);
- `src/OpenProject.Revit/LiveSync/Bim/IfcNativeSets.cs`;
- en el repositorio del CDE: `modules/bim/app/services/bim/ifc_models/element_measurements.rb`, `frontend/src/app/features/bim/ifc_models/revit-live/revit-quantities.ts` e `ifc-viewer.service.ts` (`buildElementFicha`, `computeGeometryFromAABB`);
- `Convertidor-revit-json-malla/expediente_2026-10-24_235915Z/Auditoria/seguimiento_0002.md` (apartado «Fase 2 — semántica»);
- comprobado el 2026-10-25.
