---
version: "0001"
---
# Inventario de fichas del convertidor de Archivo RVT para revisión humana

Versión del documento: 0001

Fecha: 2026-10-25. Carpeta de subida: `Convertidor-revit-json-malla/expediente_2026-10-24_235915Z/para_subir_RAG/` (solo fichas). Contraste: `consultas/informe_rag_0001.md`. **Nada subido ni indexado: la subida corresponde al usuario.**

| Ficha | Nueva o corrección | Regla o incidente | Fuente | Conceptos y relaciones | Documento que sustituiría | Estado |
|---|---|---|---|---|---|---|
| `arq-convertidor-rvt-json-mallas-rvt-rs-versiones_0001.md` | Nueva | Regla: lo que da cada versión lo decide rvt-rs; el convertidor no inventa, avisa | `README.md`, `seguimiento_0002.md`, `corpus_externo_0001.md` | Archivo RVT → convertidor cmconv → Revisión en Vivo; depende de rvt-rs | Ninguno | Lista para revisar |
| `aud-convertidor-rvt-oraculo-vivo-ifc-y-muestras_0001.md` | Nueva | Regla: auditar contra la referencia del modelo en vivo, la Exportación IFC de Revit y el mismo modelo en cada versión | `corpus_externo_0001.md`, `pruebas/auditar_*.py` | Revisión en Vivo y Exportación IFC como referencias; GlobalId y ElementId como claves | Ninguno | Lista para revisar |
| `cmp-cantidades-qto-las-calcula-revit-no-el-rvt_0001.md` | Nueva | Regla: las Cantidades Qto_ las calcula Revit; el Archivo RVT no guarda las de ejemplares de familia | `BimElementReader.cs`, `IfcNativeSets.cs`; CDE: `element_measurements.rb`, `revit-quantities.ts` | Aplica «Cantidades Qto_» y «Cálculo de Volumen por Caja Envolvente» (prohibido) | Ninguno | Lista para revisar |
| `met-mallas-guardadas-rvt-quitar-caras-dobles_0001.md` | Nueva | Regla: quitar caras dobles y franjas antes de simplificar | `Simplificador.cs`, `seguimiento_0002.md` C1 | Mallas del Archivo RVT → Revisión en Vivo | Ninguno | Lista para revisar |
| `met-rvt-nivel-referencia-vigas-y-riostras_0001.md` | Nueva | Regla: nivel de referencia por `m_level_parameter`; viga inclinada = riostra | `RvtRsReader.cs`, `Traductor.cs`, `seguimiento_0002.md` C2/C3/C7 | Coherente con `BimElementReader.AssemblyClass` de Cmcoordinator | Ninguno | Lista para revisar |
| `fallo-2026-10-25-rvt-rs-registros-sin-elementid_0001.md` | Nueva | Incidente: registros sin ElementId abortaban la conversión | `RvtRsReader.cs`, `corpus_externo_0001.md` E3 | rvt-rs → convertidor cmconv | Ninguno | Lista para revisar |

## Comprobaciones

- Nombres en minúsculas, con guiones, sin acentos y con el sufijo de versión; los seis tienen entre 49 y 56 caracteres (límite: 62).
- Prefijos existentes (`arq`, `aud`, `cmp`, `met`, `fallo`); el incidente lleva la fecha comprobada.
- Ninguna credencial, token, contraseña ni dato de cliente: las referencias se citan por canal, no por contenido.
- Hechos frente a hipótesis:
  - el caso de los 21 montantes de 2027 se presenta como pendiente de comprobar, no como causa confirmada;
  - el cálculo de cantidades sobre la malla se presenta como posibilidad condicionada, no como algo implementado.
- Contrato visual: no aplica. Ninguna ficha crea ni cambia una interfaz; la ficha de cantidades describe lo que muestra el CDE sin modificarlo.

## Glosario: conceptos nuevos propuestos (subir antes que las fichas)

- **convertidor cmconv:** programa de línea de órdenes para Linux que transforma un Archivo RVT, sin Revit y sin Exportación IFC, en los paquetes del protocolo 3 de una Revisión en Vivo. Vive en `Convertidor-revit-json-malla/`.
- **rvt-rs:** lector de código abierto (Apache 2.0) del formato de Archivo RVT, que el convertidor cmconv ejecuta como programa externo. Su cobertura decide lo que se puede convertir de cada versión de Revit.

No se ha modificado el glosario indexado: requiere autorización.

## Orden de subida recomendado

1. Glosario con los dos conceptos nuevos.
2. `arq-…` (el contexto general).
3. `cmp-…` y `aud-…`.
4. `met-…` (las dos).
5. `fallo-…`.

Después de la subida, comprobar con una búsqueda `mix` que «rvt-rs» y «convertidor cmconv» aparecen como una sola entidad cada uno.
