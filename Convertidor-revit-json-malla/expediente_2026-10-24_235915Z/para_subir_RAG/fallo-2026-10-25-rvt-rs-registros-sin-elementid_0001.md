---
version: "0001"
---
# Fallo: registros de rvt-rs sin ElementId hacían fallar toda la conversión

Versión del documento: 0001

**Ámbito:** convertidor `cmconv` con rvt-rs v0.7.0 (`rvt-elements`). Archivos RVT públicos `2024_Core_Interior.rvt` (Revit 2024) y `Revit_IFC5_Einhoven.rvt` (Revit 2023), del repositorio `magnetar-io/revit-test-datasets`. Ocurrió y se corrigió el 2026-10-25.

**Qué pasó.** Los dos archivos terminaban con código 1 y el error «Can not convert Null to Int64», sin ninguna salida. El corpus del equipo no lo había mostrado: apareció al probar modelos públicos.

**Por qué.** `rvt-elements` devuelve algunos registros con `id` nulo. Core Interior tenía 3.777. El lector suponía que todo registro tiene ElementId y convertía el campo sin comprobarlo, así que un solo registro anónimo abortaba la lectura de todo el archivo.

**Qué dejó (la regla).** Un dato de rvt-rs que puede faltar se comprueba antes de usarlo, y un registro inutilizable se omite y se cuenta en un aviso, sin abortar la conversión. Un registro sin ElementId no puede ser referenciado por ningún otro elemento ni por la Revisión en Vivo, así que omitirlo no pierde nada utilizable. Tras la corrección, Core Interior da 854 elementos válidos con el aviso «3777 registros sin ElementId omitidos», y Einhoven da 35. La lección general: probar con modelos que no ha hecho el equipo, porque cada autor y versión de Revit deja estructuras distintas.

## Relacionado

Archivo RVT, convertidor cmconv, rvt-rs, Revisión en Vivo.

Fuente:
- `Convertidor-revit-json-malla/src/CmConv/Lectura/RvtRsReader.cs` (lectura de `elements.json`, aviso «registros sin ElementId omitidos»);
- `Convertidor-revit-json-malla/expediente_2026-10-24_235915Z/Auditoria/corpus_externo_0001.md` (hallazgo E3);
- comprobado el 2026-10-25.
