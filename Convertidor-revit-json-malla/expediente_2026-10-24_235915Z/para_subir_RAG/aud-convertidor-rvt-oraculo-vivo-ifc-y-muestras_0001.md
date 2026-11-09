---
version: "0001"
---
# Una conversión de Archivo RVT se audita contra la Revisión en Vivo, la Exportación IFC de Revit y el mismo modelo en cada versión

Versión del documento: 0001

**Ámbito:** auditoría del convertidor `cmconv` (`Convertidor-revit-json-malla/pruebas/`), en WSL Ubuntu, comprobada el 2026-10-25.

**Regla.** Una conversión no se da por buena porque el archivo se abra, pase la validación o «se vea bien». Se compara elemento a elemento con una referencia producida por Revit:

1. **Referencia del modelo en vivo.** Se exporta del CDE la Revisión en Vivo que Cmcoordinator sincronizó del mismo modelo y se compara por ElementId: categoría, clase IFC, tipo predefinido, nivel, tipo, familia, conjunto padre, GlobalId, parámetros, Pset y Cantidades Qto_, malla y triángulos.
2. **Exportación IFC de Revit**, cuando no hay modelo en vivo. Se compara por GlobalId: clase, tipo predefinido y planta, con las piezas heredando la planta de su conjunto. En IFC2X3, la clase IFC4 cuenta como su superclase IFC2X3 (por ejemplo, `IfcPipeSegment` = `IFCFLOWSEGMENT`).
3. **El mismo modelo en cada versión.** Autodesk publica los mismos proyectos de ejemplo para cada versión de Revit, y Revit conserva los ElementId al actualizar. Así, una sola referencia sirve para 2023, 2024, 2025, 2026 y 2027:
   - `racbasicsampleproject` es la «casa» (canal 8);
   - `racadvancedsampleproject` es «Arquitectura» (canal 9).

La comprobación visual (visor local con el mismo xeokit que el CDE) complementa los números; no los sustituye.

## Por qué

- La comparación por versión separó los límites del lector de los del convertidor. El mismo modelo dio los mismos porcentajes en 2024–2027 y solo cayó en 2023, lo que señala al lector rvt-rs.
- También aisló un caso solo de 2027: 21 montantes en otro nivel, justo los que en la referencia tienen un nivel distinto del de su muro cortina.
- La Exportación IFC de Revit demostró que Revit 2025 sí convierte (modelos RE1 al 100 %). Lo que falla es una compilación concreta (20250815).
- Los modelos públicos encontraron un fallo real que el corpus del equipo no mostraba: registros sin ElementId que hacían fallar toda la conversión.

## Cómo se aplica

- **Corpus del equipo:**
  - `pruebas/corpus.sh [mixtas]`, que convierte de uno en uno por memoria;
  - `pruebas/resumen_corpus.py` para ver los resultados;
  - `pruebas/auditar_salida.py <salida> <referencia.json>` para comparar con la referencia del modelo en vivo;
  - `pruebas/triangulos_por_categoria.py` para el peso de las mallas.
- **Corpus externo:**
  - `pruebas/descargar_corpus_github.sh` y `pruebas/corpus_github.sh`, con modelos MIT y su IFC (repositorios `Drshelden/IFC-ECS` y `magnetar-io/revit-test-datasets`);
  - `pruebas/descargar_muestras_autodesk.sh` y `pruebas/corpus_autodesk.sh`, con las muestras de `revit.downloads.autodesk.com`. El SHA256 se comprueba contra `research/autodesk-sample-projects.tsv` de rvt-rs;
  - `pruebas/auditar_contra_ifc.py <salida> <archivo.ifc>`.
- **Visor:** `pruebas/visor/visor.html` con `captura.js` (Chrome sin ventana, que siempre se cierra al terminar).
- **Datos privados y de terceros:**
  - las referencias del modelo en vivo son datos de clientes: van excluidas de Git;
  - las muestras de Autodesk se descargan solo para medir y quedan fuera del repositorio, porque sus condiciones de uso no se han comprobado.
- Una diferencia que no se puede atribuir sin una referencia nueva queda «pendiente de comprobar». No se corrige a ciegas.

## Qué pasa si no

Con una sola versión o un solo modelo, un fallo de compilación concreta se toma por un fallo de toda la versión (ocurrió con 2025). Sin referencia de Revit, un dato verosímil pero equivocado pasa como correcto: por ejemplo, vigas un nivel por debajo en el 36 % de un modelo estructural.

## Relacionado

Archivo RVT, Revisión en Vivo, Cmcoordinator, CDE, Exportación IFC, Modelos IFC, GlobalId, Cantidades Qto_, rvt-rs, convertidor cmconv, Prueba Real de Sincronización.

Fuente:
- `Convertidor-revit-json-malla/expediente_2026-10-24_235915Z/Auditoria/corpus_externo_0001.md` (apartados «Fuentes» y «Hallazgos»);
- `Convertidor-revit-json-malla/pruebas/auditar_contra_ifc.py`;
- `Convertidor-revit-json-malla/pruebas/auditar_salida.py`;
- comprobado el 2026-10-25.
