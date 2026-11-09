---
version: "0003"
---
# Informe: Revit Batch Processor y opciones del convertidor .rvt → JSON con mallas

Versión del documento: 0003

Fecha UTC: 2026-10-24. Fuente: lectura de `Convertidor-revit-json-malla/terceros/RevitBatchProcessor` (commit `fe5e1ca`, 2026-10-13). No se ejecutó ni compiló nada.

## 1. Qué es Revit Batch Processor (RBP)

| Dato | Valor |
|---|---|
| Repositorio | https://github.com/bvn-architecture/RevitBatchProcessor (unas 420 estrellas) |
| Versión | 1.13.1-beta, Revit 2015–2027 |
| Tecnología | Orquestador C# (`BatchRvt.exe`), add-in de Revit por versión y scripts IronPython que hacen el trabajo |
| Licencia | **GPL v3** (cabeceras en `.cs`/`.py`). Terceros: IronPython (Apache 2.0), JSON.NET (MIT); **DLL de Revit de Autodesk en `References/`: no redistribuir** |
| Mantenimiento | **El autor ya no lo mantiene** (README:2); solo contribuciones de la comunidad |
| Plataforma | **Solo Windows**, con Revit instalado y una **sesión de escritorio interactiva** (no funciona como servicio de Windows) |

## 2. Cómo resuelve la ejecución desatendida

1. **Lanzamiento:** `revit.exe` sin argumentos. Los parámetros van por **variables de entorno** (`BATCHRVT__*`): ruta del script, JSON de datos, tubería de salida e id de proceso (`script_environment.py:30-36,143-160`; `revit_process.py:31-41`).
2. **Versión del archivo:** lee el flujo OLE `BasicFileInfo` (`Format:` / `Build:`) y elige el Revit instalado de esa versión. La ruta la toma del registro, `HKLM\SOFTWARE\Autodesk\Revit\<año>\REVIT-*\InstallationLocation` (`revit_file_version.py:43-121`, `RevitVersion.cs:144-163`).
3. **Add-in dormido:** un `IExternalApplication` que en `OnStartup` hace `ExternalEvent.Create(…).Raise()`. Si no hay variables de entorno, queda inerte (`BatchRvtAddin2024/BatchRvtAddinApplication.cs:36-92`, `ScriptHostUtil.cs:36-79`).
4. **Apertura en memoria:** `Application.OpenDocumentFile(ModelPath, OpenOptions)` con `DetachFromCentralOption.DetachAndPreserveWorksets` y `WorksetConfiguration(OpenAllWorksets)`. Cierre con `doc.Close(false)` y `PurgeReleasedAPIObjects()` (`revit_file_util.py:160-304`, `revit_script_util.py:171-218`). **Sin vista activa.**
5. **Diálogos y errores, en tres capas:**
   - `DialogBoxShowing` → `OverrideResult(1)`, con excepciones por `DialogId` (`TaskDialog_Missing_Third_Party_Updater`=1001, `TaskDialog_Location_Position_Changed`=1002, `TaskDialog_Update_Resources`=1001);
   - `FailuresProcessing` → borrar avisos, resolver errores y, si no se puede, volver atrás;
   - un vigilante Win32 externo que pulsa botones de ventanas `#32770` por su texto (`revit_dialog_util.py`, `revit_failure_handling.py`, `revit_dialog_detection.py`).
6. **Vigilancia:** sondeo cada 0,25 s y lectura de un `progress.json` del add-in. Plazos que terminan en `Kill()`: no arranca (5 min), por archivo (configurable) y no sale (10 min). Si Revit se cuelga, reanuda en una sesión nueva desde el archivo siguiente (`batch_rvt_monitor.py`, `monitor_process.py`).
7. **Código propio:** desde el script, `clr.AddReference("MiDll")` y una llamada con el `Document`. Se ejecuta en contexto API válido.
8. **CLI:** `BatchRvt.exe --settings_file X.json` o parámetros sueltos. **No devuelve códigos de salida**: el resultado solo se sabe leyendo el log.

## 3. Restricción del usuario: servidor Linux

Revit solo funciona en Windows. **RBP y cualquier motor basado en Revit no corren de forma nativa en Linux.** Sirven dentro de una **máquina virtual Windows** alojada en el servidor Linux (KVM/QEMU), con:
- licencia de Windows y licencia de Revit (educativa gratuita para centros educativos);
- inicio de sesión automático (autologon) del usuario licenciado; Revit abierto a mano una vez para pasar los diálogos del primer arranque; CIP desactivado;
- recursos: 16 GB o más de RAM para la VM y disco para Revit, los `.rvt` y la caché.

