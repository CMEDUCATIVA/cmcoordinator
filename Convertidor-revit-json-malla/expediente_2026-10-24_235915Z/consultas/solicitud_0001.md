---
version: "0001"
---
# Solicitud: convertidor de .rvt a JSON con mallas

Versión del documento: 0001

Fecha UTC: 2026-10-24.

## Encargo del usuario

> «vas hacer una sub carpeta aqui en cmcoorinator llamalo "Convertidor-revit-json-malla" alli descargas revit barch processor analiza y estudia ponlo en un expediente por que vamos hacer skill_functional_0002.md Fully Functional Application with Pluggable API Adapter cierto seria asi aun no desarrolles»

**Alcance autorizado:** crear la carpeta, descargar Revit Batch Processor, analizarlo y documentarlo. **No desarrollar.**

## Objetivo del producto (contexto de la conversación)

1. Un usuario sube un archivo `.rvt` al módulo **Almacenamiento** del CDE (plugin `plugins/almacenamiento`).
2. El CDE lo convierte **directamente al formato del modelo en vivo**: paquetes JSON con mallas («Revit Live, protocolo 3»), **sin pasar por IFC ni XKT**.
3. El modelo se ve en el visor BIM con la misma clasificación buildingSMART, Psets, cantidades, piezas de conjuntos y filtros que un modelo sincronizado con Cmcoordinator.

## Motivo

Datos medidos en el CDE DEV (2026-10-24):

| Camino | Ejemplo | Tiempo en el servidor |
|---|---|---|
| IFC → IfcConvert → COLLADA2GLTF → gltf2xkt + 2 extracciones de metadatos | ELECTRICAS_IFC, 105,5 MB | 2 min 20 s; los IFC de 154 MB y 504 MB **fallaron** |
| Paquetes JSON con mallas (Cmcoordinator) | «nuevo modelo», 5.570 elementos, 44 MB | 4 s desde la recepción hasta el guardado |

## Restricciones

- El CDE es **educativo, gratuito y de código abierto**.
- No convertir a IFC por ahora (indicación previa del usuario).
- **El convertidor irá a un servidor Linux** (indicación del usuario, 2026-10-24). Revit solo funciona en Windows, así que un motor basado en Revit (Revit Batch Processor o Cmcoordinator) necesitaría una máquina virtual Windows dentro de ese servidor, o un equipo Windows aparte.
- Un `.rvt` solo se interpreta con fidelidad mediante el motor de Revit; las alternativas sin Revit (rvt-rs, DDC, ODA) se analizan en `informe_0001.md`.
