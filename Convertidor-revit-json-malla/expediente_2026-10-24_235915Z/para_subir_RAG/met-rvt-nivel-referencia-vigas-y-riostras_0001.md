---
version: "0001"
---
# En un Archivo RVT el nivel de una viga es el de referencia y una viga inclinada es una riostra

Versión del documento: 0001

**Ámbito:** convertidor `cmconv` con rvt-rs v0.7.0 (`rvt-elements`), Archivos RVT de Revit 2024–2027. Comprobado el 2026-10-25 contra la referencia del modelo en vivo de Cmcoordinator.

**Regla.**
- **Nivel.** El nivel de un elemento es el que Revit muestra, que rvt-rs entrega en el campo `m_level_parameter` como nombre de nivel. Si ese nombre coincide con un único nivel del modelo, sustituye al enlace del registro (`m_levelId`). En las vigas, `m_levelId` apunta a otro nivel; el bueno es el nivel de referencia («Nivel de referencia», −1001383).
- **Sin ningún nivel en el archivo.** Como último recurso, se toma el nivel más alto que quede por debajo de la base del elemento; en el armazón estructural, por debajo de su cara superior, porque el nivel de referencia está arriba de la viga.
- **Riostras.** Un armazón estructural cuyo eje (`m_beam_axis_*`) sube más de 5° es una riostra, `IfcMember` con tipo predefinido `BRACE`. Es lo que Revit marca como `StructuralType.Brace` y lo que envía Cmcoordinator. Sin eje en el archivo, sigue como `IfcBeam`.

## Por qué

- Con `m_levelId`, Estructura 2024 tenía el nivel correcto solo en el **63,5 %**: todas las vigas quedaban un nivel por debajo (115 + 107 + 107). Con `m_level_parameter` pasó al **99,8 %**, y Arquitectura 2024 al **100 %**.
- Las riostras con eje están entre 26° y 40°, y las vigas, viguetas y cordones con eje están a 0°: separación limpia. Se corrigieron 12 de 22 riostras; las otras 10 no tienen eje ni «Uso estructural» (−1001381) en lo que lee rvt-rs.
- Límite comprobado: en Arquitectura 2026, 21 vigas están colgadas del nivel 03 (7,6 m) con un desfase de unos −3,8 m. El archivo no da ni la referencia ni el desfase, así que el último recurso las deja en el nivel donde están físicamente. Es coherente con la geometría, pero distinto de la referencia.

## Cómo se aplica

- Implementado en:
  - `Convertidor-revit-json-malla/src/CmConv/Lectura/RvtRsReader.cs` (`LevelName`, `AxisInclinationDegrees`, sustitución por nombre tras leer los niveles);
  - `Semantica/Traductor.cs` (`LevelByHeight`, regla de riostra con `BraceInclinationDegrees`).
- Las piezas sin nivel propio (montantes, paneles, tramos) heredan antes el de su conjunto padre. La altura se usa solo si no hay ningún dato.
- Para diagnosticar diferencias de nivel, usar `diferencias_nivel` de `pruebas/auditar_salida.py`, que agrupa por categoría, nivel de la referencia y nivel convertido.

## Qué pasa si no

Sin estas reglas, el filtro por planta del CDE muestra la estructura desplazada una planta hacia abajo y las riostras se clasifican como vigas, con su `Pset` y sus cantidades de viga.

## Relacionado

Archivo RVT, Revisión en Vivo, Cmcoordinator, convertidor cmconv, rvt-rs, Modelos IFC, clase IFC.

Fuente:
- `Convertidor-revit-json-malla/expediente_2026-10-24_235915Z/Auditoria/seguimiento_0002.md` (correcciones C2, C3 y C7);
- `src/OpenProject.Revit/LiveSync/Bim/BimElementReader.cs` (`AssemblyClass`: riostra por `StructuralType.Brace`);
- comprobado el 2026-10-25.