| Motor | Nativo en Linux | Fidelidad | Coste y licencia |
|---|---|---|---|
| **Revit en VM Windows** + captura de Cmcoordinator | Alojado (VM) | **Máxima** (la de hoy: 99,4 % de clase IFC igual al exportador) | Windows + Revit educativo |
| **rvt-rs** (https://github.com/Erry91/rvt-rs) | ✅ | Parcial: geometría real solo Revit 2024–2025, casi sin parámetros, familias como cajas | Apache 2.0 |
| **DDC cad2data** | ✅ | Alta según el fabricante (sin comprobar) | Binarios propietarios; gratis solo para uso no comercial: confirmar el uso educativo por escrito |
| **ODA BimRv SDK** | ✅ | Alta | Comercial, cara |
| Autodesk Design Automation | ❌ (nube) | Máxima | Pago por uso; los archivos salen de la red |

## 4. Encaje con `skill_functional_0002.md`

El skill describe **aplicaciones de navegador** con un adaptador de datos intercambiable (Mock, Local, Rails). El convertidor tiene dos partes:

- **En el CDE** (Almacenamiento: «Convertir a modelo BIM», cola, estado, errores): **sí** sigue el patrón. Habrá un contrato de API, un adaptador Mock y un adaptador Rails, con permisos en Rails.
- **El motor de conversión** es un servicio, no una aplicación de navegador. Se le aplica la misma **idea de adaptador intercambiable**, en el servidor: un contrato «convertir `.rvt` → paquetes protocolo 3» con motores enchufables (`mock`, `rvt-rs`, `revit-vm`, y en el futuro `ddc` u `oda`). La cola del CDE no cambia al cambiar de motor.

Lista del §3 del skill aplicada a **RBP como pieza reutilizable**:

| # | Requisito | Estado | Evidencia |
|---|---|---|---|
| 1 | Reutilizar el código y respetar la licencia | ❌ **Parar y consultar** | GPL v3 (`LICENSE.txt`, cabeceras). Integrarlo convertiría en GPL lo que lo enlace. Usarlo como herramienta externa sin modificar es discutible |
| 2 | Funciona sin el backend original | — | No aplica: no es un frontend |
| 3–8 | Pantallas, contrato, Mock, Rails, permisos, sin llamadas ocultas | — | No aplica a RBP. Se aplicará a la parte del CDE del convertidor |
| 9 | Pruebas | 🟡 | Tiene pruebas unitarias (Moq, FluentAssertions); no ejecutadas |
| 10 | Mantenible y actualizable | ❌ | Sin mantenedor; Windows y sesión interactiva obligatorios; sin códigos de salida |

**Conclusión:** no integrar RBP. **Reimplementar sus ideas** (no su código) en un componente propio, solo si se elige el motor «Revit en VM».

## 5. Ideas de RBP que reimplementaríamos (motor «Revit en VM»)

1. Add-in de Cmcoordinator **dormido**, que se activa solo con una variable de entorno `CMCONV_JOB=<job.json>` y trabaja en `ExternalEvent.Execute`.
2. Orquestador propio: lee la versión del `.rvt` del flujo OLE `BasicFileInfo` (librería OpenMcdf, MPL 2.0), localiza Revit en el registro y lo lanza con las variables.
3. **Una sesión de Revit por archivo**, con plazos y `Kill()`.
4. `job.json` de entrada, `progress.json` y `result.json` de salida, y **códigos de salida reales**.
5. Apertura `DetachAndPreserveWorksets` + `OpenAllWorksets`; cierre con `Close(false)` y `PurgeReleasedAPIObjects()`.
6. `DialogBoxShowing` y `FailuresProcessing` con las respuestas descritas; vigilante Win32 con textos en español e inglés.
7. **Captura sin vista activa:** crear una `View3D` isométrica por código con todas las categorías del modelo. Cmcoordinator ya captura sobre una `View3D`, sin depender de la vista activa.
8. Envío por el mismo protocolo 3 a un canal creado por el CDE para la conversión.

## 6. Decisiones pendientes del usuario

1. **Servidor Linux de destino:** ¿cuál y con qué recursos (RAM, CPU, disco)? Producción (192.168.1.50) está al 91 % de disco.
2. **Motor inicial:** rvt-rs (libre, nativo, parcial), VM con Revit (máxima calidad, licencias) o ambos detrás del adaptador.
3. **DDC:** ¿se les pide por escrito permiso para el uso educativo?
4. **Licencia de Cmcoordinator y del convertidor:** determina si RBP (GPL) podría usarse algún día como herramienta externa.

## 7. Decisión del usuario y prueba de DDC (2026-10-24)

**Decisión:** «yo no quiero llevar revit yo solo quiero llevar el archivo .rvt y convertirlo a json mallas». Se descarta cualquier motor basado en Revit (incluidos RBP y la VM Windows). Quedan los motores nativos de Linux: DDC, rvt-rs y ODA.

**Prueba de evaluación (uso no comercial) de DDC `rvt-decode` 2.0.1 para Linux x64**, ejecutado en WSL Ubuntu de este equipo:
- descarga de la versión `v2026.10.1` de `datadrivenconstruction/cad2data-Revit-IFC-DWG-DGN`, SHA-256 `fc176c46…39550` comprobado con su `SHA256SUMS.txt`;
- guiones en `Convertidor-revit-json-malla/pruebas/`; salidas en `~/ddc-prueba/out/` de WSL.

| Archivo | Versión Revit | Resultado | Tiempo · memoria | Elementos con geometría | Mallas |
|---|---|---|---|---|---|
| `Arquitectura (1).rvt` (16 MB) | **2026** | ❌ «unsupported Revit version string: "2026"» | 0,1 s | — | — |
| `2023 racbasicsampleproject` («casa») | 2023 | ✅ | 14 s · 436 MB | 931 de 8.276 registros; 605 cuerpos descartados por ser solo cajas | 238 cuerpos, 42.000 caras |
| `Estructura.rvt` (15 MB) | 2024 | ✅ | 18 s · 460 MB | 1.015 (vigas 426, cimentación 304, pilares 157); 176 cajas descartadas | 847 cuerpos, 46.000 caras |
| `MEP.rvt` (38 MB) | 2024 | ✅ | 51 s · 1,06 GB | 4.068 (629 sin categoría) | 2.992 cuerpos, 187.000 caras |

**Qué entrega** (`extract`): `manifest.json`, `elements.tsv` (EID, categoría, familia, tipo, caja envolvente, cuerpos), `types.tsv`, `categories.tsv` y una malla `.obj` por cuerpo. También `glb` (glTF binario).

**Qué no entrega o entrega mal:**
- **Sin parámetros** de instancia ni de tipo, sin Psets ni cantidades, **sin clase IFC**, **sin niveles**.
- Muchas categorías sin nombre (`OST_-2009645`, «sin categoría»: 629 en MEP) y familias o tipos vacíos.
- **Ruido no constructivo:** en casa salen como «con geometría» tablas de planificación (50), cotas (36), elementos de detalle (29) y tipos de texto clasificados como «Mass» («6.5mm Bubble», «Arial»).
- **Faltan elementos:** en casa, 26 muros frente a 46, 28 montantes frente a 144 y 12 paneles frente a 44 que envía Cmcoordinator del mismo modelo.
- **Cobertura declarada en su ayuda:** Revit 2019–2023 completo; 2024–2025 parcial; **2026 no soportado**. Versión `0.1.0-alpha.0`.

**Licencia (EULA):** gratis solo para uso no comercial (2.4: aprendizaje, investigación académica, docencia). Es **comercial** usarlo «como parte de, o para dar, un producto, servicio o SaaS ofrecido a otros», aunque no se cobre (2.3 d). Integrarlo en el CDE requiere licencia o permiso escrito: info@datadrivenconstruction.io.

**Conclusión de la prueba:** DDC para Linux lee bien la geometría estructural de 2023–2024 y es rápido (de 14 a 51 s), pero **no da los datos BIM** que hoy ofrece el modelo en vivo, introduce ruido y **no abre los archivos de Revit 2026**, que son los actuales del usuario. Pendiente, si se quiere: generar el `.glb` y verlo, para juzgar la calidad visual de la geometría.

## 8. Prueba de rvt-rs v0.7.0 (2026-10-24)

**Origen:** https://github.com/DrunkOnJava/rvt-rs (repositorio original; Apache 2.0; 73 estrellas; versión v0.7.0 publicada el 2026-10-24). Binario `x86_64-unknown-linux-musl` con SHA-256 comprobado con su `SHA256SUMS`. Ejecutado en WSL Ubuntu. Guiones en `Convertidor-revit-json-malla/pruebas/` (`rvtrs_lote.sh`, `rvtrs_campos.py`, `comparar_rvtrs_vivo.py`, `glb_resumen.py`).

### Rendimiento

| Archivo | Revit | Elementos (`rvt-elements`) | Tiempo · memoria | glTF con cajas | glTF con mallas guardadas por Revit (`--saved-meshes`) |
|---|---|---|---|---|---|
| Arquitectura (1).rvt | **2026** | 5.843 | 15 s · 0,5 GB | 10 s · 2,9 MB | 29 s · 2 GB · 40 MB |
| Estructura.rvt | 2024 | 1.044 | 11 s · 0,2 GB | 7 s · 0,4 MB | 17 s · 0,8 GB · 41 MB |
| MEP.rvt | 2024 | 6.883 | 43 s · 0,9 GB | 36 s · 2,8 MB | 67 s · 2,8 GB · 66 MB |
| casa (2023) | 2023 | 639 | 6 s · 0,25 GB | 4 s · 0,3 MB | 12 s · 1,1 GB · 24 MB |

### Comparación elemento a elemento con Cmcoordinator (Arquitectura, ElementId)

Referencia: «nuevo modelo» (canal 9 del CDE DEV), 5.570 elementos enviados por Cmcoordinator desde el mismo archivo.

| Aspecto | Resultado |
|---|---|
| Elementos | **5.566 de 5.570 (99,9 %)** con el mismo ElementId. Solo faltan 3 topografías y 1 sistema de vigas. rvt-rs añade 184 materiales, 91 habitaciones y 2 masas |
| Categoría | **1 a 1** en todas: montantes 3.276, paneles 1.349, pilares 176, muros 146, mobiliario 104, puertas 100, techos 65, zancas 36, muros cortina 34, tramos 10… |
| Tipo | Presente en 5.463 (98 %); **coincide en el 100 %** de los que lo traen |
| Familia | Presente en 5.463; coincide en 5.210. Las diferencias son familias del sistema con nombre en inglés («Basic Wall» frente a «Muro básico») |
| Nivel | Presente en 5.491; **coherente en 5.421 (98,7 %)** |
| Clase IFC (`extras.ifcType` del glTF) | Propia de rvt-rs y muy parecida a la nuestra: IFCMEMBER 3.312 (montantes + zancas), IFCPLATE 1.349, IFCCOLUMN 176… |
| Parámetros | 58.331 frente a 102.518 de Cmcoordinator; **49 % de los mismos identificadores**. Casi todos sin nombre (solo el id interno de Revit). Los 89 valores de texto comparables son idénticos |
| Geometría | **5.524 de 5.527** elementos con malla en el vivo tienen malla en rvt-rs |

**Geometría por modo:**
- **Sin `--saved-meshes`:** muros, paneles y montantes con forma parecida (muros 6.448 frente a 7.402 triángulos; paneles 16.188 frente a 16.736). Mobiliario, puertas, luminarias, barandillas y vegetación salen como **cajas** (12 triángulos).
- **Con `--saved-meshes`:** forma real tomada de las mallas que Revit guarda para mostrar, pero **mucho más pesada** que la de Cmcoordinator (luminarias 525.216 frente a 19.980 triángulos; pilares 641.972 frente a 14.688). La vegetación sigue como caja. Haría falta simplificar las mallas o elegir el nivel de detalle.

### Conclusión de la prueba

rvt-rs **sí sirve como base** del convertidor libre en Linux para modelos de Revit 2023–2026:
- abre el archivo actual del usuario (2026), en unos 15–30 s;
- identifica casi todos los elementos con su categoría, tipo, familia y nivel, coincidiendo con Cmcoordinator;
- da geometría para casi todos.

Lo que faltaría para igualar el modelo en vivo:
1. **Nombres de parámetros:** traducir el id interno a nombre con un diccionario, que se puede construir con los datos que ya envía Cmcoordinator (id → nombre).
2. **Psets y cantidades IFC:** aplicar nuestras reglas (`DirectIfcSemantics`) sobre los parámetros y medidas disponibles.
3. **Peso de las mallas** con `--saved-meshes`: simplificar o limitar el detalle.
4. **Materiales y colores** por malla (no evaluados todavía).
5. **Comprobación visual** en el visor del CDE (pendiente).
6. Topografía y sistemas de vigas (no aparecen).
