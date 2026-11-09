---
version: "0001"
---
# Contraste con la memoria de desarrollo para las fichas del convertidor de Archivo RVT

Versión del documento: 0001

Fecha: 2026-10-25. Guía: `skill_desarrollo/0_Referencias/preparar_fichas_rag_0003.md`. Solo lectura: no se escribió ni se indexó nada.

## Consultas

- `GET /estado`: memoria `op_cmproyectos`, 286 archivos.
- `POST /buscar`, modo `mix`:
  1. «Convertidor de archivos .rvt de Revit a JSON con mallas sin Revit en Linux con rvt-rs: compatibilidad por versión y límites».
  2. «Cantidades Qto de Revit en Cmcoordinator y en el CDE: de dónde salen, HOST_VOLUME_COMPUTED, nunca derivar de la caja envolvente».
  3. «Cómo se estructura una regla o un incidente para incorporarlo a la memoria y qué denominaciones canónicas del glosario usar para Cmcoordinator, CDE, modelo en vivo y protocolo 3».
  4. «Glosario: nombres canónicos de modelo en vivo, revisión, paquetes del protocolo 3, sincronizador, Almacenamiento del CDE, caja BCF, clase IFC, Pset, Qto».

## Recuperado de la memoria

- **No hay conocimiento sobre el convertidor ni sobre rvt-rs.** Las fichas son conocimiento nuevo, sin duplicado.
- **Entidades aplicables, cuyos nombres se reutilizan:**
  - Archivo RVT; RVT («su semántica completa no queda representada mediante una malla triangulada»);
  - Revit, Cmcoordinator, Revisión en Vivo, Modelos IFC, GlobalId;
  - Exportación IFC («sin inventar niveles, anfitriones, CRS, clases o unidades faltantes»);
  - Cantidades Qto_ («permiten mostrar largo, alto, ancho, área y volúmenes neto y bruto sin completar valores ausentes»);
  - Cálculo de Volumen por Caja Envolvente («procedimiento prohibido […] incorrecto para elementos con huecos»);
  - Prueba Real de Sincronización.
- **Convenciones:**
  - una regla por ficha, con su sección «Relacionado» nombrada según el glosario;
  - la «Corrección de Fichas» exige borrar y verificar el documento indexado anterior antes de subir el corregido;
  - el glosario se sube primero, porque el primer nombre introducido es el que permanece.

## Relaciones propuestas por el agente (no recuperadas)

- La ficha de cantidades aplica al convertidor dos reglas existentes: «Cantidades Qto_ sin completar valores ausentes» y la prohibición del cálculo por caja envolvente. Añade la distinción entre lo que guarda el Archivo RVT y lo que Revit calcula al abrir el modelo.
- Conceptos nuevos que se repiten y que convendría añadir al glosario **antes** de subir las fichas: «convertidor cmconv» y «rvt-rs». Propuesta de definición en `Auditoria/informe_para_rag_0001.md`.

## Discrepancias

Ninguna con la memoria. La entidad «Revit2026/2027» describe el ámbito de captura de Cmcoordinator. El convertidor cubre 2023–2027 sin Revit: no es una contradicción, sino otro sistema.
