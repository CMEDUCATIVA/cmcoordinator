---
version: "0004"
---
# Vistas, láminas y filtros de Revit en el CDE: Cmcoordinator, convertidor y herramientas de GitHub

Versión del documento: 0004

Fecha: 2026-10-25. Consulta del usuario: «¿Con Cmcoordinator puedo mostrar las vistas también en el CDE?», «¿Se puede desarrollar para que se envíe?», «¿También en el convertidor?» y «¿Hay desarrollo en GitHub para implementarlo?». **Solo análisis: no se ha desarrollado nada.** Decisión del usuario: documentarlo y retomarlo más adelante; el foco pasa a Cmcoordinator.

## 1. Estado actual de Cmcoordinator

- **Puntos de vista BCF** (ya existen):
  - Al crear una incidencia desde el panel del CDE en Revit (`VIEWPOINT_GENERATION_REQUESTED`), se toma la vista 3D activa y se envía al CDE:
    - cámara en perspectiva u ortogonal;
    - caja de sección convertida en planos de corte;
    - elementos visibles y seleccionados;
    - captura PNG.
  - Al revés (`VIEWPOINT_DATA`), Revit abre un punto de vista del CDE.
  - Código: `src/OpenProject.Revit/Extensions/BcfExtensions.cs` (`GenerateViewpoint`) y `src/OpenProject.Revit/Entry/IpcHandler.cs` / `OpenViewpointEventHandler.cs`.
- **Sincronización en vivo:**
  - Publica el modelo tal como se ve en la vista 3D activa: las categorías y subcategorías ocultas no se envían (`CaptureScheduler.cs`).
  - No publica la vista como objeto: ni cámara, ni filtros, ni colores.
- **Por comprobar:** si un punto de vista BCF se aplica bien sobre un modelo en vivo. Puede haber diferencias de origen de coordenadas entre IFC y modelo en vivo.

## 2. Lo que no se envía y cómo se haría en Cmcoordinator

| Qué | Cómo lo enviaría Cmcoordinator (API de Revit) | Qué necesita el CDE | Esfuerzo |
|---|---|---|---|
| Vistas 3D guardadas | Recorrer las `View3D` que no son plantilla y enviar por cada una: nombre, cámara (`ViewOrientation3D`), caja de sección y elementos visibles (`FilteredElementCollector(doc, view.Id)`) | Lista «Vistas de Revit» en el visor que aplique cámara, corte y visibilidad (como un punto de vista BCF) | Bajo |
| Plantas, secciones y alzados | a) Exportar PDF o imagen (exportación PDF nativa desde Revit 2022; `ImageExportOptions`). b) Vectores con el exportador 2D de la API, como SVG | a) Documentos versionados ligados a la revisión. b) Visor 2D nuevo enlazado con el 3D | a) Bajo · b) Alto |
| Láminas | Exportar cada lámina a PDF (o DWG) ligada a la revisión | Documentos versionados y lista de láminas por modelo | Bajo–medio |
| Filtros, colores y gráficos de vista | Por vista: categorías y elementos ocultos, filtros de vista con sus modificaciones, modificaciones por elemento → «estado de vista» (elemento → oculto / color / opacidad) | Aplicar el estado en el visor (xeokit ya sabe ocultar, colorear y transparentar) | Medio |

**Riesgos:**
- Cotas y textos solo existen en 2D: llegan por PDF o SVG, no al 3D.
- Exportar muchas vistas o láminas consume tiempo de Revit: hay que hacerlo por tandas.
- Revit no da el color final de cada elemento: hay que resolver los filtros y su prioridad.
- Coordenadas: el mismo riesgo que con los puntos de vista BCF.

**Orden recomendado:**
1. Vistas 3D guardadas.
2. Láminas y plantas en PDF.
3. Colores y filtros de vista.
4. Plantas en vectores con visor 2D, solo si hace falta.

## 3. El convertidor `cmconv` (sin Revit)

rvt-rs v0.7.0 no decodifica vistas, láminas ni filtros: `rvt-elements` no devuelve ninguna clase de vista (comprobado con `pruebas/rvtrs_vistas.sh` sobre la muestra de Autodesk 2026).

| Herramienta de rvt-rs | Qué da | Límite |
|---|---|---|
| `rvt-sheet` | Una planta en SVG dibujada a partir de los elementos decodificados | No es la vista de Revit: sin cotas, etiquetas, recortes ni gráficos de vista |
| `rvt-schedule` | CSV de elementos y habitaciones | No son las tablas configuradas en Revit |

Lo que el convertidor sí podría añadir con lo que rvt-rs ya lee:
- habitaciones (número, nombre, nivel y forma; hoy se omiten);
- capas de muros, suelos y cubiertas con el color de su material (2024 y 2025);
- una planta orientativa por nivel.

## 4. Desarrollo encontrado en GitHub

Proyecto del autor `baleti`, licencia MIT, experimental («de investigación»; sin garantías para archivos cualquiera):

| Repositorio | Qué hace | Datos publicados |
|---|---|---|
| [rvt-reader](https://github.com/baleti/rvt-reader) | Lector del Archivo RVT en Python (olefile, numpy; Rust opcional) para 2011–2026 | Geometría: 99,5 % en ejemplares de familia y 99,3 % en muros. Parámetros: ~95 % de 8,98 millones de valores exactos frente a la API de Revit |
| [rvt-sheets](https://github.com/baleti/rvt-sheets) | Dibuja láminas y vistas en SVG (PDF por conversión): cajetines, ventanas de vista, plantas, secciones, alzados, leyendas, tablas, 3D ortogonal, **filtros y modificaciones de vista**, fases, grosores y tramas de línea. Cotas, etiquetas y textos en parte | Concordancia visual con Revit: 78/100 de media (55–90) en 16 láminas. Faltan tramas de superficie en proyección y nubes de revisión |
| [rvt-viewer](https://github.com/baleti/rvt-viewer) | Visor web: 3D con caja de sección, plantas, secciones y alzados con anotaciones, estilos visuales y navegación de láminas | Corpus de 89 muestras de Autodesk; Revit 2017–2026 |

**Valoración:**
- Cubre casi todo lo que el convertidor no puede dar hoy (vistas, láminas, filtros).
- Además, rvt-reader declara muchos más parámetros exactos que rvt-rs (≈95 % frente a ≈50 % de los identificadores medidos con rvt-rs). Podría probarse como segundo lector del convertidor, que ya tiene el lector intercambiable (`IRvtReader`).
- No consta compatibilidad con Revit 2027.
- La calidad de láminas aún no sirve para entregar planos.

## 5. Evaluación pendiente (cuando el usuario lo decida)

1. Descargar los tres repositorios en `Convertidor-revit-json-malla/terceros/`.
2. Pasar el corpus 2023–2027 (casa, Arquitectura, Estructura, RE1).
3. Medir rvt-reader frente a rvt-rs con las mismas referencias y guiones (`auditar_salida.py`, `auditar_contra_ifc.py`).
4. Generar con rvt-sheets láminas y plantas de las muestras de Autodesk y compararlas con Revit.
5. Informe con la recomendación.

Fuentes:
- `pruebas/rvtrs_vistas.sh` (salida del 2026-10-25);
- páginas de GitHub de rvt-reader, rvt-sheets y rvt-viewer consultadas el 2026-10-25 (datos declarados por su autor, no verificados aquí).
