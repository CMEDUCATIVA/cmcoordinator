---
version: "0001"
---
# Las mallas guardadas en el Archivo RVT se limpian de caras dobles y franjas antes de simplificarlas

Versión del documento: 0001

**Ámbito:** convertidor `cmconv`, modo de mallas mixtas (`rvt-gltf --saved-meshes` de rvt-rs v0.7.0). Comprobado el 2026-10-25 en Estructura (Revit 2024), Arquitectura (2026) y MEP (2024).

**Regla.** La malla de visualización que Revit guarda en el Archivo RVT se limpia en este orden:
1. **Soldar** los vértices repetidos.
2. **Quitar las caras repetidas.** Muchos sólidos están guardados a dos caras: cada triángulo aparece dos veces, primero con la orientación hacia fuera y después invertido. Se conserva la primera.
3. **Simplificar sin pérdida.** Se colapsa todo vértice que no aporta forma: subdivisiones dentro de caras planas y las franjas horizontales con que Revit corta los lados curvos. Un vértice solo se colapsa si se cumplen las tres condiciones:
   - el vecino está en el plano de todas sus caras, con una tolerancia de 0,1 mm;
   - ninguna cara se invierte;
   - la superficie sigue siendo cerrada (cada arista compartida por dos caras).
4. **Ajustar a un presupuesto de triángulos** (1.500 por elemento), agrupando vértices por celdas, solo si el elemento sigue por encima.

La silueta curva real (por ejemplo, un pilar redondo de 84 lados) no se reduce más: hacerlo sería deformar.

## Por qué

- Con solo el presupuesto, Estructura 2024 pesaba **6,6 veces** los triángulos de Cmcoordinator (546.796 frente a 83.144). Los pilares redondos y los pilotes tenían unos 1.200 triángulos cada uno frente a unos 100: cada cara estaba dos veces y el cilindro iba cortado en 13 a 21 alturas.
- Con las caras dobles, cada arista la comparten 4 caras, la superficie deja de ser cerrada y la simplificación sin pérdida no puede actuar.
- Tras la limpieza:
  - Estructura bajó a 162.194 triángulos (1,95×);
  - Arquitectura, de 535.958 a 222.210 (por debajo de Cmcoordinator);
  - MEP, de 1.574.162 a 754.983;
  - todo sin cambiar la forma ni la validez del protocolo 3.
- Comprobación: la primera copia de cada cara forma un sólido cerrado bien orientado. Un pilar de 450 mm × 3,5 m da 0,549 m³, frente a 0,556 m³ del cilindro teórico.

## Cómo se aplica

- Implementado en `Convertidor-revit-json-malla/src/CmConv/Lectura/Simplificador.cs` (`Lossless`, `ToBudget`).
- Diagnóstico de una malla pesada:
  - contar cuántas caras comparten cada arista: 4 indica caras dobles;
  - contar las alturas distintas de los vértices: muchas en un elemento recto indican franjas.
- Medir el peso por categoría frente a la referencia del modelo en vivo con `pruebas/triangulos_por_categoria.py`.
- Si se calculan cantidades sobre la malla, hacerlo después de la limpieza sin pérdida y antes del presupuesto, que sí cambia la forma.

## Qué pasa si no

El visor recibe varias veces más triángulos de los necesarios: más peso de descarga y menos fluidez. Y una simplificación por agrupación aplicada a una malla de caras dobles conserva los triángulos invertidos.

## Relacionado

Archivo RVT, Revisión en Vivo, convertidor cmconv, rvt-rs, Cmcoordinator, visor del CDE.

Fuente:
- `Convertidor-revit-json-malla/src/CmConv/Lectura/Simplificador.cs` (`Lossless`, `CanCollapse`, `Interior`);
- `Convertidor-revit-json-malla/expediente_2026-10-24_235915Z/Auditoria/seguimiento_0002.md` (apartado «Fase 3 — mallas reales» y corrección C1);
- comprobado el 2026-10-25.
